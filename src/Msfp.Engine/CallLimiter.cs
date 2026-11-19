using System.Net;

namespace Msfp;

/// <summary>
/// The limits on calls here (core section 8.3): token buckets per network
/// address (before any signature), per peer and in all, play's own per
/// peer, and requests at once per peer and in all, with queues.
/// </summary>
internal sealed class CallLimiter(CallLimits limits)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Bucket> _addresses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bucket> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bucket> _plays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Gate> _peerGates = new(StringComparer.Ordinal);
    private readonly Bucket _all = new(limits.AllBurst, DateTimeOffset.MinValue);
    private readonly Gate _allGate = new(limits.AllAtOnce);
    private DateTimeOffset _swept;

    private sealed class Bucket(double tokens, DateTimeOffset at)
    {
        public double Tokens = tokens;
        public DateTimeOffset At = at;

        // Takes one token, refilled at rate up to burst: or how many
        // seconds until one is there.
        public (bool Ok, int RetryAfter) Take(DateTimeOffset now, double rate, double burst)
        {
            // A clock set back refills nothing, and takes nothing back.
            if (At != DateTimeOffset.MinValue && now > At)
            {
                Tokens = Math.Min(burst, Tokens + ((now - At).TotalSeconds * rate));
            }

            if (now > At)
            {
                At = now;
            }

            if (Tokens >= 1)
            {
                Tokens--;
                return (true, 0);
            }

            return (false, Math.Max(1, (int)Math.Ceiling((1 - Tokens) / rate)));
        }
    }

    private sealed class Gate(int size)
    {
        public readonly SemaphoreSlim Slots = new(size, size);
        public int Queued;
        public readonly int Size = size;
    }

    // An IPv6 client is limited by its /64, as one host usually has one.
    private static string AddressKey(IPAddress? a)
    {
        if (a is null)
        {
            return "-";
        }

        if (a.IsIPv4MappedToIPv6)
        {
            a = a.MapToIPv4();
        }

        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = a.GetAddressBytes();
            Array.Clear(b, 8, 8);
            return new IPAddress(b).ToString();
        }

        return a.ToString();
    }

    /// <summary>Whether a request from this address may be read at all.</summary>
    public bool FromAddress(IPAddress? address, DateTimeOffset now)
    {
        lock (_lock)
        {
            Sweep(now);
            var k = AddressKey(address);
            if (!_addresses.TryGetValue(k, out var b))
            {
                b = new Bucket(limits.AddressBurst, now);
                _addresses[k] = b;
            }

            return b.Take(now, limits.AddressRate, limits.AddressBurst).Ok;
        }
    }

    /// <summary>A known peer's call, by its key: null when allowed, or the refusal and its retryAfter.</summary>
    public (string Problem, int RetryAfter)? Call(string key, bool play, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_peers.TryGetValue(key, out var b))
            {
                b = new Bucket(limits.PeerBurst, now);
                _peers[key] = b;
            }

            var (ok, after) = b.Take(now, limits.PeerRate, limits.PeerBurst);
            if (!ok)
            {
                return (ProblemType.RateLimited, after);
            }

            if (play)
            {
                if (!_plays.TryGetValue(key, out var p))
                {
                    p = new Bucket(limits.PlayBurst, now);
                    _plays[key] = p;
                }

                (ok, after) = p.Take(now, limits.PlayRate, limits.PlayBurst);
                if (!ok)
                {
                    return (ProblemType.RateLimited, after);
                }
            }

            (ok, after) = _all.Take(now, limits.AllRate, limits.AllBurst);
            return ok ? null : (ProblemType.ServerBusy, after);
        }
    }

    /// <summary>
    /// A slot for a request at once, per peer (key "" for strangers) and in
    /// all, waiting at most QueueWait in a queue twice the slots: what frees
    /// it, or null when there's none.
    /// </summary>
    public async Task<Action?> SlotsAsync(string key, CancellationToken ct)
    {
        Gate peer;
        lock (_lock)
        {
            if (!_peerGates.TryGetValue(key, out peer!))
            {
                peer = new Gate(limits.PeerAtOnce);
                _peerGates[key] = peer;
            }
        }

        if (!await Enter(peer, ct).ConfigureAwait(false))
        {
            return null;
        }

        if (!await Enter(_allGate, ct).ConfigureAwait(false))
        {
            peer.Slots.Release();
            return null;
        }

        return () =>
        {
            _allGate.Slots.Release();
            peer.Slots.Release();
        };
    }

    private async Task<bool> Enter(Gate g, CancellationToken ct)
    {
        if (g.Slots.Wait(0, CancellationToken.None))
        {
            return true;
        }

        if (Interlocked.Increment(ref g.Queued) > 2 * g.Size)
        {
            Interlocked.Decrement(ref g.Queued);
            return false;
        }

        try
        {
            return await g.Slots.WaitAsync(limits.QueueWait, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref g.Queued);
        }
    }

    // Forgets buckets full again, once a minute: those that wouldn't limit anyone.
    private void Sweep(DateTimeOffset now)
    {
        if (now - _swept < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _swept = now;
        Forget(_addresses, now, limits.AddressRate, limits.AddressBurst);
        Forget(_peers, now, limits.PeerRate, limits.PeerBurst);
        Forget(_plays, now, limits.PlayRate, limits.PlayBurst);
    }

    private static void Forget(Dictionary<string, Bucket> m, DateTimeOffset now, double rate, double burst)
    {
        foreach (var (k, b) in m.ToList())
        {
            if (b.Tokens + ((now - b.At).TotalSeconds * rate) >= burst)
            {
                m.Remove(k);
            }
        }
    }

    // The other side: this server's own calls to a peer. Until when it asked
    // them to wait, and, after calls it didn't answer, until when calls made
    // in the background hold off.
    private sealed class Wait
    {
        public DateTimeOffset RetryAt;
        public DateTimeOffset BackoffAt;
        public int Failures;
    }

    private readonly Dictionary<string, Wait> _waits = new(StringComparer.Ordinal);

    // How long this server's background calls hold off after the first call
    // a peer didn't answer, doubling each time, up to an hour, give or take
    // a quarter.
    internal static readonly TimeSpan BackoffFirst = TimeSpan.FromSeconds(2);

    // Whether a call to the peer with key may go now: an operator's call
    // goes unless the peer asked to wait (a signed retryAfter); a background
    // one also holds off after calls the peer didn't answer.
    public bool MayCall(string key, bool background, DateTimeOffset now)
    {
        lock (_lock)
        {
            return !_waits.TryGetValue(key, out var w) || (now >= w.RetryAt && (!background || now >= w.BackoffAt));
        }
    }

    // Keeps what a call to the peer with key gave: a signed answer (status,
    // and its retryAfter for a 429 or 503), or none.
    public void Heard(string key, bool answered, int status, int retryAfter, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_waits.TryGetValue(key, out var w))
            {
                w = new Wait();
                _waits[key] = w;
            }

            var busy = status is 429 or 503;
            if (answered && busy && retryAfter > 0)
            {
                w.RetryAt = now + TimeSpan.FromSeconds(retryAfter);
            }
            else if (!answered || busy)
            {
                w.Failures++;
                w.BackoffAt = now + Backoff(w.Failures);
            }
            else
            {
                w.Failures = 0;
                w.BackoffAt = default;
            }
        }
    }

    // How long background calls hold off after n calls in a row went unanswered.
    internal static TimeSpan Backoff(int n)
    {
        var most = TimeSpan.FromSeconds(Protocol.RetryAfter.Max);
        var d = BackoffFirst * Math.Pow(2, Math.Min(n - 1, 20));
        d = d > most ? most : d;
        d *= 0.75 + (Random.Shared.NextDouble() / 2);
        return d > most ? most : d;
    }
}
