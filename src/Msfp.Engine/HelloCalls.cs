using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

public sealed partial class MsfpEngine
{
    // The hello this server sends or answers (answering) p in state (paired
    // or waiting): its versions and the address it names; its pins, or,
    // while it can't be called, inbound false and, in its requests, the key
    // it knows p by (knownKey) and p's rotation it waits on; to a peer that
    // knows our current key, our commitment (null p: none). Core and parts
    // are always sent, parts {} when none is on (core section 10.2). An
    // answer adds what Pullable gives, and callsFailed.
    private async Task<byte[]> OurHelloAsync(string state, PeerRecord? p, bool answering, CancellationToken ct,
        (Rotation? Rotation, Revocation? Revocation) pulled = default, bool callsFailed = false)
    {
        var named = Named();
        var uncallable = Uncallables.Valid(named);
        var pins = uncallable ? null : await OwnPinsAsync(answering, ct).ConfigureAwait(false);
        return JsonOut.Object(o =>
        {
            o.String("state", state);
            OurVersions().WriteTo(o);
            o.Strings("certificates", pins is { Count: > 0 } ? pins : null);
            o.String("address", named);
            o.Bytes("nextCommitment", p is not null && string.IsNullOrEmpty(p.OurKey) ? OurKeys().Commitment : null);
            o.Bool("individual", p is null ? null : !p.NotByHand);
            if (uncallable)
            {
                o.Bool("inbound", false);
                if (!answering && p is not null)
                {
                    o.Bytes("knownKey", p.Key);
                    if (p.Pending is { Old: { } old } && Keys.Same(old, p.Key))
                    {
                        o.String("rotation", Rotations.StatePending);
                    }
                    else if (p.Refused is not null)
                    {
                        o.String("rotation", Hello.RotationRefused);
                    }
                }
            }

            if (pulled.Rotation is { } rot)
            {
                o.Object("rotation", rot.WriteBody);
            }

            if (pulled.Revocation is { } rev)
            {
                o.Object("revocation", rev.WriteBody);
            }

            o.Bool("callsFailed", answering && callsFailed ? true : null);
        });
    }

    // How this server describes a pairing to the peer.
    private static string ViewOf(PeerRecord p) => p.State == PeerState.Paired ? Hello.StatePaired : Hello.StateWaiting;

    private static readonly byte[] EndedHello = JsonOut.Object(o => o.String("state", Hello.StateEnded));

    // hello: a peer's (core section 9.2).
    private async Task<Answer> HelloAsync(CallRequest req, CancellationToken ct)
    {
        if (req.Peer is not { } p)
        {
            return Answer.PlainRefusal;
        }

        // A body without state, with a member of the wrong type, or with
        // inbound false and an address not of its form, is refused, and
        // nothing is kept.
        Hello msg;
        try
        {
            msg = Hello.Read(req.Body, answer: false);
            Uncallables.CheckInbound(msg.Uncallable, msg.Address ?? p.Address);
        }
        catch (ProtocolException)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        if (msg.State == Hello.StateEnded)
        {
            // Forgotten without its key remembered: only the side that ends
            // remembers (core section 7.4, step 3).
            await _store.UpdateAsync(tx => tx.DeletePeerAsync(p.Id), ct).ConfigureAwait(false);
            _log.LogInformation("msfp: a peer ended the pairing: {Peer} {Name}", p.Id, p.Name);
            Notify(new PeerEnded(p.Id, p.Name, true));
            return Answer.Ok(EndedHello);
        }

        var events = new List<MsfpEvent>();
        var confirm = false;
        PeerRecord now;
        try
        {
            now = await UpdatePeerAsync(p.Id, q =>
            {
                events.Clear();
                if (msg.State == Hello.StatePaired)
                {
                    events.AddRange(TheyPaired(q));
                }

                var kept = KeepHello(q, msg);
                events.AddRange(kept.Events);
                confirm = kept.Confirm;
                q.Seen = NowSecond();
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        Notify([.. events]);
        if (confirm)
        {
            ConfirmSoon(p.Id);
        }

        await RelationsKeptAsync(p.Id, events, ct).ConfigureAwait(false);

        // From a peer that can't be called: which of our keys it knows us by
        // (core section 12.4); then our rotation for it to pull.
        if (msg.Uncallable)
        {
            now = await KnownKeyAsync(req, now, msg, ct).ConfigureAwait(false);
        }

        return Answer.Ok(await OurHelloAsync(ViewOf(now), now, answering: true, ct, Pullable(now), CallsFailedTo(now.Id)).ConfigureAwait(false));
    }

    // A peer saying it has accepted (core sections 7.1 and 9.2): if we were
    // waiting for it, or lost it after a pairing both had accepted (and it
    // isn't suspended), it is paired.
    private List<MsfpEvent> TheyPaired(PeerRecord p)
    {
        if (p.State == PeerState.Theirs || (p.State == PeerState.Gone && p.WasPaired && p.Suspended is null))
        {
            p.State = PeerState.Paired;
            p.WasPaired = true;
            p.Paired = NowSecond();
            _log.LogInformation("msfp: paired: {Peer} {Name}", p.Id, p.Name);
            return [new PeerPaired(p.Id, p.Name)];
        }

        return [];
    }

    // Keeps what a peer's signed hello, request or answer, says of it:
    // whether it can be called (first: where its address is checked, or
    // stored at once, depends on it), its address (KeepAddress: confirm says
    // a confirmation is due), its pins, its versions. Its nextCommitment is
    // ignored: a commitment comes from pair or a rotation only (core section
    // 12.5).
    private (List<MsfpEvent> Events, bool Confirm) KeepHello(PeerRecord p, Hello h)
    {
        var events = KeepInbound(p, h.Uncallable);
        events.AddRange(KeepIndividual(p, h.Individual));
        var (moved, confirm) = KeepAddress(p, h.Address);
        events.AddRange(moved);
        p.Pins = [.. Pins.Clean(h.Certificates)];
        var kept = ToRecord(h.Versions);
        if (p.Versions is not null && SameVersions(p.Versions, kept))
        {
            return (events, confirm);
        }

        p.Versions = kept;
        if (Agreed(p) is null)
        {
            _log.LogInformation("msfp: a peer speaks no version of MSFP's core this server speaks: {Peer}", p.Id);
        }

        events.Add(new VersionsChanged(p.Id, p.Name));
        return (events, confirm);
    }

    // Keeps a hello answer's callsFailed: shown on the panel (CallsFailed,
    // when it turns true), nothing else (core section 7.5).
    private List<MsfpEvent> KeepCallsFailed(PeerRecord p, bool failed)
    {
        var was = p.CallsFailed;
        p.CallsFailed = failed;
        if (failed && !was)
        {
            _log.LogInformation("msfp: a peer says this server's calls to it couldn't connect; maybe this server can't be called: {Peer}", p.Id);
            return [new CallsFailed(p.Id, p.Name)];
        }

        return [];
    }

    private static bool SameVersions(WireVersions a, WireVersions b) =>
        (a.Core ?? []).SequenceEqual(b.Core ?? []) &&
        (a.Parts ?? []).Count == (b.Parts ?? []).Count &&
        (a.Parts ?? []).All(p => b.Parts!.TryGetValue(p.Key, out var l) && l.SequenceEqual(p.Value));

    /// <summary>
    /// This server's operator accepting a peer that redeemed our code, after
    /// comparing fingerprints: it is paired, and told so at once (when it
    /// can't be reached now, it learns at its next hello).
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotWaiting"/>.</exception>
    public async Task<PeerView> AcceptAsync(string id, CancellationToken ct = default)
    {
        PeerRecord? p = null;
        await _store.UpdateAsync(async tx =>
        {
            var q = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            if (q.State != PeerState.Ours)
            {
                throw new MsfpException(MsfpError.NotWaiting);
            }

            q.State = PeerState.Paired;
            q.WasPaired = true;
            q.Paired = NowSecond();
            p = q;
            await tx.PutPeerAsync(q).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        _log.LogInformation("msfp: paired: {Peer} {Name}", p!.Id, p.Name);
        var joining = p.JoiningGroup;
        if (!string.IsNullOrEmpty(joining))
        {
            // A group's code: the newcomer is signed in before it is told it
            // is paired (MSFP groups section 4, step 3).
            await PairedAsync(p, ct).ConfigureAwait(false);
        }

        Notify(new PeerPaired(p.Id, p.Name));
        if (p.Inbound != Inbound.Yes)
        {
            return View(p); // it learns at its next scheduled hello (core section 7.5)
        }

        try
        {
            var v = await CheckAsync(id, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(joining))
            {
                // Its versions known now: the whole list, pushed to it.
                await PushAllAsync(joining, id, ct).ConfigureAwait(false);
            }

            return v;
        }
        catch (MsfpException e) when (e.Code != MsfpError.Internal)
        {
            _log.LogInformation(e, "msfp: the peer couldn't be told it is paired; it learns at its next hello: {Peer}", id);
            return await PeerAsync(id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends a link, whatever its relations, or refuses a waiting one, in any
    /// state: the peer is deleted, every key of its history recorded for the
    /// signed 404 (not blocked: it isn't kept away from groups), then it is
    /// told (core section 7.4). Panels end relations instead
    /// (<see cref="EndIndividualAsync"/>, <see cref="LeaveGroupAsync"/>) and
    /// keep this for refusing a waiting server.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public Task EndAsync(string id, CancellationToken ct = default) => EndLinkAsync(id, blocked: false, ct);

    private async Task EndLinkAsync(string id, bool blocked, CancellationToken ct)
    {
        PeerRecord? p = null;
        var now = NowSecond();
        await _store.UpdateAsync(async tx =>
        {
            p = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            await ForgetPeerAsync(tx, p, now, byReplacement: false, blocked).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        _log.LogInformation("msfp: pairing ended: {Peer} {Name}", p!.Id, p.Name);
        Notify(new PeerEnded(p.Id, p.Name, false));
        if (p.Inbound != Inbound.Yes || p.Suspended is not null)
        {
            // Not told: a peer that can't be called learns by its next hello's
            // signed 404 (core section 7.5); a suspended one is called no more.
            return;
        }

        try
        {
            await CallAsync(p, CallHello, EndedHello, ct).ConfigureAwait(false);
        }
        catch (MsfpException e)
        {
            _log.LogInformation(e, "msfp: the other server couldn't be told the pairing ended: {Peer}", p.Id);
        }
    }

    // Deletes p in tx, every key of its history forgotten at now (core
    // section 7.4, step 1); byReplacement marks them ended by ReplaceKeyAsync.
    private static async Task ForgetPeerAsync(IStoreTx tx, PeerRecord p, DateTimeOffset now, bool byReplacement, bool blocked = false)
    {
        foreach (var k in HistoryKeys(p))
        {
            await tx.ForgetAsync(new ForgottenKey { KeyHash = SHA256.HashData(k), At = now, Revoked = byReplacement, Blocked = blocked }).ConfigureAwait(false);
        }

        await tx.DeletePeerAsync(p.Id).ConfigureAwait(false);
    }

    // Every key of a peer's history (core section 7.4).
    private static List<byte[]> HistoryKeys(PeerRecord p)
    {
        var keys = new List<byte[]> { p.Key };
        if (p.PreviousKey is not null)
        {
            keys.Add(p.PreviousKey);
        }

        if (p.Pending?.New is { } pending)
        {
            keys.Add(pending);
        }

        foreach (var r in p.Rotations ?? [])
        {
            if (r.Old is not null)
            {
                keys.Add(r.Old);
            }

            if (r.New is not null)
            {
                keys.Add(r.New);
            }
        }

        return keys;
    }

    /// <summary>
    /// Sends a peer a hello now, telling it where the pairing stands here,
    /// and applies its answer: an answer carrying a valid revocation of the
    /// peer's key suspends it, and nothing else of it is kept.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.Uncallable"/>,
    /// <see cref="MsfpError.Suspended"/>, <see cref="MsfpError.KeyLost"/>, <see cref="MsfpError.Unreachable"/>,
    /// <see cref="MsfpError.Unknown"/> (its signed 404: it doesn't know us, and is marked gone).</exception>
    public Task<PeerView> CheckAsync(string id, CancellationToken ct = default) => CheckAsync(id, background: false, ct);

    // CheckAsync, as the operator asks it, or in the background. While this
    // server can't be called, an answer's rotation is applied too (after its
    // revocation), and of a suspended peer's answers, that only (core
    // sections 12.4 and 12.6).
    internal async Task<PeerView> CheckAsync(string id, bool background, CancellationToken ct)
    {
        var rec = await PeerRecordAsync(id, ct).ConfigureAwait(false);
        var uncallable = SaysUncallable();
        var r = await CallAsync(rec, CallHello, await OurHelloAsync(ViewOf(rec), rec, answering: false, ct).ConfigureAwait(false), ct, background)
            .ConfigureAwait(false);
        var events = new List<MsfpEvent>();
        switch (r.Status)
        {
            case 200:
                // An answer with a member of the wrong type, or inbound false
                // with an address not of its form, changes nothing: as if it
                // weren't signed (core section 9.2).
                Hello ans;
                try
                {
                    ans = Hello.Read(r.Body, answer: true);
                    Uncallables.CheckInbound(ans.Uncallable, ans.Address ?? rec.Address);
                }
                catch (ProtocolException e)
                {
                    throw new MsfpException(MsfpError.Unreachable, e.Message, e);
                }

                var pulled = ans.Pull(rec.Key, Now());
                if (rec.Suspended is not null || pulled.Revocation is not null)
                {
                    // A revocation, in an answer signed with the key it revokes:
                    // suspended, and nothing else of that answer kept (core
                    // sections 9.2, 12.6 and 12.7). Any server applies it; one
                    // that can't be called then applies its rotation, the only
                    // thing it reads of a suspended peer's answers.
                    if (pulled.Revocation is not null && rec.Suspended is null)
                    {
                        Notify([.. await RevokedAsync(id, rec.Key, ct).ConfigureAwait(false)]);
                    }

                    if (uncallable)
                    {
                        await PulledRotationAsync(id, pulled.Rotation, ct).ConfigureAwait(false);
                    }

                    return await PeerAsync(id, ct).ConfigureAwait(false);
                }

                var confirm = false;
                await UpdatePeerAsync(id, q =>
                {
                    events.Clear();
                    var kept = KeepHello(q, ans);
                    events.AddRange(kept.Events);
                    confirm = kept.Confirm;
                    if (ans.State == Hello.StatePaired)
                    {
                        events.AddRange(TheyPaired(q));
                    }

                    events.AddRange(KeepCallsFailed(q, ans.CallsFailed));
                    q.Seen = NowSecond();
                }, ct).ConfigureAwait(false);
                Notify([.. events]);
                if (confirm)
                {
                    ConfirmSoon(id);
                }

                if (uncallable)
                {
                    await PulledRotationAsync(id, pulled.Rotation, ct).ConfigureAwait(false);
                }

                await RelationsKeptAsync(id, events, ct).ConfigureAwait(false);
                return await PeerAsync(id, ct).ConfigureAwait(false);
            case 404:
                // It doesn't know us, or suspended our key: gone (core section
                // 9.2). Its pins and versions stay as they were.
                await UpdatePeerAsync(id, q =>
                {
                    events.Clear();
                    if (q.State != PeerState.Gone)
                    {
                        q.State = PeerState.Gone;
                        events.Add(new PeerGone(q.Id, q.Name));
                        _log.LogInformation("msfp: a peer no longer knows this server: {Peer} {Name}", q.Id, q.Name);
                    }
                }, ct).ConfigureAwait(false);
                Notify([.. events]);
                throw new MsfpException(MsfpError.Unknown);
            default:
                throw Problems.RefusalError(r.Status, r.Problem ?? "");
        }
    }

    // Applies a rotation pulled from a hello answer, checked (Hello.Pull), as
    // a rotate would be (core section 12.4, step 2): once the peer's new key
    // is current, the next hello goes at once, with knownKey the new key
    // (step 3).
    private async Task PulledRotationAsync(string id, Rotation? rot, CancellationToken ct)
    {
        if (rot is null)
        {
            return;
        }

        try
        {
            if (await ReceiveRotationAsync(id, rot, ct).ConfigureAwait(false) == Rotations.StateRotated)
            {
                HelloNow(id);
            }
        }
        catch (MsfpException e)
        {
            _log.LogWarning(e, "msfp: a peer's change of key couldn't be kept: {Peer}", id);
        }
    }

    // What a hello answer to p, a peer that can't be called, adds that it
    // can't be sent otherwise (core sections 12.4 and 12.6): while p knows us
    // by our old key, our rotation statement, newly dated, and when that key
    // was revoked, the revocation, newly dated too. Both travel only in
    // answers signed with the old key.
    private (Rotation? Rotation, Revocation? Revocation) Pullable(PeerRecord p)
    {
        var k = OurKeys();
        if (p.Inbound != Inbound.No || p.OurKey != KeyOld || k.Old is null)
        {
            return default;
        }

        var now = Now();
        return (Rotations.SignRotation(k.Old, k.Current, k.Commitment, now), k.OldRevoked ? Rotations.SignRevocation(k.Old, now) : null);
    }

    // Reads, from the hello of p, a peer that can't be called, which of our
    // keys it knows us by (core section 12.4, step 4): during a change, the
    // new one means p moved, and the answer is signed with it already; the
    // old one, with pending or refused, is its answer to our rotation. A key
    // that is neither of ours is ignored.
    private async Task<PeerRecord> KnownKeyAsync(CallRequest req, PeerRecord p, Hello h, CancellationToken ct)
    {
        var k = OurKeys();
        if (p.OurKey != KeyOld || h.KnownKey is null || k.Old is null)
        {
            return p;
        }

        if (Keys.Same(h.KnownKey, k.Current.PublicSpan))
        {
            if (await PeerMovedAsync(p.Id, h.KnownKey, ct).ConfigureAwait(false))
            {
                req.Signer = k.Current;
            }
        }
        else if (Keys.Same(h.KnownKey, k.Old.PublicSpan) && h.RotationState is { } state)
        {
            await RotationAnswerAsync(p.Id, k.Current.Public(), state, ct).ConfigureAwait(false);
        }
        else
        {
            return p;
        }

        try
        {
            return await PeerRecordAsync(p.Id, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return p;
        }
    }
}
