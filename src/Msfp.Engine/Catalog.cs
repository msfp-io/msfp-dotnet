using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// The catalog, on the owning side (MSFP Media listing 2 and 3): what
// this server offers each peer, the libraries and titles calls, and the
// change log of its own titles, kept by the host (IChangeLog) or by the
// engine from snapshots (IOwnLogTx).
public sealed partial class MsfpEngine
{
    /// <summary>How many keys a titles page holds at most (MSFP Media listing 3.1).</summary>
    internal const int TitlesPage = 5000;

    /// <summary>How long the engine keeps its own change log (MSFP Media listing 3.2: at least 30 days).</summary>
    internal static readonly TimeSpan ChangesKept = TimeSpan.FromDays(30);

    internal const string CallLibraries = "libraries";
    internal const string CallTitles = "titles";
    internal const string CallPlay = "play";
    internal const string CallProgress = "progress";
    internal const string CallStop = "stop";

    // The longest library name sent, in code points.
    private const int MaxLibraryName = 80;

    // What the engine knows of its own catalog's log: whether it was
    // refreshed since the start, whether the host said it changed since,
    // and the highest change number given out (a change of what a peer
    // sees is noted at least there, so that a reading before it is never
    // answered with changes after it).
    private readonly SemaphoreSlim _ownLock = new(1, 1);
    private int _ownRefreshed;
    private int _ownDirty;
    private long _seqSeen;
    private bool _logKept;

    // Whether part is on between this server and p.
    internal bool On(PeerRecord p, string part) => Agreed(p) is { } a && a.Parts.ContainsKey(part);

    // A title key of a content part on with p, in its canonical form.
    internal bool KeyOn(PeerRecord p, string key, out TitleKey k) => TitleKey.TryParse(key, out k) && On(p, k.Part);

    private async Task<IReadOnlyList<Library>> HostLibrariesAsync(CancellationToken ct) =>
        _cfg.Catalog is null ? [] : await _cfg.Catalog.LibrariesAsync(ct).ConfigureAwait(false);

    // Gives each host library its public ID, made when it is first offered:
    // 96 random bits, never reused (MSFP Media listing 2.1).
    private async ValueTask<Dictionary<string, string>> PublicIdsAsync(IStoreTx tx, IEnumerable<string> hostIds)
    {
        var ids = new Dictionary<string, string>(await tx.LibraryIdsAsync().ConfigureAwait(false), StringComparer.Ordinal);
        foreach (var id in hostIds)
        {
            if (ids.ContainsKey(id))
            {
                continue;
            }

            var pub = Base64UrlText.Encode(RandomBytes(12));
            await tx.PutLibraryIdAsync(id, pub).ConfigureAwait(false);
            ids[id] = pub;
        }

        return ids;
    }

    private async Task<Dictionary<string, string>> LibraryIdsAsync(CancellationToken ct)
    {
        Dictionary<string, string> ids = [];
        await _store.ReadAsync(async tx =>
            ids = new Dictionary<string, string>(await tx.LibraryIdsAsync().ConfigureAwait(false), StringComparer.Ordinal), ct)
            .ConfigureAwait(false);
        return ids;
    }

    /// <summary>Lists the host's libraries, those offered to the peer marked.</summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task<IReadOnlyList<Library>> OffersAsync(string peer, CancellationToken ct = default)
    {
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        var libs = await HostLibrariesAsync(ct).ConfigureAwait(false);
        return libs.Select(l => l with { Offered = p.Offers?.Contains(l.Id) == true }).ToList();
    }

    /// <summary>
    /// Sets the host libraries offered to a paired peer (IDs that aren't the
    /// host's are left out). The peer's next reading is a whole one, and its
    /// playbacks from libraries no longer offered end at once (MSFP
    /// Media playing 5).
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="libraries">The host's library IDs.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotPaired"/>.</exception>
    public async Task SetOffersAsync(string peer, IEnumerable<string> libraries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var asked = libraries.ToHashSet(StringComparer.Ordinal);
        var ours = await HostLibrariesAsync(ct).ConfigureAwait(false);
        var keep = ours.Select(l => l.Id).Where(asked.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var mark = await SeqMarkAsync(ct).ConfigureAwait(false);
        var changed = false;
        await _store.UpdateAsync(async tx =>
        {
            var p = await tx.PeerAsync(peer).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            if (p.State != PeerState.Paired)
            {
                throw new MsfpException(MsfpError.NotPaired);
            }

            var had = (p.Offers ?? []).Order(StringComparer.Ordinal).ToList();
            changed = !had.SequenceEqual(keep, StringComparer.Ordinal);
            if (!changed)
            {
                return;
            }

            await PublicIdsAsync(tx, keep).ConfigureAwait(false);
            p.OffersSeq = await OffersSeqAsync(tx, mark).ConfigureAwait(false);
            p.Offers = keep;
            await tx.PutPeerAsync(p).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (!changed)
        {
            return;
        }

        await BumpLogAsync(ct).ConfigureAwait(false);
        EndPlaybacks(pb => pb.Peer == peer && !OfferedIn(pb.Libraries, keep), EndReason.NotOffered);
    }

    /// <summary>
    /// Chooses whether the individual relation with a paired peer shares the
    /// host's local titles too (MSFP catalog section 5), in the libraries
    /// offered to it: off by default. A change forces the peer's next reading
    /// to be a whole one.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="on">Share them.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotPaired"/>.</exception>
    public async Task SetLocalAsync(string peer, bool on, CancellationToken ct = default)
    {
        var mark = await SeqMarkAsync(ct).ConfigureAwait(false);
        var changed = false;
        await _store.UpdateAsync(async tx =>
        {
            var p = await tx.PeerAsync(peer).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            if (p.State != PeerState.Paired)
            {
                throw new MsfpException(MsfpError.NotPaired);
            }

            if (!(changed = p.Local != on))
            {
                return;
            }

            p.OffersSeq = await OffersSeqAsync(tx, mark).ConfigureAwait(false);
            p.Local = on;
            await tx.PutPeerAsync(p).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (changed)
        {
            await BumpLogAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An opaque part of a title key the host may use for one of its titles
    /// (MSFP Media listing 4.2): the identifier of a local title, or an
    /// extra's part after x/, made from the host's own identifier of it (its
    /// media server's item ID, say) as a keyed MAC under this server's IDs
    /// secret: 22 characters of base64url, stable as long as id is, and never
    /// computable by a peer from it.
    /// </summary>
    /// <param name="id">The host's identifier of the title.</param>
    public string OpaqueKeyPart(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var mac = HMACSHA256.HashData(OurKeys().Ids, Encoding.UTF8.GetBytes("key part\n" + id));
        return Base64UrlText.Encode(mac.AsSpan(0, 16));
    }

    /// <summary>
    /// The host saying one of its libraries is gone: it is offered to nobody
    /// from then on, and peers' playbacks from it end. Its public ID is kept,
    /// never given to another library.
    /// </summary>
    /// <param name="id">The host's library ID.</param>
    /// <param name="ct">Cancels.</param>
    public async Task LibraryRemovedAsync(string id, CancellationToken ct = default)
    {
        var removed = false;
        try
        {
            var mark = await SeqMarkAsync(ct).ConfigureAwait(false);
            await _store.UpdateAsync(async tx =>
            {
                removed = false;
                foreach (var p in await tx.PeersAsync().ConfigureAwait(false))
                {
                    if (p.Offers is { } offers && offers.Remove(id))
                    {
                        removed = true;
                        p.OffersSeq = await OffersSeqAsync(tx, mark).ConfigureAwait(false);
                        await tx.PutPeerAsync(p).ConfigureAwait(false);
                    }
                }
            }, ct).ConfigureAwait(false);
            if (removed)
            {
                await BumpLogAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            EndPlaybacks(pb => pb.Libraries.Count > 0 && pb.Libraries.All(l => l == id), EndReason.NotOffered);
        }
    }

    /// <summary>
    /// Sets what a peer may use of this server at once (MSFP
    /// playback section 5), from its next play on.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.InvalidLimits"/>.</exception>
    public async Task SetLimitsAsync(string peer, Limits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Check();
        var l = limits with { };
        if (!l.Audio)
        {
            l.AudioStreams = 0;
            l.AudioTranscodes = 0;
        }

        await UpdatePeerAsync(peer, p => p.Limits = l, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The host saying its titles changed: a hint. When the engine keeps the
    /// change log, it reads the titles again before its next titles answer,
    /// and in the background now.
    /// </summary>
    public void CatalogChanged()
    {
        if (!_logKept)
        {
            return;
        }

        Volatile.Write(ref _ownDirty, 1);
        Background(async ct =>
        {
            try
            {
                await RefreshOwnAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "msfp: the catalog's change log couldn't be refreshed");
            }
        });
    }

    // The host's own change log, null when it keeps none.
    private IChangeLog? ChangeLog() => _cfg.Catalog as IChangeLog;

    // The catalog's change number now, and the oldest it still answers
    // changes since (floor); 0, 0 when no log is kept.
    private async Task<(long Seq, long Floor)> CatalogSeqAsync(CancellationToken ct)
    {
        if (ChangeLog() is { } cl)
        {
            var s = await cl.SeqAsync(ct).ConfigureAwait(false);
            SawSeq(s);
            return (s, 0);
        }

        if (!_logKept)
        {
            return (0, 0);
        }

        if (Volatile.Read(ref _ownRefreshed) == 0 || Volatile.Read(ref _ownDirty) == 1)
        {
            await RefreshOwnAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // A refresh under way (CatalogChanged's, in the background) has
            // cleared the hint but not yet logged what changed: wait for it,
            // so that this answer comes after the change it was told of.
            await _ownLock.WaitAsync(ct).ConfigureAwait(false);
            _ownLock.Release();
        }

        long seq = 0, floor = 0;
        await _store.ReadAsync(async tx =>
        {
            var log = (IOwnLogTx)tx;
            seq = await log.OwnSeqAsync().ConfigureAwait(false);
            floor = await log.OwnFloorAsync().ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        SawSeq(seq);
        return (seq, floor);
    }

    private void SawSeq(long seq)
    {
        while (true)
        {
            var old = Interlocked.Read(ref _seqSeen);
            if (seq <= old || Interlocked.CompareExchange(ref _seqSeen, seq, old) == old)
            {
                return;
            }
        }
    }

    // The change number from which a peer's readings know of a change of
    // what it sees (its offers, the parts on), set as its OffersSeq inside
    // the change's transaction (MSFP Media listing 3.2). With the engine's own
    // log, a number of its own; with the host's, one above every number
    // given out, and above the host's now (mark, read before the
    // transaction): until the host's log moves past it, readings are whole.
    private async ValueTask<long> OffersSeqAsync(IStoreTx tx, long mark)
    {
        if (_logKept && tx is IOwnLogTx log)
        {
            return await log.BumpOwnSeqAsync().ConfigureAwait(false);
        }

        return mark + 1;
    }

    // Moves the host's change log past the change numbers noted for a
    // change of what peers see, when it can (ISeqBumper).
    private async Task BumpLogAsync(CancellationToken ct)
    {
        if (ChangeLog() is not ISeqBumper b)
        {
            return;
        }

        try
        {
            SawSeq(await b.BumpSeqAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the catalog's change log couldn't take a number");
        }
    }

    // The highest change number given out, at least the host's catalog's
    // now: read before a transaction that changes what a peer sees.
    private async Task<long> SeqMarkAsync(CancellationToken ct)
    {
        if (_logKept)
        {
            return 0;
        }

        await CatalogSeqAsync(ct).ConfigureAwait(false);
        return Interlocked.Read(ref _seqSeen);
    }

    // A change of the parts on with a peer: its next reading of our titles
    // is a whole one.
    private async Task PartsChangedAsync(string id, CancellationToken ct)
    {
        try
        {
            var mark = await SeqMarkAsync(ct).ConfigureAwait(false);
            await _store.UpdateAsync(async tx =>
            {
                if (await tx.PeerAsync(id).ConfigureAwait(false) is not { } p)
                {
                    return;
                }

                p.OffersSeq = await OffersSeqAsync(tx, mark).ConfigureAwait(false);
                await tx.PutPeerAsync(p).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            await BumpLogAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: a peer's change of versions couldn't be noted: {Peer}", id);
        }
    }

    // Reads the host's titles whole and notes what changed since the
    // snapshot, all under one new change number.
    private async Task RefreshOwnAsync(CancellationToken ct)
    {
        await _ownLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _ownDirty, 0);
            var ids = (await HostLibrariesAsync(ct).ConfigureAwait(false)).Select(l => l.Id).ToList();
            var now = new Dictionary<string, Title>(StringComparer.Ordinal);
            for (var after = ""; ids.Count > 0;)
            {
                var page = await _cfg.Catalog!.TitlesAsync(ids, after, TitlesPage, ct).ConfigureAwait(false);
                foreach (var t in page.Titles)
                {
                    if (string.CompareOrdinal(t.Key, after) <= 0)
                    {
                        throw new MsfpException(MsfpError.Internal, "the host's titles aren't in key order");
                    }

                    t.Libraries?.Sort(StringComparer.Ordinal);
                    now[t.Key] = t;
                    after = t.Key;
                }

                if (!page.More || page.Titles.Count == 0)
                {
                    break;
                }
            }

            var at = Now();
            await _store.UpdateAsync(async tx =>
            {
                var log = (IOwnLogTx)tx;
                var was = await log.OwnSnapshotAsync().ConfigureAwait(false);
                var seq = await log.OwnSeqAsync().ConfigureAwait(false) + 1;
                var changes = new List<OwnChange>();
                foreach (var (key, t) in now)
                {
                    if (!was.TryGetValue(key, out var old) || !SameTitle(old, t))
                    {
                        changes.Add(new OwnChange(seq, key, t, at));
                    }
                }

                foreach (var key in was.Keys)
                {
                    if (!now.ContainsKey(key))
                    {
                        changes.Add(new OwnChange(seq, key, null, at));
                    }
                }

                await log.TrimOwnChangesAsync(at - ChangesKept).ConfigureAwait(false);
                if (changes.Count == 0)
                {
                    return;
                }

                await log.AppendOwnChangesAsync(changes).ConfigureAwait(false);
                await log.PutOwnSnapshotAsync(now).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            Volatile.Write(ref _ownRefreshed, 1);
        }
        finally
        {
            _ownLock.Release();
        }
    }

    // Whether two titles would be sent alike.
    private static bool SameTitle(Title a, Title b) => TitleJson.Marshal(a).AsSpan().SequenceEqual(TitleJson.Marshal(b));

    // Answers a paired peer with what this server offers it (MSFP
    // Media listing 2), and, with playback on, its limits.
    private async Task<Answer> LibrariesAsync(CallRequest req, CancellationToken ct)
    {
        var p = req.Peer;
        if (p is null || p.State != PeerState.Paired || !EmptyRequest(req.Body))
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        if (!On(p, Versions.PartCatalog))
        {
            return Answer.Refuse(ProblemType.NotSupported);
        }

        IReadOnlyList<Library> ours;
        try
        {
            ours = await HostLibrariesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the host's libraries couldn't be read");
            return Answer.Refuse(ProblemType.Internal);
        }

        var ids = await LibraryIdsAsync(ct).ConfigureAwait(false);
        var offers = Offered(p);
        var offered = ours.Where(l => ids.ContainsKey(l.Id) && offers.Contains(l.Id)).ToList();
        var playback = On(p, Versions.PartPlayback);
        var music = On(p, Versions.PartMusic);
        return Answer.Ok(JsonOut.Object(o => o.String("name", Name())
            // A library carries no kind (MSFP Media listing 2): its titles' keys
            // say what it holds.
            .Objects("libraries", offered, (w, l) => w.String("id", ids[l.Id]).String("name", Music.CleanText(l.Name ?? "", MaxLibraryName)))
            .Object("limits", playback ? w => WriteLimits(w, Generous(p), music) : null)));
    }

    // A limits object as sent (MSFP playback section 5, MSFP Media music 5.1).
    private static void WriteLimits(JsonOut w, Limits l, bool music)
    {
        w.Integer("streams", l.Streams).Integer("transcodes", l.Transcodes).Integer("bitrateKbps", l.BitrateKbps);
        if (l.Downloads > 0)
        {
            w.Integer("downloads", l.Downloads).Integer("downloadsPerDay", NonZero(l.DownloadsPerDay))
                .Integer("downloadKbps", NonZero(l.DownloadKbps));
        }

        // No scanBytes: each scan's answer states its cap (MSFP playback section 8).
        w.Integer("scansPerDay", NonZero(l.ScansPerDay));
        if (music && l.Audio)
        {
            w.Integer("audioStreams", l.AudioStreams).Integer("audioTranscodes", l.AudioTranscodes);
        }
    }

    // Whether a body is a valid request with no members MSFP reads.
    private static bool EmptyRequest(byte[] body)
    {
        try
        {
            StrictJson.ReadRequest(body, []);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    // A page of titles as it is built.
    private sealed class TitlesOut
    {
        public List<byte[]> Titles { get; } = [];

        public string? Next { get; set; }

        public bool Changes { get; set; }

        public List<string>? Gone { get; set; }

        public long Seq { get; set; }

        public byte[] Body() => JsonOut.Object(o => o.Raw("titles", Array()).String("next", Next)
            .Bool("changes", Changes ? true : null).Strings("gone", Gone is { Count: > 0 } ? Gone : null)
            .Integer("seq", Seq > 0 ? Seq : null));

        // The titles as one JSON array, each as it was written.
        private byte[] Array()
        {
            var output = new List<byte>(Titles.Sum(t => t.Length + 1) + 2) { (byte)'[' };
            for (var i = 0; i < Titles.Count; i++)
            {
                if (i > 0)
                {
                    output.Add((byte)',');
                }

                output.AddRange(Titles[i]);
            }

            output.Add((byte)']');
            return [.. output];
        }
    }

    // Answers a paired peer with a page of the titles in the libraries it
    // asks for (all those offered to it, when it names none), of those
    // offered to it, of the content parts on between the two, that pass its
    // filters: the whole list, or what changed since its last reading (MSFP
    // Media listing 3).
    private async Task<Answer> TitlesAsync(CallRequest req, CancellationToken ct)
    {
        var p = req.Peer;
        if (p is null || p.State != PeerState.Paired || TitlesRequest.Read(req.Body) is not { } input)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        if (!On(p, Versions.PartCatalog))
        {
            return Answer.Refuse(ProblemType.NotSupported);
        }

        var output = new TitlesOut();
        if (_cfg.Catalog is null)
        {
            return Answer.Ok(output.Body());
        }

        var ids = await LibraryIdsAsync(ct).ConfigureAwait(false);
        // Those asked for that are offered to it, by their public IDs: any
        // other is ignored.
        var byPublic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in Offered(p))
        {
            if (ids.TryGetValue(l, out var pub))
            {
                byPublic[l] = pub;
            }
        }

        var libs = byPublic.Where(kv => input.Libraries is null || input.Libraries.Contains(kv.Value)).Select(kv => kv.Key)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        long seq, floor;
        try
        {
            (seq, floor) = await CatalogSeqAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the catalog's change number couldn't be read");
            return Answer.Refuse(ProblemType.Internal);
        }

        output.Seq = seq;
        if (libs.Count == 0 || (input.Filter.Kind.Length > 0 && !TitleFilter.KnownKind(input.Filter.Kind)))
        {
            // A kind of no content part lists nothing (MSFP Media listing 3.6).
            return Answer.Ok(output.Body());
        }

        var sender = new TitleSender(this, p, libs, LocalOffered(p), byPublic, ct) { Filter = input.Filter };
        var sent = new List<string>();
        try
        {
            if (input.Since > 0 && input.Since >= floor && input.Since <= seq && input.Since >= p.OffersSeq && input.Filter.Changes &&
                !await sender.ChangesPageAsync(input, output, sent).ConfigureAwait(false))
            {
                output = new TitlesOut { Seq = seq };
                sent.Clear();
            }

            if (!output.Changes)
            {
                await sender.WholePageAsync(input.After, output, sent).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the catalog's titles couldn't be read");
            return Answer.Refuse(ProblemType.Internal);
        }

        if (sent.Count > 0)
        {
            await _store.UpdateAsync(tx => tx.MarkSentAsync(p.Id, sent), ct).ConfigureAwait(false);
        }

        return Answer.Ok(output.Body());
    }

    // Writes the host's titles for one titles answer to a peer: the host
    // libraries asked (offered to it), those of them whose local titles it
    // is shared, their public IDs, and the extras' titles looked at, by key,
    // with the library each was sent in ("" for not sent).
    private sealed class TitleSender(MsfpEngine e, PeerRecord p, List<string> libs, List<string> local,
        Dictionary<string, string> pub, CancellationToken ct)
    {
        private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MusicTitle?> _albums = new(StringComparer.Ordinal);

        // The request's filters (MSFP Media listing 3.6).
        public TitleFilter Filter { get; init; } = new();

        // Whether a host's title passes the request's filters.
        private bool Passes(Title t) => Filter.None || Filter.Passes(new FilterFacts
        {
            Key = t.Key,
            PlaceSeason = (t as VideoTitle)?.Place?.Season,
            Album = (t as MusicTitle)?.Album ?? "",
            Artists = (t as MusicTitle)?.Artists ?? [],
        });

        // The host's album of that key, read once per sender; null when it has none.
        private async Task<MusicTitle?> AlbumAsync(string key)
        {
            if (_albums.TryGetValue(key, out var a))
            {
                return a;
            }

            try
            {
                a = await e.HostTitleAsync(key, libs, ct).ConfigureAwait(false) as MusicTitle;
            }
            catch (Exception x) when (x is not OperationCanceledException)
            {
                a = null;
            }

            _albums[key] = a is not null && a.Key == key ? a : null;
            return _albums[key];
        }

        // Fills a whole-list page: the titles after after that pass the
        // request's filters, up to 5,000 and 3 MiB of answer (MSFP Media
        // listing 3.1), next set whenever a title remains after the page.
        // Titles the peer can't be sent are skipped, and the host asked for
        // more. Under a video title, only the keys of its film or series are
        // looked at (TitleFilter.UnderPrefix).
        public async Task WholePageAsync(string after, TitlesOut output, List<string> sent)
        {
            var budget = new PageBudget(CatalogRules.PageReserve);
            var last = "";
            var prefix = Filter.UnderPrefix;
            if (prefix.Length > 0 && string.CompareOrdinal(after, prefix) < 0)
            {
                after = prefix;
            }

            while (true)
            {
                var page = await e._cfg.Catalog!.TitlesAsync(libs, after, TitlesPage, ct).ConfigureAwait(false);
                foreach (var t in page.Titles)
                {
                    if (string.CompareOrdinal(t.Key, after) <= 0)
                    {
                        throw new MsfpException(MsfpError.Internal, "the host's titles aren't in key order");
                    }

                    if (prefix.Length > 0 && !t.Key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return; // past every key of the prefix
                    }

                    after = t.Key;
                    if (!Passes(t) || await TitleAsync(t).ConfigureAwait(false) is not { } raw)
                    {
                        continue;
                    }

                    if (!budget.TryAdd(raw.Length))
                    {
                        if (budget.Keys == 0)
                        {
                            e._log.LogWarning("msfp: a title of the host's catalog passes 3 MiB alone; it isn't sent: {Key}", t.Key);
                            continue;
                        }

                        output.Next = last;
                        return;
                    }

                    output.Titles.Add(raw);
                    sent.Add(t.Key);
                    last = t.Key;
                }

                if (!page.More || page.Titles.Count == 0)
                {
                    return;
                }
            }
        }

        // Fills a changes page: what changed after since, titles new or
        // changed and keys gone (of those the peer was ever sent), in one byte
        // order, 5,000 keys looked at at most, and within 5,000 keys and 3 MiB
        // of answer; false when the log doesn't reach back that far.
        public async Task<bool> ChangesPageAsync(TitlesRequest input, TitlesOut output, List<string> sent)
        {
            var present = new List<Title>();
            var gone = new List<string>();
            var last = "";
            bool more;
            if (e.ChangeLog() is { } cl)
            {
                ChangePage page;
                try
                {
                    page = await cl.ChangesAsync(libs, input.Since, input.After, TitlesPage, ct).ConfigureAwait(false);
                }
                catch (MsfpException x) when (x.Code == MsfpError.TooOld)
                {
                    return false;
                }

                if (page.Present.Count + page.Gone.Count > TitlesPage)
                {
                    throw new MsfpException(MsfpError.Internal, $"a page of changes of more than {TitlesPage} keys");
                }

                present.AddRange(page.Present);
                gone.AddRange(page.Gone);
                last = page.Last;
                more = page.More;
            }
            else
            {
                IReadOnlyList<OwnChange> changes = [];
                await e._store.ReadAsync(async tx =>
                    changes = await ((IOwnLogTx)tx).OwnChangesAsync(input.Since, input.After, TitlesPage).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
                foreach (var c in changes)
                {
                    last = c.Key;
                    if (c.Title is { } t && (t.Libraries ?? []).Any(libs.Contains))
                    {
                        present.Add(t);
                    }
                    else
                    {
                        gone.Add(c.Key);
                    }
                }

                more = changes.Count == TitlesPage;
            }

            output.Changes = true;
            var entries = new List<(string Key, byte[]? Raw)>(); // Raw null: gone
            foreach (var t in present)
            {
                if (!Passes(t))
                {
                    continue;
                }

                if (await TitleAsync(t).ConfigureAwait(false) is { } raw)
                {
                    entries.Add((t.Key, raw));
                }
                else
                {
                    gone.Add(t.Key);
                }
            }

            // A title's extras go with it (MSFP Media listing 3.5), whether the
            // host still lists them or not.
            foreach (var k in gone.ToList())
            {
                foreach (var x in await ExtrasOfAsync(k).ConfigureAwait(false))
                {
                    if (!gone.Contains(x))
                    {
                        gone.Add(x);
                    }
                }
            }

            entries.RemoveAll(en => gone.Contains(en.Key));
            if (Filter.Kind.Length > 0)
            {
                // Changes of the titles of that kind only (MSFP Media listing
                // 3.6): a key names its kind, so gone ones are told apart too.
                gone.RemoveAll(k => !TitleKey.TryParse(k, out var tk) || tk.Kind != Filter.Kind);
            }

            // gone names only titles the peer was told of (MSFP
            // Media listing 3.2), else it would learn of titles it never could see.
            if (gone.Count > 0)
            {
                IReadOnlyList<string> told = [];
                await e._store.ReadAsync(async tx => told = await tx.SentAsync(p.Id, gone).ConfigureAwait(false), ct).ConfigureAwait(false);
                entries.AddRange(told.Select(k => (k, (byte[]?)null)));
            }

            entries.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            var budget = new PageBudget(CatalogRules.PageReserve);
            var included = "";
            foreach (var (key, raw) in entries)
            {
                var size = raw?.Length ?? key.Length + 2; // its quotes; keys need no escaping
                if (!budget.TryAdd(size))
                {
                    if (budget.Keys == 0)
                    {
                        e._log.LogWarning("msfp: a title of the host's catalog passes 3 MiB alone; it isn't sent: {Key}", key);
                        continue;
                    }

                    // The page ends before it: the next asks on from the last
                    // key of this one.
                    output.Next = included;
                    return true;
                }

                included = key;
                if (raw is null)
                {
                    (output.Gone ??= []).Add(key);
                }
                else
                {
                    output.Titles.Add(raw);
                    sent.Add(key);
                }
            }

            if (more && string.CompareOrdinal(last, input.After) > 0)
            {
                output.Next = last;
            }

            return true;
        }

        // A title of the host as sent to the peer, as JSON: null when its key
        // isn't canonical or isn't of its kind (with a warning: the host's
        // bug), of a content part off with the peer, in none of the libraries
        // asked, local while its libraries share no local titles with the
        // peer (MSFP catalog section 5), an extra whose title isn't sent in the
        // same library (MSFP Media listing 3.5), or left with nothing a receiver
        // would keep. Its library is the one asked whose public ID comes first
        // in byte order (MSFP Media listing 3).
        public async Task<byte[]?> TitleAsync(Title t)
        {
            if (!TitleKey.TryParse(t.Key, out var k) || k.Kind != t.Kind)
            {
                e._log.LogWarning("msfp: a title of the host's catalog has a key MSFP doesn't take; it isn't sent: {Key}", t.Key);
                return null;
            }

            if (!e.On(p, k.Part))
            {
                return null;
            }

            var asked = LocalKey(t.Key) ? libs.Where(local.Contains).ToList() : libs;
            var library = "";
            foreach (var l in t.Libraries ?? [])
            {
                if (asked.Contains(l) && pub.TryGetValue(l, out var id) && (library.Length == 0 || string.CompareOrdinal(id, library) < 0))
                {
                    library = id;
                }
            }

            var raw = library.Length == 0 ? null : await OutAsync(t, k, library).ConfigureAwait(false);
            if (k.Kind != "extra")
            {
                _parents[t.Key] = raw is null ? "" : library;
            }

            return raw;
        }

        private async Task<byte[]?> OutAsync(Title t, TitleKey k, string library)
        {
            byte[]? raw = null;
            switch (t)
            {
                case VideoTitle v when k.Part == Versions.PartVideo && VideoKey.TryParse(t.Key, out var vk):
                    if (vk.Parent is { } parent && await ParentLibraryAsync(parent.ToString()).ConfigureAwait(false) != library)
                    {
                        return null;
                    }

                    raw = e.VideoOut(p.Id, v, library);
                    break;
                case MusicTitle m when k.Part == Versions.PartMusic:
                    raw = e.MusicOut(p.Id, m, library, m.Kind == "track" && m.Album is { Length: > 0 } album ? await AlbumAsync(album).ConfigureAwait(false) : null);
                    break;
            }

            if (raw is null)
            {
                e._log.LogDebug("msfp: a title of the host's catalog has nothing MSFP takes; it isn't sent: {Key}", t.Key);
            }

            return raw;
        }

        // The library an extra's title is sent in to the peer, "" when it
        // isn't sent: noted when the title came before in this answer, else
        // looked up in the host's catalog.
        private async Task<string> ParentLibraryAsync(string key)
        {
            if (_parents.TryGetValue(key, out var l))
            {
                return l;
            }

            if (await e.HostTitleAsync(key, libs, ct).ConfigureAwait(false) is not { } t)
            {
                _parents[key] = "";
                return "";
            }

            await TitleAsync(t).ConfigureAwait(false);
            return _parents.GetValueOrDefault(key, "");
        }

        // The keys of the extras the host lists under a film, a series or a
        // season (MSFP Media video 4.6); none for another key.
        private async Task<List<string>> ExtrasOfAsync(string key)
        {
            var output = new List<string>();
            if (!VideoKey.TryParse(key, out var k) || k.Kind is not ("movie" or "series" or "season"))
            {
                return output;
            }

            var prefix = key + "/x/";
            for (var after = prefix; ;)
            {
                var page = await e._cfg.Catalog!.TitlesAsync(libs, after, 64, ct).ConfigureAwait(false);
                foreach (var t in page.Titles)
                {
                    if (!t.Key.StartsWith(prefix, StringComparison.Ordinal) || string.CompareOrdinal(t.Key, after) <= 0)
                    {
                        return output;
                    }

                    output.Add(t.Key);
                    after = t.Key;
                }

                if (!page.More || page.Titles.Count == 0)
                {
                    return output;
                }
            }
        }
    }

    // Whether a title key is of the local scheme, of either content part.
    internal static bool LocalKey(string key) =>
        VideoKey.TryParse(key, out var v) ? v.Scheme == Video.SchemeLocal : MusicKey.TryParse(key, out var m) && m.Local;

    // The host's title of that key in one of these libraries; null for none.
    // The host's catalog gives titles after a key: it is asked for the first
    // after the greatest string below key that no key can be (keys are of
    // A–Z a–z 0–9 _ - /, all below ~).
    internal async Task<Title?> HostTitleAsync(string key, IReadOnlyList<string> libs, CancellationToken ct)
    {
        if (_cfg.Catalog is null || key.Length == 0 || libs.Count == 0)
        {
            return null;
        }

        var before = key[..^1] + (char)(key[^1] - 1) + "~";
        var page = await _cfg.Catalog.TitlesAsync(libs, before, 1, ct).ConfigureAwait(false);
        return page.Titles.Count > 0 && page.Titles[0].Key == key ? page.Titles[0] : null;
    }

    // The host's video title as sent: its copies made ready to leave
    // (SendCopy), then the whole read back as a receiver reads it
    // (Video.ReadTitle), so that nothing leaves that a receiver would skip or
    // drop, and nothing MSFP doesn't take. Null when a receiver would skip
    // it: a film, an episode or an extra with no copy left, an episode keyed
    // by its TMDB episode ID without a valid place (MSFP
    // Media video 3.1 and 4.1), a local film or series without a name, or an extra whose type
    // isn't a token. Theme songs and videos are extras like any (MSFP
    // Media video 3.9): sent when the host gives them. The copies keep the host's
    // order, its preference (MSFP Media video 3.7).
    private byte[]? VideoOut(string peer, VideoTitle t, string library)
    {
        var copies = (t.Copies ?? []).Select(c => SendCopy(peer, c)).ToList();
        var input = JsonOut.Object(o => o.String("key", t.Key).String("library", library)
            .Objects("copies", copies.Count > 0 ? copies : null, Copies.Write)
            .Object("place", t.Place is { } pl ? w => w.Integer("season", pl.Season).Integer("episode", pl.Episode) : null)
            .Strings("with", t.With is { Count: > 0 } ? t.With : null)
            .Object("ids", t.Ids is { Count: > 0 } ids ? w =>
            {
                foreach (var (scheme, v) in ids)
                {
                    w.String(scheme, v);
                }
            }
            : null)
            .String("extraType", string.IsNullOrEmpty(t.ExtraType) ? null : t.ExtraType)
            // An extra carries no name (MSFP Media video 3.9).
            .String("name", string.IsNullOrEmpty(t.Name) || (VideoKey.TryParse(t.Key, out var vk) && vk.Kind == "extra") ? null : t.Name)
            .Integer("year", t.Year != 0 ? t.Year : null)
            .Bool("image", t.Image ? true : null));
        if (Video.ReadTitle(input) is not { } v)
        {
            return null;
        }

        var name = v.Description?.Name ?? "";
        var year = v.Description?.Year ?? 0;
        var image = v.Description?.Image ?? false;
        var with = v.With.Order(StringComparer.Ordinal).ToList();
        // A title carries no kind: its key names it (MSFP Media video 3).
        return JsonOut.Object(o => o.String("key", v.Key).String("library", library)
            .Objects("copies", v.Copies.Count > 0 ? v.Copies : null, Copies.Write)
            .Object("place", v.Place is { } pl ? w => w.Integer("season", pl.Season).Integer("episode", pl.Episode) : null)
            .Strings("with", with.Count > 0 ? with : null)
            .Object("ids", v.Ids.Count > 0 ? w =>
            {
                foreach (var (scheme, value) in v.Ids)
                {
                    w.String(scheme, value);
                }
            }
            : null)
            .String("extraType", v.ExtraType.Length > 0 ? v.ExtraType : null)
            .String("name", name.Length > 0 ? name : null)
            .Integer("year", year != 0 ? year : null)
            .Bool("image", image ? true : null));
    }

    // One of the host's copies as it leaves toward a peer: its ID the version
    // ID play gives for it, never the host's handle; its language tags as
    // Languages.TrySentTag gives them. A copy carries no name and its tracks
    // no title (MSFP Media video 3.3): a viewer's server labels them from their
    // facts. Its previewLayout leaves only with a SheetEncoder, which
    // re-encodes every sheet it serves (.NET has no JPEG codec of its own).
    // What remains out of MSFP's bounds is dropped when it is read back.
    internal Protocol.Copy SendCopy(string peer, Protocol.Copy c) => c with
    {
        Id = VersionId(peer, c.Id),
        AudioTracks = (c.AudioTracks ?? []).Select(a => a with { Language = SentTag(a.Language) }).ToList(),
        SubtitleTracks = (c.SubtitleTracks ?? []).Select(s => s with { Language = SentTag(s.Language) }).ToList(),
        PreviewLayout = _cfg.SheetEncoder is null ? null : c.PreviewLayout,
    };

    // SendCopy for a track's copy (MSFP Media music 3.3).
    internal MusicCopy SendMusicCopy(string peer, MusicCopy c) => c with { Id = VersionId(peer, c.Id) };

    private static string? SentTag(string? code) => code is not null && Languages.TrySentTag(code, out var tag) ? tag : null;

    // The host's music title as sent, its copies made ready to leave, read
    // back as a receiver reads it: what a receiver would skip or drop isn't
    // sent. A track's artists, artistNames, dates and genres leave only when
    // they differ from its album's (MSFP Media music 3): an absent one is the
    // album's.
    private byte[]? MusicOut(string peer, MusicTitle t, string library, MusicTitle? album)
    {
        var w = new Protocol.MusicTitle
        {
            Key = t.Key, Kind = t.Kind, Library = library, Name = t.Name ?? "",
            Artists = Same(t.Artists, album?.Artists) ? [] : t.Artists ?? [],
            ArtistNames = album is not null && t.ArtistNames == album.ArtistNames ? "" : t.ArtistNames ?? "",
            PrimaryType = t.PrimaryType ?? "", SecondaryTypes = t.SecondaryTypes ?? [], Compilation = t.Compilation,
            Date = album is not null && t.Date == album.Date ? "" : t.Date ?? "",
            OriginalDate = album is not null && t.OriginalDate == album.OriginalDate ? "" : t.OriginalDate ?? "",
            Genres = Same(t.Genres, album?.Genres) ? [] : t.Genres ?? [],
            Album = t.Album ?? "", Disc = t.Disc, Number = t.Number,
            Copies = (t.Copies ?? []).Select(c => SendMusicCopy(peer, c)).ToList(),
            Image = t.Image ? true : null,
            Ids = new SortedDictionary<string, string>(t.Ids ?? [], StringComparer.Ordinal),
        };
        return Music.ReadTitle(w.ToJson()) is { } m ? (m with { Library = library }).ToJson() : null;
    }

    // Whether a track's list is its album's (both said, and equal).
    private static bool Same(List<string>? track, List<string>? album) =>
        album is not null && (track ?? []).SequenceEqual(album, StringComparer.Ordinal);
}
