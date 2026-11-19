using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

public sealed partial class MsfpEngine
{
    // The hello this server sends or answers p in state (paired or
    // waiting): its versions and the address it names; its pins, or, while
    // it can't be called, inbound false and, in its requests, the key it
    // knows p by; to a peer that knows our current key, our commitment.
    // Core and parts are always sent, parts {} when none is on (core
    // section 10.2).
    private byte[] OurHello(string state, PeerRecord? p, bool answering, bool callsFailed = false)
    {
        var named = Named();
        var uncallable = Uncallables.Valid(named);
        var pins = uncallable ? null : _cfg.Pins is { Count: > 0 } list ? Pins.Clean(list) : null;
        return JsonOut.Object(o =>
        {
            o.String("state", state);
            OurVersions().WriteTo(o);
            o.Strings("certificates", pins);
            o.String("address", named);
            o.Bytes("nextCommitment", p is not null && string.IsNullOrEmpty(p.OurKey) ? OurKeys().Commitment : null);
            if (uncallable)
            {
                o.Bool("inbound", false);
                if (!answering && p is not null)
                {
                    o.Bytes("knownKey", p.Key);
                }
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

                events.AddRange(KeepHello(q, msg));
                q.Seen = NowSecond();
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        Notify([.. events]);
        return Answer.Ok(OurHello(ViewOf(now), now, answering: true));
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
    // whether it can be called, its address, its pins, its versions. Its
    // nextCommitment is ignored: a commitment comes from pair or a rotation
    // only (core section 12.5).
    private List<MsfpEvent> KeepHello(PeerRecord p, Hello h)
    {
        var events = new List<MsfpEvent>();
        var inbound = h.Uncallable ? Inbound.No : Inbound.Yes;
        if (p.Inbound != inbound)
        {
            var was = p.Inbound;
            p.Inbound = inbound;
            if (was.Value.Length > 0)
            {
                events.Add(new InboundChanged(p.Id, p.Name, inbound));
            }
        }

        // An address change (core section 13.2) is confirmed by a hello
        // there in Go's engine; this first cut keeps the stored one and
        // records nothing (a later phase).
        p.Pins = [.. Pins.Clean(h.Certificates)];
        var kept = ToRecord(h.Versions);
        if (p.Versions is not null && SameVersions(p.Versions, kept))
        {
            return events;
        }

        p.Versions = kept;
        if (Agreed(p) is null)
        {
            _log.LogInformation("msfp: a peer speaks no version of MSFP's core this server speaks: {Peer}", p.Id);
        }

        events.Add(new VersionsChanged(p.Id, p.Name));
        return events;
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
        Notify(new PeerPaired(p.Id, p.Name));
        if (p.Inbound != Inbound.Yes)
        {
            return View(p); // it learns at its next scheduled hello (core section 7.5)
        }

        try
        {
            return await CheckAsync(id, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code != MsfpError.Internal)
        {
            _log.LogInformation(e, "msfp: the peer couldn't be told it is paired; it learns at its next hello: {Peer}", id);
            return await PeerAsync(id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// This server's operator ending a pairing, or refusing a waiting one, in
    /// any state: the peer is deleted, every key of its history forgotten,
    /// then it is told (core section 7.4).
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task EndAsync(string id, CancellationToken ct = default)
    {
        PeerRecord? p = null;
        var now = NowSecond();
        await _store.UpdateAsync(async tx =>
        {
            p = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            foreach (var k in HistoryKeys(p))
            {
                await tx.ForgetAsync(new ForgottenKey { KeyHash = SHA256.HashData(k), At = now }).ConfigureAwait(false);
            }

            await tx.DeletePeerAsync(p.Id).ConfigureAwait(false);
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
    /// and keeps its answer.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.Uncallable"/>,
    /// <see cref="MsfpError.Suspended"/>, <see cref="MsfpError.KeyLost"/>, <see cref="MsfpError.Unreachable"/>,
    /// <see cref="MsfpError.Unknown"/> (its signed 404: it doesn't know us, and is marked gone).</exception>
    public async Task<PeerView> CheckAsync(string id, CancellationToken ct = default)
    {
        var rec = await PeerRecordAsync(id, ct).ConfigureAwait(false);
        var r = await CallAsync(rec, CallHello, OurHello(ViewOf(rec), rec, answering: false), ct).ConfigureAwait(false);
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

                await UpdatePeerAsync(id, q =>
                {
                    events.Clear();
                    events.AddRange(KeepHello(q, ans));
                    if (ans.State == Hello.StatePaired)
                    {
                        events.AddRange(TheyPaired(q));
                    }

                    var was = q.CallsFailed;
                    q.CallsFailed = ans.CallsFailed;
                    if (ans.CallsFailed && !was)
                    {
                        events.Add(new CallsFailed(q.Id, q.Name));
                    }

                    q.Seen = NowSecond();
                }, ct).ConfigureAwait(false);
                Notify([.. events]);
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
                    }
                }, ct).ConfigureAwait(false);
                Notify([.. events]);
                throw new MsfpException(MsfpError.Unknown);
            default:
                throw Problems.RefusalError(r.Status, r.Problem ?? "");
        }
    }
}
