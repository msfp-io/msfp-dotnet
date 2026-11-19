using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>This server's keys, opened.</summary>
internal sealed record ServerKeys(MsfpKey Current, byte[] Commitment, byte[] Ids, MsfpKey? Old, DateTimeOffset? OldSince,
    bool OldRevoked, MsfpKey? Revoked, DateTimeOffset? RevokedAt);

/// <summary>Where this server is: its address, the former one during a move, whether it can be called.</summary>
internal sealed record AddressState(string Address, string? Former, bool Reached, bool Uncallable, string? UncallableAddress);

public sealed partial class MsfpEngine
{
    /// <summary>What a peer knows this server by: its current key.</summary>
    public const string KeyCurrent = "";

    /// <summary>What a peer knows this server by: its old key (it hasn't moved to the new one yet).</summary>
    public const string KeyOld = "old";

    /// <summary>It never moved to the new key: pair again.</summary>
    public const string KeyLost = "lost";

    internal ServerKeys OurKeys()
    {
        lock (_keysLock)
        {
            return _keys;
        }
    }

    internal MsfpKey Current() => OurKeys().Current;

    // The key to sign for p with: our old key while it knows us by it.
    internal MsfpKey SignerFor(PeerRecord? p)
    {
        var k = OurKeys();
        return p is not null && p.OurKey == KeyOld && k.Old is not null ? k.Old : k.Current;
    }

    internal AddressState OurAddress()
    {
        lock (_keysLock)
        {
            return _addr;
        }
    }

    // The address this server's hellos and codes name.
    internal string Named()
    {
        var a = OurAddress();
        if (a.Uncallable && a.UncallableAddress is not null)
        {
            return a.UncallableAddress;
        }

        return a.Former is not null && !a.Reached ? a.Former : a.Address;
    }

    internal string OurHost() => Address.Host(OurAddress().Address);

    private async Task LoadKeysAsync(CancellationToken ct)
    {
        await ChangeServerAsync(async (_, r) =>
        {
            if (_cfg.Address.Length == 0 && !r.Uncallable)
            {
                throw new MsfpException(MsfpError.InvalidAddress,
                    "no address, and this server's record doesn't say it can't be called");
            }

            if (r.Current.Length == 0)
            {
                var made = await NewServerAsync(ct).ConfigureAwait(false);
                r.Current = made.Current;
                r.Next = made.Next;
                r.NextCommitment = made.NextCommitment;
                r.IdsSecret = made.IdsSecret;
                r.Address = made.Address;
            }

            if (!r.Uncallable && r.Address != _cfg.Address)
            {
                // A changed address: the move of core section 13 is a later
                // phase of this port; the new one is taken at once.
                _log.LogInformation("msfp: this server's address changed from {From} to {To}", r.Address, _cfg.Address);
                r.Address = _cfg.Address;
            }
        }, ct).ConfigureAwait(false);
    }

    // Changes the server record in a transaction, then opens its keys.
    internal async Task ChangeServerAsync(Func<IStoreTx, ServerRecord, ValueTask> fn, CancellationToken ct)
    {
        await _recordLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ServerRecord? rec = null;
            await _store.UpdateAsync(async tx =>
            {
                var r = await tx.ServerAsync().ConfigureAwait(false) ?? new ServerRecord();
                await fn(tx, r).ConfigureAwait(false);
                rec = r;
                await tx.PutServerAsync(r).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            await OpenKeysAsync(rec!, ct).ConfigureAwait(false);
        }
        finally
        {
            _recordLock.Release();
        }
    }

    private async Task<ServerRecord> NewServerAsync(CancellationToken ct)
    {
        var current = RandomBytes(32);
        var next = RandomBytes(32);
        var ids = RandomBytes(32);
        var nextKey = MsfpKey.FromSeed(next);
        return new ServerRecord
        {
            NextCommitment = Keys.Commitment(nextKey.PublicSpan),
            Address = _cfg.Address,
            Current = await _sealer.SealAsync(current, SealContext.Current, ct).ConfigureAwait(false),
            Next = await _sealer.SealAsync(next, SealContext.Next, ct).ConfigureAwait(false),
            IdsSecret = await _sealer.SealAsync(ids, SealContext.Ids, ct).ConfigureAwait(false),
        };
    }

    private async Task OpenKeysAsync(ServerRecord r, CancellationToken ct)
    {
        var current = await OpenKeyAsync(r.Current, SealContext.Current, ct).ConfigureAwait(false);
        byte[] ids;
        try
        {
            ids = await _sealer.OpenAsync(r.IdsSecret, SealContext.Ids, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new MsfpException(MsfpError.Sealed, null, e);
        }

        var old = r.Old is null ? null : await OpenKeyAsync(r.Old, SealContext.Old, ct).ConfigureAwait(false);
        var revoked = r.Revoked is null ? null : await OpenKeyAsync(r.Revoked, SealContext.Revoked, ct).ConfigureAwait(false);
        var k = new ServerKeys(current, (byte[])(r.NextCommitment ?? []).Clone(), ids, old, r.OldSince, r.OldRevoked, revoked, r.RevokedAt);
        var a = new AddressState(r.Address, r.Former, r.Reached, r.Uncallable, r.UncallableAddress);
        lock (_keysLock)
        {
            _keys = k;
            _addr = a;
        }

        NewVerifier();
    }

    private async Task<MsfpKey> OpenKeyAsync(byte[] sealedSeed, string context, CancellationToken ct)
    {
        try
        {
            return MsfpKey.FromSeed(await _sealer.OpenAsync(sealedSeed, context, ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new MsfpException(MsfpError.Sealed, null, e);
        }
    }

    /// <summary>
    /// The committed next key's fingerprint (made from its commitment, core
    /// section 4.2), and whether its seed is still on this server.
    /// </summary>
    public async Task<(string Fingerprint, bool OnServer)> NextKeyAsync(CancellationToken ct = default)
    {
        var r = await ServerRecordAsync(ct).ConfigureAwait(false);
        return (Keys.CommitmentFingerprint(r.NextCommitment ?? []), r.Next is not null);
    }

    /// <summary>
    /// The next key's seed as text to keep offline ("msfp-next-key-1." then
    /// the seed, base64url), and its fingerprint for the operator to compare.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NoNextKey"/>.</exception>
    public async Task<(string Text, string Fingerprint)> ExportNextKeyAsync(CancellationToken ct = default)
    {
        var r = await ServerRecordAsync(ct).ConfigureAwait(false);
        if (r.Next is null)
        {
            throw new MsfpException(MsfpError.NoNextKey);
        }

        var key = await OpenKeyAsync(r.Next, SealContext.Next, ct).ConfigureAwait(false);
        return (Rotations.ExportNextKey(key), Keys.CommitmentFingerprint(r.NextCommitment ?? []));
    }

    /// <summary>
    /// Removes the next key's seed from this server, once the operator
    /// exported it; its commitment stays. Someone who copies the server's
    /// storage then can't change its key.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NoNextKey"/>.</exception>
    public Task DeleteNextKeyAsync(CancellationToken ct = default) =>
        ChangeServerAsync((_, r) =>
        {
            if (r.Next is null)
            {
                throw new MsfpException(MsfpError.NoNextKey);
            }

            r.Next = null;
            return ValueTask.CompletedTask;
        }, ct);
}
