namespace Msfp.Protocol;

/// <summary>
/// The nonces of the requests accepted within the last 2 × MaxSkew, each
/// with the key that signed it: any older request is refused by its date
/// anyway (MSFP core section 6.4). Known servers' nonces are kept per key, so
/// one server, however fast it asks, never crowds out another; strangers'
/// (servers pairing, heard while a code is live) in a small memory of
/// their own.
/// </summary>
public sealed class NonceMemory
{
    /// <summary>
    /// The nonces kept for one known server's key: past it (after clearing
    /// the old ones), its requests are refused until room comes back. Its
    /// limits let a server through at most 12,200 requests in the time a
    /// nonce is kept.
    /// </summary>
    public const int MaxPerKey = 20_000;

    /// <summary>The nonces kept of strangers' requests.</summary>
    public const int MaxStrangers = 1_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _strangers = new(StringComparer.Ordinal);
    private DateTimeOffset _last;

    /// <summary>A memory with no start: requests are refused by their date and nonce only.</summary>
    public NonceMemory()
    {
    }

    /// <summary>
    /// A memory that begins at <paramref name="since"/>: give it the time
    /// the process started, before any request is heard. A request signed
    /// before it is refused, since its nonce may have been seen before a
    /// restart (decision 13: nonces in memory only).
    /// </summary>
    public NonceMemory(DateTimeOffset since) => Since = since;

    /// <summary>When the memory began; null for no such check.</summary>
    public DateTimeOffset? Since { get; }

    /// <summary>
    /// Whether a request signed at <paramref name="when"/> (Unix seconds)
    /// may predate the memory: a date is a whole second, so one in the
    /// second the memory began counts as before it, unless that second
    /// began exactly then.
    /// </summary>
    public bool Before(long when) => Since is { } s && UnixTime.Before(when, s);

    /// <summary>
    /// Remembers a request's nonce, signed by key: false when it was seen
    /// already, or there's no room for it.
    /// </summary>
    public bool Add(ReadOnlySpan<byte> key, string nonce, DateTimeOffset now, bool known)
    {
        var k = Convert.ToBase64String(key);
        lock (_lock)
        {
            var full = _strangers.Count >= MaxStrangers;
            if (known)
            {
                full = _seen.TryGetValue(k, out var mine) && mine.Count >= MaxPerKey;
            }

            // Full, the old ones are cleared once a second at most.
            if (now - _last > TimeSpan.FromMinutes(1) || (full && now - _last >= TimeSpan.FromSeconds(1)))
            {
                Forget(now);
            }

            var dup = _seen.TryGetValue(k, out var m) && m.ContainsKey(nonce);
            if (dup || _strangers.ContainsKey(k + nonce))
            {
                return false;
            }

            if (!known)
            {
                if (_strangers.Count >= MaxStrangers)
                {
                    return false;
                }

                _strangers[k + nonce] = now;
                return true;
            }

            if (m is null)
            {
                m = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                _seen[k] = m;
            }

            if (m.Count >= MaxPerKey)
            {
                return false;
            }

            m[nonce] = now;
            return true;
        }
    }

    // Clears the nonces old enough to be refused by their date; the lock is held.
    private void Forget(DateTimeOffset now)
    {
        var keep = 2 * Signing.MaxSkew;
        foreach (var (key, m) in _seen.ToList())
        {
            foreach (var (n, t) in m.ToList())
            {
                if (now - t > keep)
                {
                    m.Remove(n);
                }
            }

            if (m.Count == 0)
            {
                _seen.Remove(key);
            }
        }

        foreach (var (n, t) in _strangers.ToList())
        {
            if (now - t > keep)
            {
                _strangers.Remove(n);
            }
        }

        _last = now;
    }
}
