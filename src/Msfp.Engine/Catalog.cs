using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// The catalog, on the owning side (MSFP catalog sections 2 and 3): what
// this server offers each peer, the libraries and titles calls, and the
// change log of its own titles, kept by the host (IChangeLog) or by the
// engine from snapshots (IOwnLogTx).
public sealed partial class MsfpEngine
{
    /// <summary>How many keys a titles page holds at most (catalog section 3.1).</summary>
    internal const int TitlesPage = 5000;

    /// <summary>How long the engine keeps its own change log (catalog section 3.2: at least 30 days).</summary>
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

    // The kinds of library MSFP lists (catalog section 2).
    private static readonly string[] LibraryKinds =
        ["movies", "tvshows", "music", "musicvideos", "homevideos", "boxsets", "books", "photos", "mixed"];

    internal static string CleanKind(string? kind) => kind is not null && LibraryKinds.Contains(kind) ? kind : "";

    // Whether part is on between this server and p.
    internal bool On(PeerRecord p, string part) => Agreed(p) is { } a && a.Parts.ContainsKey(part);

    // A title key of a content part on with p, in its canonical form.
    internal bool KeyOn(PeerRecord p, string key, out TitleKey k) => TitleKey.TryParse(key, out k) && On(p, k.Part);

    private async Task<IReadOnlyList<Library>> HostLibrariesAsync(CancellationToken ct) =>
        _cfg.Catalog is null ? [] : await _cfg.Catalog.LibrariesAsync(ct).ConfigureAwait(false);

    // Gives each host library its public ID, made when it is first offered:
    // 96 random bits, never reused (catalog section 2.1).
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
    /// playbacks from libraries no longer offered end at once (MSFP playback
    /// section 6.3).
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
        EndPlaybacks(pb => pb.Peer == peer && !keep.Contains(pb.Library), EndReason.NotOffered);
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
            EndPlaybacks(pb => pb.Library == id, EndReason.NotOffered);
        }
    }

    /// <summary>
    /// Sets what a peer may use of this server at once (MSFP playback section
    /// 5), from its next play on.
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
    // the change's transaction (catalog section 3.2). With the engine's own
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

    // Answers a paired peer with what this server offers it (catalog section
    // 2), and, with playback on, its limits.
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
        var offered = ours.Where(l => ids.ContainsKey(l.Id) && p.Offers?.Contains(l.Id) == true).ToList();
        var playback = On(p, Versions.PartPlayback);
        var music = On(p, Versions.PartMusic);
        return Answer.Ok(JsonOut.Object(o => o.String("name", Name())
            .Objects("libraries", offered, (w, l) => w.String("id", ids[l.Id]).String("name", Music.CleanText(l.Name ?? "", MaxLibraryName))
                .String("kind", CleanKind(l.Kind)))
            .Object("limits", playback ? w => WriteLimits(w, p.Limits, music) : null)));
    }

    // A limits object as sent (playback section 5, music section 5.1).
    private static void WriteLimits(JsonOut w, Limits l, bool music)
    {
        w.Integer("streams", l.Streams).Integer("transcodes", l.Transcodes).Integer("bitrateKbps", l.BitrateKbps);
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

    // A titles request (catalog section 3).
    private sealed record TitlesRequest(List<string> Libraries, string After, long Since);

    // A page of titles as it is built.
    private sealed class TitlesOut
    {
        public List<Action<JsonOut>> Titles { get; } = [];

        public string? Next { get; set; }

        public bool Changes { get; set; }

        public List<string>? Gone { get; set; }

        public long Seq { get; set; }

        public byte[] Body() => JsonOut.Object(o => o.Objects("titles", Titles, (w, t) => t(w)).String("next", Next)
            .Bool("changes", Changes ? true : null).Strings("gone", Gone is { Count: > 0 } ? Gone : null)
            .Integer("seq", Seq > 0 ? Seq : null));
    }

    // Answers a paired peer with a page of the titles in the libraries it
    // asks for, of those offered to it, of the content parts on between the
    // two: the whole list, or what changed since its last reading (catalog
    // section 3).
    private async Task<Answer> TitlesAsync(CallRequest req, CancellationToken ct)
    {
        var p = req.Peer;
        TitlesRequest? input = null;
        try
        {
            var body = StrictJson.ReadRequest(req.Body, ["libraries", "after", "since"]);
            if (body.GetStrings("libraries") is { } libraries)
            {
                input = new TitlesRequest(libraries, body.GetString("after") ?? "", body.GetInteger("since") ?? 0);
            }
        }
        catch (ProtocolException)
        {
        }

        if (p is null || p.State != PeerState.Paired || input is null || input.After.Length > 512)
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
        foreach (var l in p.Offers ?? [])
        {
            if (ids.TryGetValue(l, out var pub))
            {
                byPublic[l] = pub;
            }
        }

        var libs = byPublic.Where(kv => input.Libraries.Contains(kv.Value)).Select(kv => kv.Key)
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
        if (libs.Count == 0)
        {
            return Answer.Ok(output.Body());
        }

        var sent = new List<string>();
        try
        {
            if (input.Since > 0 && input.Since >= floor && input.Since <= seq && input.Since >= p.OffersSeq &&
                !await ChangesPageAsync(p, libs, byPublic, input, output, sent, ct).ConfigureAwait(false))
            {
                output = new TitlesOut { Seq = seq };
                sent.Clear();
            }

            if (!output.Changes)
            {
                await WholePageAsync(p, libs, byPublic, input.After, output, sent, ct).ConfigureAwait(false);
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

    // Fills a whole-list page: up to 5,000 titles after after, with next set
    // only on a full page (catalog section 3.1). Titles the peer can't be
    // sent are skipped, and the host asked for more.
    private async Task WholePageAsync(PeerRecord p, List<string> libs, Dictionary<string, string> pub, string after,
        TitlesOut output, List<string> sent, CancellationToken ct)
    {
        while (output.Titles.Count < TitlesPage)
        {
            var page = await _cfg.Catalog!.TitlesAsync(libs, after, TitlesPage - output.Titles.Count, ct).ConfigureAwait(false);
            foreach (var t in page.Titles)
            {
                if (string.CompareOrdinal(t.Key, after) <= 0)
                {
                    throw new MsfpException(MsfpError.Internal, "the host's titles aren't in key order");
                }

                after = t.Key;
                if (TitleOut(p, t, libs, pub) is { } w)
                {
                    output.Titles.Add(w);
                    sent.Add(t.Key);
                }
            }

            if (!page.More || page.Titles.Count == 0)
            {
                return;
            }
        }

        output.Next = after;
    }

    // Fills a changes page: what changed after since, titles new or changed
    // and keys gone (of those the peer was ever sent), 5,000 keys looked at
    // at most; false when the log doesn't reach back that far.
    private async Task<bool> ChangesPageAsync(PeerRecord p, List<string> libs, Dictionary<string, string> pub, TitlesRequest input,
        TitlesOut output, List<string> sent, CancellationToken ct)
    {
        var present = new List<Title>();
        var gone = new List<string>();
        var last = "";
        bool more;
        if (ChangeLog() is { } cl)
        {
            ChangePage page;
            try
            {
                page = await cl.ChangesAsync(libs, input.Since, input.After, TitlesPage, ct).ConfigureAwait(false);
            }
            catch (MsfpException e) when (e.Code == MsfpError.TooOld)
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
            await _store.ReadAsync(async tx =>
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
        foreach (var t in present)
        {
            if (TitleOut(p, t, libs, pub) is { } w)
            {
                output.Titles.Add(w);
                sent.Add(t.Key);
            }
            else
            {
                gone.Add(t.Key);
            }
        }

        // gone names only titles the peer was told of (catalog section 3.2),
        // else it would learn of titles it never could see.
        if (gone.Count > 0)
        {
            IReadOnlyList<string> told = [];
            await _store.ReadAsync(async tx => told = await tx.SentAsync(p.Id, gone).ConfigureAwait(false), ct).ConfigureAwait(false);
            output.Gone = told.Order(StringComparer.Ordinal).ToList();
        }

        if (more && string.CompareOrdinal(last, input.After) > 0)
        {
            output.Next = last;
        }

        return true;
    }

    // A title of the host as sent to p: null when its key isn't canonical
    // (with a warning: the host's bug), of a content part off with p, or in
    // none of the libraries asked. Its library is the one asked whose
    // public ID comes first in byte order (catalog section 3).
    private Action<JsonOut>? TitleOut(PeerRecord p, Title t, List<string> libs, Dictionary<string, string> pub)
    {
        if (!TitleKey.TryParse(t.Key, out var k) || k.Kind != t.Kind)
        {
            _log.LogWarning("msfp: a title of the host's catalog has a key MSFP doesn't take; it isn't sent: {Key}", t.Key);
            return null;
        }

        if (!On(p, k.Part))
        {
            return null;
        }

        string? library = null;
        foreach (var l in t.Libraries ?? [])
        {
            if (libs.Contains(l) && pub.TryGetValue(l, out var id) && (library is null || string.CompareOrdinal(id, library) < 0))
            {
                library = id;
            }
        }

        if (library is null)
        {
            return null;
        }

        switch (t)
        {
            case VideoTitle v when k.Part == Versions.PartVideo:
                if (VideoOut(k, v, library) is { } vw)
                {
                    return vw;
                }

                _log.LogWarning("msfp: an episode of the host's catalog keyed by its TMDB ID has no place in TMDB's numbering; it isn't sent: {Key}", t.Key);
                break;
            case MusicTitle m when k.Part == Versions.PartMusic:
                if (MusicOut(m, library) is { } w)
                {
                    return w;
                }

                _log.LogWarning("msfp: a music title of the host's catalog has no name; it isn't sent: {Key}", t.Key);
                break;
        }

        return null;
    }

    // The host's video title as sent: its members cleaned as a receiver would
    // keep them, so nothing leaves that MSFP doesn't take; null for an
    // episode keyed by its TMDB ID without a valid place, which a receiver
    // would skip (video section 3.1).
    private static Action<JsonOut>? VideoOut(TitleKey k, VideoTitle t, string library)
    {
        IReadOnlyList<string>? audio = null, subtitles = null, with = null;
        Picture? picture = null;
        Place? place = null;
        if (k.Playable)
        {
            audio = Languages.SentTags(t.Audio ?? []);
            subtitles = Languages.SentTags(t.Subtitles ?? []);
            picture = Picture.Best(t.Pictures ?? []);
        }

        if (k.Kind == "episode" && VideoKey.TryParse(t.Key, out var vk))
        {
            with = Video.CleanWith(vk, t.With ?? []).Order(StringComparer.Ordinal).ToList();
            if (vk.ByEpisodeId)
            {
                if (t.Place is not { } pl || !new Protocol.Place(pl.Season, pl.Episode).Valid)
                {
                    return null;
                }

                place = pl;
            }
        }

        SortedDictionary<string, string>? ids = null;
        if (k.Kind != "season")
        {
            foreach (var (scheme, v) in t.Ids ?? [])
            {
                if (Video.OtherId(scheme, v))
                {
                    ids ??= new SortedDictionary<string, string>(StringComparer.Ordinal);
                    ids[scheme] = v;
                }
            }
        }

        return w => w.String("key", t.Key).String("kind", t.Kind).String("library", library)
            .Strings("audio", audio is { Count: > 0 } ? audio : null)
            .Strings("subtitles", subtitles is { Count: > 0 } ? subtitles : null)
            .Object("video", picture is null ? null : v => v.Integer("height", picture.Height).Strings("hdr", picture.Hdr is { Count: > 0 } ? picture.Hdr : null))
            .Object("place", place is null ? null : p => p.Integer("season", place.Season).Integer("episode", place.Episode))
            .Strings("with", with is { Count: > 0 } ? with : null)
            .Object("ids", ids is null ? null : o =>
            {
                foreach (var (scheme, v) in ids)
                {
                    o.String(scheme, v);
                }
            });
    }

    // The host's music title as sent, read back as a receiver reads it: what
    // a receiver would skip or drop isn't sent.
    private static Action<JsonOut>? MusicOut(MusicTitle t, string library)
    {
        Action<JsonOut> fill(string key, string kind, string name, IEnumerable<string>? artists, string? artistNames,
            string? album, int disc, int number, int year, long durationMs, IEnumerable<KeyValuePair<string, string>>? ids) =>
            w => w.String("key", key).String("kind", kind).String("library", library).String("name", name)
                .Strings("artists", artists?.Any() == true ? artists : null)
                .String("artistNames", string.IsNullOrEmpty(artistNames) ? null : artistNames)
                .String("album", string.IsNullOrEmpty(album) ? null : album)
                .Integer("disc", disc != 0 ? disc : null).Integer("number", number != 0 ? number : null)
                .Integer("year", year != 0 ? year : null).Integer("durationMs", durationMs != 0 ? durationMs : null)
                .Object("ids", ids?.Any() == true ? o =>
                {
                    foreach (var (scheme, v) in ids.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        o.String(scheme, v);
                    }
                }
                : null);

        var raw = JsonOut.Object(fill(t.Key, t.Kind, t.Name, t.Artists, t.ArtistNames, t.Album, t.Disc, t.Number, t.Year, t.DurationMs, t.Ids));
        if (Music.ReadTitle(raw) is not { } m)
        {
            return null;
        }

        return fill(m.Key, m.Kind, m.Name, m.Artists, m.ArtistNames, m.Album, m.Disc, m.Number, m.Year, m.DurationMs, m.Ids);
    }
}
