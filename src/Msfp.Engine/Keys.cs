using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>This server's keys, opened.</summary>
internal sealed record ServerKeys(MsfpKey Current, byte[] Commitment, byte[] Ids, MsfpKey? Old, DateTimeOffset? OldSince,
    bool OldRevoked, MsfpKey? Revoked, DateTimeOffset? RevokedAt);

/// <summary>
/// Where this server is, as the server record holds it: its address, the
/// former one during a move (null for none) and since when, whether it
/// reached itself at the new one, whether it can be called.
/// </summary>
internal sealed record AddressState(string Address, string? Former, DateTimeOffset? MovedAt, bool Reached, bool Uncallable, string? UncallableAddress);

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

    // The address this server names, in its hellos, codes and pair
    // requests: its former one while its new one isn't reached yet (core
    // section 13.1, step 2).
    internal string Named() => Named(OurAddress());

    private static string Named(AddressState a) => !string.IsNullOrEmpty(a.Former) && !a.Reached ? a.Former : a.Address;

    internal string OurHost() => Address.Host(OurAddress().Address);

    // Reads this server's keys, making them on the very first start, and
    // notes the configured address: a different one than noted last starts
    // a move (core section 13.1), announced once this server reaches itself
    // there (RunAsync checks it at once). While this server can't be
    // called, it stays at its address of the .invalid form.
    private async Task LoadKeysAsync(CancellationToken ct)
    {
        var moved = false;
        await ChangeServerAsync(async (tx, r) =>
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
                var from = r.Address;
                await StartMoveAsync(tx, r, _cfg.Address).ConfigureAwait(false);
                moved = true;
                _log.LogInformation("msfp: this server's address changed; peers are told once it answers there: {From} {To}", from, r.Address);
            }
        }, ct).ConfigureAwait(false);
        // A move not announced yet, begun now or before a restart: this
        // server tries its new address at once.
        var a = OurAddress();
        if (moved || (a.Former is not null && !a.Reached))
        {
            ReachNow();
        }
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
        var a = new AddressState(r.Address, string.IsNullOrEmpty(r.Former) ? null : r.Former, r.MovedAt, r.Reached, r.Uncallable, r.UncallableAddress);
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
