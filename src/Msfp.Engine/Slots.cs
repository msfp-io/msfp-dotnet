namespace Msfp;

/// <summary>
/// Bounds how many of something run at once, by key (one playback, or one
/// peer), with a bounded queue: a request waits for a slot only while fewer
/// than queue others wait, and only for wait; otherwise it is refused at
/// once (MSFP core section 8.3, items 3 and 5; media section 2.4).
/// </summary>
internal sealed class Slots(int size, int queue, TimeSpan wait)
{
    private readonly Dictionary<string, Entry> _keys = new(StringComparer.Ordinal);

    private sealed class Entry(int size)
    {
        public SemaphoreSlim Free { get; } = new(size, size);

        /// <summary>Holding a slot or waiting for one.</summary>
        public int Users { get; set; }
    }

    /// <summary>
    /// Takes a slot of key, waiting as above, and gives what frees it (once,
    /// however often it is called); null when it wasn't taken: too many
    /// waiting, the wait over, or ct cancelled.
    /// </summary>
    public async Task<Action?> TakeAsync(string key, CancellationToken ct)
    {
        Entry e;
        lock (_keys)
        {
            if (!_keys.TryGetValue(key, out e!))
            {
                e = new Entry(size);
                _keys[key] = e;
            }

            if (e.Users >= size + queue)
            {
                return null;
            }

            e.Users++;
        }

        void Leave()
        {
            lock (_keys)
            {
                if (--e.Users == 0)
                {
                    _keys.Remove(key);
                }
            }
        }

        bool taken;
        try
        {
            taken = await e.Free.WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            taken = false;
        }

        if (!taken)
        {
            Leave();
            return null;
        }

        var freed = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref freed, 1) == 0)
            {
                e.Free.Release();
                Leave();
            }
        };
    }
}
