using System.Text.Json;
using System.Text.Json.Serialization;

namespace Msfp.Stores;

// What the shipped stores share (Go's store/internal/state): the engine's
// records in memory, read and written in transactions, copied on write.
// The memory store keeps it as it is; the JSON file store writes it to
// files at each commit.
//
// Records are kept as their JSON: a record read back is a new copy, so
// nothing the engine changes reaches the store until it puts it back, and
// a transaction that fails leaves nothing behind.
//
// A State is never changed once it is current: an Update works on a
// shallow copy whose collections are cloned on their first write, then
// takes its place. So a Read needs no lock: the state it took stays as it
// was, a consistent view, whatever Updates follow.

/// <summary>Everything a store holds.</summary>
internal sealed class State
{
    public JsonElement? Server { get; set; }

    public Dictionary<string, JsonElement> Peers { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonElement> Codes { get; set; } = new(StringComparer.Ordinal);

    // By the key hash, hex.
    public Dictionary<string, JsonElement> Forgotten { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> LibraryIds { get; set; } = new(StringComparer.Ordinal);

    // By peer, a line feed, playback ID.
    public Dictionary<string, Leftover> Leftovers { get; set; } = new(StringComparer.Ordinal);

    // The groups this server is in (IGroupsTx), by ID.
    public Dictionary<string, JsonElement> Groups { get; set; } = new(StringComparer.Ordinal);

    // The peers' titles and the keys each was sent, by peer ID: kept apart,
    // since they are big and change on their own.
    public Dictionary<string, Catalog> Catalogs { get; set; } = new(StringComparer.Ordinal);

    // The change log of the host's own catalog.
    public Own Own { get; set; } = new();

    // Finds a peer by its current key (hex); made from Peers.
    public Dictionary<string, string> ByKey { get; set; } = new(StringComparer.Ordinal);

    // Index makes the lookups made from what was read (after loading).
    public void Index()
    {
        ByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, raw) in Peers)
        {
            if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("key", out var key) &&
                key.ValueKind == JsonValueKind.String && key.TryGetBytesFromBase64(out var bytes))
            {
                ByKey[Convert.ToHexStringLower(bytes)] = id;
            }
        }
    }

    public State Shallow() => (State)MemberwiseClone();
}

/// <summary>What state.json holds: the records, by the Go store's names.</summary>
internal sealed class StateFile
{
    [JsonPropertyName("server"), OmitEmpty]
    public JsonElement? Server { get; set; }

    [JsonPropertyName("peers")]
    public SortedDictionary<string, JsonElement>? Peers { get; set; }

    [JsonPropertyName("codes")]
    public SortedDictionary<string, JsonElement>? Codes { get; set; }

    [JsonPropertyName("forgotten")]
    public SortedDictionary<string, JsonElement>? Forgotten { get; set; }

    [JsonPropertyName("libraryIds")]
    public SortedDictionary<string, string>? LibraryIds { get; set; }

    [JsonPropertyName("leftovers")]
    public SortedDictionary<string, Leftover>? Leftovers { get; set; }

    [JsonPropertyName("groups"), OmitEmpty]
    public SortedDictionary<string, JsonElement>? Groups { get; set; }
}

/// <summary>One peer's titles, and the keys of our titles it was sent.</summary>
internal sealed class Catalog
{
    [JsonPropertyName("titles")]
    public Dictionary<string, Stored> Titles { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("sent")]
    public Dictionary<string, bool> Sent { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A peer's title: the reading it was last seen in, and the title as TitleJson writes it.</summary>
internal sealed record Stored(
    [property: JsonPropertyName("reading")] long Reading,
    [property: JsonPropertyName("title")] JsonElement Title);

/// <summary>The change log of the host's own catalog.</summary>
internal sealed class Own
{
    [JsonPropertyName("snapshot")]
    public Dictionary<string, JsonElement> Snapshot { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("changes")]
    public List<StoredChange> Changes { get; set; } = [];

    [JsonPropertyName("floor")]
    public long Floor { get; set; }

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    public Own Clone() => new()
    {
        Snapshot = new Dictionary<string, JsonElement>(Snapshot, StringComparer.Ordinal),
        Changes = [.. Changes],
        Floor = Floor,
        Seq = Seq,
    };
}

/// <summary>An own change as stored: its title as TitleJson writes it (Go's ownChangeJSON).</summary>
internal sealed record StoredChange(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("title"), OmitEmpty] JsonElement? Title,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>What a transaction changed: the records, which peers' catalogs, the own log.</summary>
internal sealed class Dirty
{
    public bool Records { get; set; }

    public HashSet<string> Catalogs { get; } = new(StringComparer.Ordinal);

    public bool Own { get; set; }
}

/// <summary>
/// Holds a State, one Update at a time; Commit, when set, writes what an
/// Update changed before it takes effect (the JSON file store).
/// </summary>
internal sealed class StateStore : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private State cur;

    public StateStore(State state) => cur = state;

    /// <summary>Told the new state and what changed; an exception undoes the Update.</summary>
    public Action<State, Dirty>? Commit { get; set; }

    public async ValueTask ReadAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fn);
        ct.ThrowIfCancellationRequested();
        var tx = new StateTx(Volatile.Read(ref cur), readOnly: true);
        try
        {
            await fn(tx).ConfigureAwait(false);
        }
        finally
        {
            tx.End();
        }
    }

    public async ValueTask UpdateAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fn);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tx = new StateTx(cur.Shallow(), readOnly: false);
            try
            {
                await fn(tx).ConfigureAwait(false);
            }
            finally
            {
                tx.End();
            }
            // Not cancelled once begun: a commit cut halfway would leave
            // the files apart from what is kept here.
            Commit?.Invoke(tx.State, tx.Dirty);
            Volatile.Write(ref cur, tx.State);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<T> ReadAsync<T>(Func<IStoreTx, ValueTask<T>> fn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fn);
        T result = default!;
        await ReadAsync(async tx => { result = await fn(tx).ConfigureAwait(false); }, ct).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<T> UpdateAsync<T>(Func<IStoreTx, ValueTask<T>> fn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fn);
        T result = default!;
        await UpdateAsync(async tx => { result = await fn(tx).ConfigureAwait(false); }, ct).ConfigureAwait(false);
        return result;
    }

    public void Dispose() => gate.Dispose();
}
