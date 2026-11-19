using System.Text.Json;

namespace Msfp.Stores;

/// <summary>A transaction on a state (Go's state.tx).</summary>
internal sealed class StateTx : IStoreTx, IOwnLogTx
{
    private readonly bool readOnly;
    private readonly HashSet<string> cloned = new(StringComparer.Ordinal);
    private bool ended;

    public StateTx(State state, bool readOnly)
    {
        State = state;
        this.readOnly = readOnly;
    }

    public State State { get; }

    public Dirty Dirty { get; } = new();

    public void End() => ended = true;

    private static JsonSerializerOptions Json => MsfpJson.Options;

    private void Check()
    {
        if (ended)
        {
            throw new InvalidOperationException("store: a transaction used after its end");
        }
    }

    // Own clones a collection before its first write in this transaction.
    private void OwnCollection(string name)
    {
        Check();
        if (readOnly)
        {
            throw new InvalidOperationException("store: a write inside a read");
        }
        if (!cloned.Add(name))
        {
            return;
        }
        var s = State;
        switch (name)
        {
            case "peers":
                s.Peers = new(s.Peers, StringComparer.Ordinal);
                s.ByKey = new(s.ByKey, StringComparer.Ordinal);
                break;
            case "codes":
                s.Codes = new(s.Codes, StringComparer.Ordinal);
                break;
            case "forgotten":
                s.Forgotten = new(s.Forgotten, StringComparer.Ordinal);
                break;
            case "libraryIds":
                s.LibraryIds = new(s.LibraryIds, StringComparer.Ordinal);
                break;
            case "leftovers":
                s.Leftovers = new(s.Leftovers, StringComparer.Ordinal);
                break;
            case "catalogs":
                s.Catalogs = new(s.Catalogs, StringComparer.Ordinal);
                break;
            case "own":
                s.Own = s.Own.Clone();
                break;
        }
        if (name == "own")
        {
            Dirty.Own = true;
        }
        else if (name != "catalogs")
        {
            Dirty.Records = true;
        }
    }

    // CatalogFor is a peer's catalog, cloned for writing (made when absent).
    private Catalog CatalogFor(string peer)
    {
        OwnCollection("catalogs");
        Dirty.Catalogs.Add(peer);
        if (!cloned.Add("catalog " + peer))
        {
            return State.Catalogs[peer];
        }
        var c = new Catalog();
        if (State.Catalogs.TryGetValue(peer, out var old))
        {
            c.Titles = new(old.Titles, StringComparer.Ordinal);
            c.Sent = new(old.Sent, StringComparer.Ordinal);
        }
        State.Catalogs[peer] = c;
        return c;
    }

    private T? Get<T>(JsonElement? raw)
        where T : class
    {
        Check();
        return raw is { } r && r.ValueKind != JsonValueKind.Null ? r.Deserialize<T>(Json) : null;
    }

    private T? Get<T>(Dictionary<string, JsonElement> map, string key)
        where T : class => map.TryGetValue(key, out var raw) ? Get<T>(raw) : Get<T>(null);

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public ValueTask<ServerRecord?> ServerAsync() => new(Get<ServerRecord>(State.Server));

    public ValueTask PutServerAsync(ServerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Check();
        if (readOnly)
        {
            throw new InvalidOperationException("store: a write inside a read");
        }
        State.Server = ToJson(record);
        Dirty.Records = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<PeerRecord>> PeersAsync()
    {
        Check();
        var out_ = new List<PeerRecord>(State.Peers.Count);
        foreach (var id in State.Peers.Keys.Order(StringComparer.Ordinal))
        {
            out_.Add(Get<PeerRecord>(State.Peers[id])!);
        }
        return new(out_);
    }

    public ValueTask<PeerRecord?> PeerAsync(string id) => new(Get<PeerRecord>(State.Peers, id));

    public ValueTask<PeerRecord?> PeerByKeyAsync(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Check();
        return State.ByKey.TryGetValue(Hex(key), out var id) ? PeerAsync(id) : new((PeerRecord?)null);
    }

    public ValueTask PutPeerAsync(PeerRecord peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        OwnCollection("peers");
        if (Get<PeerRecord>(State.Peers, peer.Id) is { } old)
        {
            State.ByKey.Remove(Hex(old.Key));
        }
        State.Peers[peer.Id] = ToJson(peer);
        State.ByKey[Hex(peer.Key)] = peer.Id;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeletePeerAsync(string id)
    {
        OwnCollection("peers");
        if (Get<PeerRecord>(State.Peers, id) is { } old)
        {
            State.ByKey.Remove(Hex(old.Key));
        }
        State.Peers.Remove(id);
        if (State.Catalogs.ContainsKey(id))
        {
            OwnCollection("catalogs");
            State.Catalogs.Remove(id);
            Dirty.Catalogs.Add(id);
        }
        OwnCollection("leftovers");
        foreach (var (k, l) in State.Leftovers.ToList())
        {
            if (l.Peer == id)
            {
                State.Leftovers.Remove(k);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<CodeRecord>> CodesAsync()
    {
        Check();
        var out_ = new List<CodeRecord>(State.Codes.Count);
        foreach (var id in State.Codes.Keys.Order(StringComparer.Ordinal))
        {
            out_.Add(Get<CodeRecord>(State.Codes[id])!);
        }
        return new(out_);
    }

    public async ValueTask<CodeRecord?> CodeByHashAsync(byte[] secretHash)
    {
        ArgumentNullException.ThrowIfNull(secretHash);
        foreach (var c in await CodesAsync().ConfigureAwait(false))
        {
            if (c.SecretHash is not null && c.SecretHash.AsSpan().SequenceEqual(secretHash))
            {
                return c;
            }
        }
        return null;
    }

    public ValueTask PutCodeAsync(CodeRecord code)
    {
        ArgumentNullException.ThrowIfNull(code);
        OwnCollection("codes");
        State.Codes[code.Id] = ToJson(code);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteCodeAsync(string id)
    {
        OwnCollection("codes");
        State.Codes.Remove(id);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ForgottenKey?> ForgottenAsync(byte[] keyHash)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        return new(Get<ForgottenKey>(State.Forgotten, Hex(keyHash)));
    }

    public ValueTask ForgetAsync(ForgottenKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        OwnCollection("forgotten");
        State.Forgotten[Hex(key.KeyHash)] = ToJson(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnforgetAsync(byte[] keyHash)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        Check();
        var h = Hex(keyHash);
        if (State.Forgotten.ContainsKey(h))
        {
            OwnCollection("forgotten");
            State.Forgotten.Remove(h);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask TrimForgottenAsync(DateTimeOffset before)
    {
        Check();
        var old = new List<string>();
        foreach (var (h, raw) in State.Forgotten)
        {
            if (Get<ForgottenKey>(raw)!.At < before)
            {
                old.Add(h);
            }
        }
        if (old.Count > 0)
        {
            OwnCollection("forgotten");
            foreach (var h in old)
            {
                State.Forgotten.Remove(h);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyDictionary<string, string>> LibraryIdsAsync()
    {
        Check();
        return new(new Dictionary<string, string>(State.LibraryIds, StringComparer.Ordinal));
    }

    public ValueTask PutLibraryIdAsync(string hostId, string publicId)
    {
        OwnCollection("libraryIds");
        State.LibraryIds[hostId] = publicId;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> SentAsync(string peer, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Check();
        var out_ = new List<string>();
        if (State.Catalogs.TryGetValue(peer, out var c))
        {
            foreach (var k in keys)
            {
                if (c.Sent.GetValueOrDefault(k))
                {
                    out_.Add(k);
                }
            }
        }
        return new(out_);
    }

    public ValueTask MarkSentAsync(string peer, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var c = CatalogFor(peer);
        foreach (var k in keys)
        {
            c.Sent[k] = true;
        }
        return ValueTask.CompletedTask;
    }

    internal static string LeftoverKey(Leftover l) => l.Peer + "\n" + l.PlaybackId;

    public ValueTask<IReadOnlyList<Leftover>> LeftoversAsync()
    {
        Check();
        return new(State.Leftovers.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList());
    }

    public ValueTask PutLeftoverAsync(Leftover leftover)
    {
        ArgumentNullException.ThrowIfNull(leftover);
        OwnCollection("leftovers");
        State.Leftovers[LeftoverKey(leftover)] = leftover;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteLeftoverAsync(Leftover leftover)
    {
        ArgumentNullException.ThrowIfNull(leftover);
        OwnCollection("leftovers");
        State.Leftovers.Remove(LeftoverKey(leftover));
        return ValueTask.CompletedTask;
    }

    public ValueTask PutPeerTitlesAsync(string peer, long reading, IReadOnlyList<Title> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var c = CatalogFor(peer);
        foreach (var title in titles)
        {
            c.Titles[title.Key] = new Stored(reading, TitleJson.ToElement(title));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask EndReadingAsync(string peer, long reading)
    {
        var c = CatalogFor(peer);
        foreach (var (k, s) in c.Titles.ToList())
        {
            if (s.Reading != reading)
            {
                c.Titles.Remove(k);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DropPeerTitlesAsync(string peer, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var c = CatalogFor(peer);
        foreach (var k in keys)
        {
            c.Titles.Remove(k);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Title>> PeerTitlesAsync(string peer, string after, int limit)
    {
        Check();
        var out_ = new List<Title>();
        if (State.Catalogs.TryGetValue(peer, out var c))
        {
            foreach (var k in c.Titles.Keys.Order(StringComparer.Ordinal))
            {
                if (string.CompareOrdinal(k, after) <= 0)
                {
                    continue;
                }
                if (out_.Count >= limit)
                {
                    break;
                }
                out_.Add(TitleJson.Unmarshal(c.Titles[k].Title));
            }
        }
        return new(out_);
    }

    public ValueTask<Title?> PeerTitleAsync(string peer, string key)
    {
        Check();
        if (State.Catalogs.TryGetValue(peer, out var c) && c.Titles.TryGetValue(key, out var s))
        {
            return new(TitleJson.Unmarshal(s.Title));
        }
        return new((Title?)null);
    }

    // The own catalog's log (IOwnLogTx).

    public ValueTask<IReadOnlyDictionary<string, Title>> OwnSnapshotAsync()
    {
        Check();
        var out_ = new Dictionary<string, Title>(StringComparer.Ordinal);
        foreach (var (k, raw) in State.Own.Snapshot)
        {
            out_[k] = TitleJson.Unmarshal(raw);
        }
        return new(out_);
    }

    public ValueTask PutOwnSnapshotAsync(IReadOnlyDictionary<string, Title> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        OwnCollection("own");
        var snap = new Dictionary<string, JsonElement>(titles.Count, StringComparer.Ordinal);
        foreach (var (k, title) in titles)
        {
            snap[k] = TitleJson.ToElement(title);
        }
        State.Own.Snapshot = snap;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OwnChange>> OwnChangesAsync(long since, string after, int limit)
    {
        Check();
        var latest = new Dictionary<string, StoredChange>(StringComparer.Ordinal);
        foreach (var c in State.Own.Changes)
        {
            if (c.Seq > since && string.CompareOrdinal(c.Key, after) > 0 &&
                (!latest.TryGetValue(c.Key, out var l) || c.Seq > l.Seq))
            {
                latest[c.Key] = c;
            }
        }
        IReadOnlyList<OwnChange> out_ = latest.Values
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Take(Math.Max(limit, 0))
            .Select(c => new OwnChange(c.Seq, c.Key, c.Title is { } t ? TitleJson.Unmarshal(t) : null, c.At))
            .ToList();
        return new(out_);
    }

    public ValueTask AppendOwnChangesAsync(IReadOnlyList<OwnChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        OwnCollection("own");
        foreach (var c in changes)
        {
            State.Own.Changes.Add(new StoredChange(c.Seq, c.Key, c.Title is null ? null : TitleJson.ToElement(c.Title), c.At));
            State.Own.Seq = Math.Max(State.Own.Seq, c.Seq);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask TrimOwnChangesAsync(DateTimeOffset before)
    {
        OwnCollection("own");
        var kept = new List<StoredChange>();
        foreach (var c in State.Own.Changes)
        {
            if (c.At < before)
            {
                State.Own.Floor = Math.Max(State.Own.Floor, c.Seq);
                continue;
            }
            kept.Add(c);
        }
        State.Own.Changes = kept;
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> OwnFloorAsync()
    {
        Check();
        return new(State.Own.Floor);
    }

    public ValueTask<long> OwnSeqAsync()
    {
        Check();
        return new(Math.Max(State.Own.Seq, State.Own.Floor));
    }

    public ValueTask<long> BumpOwnSeqAsync()
    {
        OwnCollection("own");
        State.Own.Seq = Math.Max(State.Own.Seq, State.Own.Floor) + 1;
        return new(State.Own.Seq);
    }
}
