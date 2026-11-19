using Microsoft.Extensions.Logging;

namespace Msfp;

/// <summary>Background work (engine.md section 5).</summary>
public sealed partial class MsfpEngine
{
    // How often RunAsync does the hourly work; and its ticks.
    internal static readonly TimeSpan TidyEvery = TimeSpan.FromHours(1);
    internal static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    // How long a forgotten key is kept (core section 7.4: entries may go after a year).
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
    /// <remarks>This preview has no per-second work yet (address moves, scheduled hellos, catalogs, playbacks).</remarks>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickEvery);
        DateTimeOffset? hourly = null;
        do
        {
            var now = Now();
            // A clock set back runs the hourly work again rather than
            // waiting for the hour it left.
            if (hourly is null || now - hourly.Value >= TidyEvery || now < hourly.Value)
            {
                hourly = now;
                try
                {
                    await RunOnceAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.LogWarning(e, "msfp: background work failed");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Does, once, all the background work due now, and waits for it: for
    /// tests with a fake clock, and for hosts that schedule work themselves
    /// (every few minutes).
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
    }

    // What time does to keys (core sections 12.4, 12.6, 12.7): our old key
    // deleted 30 days after the change began (that change only); a peer
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
                // back (core section 12.6).
                p.State = PeerState.Gone;
                await tx.PutPeerAsync(p).ConfigureAwait(false);
                _log.LogWarning("msfp: a peer whose key was revoked 30 days ago, with no new key since, is gone: {Peer}", p.Id);
                events.Add(new PeerGone(p.Id, p.Name));
            }
        }, ct).ConfigureAwait(false);
        Notify([.. events]);
        await EachPeerAsync(p => p.OurKey == KeyOld && p.Inbound == Inbound.Yes && p.Suspended is null,
            p => RotateIfDueAsync(p.Id, RotateDaily, ct), ct).ConfigureAwait(false);
    }
}
