using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// Servers that can't be called (MSFP core section 7.5), this one and its peers.
//
// This server can't be called when its operator says so
// (SetUncallableAsync): at home behind a router, say, it can connect out,
// but nothing can connect to it. Its address is then
// https://msfp-<26 characters>.invalid, which never resolves, made once and
// kept; it makes no codes, but redeems them; it says so in every pair and
// hello; and it asks each peer, on a schedule, what it would otherwise be
// told (scheduled hellos): that its operator accepted, the peer's changes
// of key (pulled from its answers), its changes of address. Turned off, it
// moves back to its real address, as any move: announced once it reaches
// itself there.
//
// A peer that says it can't be called is never connected to (CallAsync
// refuses every call); its hellos are answered with our changes of key
// (Pullable), and the key it knows us by is read from them (KnownKey). A
// peer that says it can be called again is called only once a hello at its
// address answers under its key (ConfirmMoveAsync).

/// <summary>
/// What RunAsync does when, and what this server remembers of its calls to
/// each peer, in memory, by peer ID. Locked on itself.
/// </summary>
internal sealed class Schedule
{
    // When this server's new address is tried next (null: not due), after
    // ReachWait; Reaching while it is.
    public DateTimeOffset? ReachAt;
    public TimeSpan ReachWait;
    public bool Reaching;

    // The scheduled hellos' next times, while this server can't be called:
    // none yet is at once; null is at once too.
    public Dictionary<string, DateTimeOffset?> Turns = new(StringComparer.Ordinal);

    // When each peer was last sent a hello announcing our address; those
    // pointed out as late to move.
    public readonly Dictionary<string, DateTimeOffset> Announced = new(StringComparer.Ordinal);
    public readonly HashSet<string> Late = new(StringComparer.Ordinal);

    // A peer's new address being confirmed.
    public readonly HashSet<string> Confirming = new(StringComparer.Ordinal);

    // Our last call to the peer couldn't connect (callsFailed); calls in a
    // row answered, but not by it, at its stored base; when its discovery
    // document was fetched again for that.
    public readonly HashSet<string> NoConnection = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);
    public readonly Dictionary<string, DateTimeOffset> Refetched = new(StringComparer.Ordinal);

    // The peers in state theirs asked by PanelOpen, and when.
    public readonly Dictionary<string, DateTimeOffset> Asked = new(StringComparer.Ordinal);
}

public sealed partial class MsfpEngine
{
    private readonly Schedule _sched = new();
    private readonly List<Task> _background = [];

    // How many calls in a row, answered but not by the peer, make its
    // discovery document fetched again.
    internal const int FailingCalls = 3;

    // How often, at most, a peer in state theirs is sent a hello while the
    // panel is open (MSFP core section 7.1, step 7).
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(20);

    /// <summary>
    /// This server's operator saying it can't be called (on), or can again.
    /// On: the address becomes one of the .invalid form, made once and kept,
    /// with no former address (nothing calls either); live codes are
    /// withdrawn, and none is made (<see cref="MsfpError.NoCodes"/>); every
    /// peer is sent a hello at once, then on the schedule of MSFP
    /// core section 7.5 (RunAsync). Off: this server moves back to its address
    /// (<see cref="MsfpConfig.Address"/>, or the last
    /// <see cref="SetAddressAsync"/>), announced as an address change once it
    /// reaches itself there; until then it still says it can't be called.
    /// </summary>
    /// <exception cref="MsfpException">Off: <see cref="MsfpError.InvalidAddress"/> for a server opened without an address (<see cref="SetAddressAsync"/> first).</exception>
    public async Task SetUncallableAsync(bool on, CancellationToken ct = default)
    {
        var made = on ? Uncallables.NewAddress(_random) : "";
        string configured;
        lock (_keysLock)
        {
            configured = _configured;
        }

        if (!on && configured.Length == 0)
        {
            throw new MsfpException(MsfpError.InvalidAddress, "this server has no address to be called at: SetAddressAsync first");
        }

        await MoveToAsync(r =>
        {
            if (r.Uncallable == on)
            {
                return r.Address;
            }

            r.Uncallable = on;
            if (!on)
            {
                return configured;
            }

            r.UncallableAddress ??= made;
            return r.UncallableAddress;
        }, ct).ConfigureAwait(false);
        if (on)
        {
            await _store.UpdateAsync(tx => new ValueTask(WithdrawCodesAsync(tx)), ct).ConfigureAwait(false);
        }
    }

    // Sends, while this server says it can't be called, every peer whose
    // turn it is a hello (suspended ones included, so that a rotation can
    // still reach them), and waits for them; each one's next turn is set by
    // where its pairing stands then (Uncallables.HelloWait), give or take a
    // fifth. They count against the peer's limits as any call does, and a
    // peer that asked to wait, or doesn't answer, is asked at its next turn.
    private async Task ScheduledHellosAsync(CancellationToken ct)
    {
        if (!SaysUncallable())
        {
            return;
        }

        IReadOnlyList<PeerRecord> peers = [];
        await _store.ReadAsync(async tx => peers = await tx.PeersAsync().ConfigureAwait(false), ct).ConfigureAwait(false);
        var now = Now();
        await Task.WhenAll(peers.Where(p => HelloDue(p.Id, now)).Select(async p =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2 * CallTimeout);
            try
            {
                await CheckAsync(p.Id, background: true, cts.Token).ConfigureAwait(false);
            }
            catch (MsfpException e) when (e.Code != MsfpError.Unknown)
            {
                _log.LogDebug(e, "msfp: a scheduled hello failed: {Peer}", p.Id);
            }
            catch (MsfpException)
            {
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await HelloDoneAsync(p.Id, ct).ConfigureAwait(false);
        })).ConfigureAwait(false);
    }

    // Whether peer id's scheduled hello goes now, and if so, notes it under
    // way: its next turn is set once it's answered.
    private bool HelloDue(string id, DateTimeOffset now)
    {
        lock (_sched)
        {
            if (_sched.Turns.TryGetValue(id, out var next) && next is { } n && now < n)
            {
                return false;
            }

            _sched.Turns[id] = now + TimeSpan.FromHours(1); // under way
            return true;
        }
    }

    // Sets peer id's next turn, as its pairing stands after its hello;
    // unless HelloNow asked for one at once meanwhile.
    private async Task HelloDoneAsync(string id, CancellationToken ct)
    {
        PeerRecord p;
        try
        {
            p = await PeerRecordAsync(id, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return;
        }

        var now = Now();
        var wait = Uncallables.Vary(Uncallables.HelloWait(p.State == PeerState.Theirs, p.State == PeerState.Gone, now - p.Created), RandomFraction());
        lock (_sched)
        {
            if (_sched.Turns.TryGetValue(id, out var next) && next is null)
            {
                return;
            }

            _sched.Turns[id] = now + wait;
        }
    }

    // Has peer id's next scheduled hello go at the next run.
    private void HelloNow(string id)
    {
        lock (_sched)
        {
            _sched.Turns[id] = null;
        }
    }

    // Has every scheduled hello go at the next run: this server now names
    // another address, or can't be called since now.
    private void ResetHellos()
    {
        lock (_sched)
        {
            _sched.Turns = new(StringComparer.Ordinal);
        }
    }

    // A number in [0, 1), from the configured randomness.
    private double RandomFraction() => (BinaryPrimitives.ReadUInt64BigEndian(RandomBytes(8)) >> 11) / (double)(1UL << 53);

    // What this server remembers of its calls to each peer.

    // Notes how a call to p went: whether it connected, for p's hello
    // answers (callsFailed: MSFP core section 7.5); and whether it was answered by
    // p at its stored base: after FailingCalls answers in a row that weren't
    // p's, its discovery document is fetched again, at most hourly (MSFP
    // core section 11).
    private void CalledPeer(PeerRecord p, MsfpException? error)
    {
        if (p.Id.Length == 0)
        {
            return;
        }

        var noConnection = error is { Code: MsfpError.Unreachable, NoConnection: true };
        var notPeers = error is { Code: MsfpError.Unreachable, NoConnection: false };
        var now = Now();
        var refetch = false;
        lock (_sched)
        {
            if (noConnection)
            {
                _sched.NoConnection.Add(p.Id);
            }
            else if (error is null)
            {
                _sched.NoConnection.Remove(p.Id);
            }

            if (notPeers)
            {
                var n = _sched.Failures[p.Id] = _sched.Failures.GetValueOrDefault(p.Id) + 1;
                if (n >= FailingCalls && (!_sched.Refetched.TryGetValue(p.Id, out var last) || now - last >= TimeSpan.FromHours(1)))
                {
                    _sched.Refetched[p.Id] = now;
                    _sched.Failures[p.Id] = 0;
                    refetch = true;
                }
            }
            else if (error is null)
            {
                _sched.Failures.Remove(p.Id);
            }
        }

        if (refetch)
        {
            var (id, address) = (p.Id, p.Address);
            Background(ct => KeepBaseAsync(id, address, ct));
        }
    }

    // Whether this server's last call to peer id couldn't connect.
    private bool CallsFailedTo(string id)
    {
        lock (_sched)
        {
            return _sched.NoConnection.Contains(id);
        }
    }

    // Forgets how calls to peer id went: it can't be called now.
    private void ForgetCalls(string id)
    {
        lock (_sched)
        {
            _sched.NoConnection.Remove(id);
            _sched.Failures.Remove(id);
        }
    }

    /// <summary>
    /// Tells the engine the operator's panel is listing peers now: each peer
    /// in state theirs gets a hello, at most one every 20 seconds, so the
    /// panel shows a pairing once the other operator accepted. It returns at
    /// once; the hellos go in the background.
    /// </summary>
    public void PanelOpen() => Background(async ct =>
    {
        if (SaysUncallable())
        {
            return; // it asks on its own schedule (the scheduled hellos)
        }

        IReadOnlyList<PeerRecord> peers = [];
        await _store.ReadAsync(async tx => peers = await tx.PeersAsync().ConfigureAwait(false), ct).ConfigureAwait(false);
        var now = Now();
        var due = new List<string>();
        lock (_sched)
        {
            foreach (var p in peers)
            {
                if (p.State != PeerState.Theirs || p.Inbound != Inbound.Yes ||
                    (_sched.Asked.TryGetValue(p.Id, out var last) && now - last < CheckEvery))
                {
                    continue;
                }

                _sched.Asked[p.Id] = now;
                due.Add(p.Id);
            }
        }

        foreach (var id in due)
        {
            Background(c => QuietCheckAsync(id, c));
        }
    });

    // Runs fn in the background, with a time bound, outside any request:
    // what a call or a hello started. Its failures are logged.
    internal void Background(Func<CancellationToken, Task> fn)
    {
        var t = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(4 * CallTimeout);
            try
            {
                await fn(cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "msfp: background work failed");
            }
        }, CancellationToken.None);
        lock (_background)
        {
            _background.RemoveAll(x => x.IsCompleted);
            _background.Add(t);
        }
    }

    // Waits for the work the engine started in the background, and the work
    // that work started: for tests.
    internal async Task WaitForTestAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_background)
            {
                _background.RemoveAll(x => x.IsCompleted);
                pending = [.. _background];
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }
}
