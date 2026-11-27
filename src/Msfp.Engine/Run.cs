using Microsoft.Extensions.Logging;

namespace Msfp;

/// <summary>Background work (engine.md section 5).</summary>
public sealed partial class MsfpEngine
{
    // How often RunAsync does the hourly work; and its ticks.
    internal static readonly TimeSpan TidyEvery = TimeSpan.FromHours(1);
    internal static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    // How long a forgotten key is kept (MSFP core section 7.4: entries may go after a year).
    internal static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(365);

    // How often a peer on our old key is told the new one again.
    internal static readonly TimeSpan RotateDaily = TimeSpan.FromHours(24);

    /// <summary>
    /// Does the engine's background work until the token is cancelled:
    /// hourly, by the configured clock, expired codes and old forgotten
    /// keys tidied, and keys' time (the end of this server's change of key
    /// after 30 days, the rotate sent again daily, peers suspended 30 days
    /// gone). Without it the engine still answers peers and the operator,
    /// but nothing happens with time. Run it in a <c>BackgroundService</c>,
    /// or call <see cref="RunOnceAsync"/> on a schedule of the host's: never both.
    /// </summary>
    /// <remarks>
    /// Every second: this server's new address tried until it answers (then
    /// announced), and while it can't be called, its scheduled hellos. Every
    /// hour too: the end of this server's move, peers late to move told
    /// again, peers' new addresses tried again; the playbacks left on peers
    /// before a restart stopped (once); the engine's own change log of the
    /// host's catalog refreshed. Every second too: peers' catalogs read when
    /// due (a minute after the start, then every SyncEvery, at once when what
    /// is wanted changes), peers' playbacks here expired (looked at every 30
    /// seconds), and peers told where this server's viewers are in their
    /// playbacks there, when 45 seconds have passed (Progress).
    /// </remarks>
    public async Task RunAsync(CancellationToken ct)
    {
        CancellationTokenSource linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        using var _ = linked;
        ct = linked.Token;
        using var timer = new PeriodicTimer(TickEvery);
        DateTimeOffset? hourly = null;
        do
        {
            var now = Now();
            try
            {
                // A clock set back runs the hourly work again rather than
                // waiting for the hour it left.
                if (hourly is null || now - hourly.Value >= TidyEvery || now < hourly.Value)
                {
                    hourly = now;
                    await RunOnceAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (_closed.IsCancellationRequested)
                {
                    return;
                }

                _log.LogWarning(e, "msfp: background work failed");
            }
        }
        while (await NextTickAsync(timer, ct).ConfigureAwait(false));
    }

    // The next tick; false once the engine is disposed. The token the host
    // gave still throws when cancelled, as before.
    private async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Does, once, all the background work due now, and waits for it: for
    /// tests with a fake clock, and for hosts that schedule work themselves
    /// (every second or so for a server that can't be called, whose hellos
    /// are on a schedule of seconds; every few minutes otherwise).
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var now = Now();
        await _store.UpdateAsync(async tx =>
        {
            foreach (var c in await tx.CodesAsync().ConfigureAwait(false))
            {
                if (c.Expires <= now)
                {
                    await tx.DeleteCodeAsync(c.Id).ConfigureAwait(false);
                }
            }

            await tx.TrimForgottenAsync(now - ForgetAfter).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await MaintainKeysAsync(ct).ConfigureAwait(false);
        await MaintainAddressesAsync(ct).ConfigureAwait(false);
        await StopLeftoversAsync(ct).ConfigureAwait(false);
        try
        {
            await TidyGroupsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: groups couldn't be tidied");
        }

        if (_logKept)
        {
            try
            {
                await RefreshOwnAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "msfp: the catalog's change log couldn't be refreshed");
            }
        }

        await ReadDueAsync(true, ct).ConfigureAwait(false);
        await ExpirePlaybacksAsync(true, ct).ConfigureAwait(false);
        await TickAsync(ct).ConfigureAwait(false);
    }

    // The work of each second: this server's new address tried, its
    // scheduled hellos sent, catalogs due read, playbacks expired, progress
    // due sent.
    private async Task TickAsync(CancellationToken ct)
    {
        await ReachAndAnnounceAsync(ct).ConfigureAwait(false);
        await ScheduledHellosAsync(ct).ConfigureAwait(false);
        await ReadDueAsync(false, ct).ConfigureAwait(false);
        await ExpirePlaybacksAsync(false, ct).ConfigureAwait(false);
        TellWatched();
        // The groups looked at every 30 seconds for a sync (MSFP groups
        // section 6); a clock set back looks at once.
        var now = Now();
        if (_groupsTick is not { } tick || now - tick >= GroupsTickEvery || now < tick)
        {
            _groupsTick = now;
            Background(SyncGroupsAsync);
        }
    }

    private static readonly TimeSpan GroupsTickEvery = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _groupsTick;

    // What time does to keys (MSFP core sections 12.4, 12.6 and 12.7): our old key
    // deleted 30 days after the change began (that change only), the key
    // revoked with no next key 30 days after; a peer
    // suspended 30 days without a valid rotation gone; a peer on our old
    // key told again daily.
    private async Task MaintainKeysAsync(CancellationToken ct)
    {
        var now = Now();
        var k = OurKeys();
        if (k.Old is not null && k.OldSince is { } since && now - since >= KeyOverlap)
        {
            await FinishRotationAsync(k.Old.Public(), ct).ConfigureAwait(false);
        }

        // The key revoked with no next key: its 30 days of answers over.
        if (k.Revoked is not null && k.RevokedAt is { } revokedAt && now - revokedAt >= KeyOverlap)
        {
            await ChangeServerAsync((_, r) =>
            {
                if (r.Revoked is not null && r.RevokedAt == revokedAt)
                {
                    r.Revoked = null;
                    r.RevokedAt = null;
                }

                return ValueTask.CompletedTask;
            }, ct).ConfigureAwait(false);
        }

        var events = new List<MsfpEvent>();
        await _store.UpdateAsync(async tx =>
        {
            events.Clear();
            foreach (var p in await tx.PeersAsync().ConfigureAwait(false))
            {
                if (p.Suspended is not { } s || p.State == PeerState.Gone || now - s < KeyOverlap)
                {
                    continue;
                }

                // Gone, and still suspended: a paired hello doesn't bring it
                // back (MSFP core section 12.6).
                p.State = PeerState.Gone;
                await tx.PutPeerAsync(p).ConfigureAwait(false);
                _log.LogWarning("msfp: a peer whose key was revoked 30 days ago, with no new key since, is gone: {Peer}", p.Id);
                events.Add(new PeerGone(p.Id, p.Name));
            }
        }, ct).ConfigureAwait(false);
        Notify([.. events]);
        await EachPeerAsync(p => p.OurKey == KeyOld && p.Inbound == Inbound.Yes && p.Suspended is null,
            p => RotateIfDueAsync(p.Id, RotateDaily, ct, background: true), ct).ConfigureAwait(false);
    }
}
