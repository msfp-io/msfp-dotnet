using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>The rotating side, toward each peer (core sections 12.4 and 12.7).</summary>
public sealed partial class MsfpEngine
{
    /// <summary>How long a change of key lasts at most: the old key is kept 30 days (core section 18, point 5).</summary>
    internal static readonly TimeSpan KeyOverlap = TimeSpan.FromDays(30);

    // How often, at most, a peer on our old key is sent a rotate at contact;
    // more often while it answered pending.
    internal static readonly TimeSpan RotateSpacing = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan RotatePendingSpacing = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _keyAction = new(1, 1);

    /// <summary>
    /// Moves this server to its next key: the seed on the server
    /// (<paramref name="nextKey"/> null or empty), or the exported text
    /// given back. Every peer that can be called is sent a rotate at once;
    /// the call returns when each has answered or failed. A change still
    /// under way is finished first: peers that hadn't moved from the key
    /// before must pair again (core section 12.4).
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NoNextKey"/>, <see cref="MsfpError.NotNextKey"/>,
    /// <see cref="MsfpError.RotationUnderWay"/> (another key action is running now).</exception>
    public async Task RotateAsync(string? nextKey = null, CancellationToken ct = default)
    {
        if (!await _keyAction.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new MsfpException(MsfpError.RotationUnderWay);
        }

        try
        {
            var next = await NextKeyFromAsync(nextKey, ct).ConfigureAwait(false);
            await RotateToAsync(next, revoked: false, ct).ConfigureAwait(false);
            await TellRotationAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _keyAction.Release();
        }
    }

    /// <summary>
    /// This server's operator saying its key was stolen (core section 12.7):
    /// the key is revoked, every peer that can be called is sent a revoke
    /// and suspends it; then the server rotates to its next key in the same
    /// action (the seed on the server, or the exported text given back), and
    /// peers holding the commitment follow.
    /// </summary>
    /// <param name="nextKey">The exported next key's text, or null for the seed on the server.</param>
    /// <param name="withoutNext">
    /// When the next key may be stolen too or is lost: a new key, not by a
    /// rotation, every pairing ended (case 2). Not built yet in this
    /// preview: it throws <see cref="NotSupportedException"/>.
    /// </param>
    /// <param name="ct">Cancels the action.</param>
    /// <returns>Whether the server rotated.</returns>
    /// <exception cref="MsfpException"><see cref="MsfpError.NoNextKey"/> (nothing is revoked), <see cref="MsfpError.NotNextKey"/>,
    /// <see cref="MsfpError.RotationUnderWay"/>.</exception>
    public async Task<bool> KeyStolenAsync(string? nextKey = null, bool withoutNext = false, CancellationToken ct = default)
    {
        if (withoutNext)
        {
            throw new NotSupportedException("msfp: a key stolen without its next key is a later phase of msfp-dotnet");
        }

        if (!await _keyAction.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new MsfpException(MsfpError.RotationUnderWay);
        }

        try
        {
            var next = await NextKeyFromAsync(nextKey, ct).ConfigureAwait(false);
            // A change under way is finished first: every peer left then
            // knows us by the key revoked.
            if (OurKeys().Old is { } old)
            {
                await FinishRotationAsync(old.Public(), ct).ConfigureAwait(false);
            }

            var stolen = Current();
            _log.LogWarning("msfp: this server's key revoked: {Fingerprint}", stolen.Fingerprint);
            var statement = Rotations.SignRevocation(stolen, Now()).Body();
            await EachPeerAsync(p => string.IsNullOrEmpty(p.OurKey) && p.Inbound == Inbound.Yes, async p =>
            {
                try
                {
                    var r = await CallAsync(p, CallRevoke, statement, ct).ConfigureAwait(false);
                    if (r.Status != 200)
                    {
                        _log.LogInformation("msfp: a peer couldn't be told this server's key is revoked: {Peer} {Status}", p.Id, r.Status);
                    }
                }
                catch (MsfpException e)
                {
                    _log.LogInformation(e, "msfp: a peer couldn't be told this server's key is revoked: {Peer}", p.Id);
                }
            }, ct).ConfigureAwait(false);
            await RotateToAsync(next, revoked: true, ct).ConfigureAwait(false);
            await TellRotationAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _keyAction.Release();
        }
    }

    // The key to rotate to: the exported text given back, or, for none, the
    // seed on the server.
    private async Task<MsfpKey> NextKeyFromAsync(string? text, CancellationToken ct)
    {
        var r = await ServerRecordAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
        {
            MsfpKey k;
            try
            {
                k = Rotations.ParseNextKey(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
            }
            catch (ProtocolException)
            {
                throw new MsfpException(MsfpError.NotNextKey);
            }

            if (!Keys.Commitment(k.PublicSpan).AsSpan().SequenceEqual(r.NextCommitment ?? []))
            {
                throw new MsfpException(MsfpError.NotNextKey);
            }

            return k;
        }

        return r.Next is null
            ? throw new MsfpException(MsfpError.NoNextKey)
            : await OpenKeyAsync(r.Next, SealContext.Next, ct).ConfigureAwait(false);
    }

    // Makes next, the committed next key, current, keeps the current one as
    // the old key, and makes a new next key, all in one Update; every peer
    // that knew us by the current key is then on the old one. A change under
    // way ends in the same Update. Live codes are withdrawn: they name the
    // old key.
    private async Task RotateToAsync(MsfpKey next, bool revoked, CancellationToken ct)
    {
        var after = MsfpKey.FromSeed(RandomBytes(32));
        var now = NowSecond();
        var events = new List<MsfpEvent>();
        await ChangeServerAsync(async (tx, r) =>
        {
            events.Clear();
            if (!Keys.Commitment(next.PublicSpan).AsSpan().SequenceEqual(r.NextCommitment ?? []))
            {
                throw new MsfpException(MsfpError.NotNextKey); // another rotation took it meanwhile
            }

            if (r.Old is not null)
            {
                events.AddRange(await EndRotationAsync(tx, r).ConfigureAwait(false));
            }

            var seed = await OpenSeedAsync(r.Current, SealContext.Current, ct).ConfigureAwait(false);
            r.Old = await _sealer.SealAsync(seed, SealContext.Old, ct).ConfigureAwait(false);
            r.Current = await _sealer.SealAsync(next.Seed(), SealContext.Current, ct).ConfigureAwait(false);
            r.Next = await _sealer.SealAsync(after.Seed(), SealContext.Next, ct).ConfigureAwait(false);
            r.OldSince = now;
            r.OldRevoked = revoked;
            r.NextCommitment = Keys.Commitment(after.PublicSpan);
            foreach (var p in await tx.PeersAsync().ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(p.OurKey))
                {
                    continue;
                }

                p.OurKey = KeyOld;
                p.RotationAnswer = null;
                p.RotateSent = null;
                p.CommitmentTold = false;
                await tx.PutPeerAsync(p).ConfigureAwait(false);
            }

            foreach (var c in await tx.CodesAsync().ConfigureAwait(false))
            {
                await tx.DeleteCodeAsync(c.Id).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        _log.LogInformation("msfp: this server's key changed to {Fingerprint}", next.Fingerprint);
        TellEnded(events);
    }

    private async Task<byte[]> OpenSeedAsync(byte[] sealedSeed, string context, CancellationToken ct)
    {
        try
        {
            return await _sealer.OpenAsync(sealedSeed, context, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new MsfpException(MsfpError.Sealed, null, e);
        }
    }

    // Sends a rotate to every peer on our old key that can be called, all
    // at once, until each answered or failed.
    private Task TellRotationAsync(CancellationToken ct) =>
        EachPeerAsync(p => p.OurKey == KeyOld && p.Inbound == Inbound.Yes,
            async p => await RotateIfDueAsync(p.Id, TimeSpan.Zero, ct).ConfigureAwait(false), ct);

    // Runs fn for every peer keep says, all at once, and waits.
    private async Task EachPeerAsync(Func<PeerRecord, bool> keep, Func<PeerRecord, Task> fn, CancellationToken ct)
    {
        IReadOnlyList<PeerRecord> peers = [];
        await _store.ReadAsync(async tx => peers = await tx.PeersAsync().ConfigureAwait(false), ct).ConfigureAwait(false);
        await Task.WhenAll(peers.Where(keep).Select(fn)).ConfigureAwait(false);
    }

    // Sends peer id a rotate, newly dated, unless one went less than
    // spacing ago (zero: whatever the last), and gives the peer as it is
    // then (null when it is gone). Of contacts at once, one only sends it:
    // the time sent is claimed in an Update first.
    private async Task<PeerRecord?> RotateIfDueAsync(string id, TimeSpan spacing, CancellationToken ct)
    {
        var now = Now();
        PeerRecord? p = null;
        var claimed = false;
        try
        {
            await _store.UpdateAsync(async tx =>
            {
                claimed = false;
                var q = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
                p = q;
                if (q.OurKey != KeyOld || q.Suspended is not null ||
                    (spacing > TimeSpan.Zero && q.RotateSent is { } sent && now - sent < spacing))
                {
                    return;
                }

                q.RotateSent = now;
                claimed = true;
                await tx.PutPeerAsync(q).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            return null;
        }

        // One copy of the keys, read after the claim: the statement names our
        // old and current keys of the same change.
        var k = OurKeys();
        if (!claimed || k.Old is null)
        {
            return p;
        }

        var rot = Rotations.SignRotation(k.Old, k.Current, k.Commitment, now);
        try
        {
            var r = await ExchangeAsync(p!, CallRotate, rot.Body(), k.Old, ct).ConfigureAwait(false);
            string? state = null;
            if (r.Status is 200 or 202)
            {
                try
                {
                    state = StrictJson.ReadAnswer(r.Body).GetString("state");
                }
                catch (ProtocolException)
                {
                }
            }

            switch (r.Status)
            {
                case 200 when state == Rotations.StateRotated:
                    await PeerMovedAsync(id, k.Current.Public(), ct).ConfigureAwait(false);
                    break;
                case 202:
                    await RotationAnswerAsync(id, k.Current.Public(), Rotations.StatePending, ct).ConfigureAwait(false);
                    break;
                case 409:
                    await RotationAnswerAsync(id, k.Current.Public(), Hello.RotationRefused, ct).ConfigureAwait(false);
                    break;
                default:
                    _log.LogInformation("msfp: a peer answered this server's new key: {Peer} {Status} {Problem}", id, r.Status, r.Problem);
                    break;
            }
        }
        catch (MsfpException e)
        {
            _log.LogInformation(e, "msfp: a peer couldn't be told this server's new key: {Peer}", id);
        }

        try
        {
            return await PeerRecordAsync(id, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return null;
        }
    }

    // Keeps a peer's answer to our rotate (pending or refused), if it is
    // still on our old key and current still our key.
    private async Task RotationAnswerAsync(string id, byte[] current, string state, CancellationToken ct)
    {
        var events = new List<MsfpEvent>();
        await _store.UpdateAsync(async tx =>
        {
            events.Clear();
            if (!Keys.Same(Current().PublicSpan, current))
            {
                return;
            }

            var q = await tx.PeerAsync(id).ConfigureAwait(false);
            if (q is null || q.OurKey != KeyOld || q.RotationAnswer == state)
            {
                return;
            }

            q.RotationAnswer = state;
            events.Add(new RotationAnswered(q.Id, q.Name, state));
            await tx.PutPeerAsync(q).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        Notify([.. events]);
    }

    // Peer id knowing us by current now. Kept only if current is still our
    // current key during a change. The last peer to move ends the change, in
    // the same Update.
    private async Task<bool> PeerMovedAsync(string id, byte[] current, CancellationToken ct)
    {
        var events = new List<MsfpEvent>();
        var moved = false;
        await ChangeServerAsync(async (tx, r) =>
        {
            events.Clear();
            moved = false;
            if (r.Old is null || !Keys.Same(Current().PublicSpan, current))
            {
                return;
            }

            var q = await tx.PeerAsync(id).ConfigureAwait(false);
            if (q is null || q.OurKey != KeyOld)
            {
                return;
            }

            q.OurKey = KeyCurrent;
            q.RotationAnswer = null;
            q.CommitmentTold = true;
            await tx.PutPeerAsync(q).ConfigureAwait(false);
            moved = true;
            events.Add(new RotationAnswered(q.Id, q.Name, Rotations.StateRotated));
            if ((await tx.PeersAsync().ConfigureAwait(false)).All(p => p.OurKey != KeyOld))
            {
                events.AddRange(await EndRotationAsync(tx, r).ConfigureAwait(false));
            }
        }, ct).ConfigureAwait(false);
        if (moved)
        {
            _log.LogInformation("msfp: a peer moved to this server's new key: {Peer}", id);
        }

        TellEnded(events);
        return moved;
    }

    // Ends the change of key away from old: the old key deleted, the peers
    // that didn't move lost. Only that change.
    private async Task FinishRotationAsync(byte[] old, CancellationToken ct)
    {
        var events = new List<MsfpEvent>();
        await ChangeServerAsync(async (tx, r) =>
        {
            events.Clear();
            if (r.Old is null)
            {
                return;
            }

            var kept = MsfpKey.FromSeed(await OpenSeedAsync(r.Old, SealContext.Old, ct).ConfigureAwait(false));
            if (Keys.Same(kept.PublicSpan, old))
            {
                events.AddRange(await EndRotationAsync(tx, r).ConfigureAwait(false));
            }
        }, ct).ConfigureAwait(false);
        TellEnded(events);
    }

    // Deletes the old key in r, and marks lost, in tx, the peers still on
    // it: they must pair again (core section 12.4).
    private static async Task<List<MsfpEvent>> EndRotationAsync(IStoreTx tx, ServerRecord r)
    {
        r.Old = null;
        r.OldSince = null;
        r.OldRevoked = false;
        var events = new List<MsfpEvent>();
        foreach (var p in await tx.PeersAsync().ConfigureAwait(false))
        {
            if (p.OurKey != KeyOld)
            {
                continue;
            }

            p.OurKey = KeyLost;
            p.RotationAnswer = null;
            await tx.PutPeerAsync(p).ConfigureAwait(false);
            events.Add(new OurKeyLost(p.Id, p.Name));
        }

        events.Add(new RotationEnded(events.Count));
        return events;
    }

    private void TellEnded(List<MsfpEvent> events)
    {
        foreach (var end in events.OfType<RotationEnded>())
        {
            if (end.Lost > 0)
            {
                _log.LogWarning("msfp: this server's old key deleted; {Peers} peers that didn't move to the new one must pair again", end.Lost);
            }
            else
            {
                _log.LogInformation("msfp: every peer moved to this server's new key; the old one deleted");
            }
        }

        Notify([.. events]);
    }

    // A contact with p, a peer on our old key: a rotate sent first, unless
    // one went within the spacing (core section 12.4).
    private async Task<PeerRecord> RotateAtContactAsync(PeerRecord p, CancellationToken ct)
    {
        var spacing = p.RotationAnswer == Rotations.StatePending ? RotatePendingSpacing : RotateSpacing;
        if (p.RotateSent is { } sent && Now() - sent < spacing)
        {
            return p;
        }

        return await RotateIfDueAsync(p.Id, spacing, ct).ConfigureAwait(false) ?? p;
    }
}
