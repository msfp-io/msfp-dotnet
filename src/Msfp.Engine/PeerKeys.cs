using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>The receiving side of key rotation and revocation (MSFP core sections 9.3, 9.4, 12.3 and 12.6).</summary>
public sealed partial class MsfpEngine
{
    /// <summary>How long a peer's previous key is still heard after it rotated (MSFP core section 12.4).</summary>
    internal static readonly TimeSpan PreviousHeard = TimeSpan.FromMinutes(10);

    private static byte[] StateBody(string state) => JsonOut.Object(o => o.String("state", state));

    private static WireRotation ToRecord(Rotation r) => new()
    {
        Old = r.Old, New = r.New, Next = r.Next, Date = r.Date, OldSignature = r.OldSignature, NewSignature = r.NewSignature,
    };

    // rotate: a peer telling it changed its key (MSFP core section 9.3).
    private async Task<Answer> RotateAsync(CallRequest req, CancellationToken ct)
    {
        if (req.Peer is not { } p)
        {
            return Answer.PlainRefusal;
        }

        Rotation rot;
        try
        {
            rot = Rotations.ReadRotate(req.Body, req.Verified!.Key, Now());
        }
        catch (ProtocolException)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        try
        {
            var state = await ReceiveRotationAsync(p.Id, rot, ct).ConfigureAwait(false);
            return state switch
            {
                null => Answer.Refuse(ProblemType.Rotation),
                Rotations.StatePending => new Answer(202, "application/json", StateBody(state)),
                _ => Answer.Ok(StateBody(state)),
            };
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }
    }

    // Applies a rotation of peer id, its statement checked, as MSFP
    // core section 12.3 says: rotated, pending, or null for refused.
    internal async Task<string?> ReceiveRotationAsync(string id, Rotation rot, CancellationToken ct)
    {
        string? state = null;
        var events = new List<MsfpEvent>();
        var now = NowSecond();
        await _store.UpdateAsync(async tx =>
        {
            state = null;
            events.Clear();
            var p = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            if (!Keys.Same(rot.Old, p.Key))
            {
                // Signed with the key it moved from, still heard: a retry of
                // the rotation that took effect changes nothing; any other is
                // refused.
                if (Keys.Same(rot.New, p.Key))
                {
                    state = Rotations.StateRotated;
                }

                return;
            }

            // Not another peer's key, nor a forgotten one, nor one of its own
            // former keys: a retired key never becomes current again.
            if (await KeyTakenAsync(tx, p, rot.New).ConfigureAwait(false))
            {
                return;
            }

            if (p.Commitment is not null && Keys.Commitment(rot.New).AsSpan().SequenceEqual(p.Commitment))
            {
                // The key it committed to: at once, a suspension lifted.
                events.AddRange(RotatePeer(p, rot, now));
                state = Rotations.StateRotated;
            }
            else if (p.Commitment is not null || p.Suspended is not null)
            {
                // Someone holding its key tried to move the pairing to another
                // key: refused, kept for our operator.
                p.Refused = new RefusedKey { Key = rot.New, At = now };
                events.Add(new RotationRefused(p.Id, p.Name, Keys.Fingerprint(rot.New)));
            }
            else
            {
                // No commitment (its last statement said "-"): our operator
                // confirms the new key's fingerprint with the other operator.
                if (p.Pending?.New is null || !Keys.Same(p.Pending.New, rot.New))
                {
                    events.Add(new RotationPending(p.Id, p.Name, Keys.Fingerprint(rot.New)));
                }

                p.Pending = ToRecord(rot);
                state = Rotations.StatePending;
            }

            await tx.PutPeerAsync(p).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        foreach (var ev in events)
        {
            switch (ev)
            {
                case PeerRotated:
                    _log.LogInformation("msfp: a peer changed its key: {Peer} {Fingerprint}", id, Keys.Fingerprint(rot.New));
                    break;
                case PeerResumed:
                    _log.LogWarning("msfp: a peer whose key was revoked moved to the key it had committed to: no longer suspended: {Peer}", id);
                    break;
                case RotationRefused:
                    _log.LogWarning("msfp: a peer's change of key refused: the new key isn't the one it committed to: {Peer}", id);
                    break;
            }
        }

        Notify([.. events]);
        return state;
    }

    // Makes rot.New p's current key (MSFP core section 12.3, step 5): its old key
    // the previous one, heard for 10 minutes unless it was revoked; the
    // statement's commitment its commitment; the statement kept. A
    // suspension lifts; a pending rotation and a refused one are dropped.
    private static List<MsfpEvent> RotatePeer(PeerRecord p, Rotation rot, DateTimeOffset now)
    {
        var events = new List<MsfpEvent> { new PeerRotated(p.Id, p.Name, Keys.Fingerprint(rot.New)) };
        p.PreviousKey = p.Key;
        p.PreviousUntil = now + PreviousHeard;
        if (p.Suspended is not null)
        {
            p.PreviousUntil = null;
            p.Suspended = null;
            events.Add(new PeerResumed(p.Id, p.Name));
        }

        p.Key = rot.New;
        p.Commitment = rot.Next;
        p.CommitmentFrom = rot.Next is null ? "" : "rotation";
        p.Pending = null;
        p.Refused = null;
        p.Rotations = [.. p.Rotations ?? [], ToRecord(rot)];
        return events;
    }

    // Whether p may not move to key (MSFP core section 12.3, step 1): another
    // peer's key, a forgotten one, or one of p's former keys.
    private static async ValueTask<bool> KeyTakenAsync(IStoreTx tx, PeerRecord p, byte[] key)
    {
        var other = await tx.PeerByKeyAsync(key).ConfigureAwait(false);
        if (other is not null && other.Id != p.Id)
        {
            return true;
        }

        if (await tx.ForgottenAsync(SHA256.HashData(key)).ConfigureAwait(false) is not null)
        {
            return true;
        }

        if (p.PreviousKey is not null && Keys.Same(p.PreviousKey, key))
        {
            return true;
        }

        return (p.Rotations ?? []).Any(r => (r.Old is not null && Keys.Same(r.Old, key)) || (r.New is not null && Keys.Same(r.New, key)));
    }

    // revoke: a peer saying its key is stolen (MSFP core sections 9.4 and 12.6).
    private async Task<Answer> RevokeAsync(CallRequest req, CancellationToken ct)
    {
        if (req.Peer is not { } p)
        {
            return Answer.PlainRefusal;
        }

        try
        {
            Rotations.ReadRevoke(req.Body, req.Verified!.Key, Now());
        }
        catch (ProtocolException)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        var events = await RevokedAsync(p.Id, req.Verified.Key, ct).ConfigureAwait(false);
        Notify([.. events]);
        return Answer.Ok(StateBody(Rotations.StateSuspended));
    }

    // Applies a valid revocation of key, a key of peer id: its current key,
    // which is suspended (already suspended: nothing changes), or its
    // previous one, no longer heard at once.
    internal async Task<List<MsfpEvent>> RevokedAsync(string id, byte[] key, CancellationToken ct)
    {
        var events = new List<MsfpEvent>();
        await UpdatePeerAsync(id, p =>
        {
            events.Clear();
            if (!Keys.Same(key, p.Key))
            {
                p.PreviousUntil = null;
            }
            else if (p.Suspended is null)
            {
                p.Suspended = NowSecond();
                _log.LogWarning("msfp: a peer revoked its key: suspended until it moves to the key it committed to: {Peer}", p.Id);
                events.Add(new PeerSuspended(p.Id, p.Name));
            }
        }, ct).ConfigureAwait(false);
        return events;
    }

    /// <summary>
    /// This server's operator confirming a peer's pending new key, after
    /// comparing its fingerprint with the other operator (MSFP
    /// core section 12.3, case 4): it becomes the peer's key, and the peer is sent a
    /// hello at once.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.Suspended"/>,
    /// <see cref="MsfpError.NoPending"/>, <see cref="MsfpError.KeyTaken"/>.</exception>
    public async Task<PeerView> ConfirmRotationAsync(string id, CancellationToken ct = default)
    {
        var events = new List<MsfpEvent>();
        var now = NowSecond();
        await _store.UpdateAsync(async tx =>
        {
            events.Clear();
            var p = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            if (p.Suspended is not null)
            {
                throw new MsfpException(MsfpError.Suspended);
            }

            if (p.Pending is not { Old: { } old, New: { } pendingNew } pending || !Keys.Same(old, p.Key))
            {
                throw new MsfpException(MsfpError.NoPending);
            }

            if (await KeyTakenAsync(tx, p, pendingNew).ConfigureAwait(false))
            {
                throw new MsfpException(MsfpError.KeyTaken);
            }

            events.AddRange(RotatePeer(p, new Rotation(old, pendingNew, pending.Next, pending.Date,
                pending.OldSignature ?? [], pending.NewSignature ?? []), now));
            await tx.PutPeerAsync(p).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        _log.LogInformation("msfp: a peer's new key confirmed by the operator: {Peer}", id);
        Notify([.. events]);
        var rec = await PeerRecordAsync(id, ct).ConfigureAwait(false);
        if (rec.Inbound == Inbound.Yes)
        {
            try
            {
                await CheckAsync(id, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogInformation(e, "msfp: the peer couldn't be told its new key was confirmed: {Peer}", id);
            }
        }

        return await PeerAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Clears the warning of a peer's refused rotation, once the operator saw it.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public Task DismissRefusedAsync(string id, CancellationToken ct = default) =>
        UpdatePeerAsync(id, p => p.Refused = null, ct);
}
