using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// The catalog, on the reading side (MSFP catalog sections 2.2, 3.2 and
// 3.3): the libraries a peer offers this server, those wanted, and the
// readings of their titles, kept in storage for the host's pages.
public sealed partial class MsfpEngine
{
    // The bounds of what is kept of a peer (catalog sections 2.2 and 6).
    private const int MaxLibraries = 200;
    private const int MaxPeerTitles = 1_000_000;
    private const int MaxGoneKey = 128;

    // How long a reading may take (catalog section 3.2: 5 minutes); the
    // longest a page is waited for when the peer asks to wait.
    private static readonly TimeSpan ReadingTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PageWait = TimeSpan.FromMinutes(1);

    // How long after the start the first readings go.
    private static readonly TimeSpan FirstReading = TimeSpan.FromMinutes(1);

    // The readings of peers' catalogs: one at a time per peer, each on its
    // schedule (in memory: after a restart, a minute after the start).
    private readonly Dictionary<string, SemaphoreSlim> _readingLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _nextReading = new(StringComparer.Ordinal);
    private long _readingNumber;

    private SemaphoreSlim ReadingLock(string id)
    {
        lock (_readingLocks)
        {
            if (!_readingLocks.TryGetValue(id, out var l))
            {
                l = new SemaphoreSlim(1, 1);
                _readingLocks[id] = l;
            }

            return l;
        }
    }

    // A number for a reading, above every one before.
    private long NewReading()
    {
        lock (_readingLocks)
        {
            var nanos = (Now() - DateTimeOffset.UnixEpoch).Ticks * 100;
            _readingNumber = Math.Max(_readingNumber + 1, nanos);
            return _readingNumber;
        }
    }

    // Has a peer's catalog read at the next tick.
    internal void ReadSoon(string id)
    {
        lock (_readingLocks)
        {
            _nextReading[id] = DateTimeOffset.MinValue;
        }
    }

    // Reads the catalogs due now: each paired peer that can be called, a
    // minute after the start, then every SyncEvery, give or take a tenth;
    // wait waits for them (RunOnceAsync).
    private async Task ReadDueAsync(bool wait, CancellationToken ct)
    {
        IReadOnlyList<PeerRecord> peers = [];
        await _store.ReadAsync(async tx => peers = await tx.PeersAsync().ConfigureAwait(false), ct).ConfigureAwait(false);
        var now = Now();
        var due = new List<string>();
        lock (_readingLocks)
        {
            foreach (var p in peers)
            {
                if (p.State != PeerState.Paired || p.Inbound != Inbound.Yes || p.Suspended is not null || !On(p, Versions.PartCatalog))
                {
                    continue;
                }

                if (!_nextReading.TryGetValue(p.Id, out var next))
                {
                    next = _startedAt + FirstReading;
                    _nextReading[p.Id] = next;
                }

                if (now >= next)
                {
                    var spread = _cfg.SyncEvery * (0.9 + (0.2 * Random.Shared.NextDouble()));
                    _nextReading[p.Id] = now + spread;
                    due.Add(p.Id);
                }
            }
        }

        var tasks = new List<Task>();
        foreach (var id in due)
        {
            async Task Run(CancellationToken c)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(c);
                cts.CancelAfter(ReadingTime);
                try
                {
                    await ReadCatalogAsync(id, true, cts.Token).ConfigureAwait(false);
                }
                catch (MsfpException e) when (e.Code != MsfpError.BackingOff)
                {
                    _log.LogInformation("msfp: a peer's catalog couldn't be read: {Peer} {Error}", id, e.Message);
                }
                catch (MsfpException)
                {
                }
            }

            if (wait)
            {
                tasks.Add(Run(CancellationToken.None));
            }
            else
            {
                Background(c => Run(CancellationToken.None));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // Checks that a peer can be asked for a part: paired, not suspended,
    // callable, with the part on.
    private void UsablePeer(PeerRecord p, string part)
    {
        if (p.State != PeerState.Paired)
        {
            throw new MsfpException(MsfpError.NotPaired);
        }

        if (p.Suspended is not null)
        {
            throw new MsfpException(MsfpError.Suspended);
        }

        if (p.Inbound != Inbound.Yes)
        {
            throw new MsfpException(MsfpError.Uncallable);
        }

        if (!On(p, part))
        {
            throw new MsfpException(MsfpError.NotSupported);
        }
    }

    private bool Usable(PeerRecord p, string part)
    {
        try
        {
            UsablePeer(p, part);
            return true;
        }
        catch (MsfpException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks a peer which libraries it offers this server now, and keeps them,
    /// those wanted marked (gone ones too, marked gone).
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotPaired"/>,
    /// <see cref="MsfpError.Uncallable"/>, <see cref="MsfpError.Suspended"/>, <see cref="MsfpError.NotSupported"/>,
    /// <see cref="MsfpError.Unreachable"/>, <see cref="MsfpError.BackingOff"/>.</exception>
    public async Task<IReadOnlyList<WantedLibrary>> TheirLibrariesAsync(string peer, CancellationToken ct = default)
    {
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        return await TheirLibrariesAsync(p, false, ct).ConfigureAwait(false);
    }

    // Calls libraries on p and keeps its answer: the libraries, its name,
    // its limits (catalog section 2.2).
    private async Task<List<WantedLibrary>> TheirLibrariesAsync(PeerRecord p, bool background, CancellationToken ct)
    {
        UsablePeer(p, Versions.PartCatalog);
        var r = await CallAsync(p, CallLibraries, "{}"u8.ToArray(), ct, background).ConfigureAwait(false);
        if (r.Status != 200)
        {
            throw Refused(p, r);
        }

        var ans = ReadAnswerBody(r.Body);
        string? name;
        JsonArray? list;
        try
        {
            name = ans.GetString("name");
            list = ans.GetArray("libraries");
        }
        catch (ProtocolException e)
        {
            throw new MsfpException(MsfpError.Unreachable, "a libraries answer of the wrong form", e);
        }

        var libs = new List<WantedLibrary>();
        foreach (var raw in list ?? [])
        {
            if (libs.Count == MaxLibraries)
            {
                break;
            }

            if (StrictJson.ReadEntry(raw) is not { } l)
            {
                continue;
            }

            string? id;
            try
            {
                id = l.GetString("id");
            }
            catch (ProtocolException)
            {
                continue;
            }

            if (id is null || !Playback.Id(id) || libs.Any(o => o.Id == id))
            {
                continue;
            }

            var n = CleanName(JsonMembers.AsString(l["name"]) ?? "", "");
            libs.Add(new WantedLibrary { Id = id, Name = n.Length > 0 ? n : "?" });
        }

        var limits = ReadLimits(ans["limits"], On(p, Versions.PartMusic));
        var events = new List<MsfpEvent>();
        var kept = new List<WantedLibrary>();
        await UpdatePeerAsync(p.Id, q =>
        {
            events.Clear();
            kept.Clear();
            if (name is not null && CleanName(name, "") is { Length: > 0 } cleaned)
            {
                q.Name = cleaned;
            }

            if (limits is not null && On(q, Versions.PartPlayback))
            {
                q.TheirLimits = limits;
            }

            var gone = new List<string>();
            var wants = q.Wants ?? [];
            foreach (var l in libs)
            {
                kept.Add(l with { Wanted = wants.Any(w => w.Id == l.Id && w.Wanted) });
            }

            foreach (var w in wants)
            {
                if (w.Wanted && !libs.Any(l => l.Id == w.Id))
                {
                    if (!w.Gone)
                    {
                        gone.Add(w.Id);
                    }

                    kept.Add(w with { Gone = true });
                }
            }

            q.Wants = [.. kept.Select(k => k with { })];
            if (gone.Count > 0)
            {
                events.Add(new WantsGone(q.Id, q.Name, gone));
            }
        }, ct).ConfigureAwait(false);
        Notify([.. events]);
        return kept;
    }

    // Reads a peer's limits (playback section 5, music section 5.1): null
    // when they aren't an object of integers in range. ScanBytes is never a
    // peer's limit: each scan's answer states its own
    // (RemotePlayback.ScanBytes).
    private static Limits? ReadLimits(JsonNode? raw, bool music)
    {
        if (raw is null || Playback.ReadLimits(raw.DeepClone()) is not { HasStreams: true, HasTranscodes: true } k)
        {
            return null;
        }

        var w = k.Limits;
        var l = new Limits
        {
            Streams = w.Streams, Transcodes = w.Transcodes, BitrateKbps = w.BitrateKbps, Downloads = w.Downloads,
            DownloadsPerDay = w.DownloadsPerDay, DownloadKbps = w.DownloadKbps, ScansPerDay = w.ScansPerDay,
        };
        if (music && k.HasAudioStreams && k.HasAudioTranscodes)
        {
            l.Audio = true;
            l.AudioStreams = w.AudioStreams;
            l.AudioTranscodes = w.AudioTranscodes;
        }

        try
        {
            l.Check();
            return l;
        }
        catch (MsfpException)
        {
            return null;
        }
    }

    // A peer's signed refusal as an error; a signed 404 that isn't
    // not-supported may mean it doesn't count us as paired yet: a hello
    // goes at once.
    private MsfpException Refused(PeerRecord p, Reply r)
    {
        var e = Problems.RefusalError(r.Status, r.Problem ?? "");
        if (e.Code == MsfpError.NotFound)
        {
            HelloNow(p.Id);
        }

        return e;
    }

    /// <summary>
    /// Lists the peer's libraries as last read, those wanted marked, those
    /// gone from its last answer marked gone.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task<IReadOnlyList<WantedLibrary>> WantsAsync(string peer, CancellationToken ct = default)
    {
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        return (p.Wants ?? []).Select(w => w with { }).ToList();
    }

    /// <summary>
    /// Sets which of a peer's libraries this server reads (their public IDs);
    /// IDs it doesn't offer are left out. A library not seen yet has the peer
    /// asked first (<see cref="TheirLibrariesAsync(string, CancellationToken)"/>).
    /// A reading follows at once.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="libraries">The peer's public library IDs.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotPaired"/>,
    /// and those of TheirLibrariesAsync.</exception>
    public async Task SetWantsAsync(string peer, IEnumerable<string> libraries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var wanted = libraries.ToHashSet(StringComparer.Ordinal);
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        if (p.State != PeerState.Paired)
        {
            throw new MsfpException(MsfpError.NotPaired);
        }

        if (wanted.Any(id => !(p.Wants ?? []).Any(w => w.Id == id)))
        {
            await TheirLibrariesAsync(p, false, ct).ConfigureAwait(false);
        }

        await UpdatePeerAsync(peer, q =>
        {
            var kept = new List<WantedLibrary>();
            foreach (var w in q.Wants ?? [])
            {
                var x = w with { Wanted = wanted.Contains(w.Id) };
                if (x.Wanted || !x.Gone)
                {
                    kept.Add(x);
                }
            }

            q.Wants = kept;
        }, ct).ConfigureAwait(false);
        ReadSoon(peer);
    }

    /// <summary>Reads a peer's catalog now, and waits for it: how many titles the reading kept.</summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotPaired"/>,
    /// <see cref="MsfpError.Uncallable"/>, <see cref="MsfpError.Suspended"/>, <see cref="MsfpError.NotSupported"/>,
    /// <see cref="MsfpError.Unreachable"/>, <see cref="MsfpError.BackingOff"/>, and a peer's refusals.</exception>
    public Task<int> ReadNowAsync(string peer, CancellationToken ct = default) => ReadCatalogAsync(peer, false, ct);

    // A titles answer as read: each title and each gone key on its own, so
    // that a bad one is skipped alone; the other members of the wrong type
    // fail the answer (catalog section 3.3).
    private sealed record TitlesReading(JsonArray Titles, string? Next, bool Changes, JsonArray? Gone, long? Seq);

    private static TitlesReading ReadTitlesAnswer(byte[] body)
    {
        var ans = ReadAnswerBody(body);
        try
        {
            return new TitlesReading(ans.GetArray("titles") ?? [], ans.GetString("next"), ans.GetBool("changes") ?? false,
                ans.GetArray("gone"), ans.GetInteger("seq"));
        }
        catch (ProtocolException e)
        {
            throw new MsfpException(MsfpError.Unreachable, "a titles answer of the wrong form", e);
        }
    }

    // Reads a peer's titles in the libraries wanted of it, and keeps them
    // (catalog section 3.2): only what changed since the last reading, when
    // the peer gives changes and the libraries are the same; the whole list
    // otherwise, after which titles not seen are dropped. A reading that
    // fails keeps what was kept before it: a whole list is held in memory
    // until its last page, then kept in one Update.
    private async Task<int> ReadCatalogAsync(string id, bool background, CancellationToken ct)
    {
        var l = ReadingLock(id);
        await l.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var p = await PeerRecordAsync(id, ct).ConfigureAwait(false);
            UsablePeer(p, Versions.PartCatalog);
            var kept = 0;
            var changes = false;
            try
            {
                kept = await ReadTitlesAsync(p, background, n => kept = n, c => changes = c, ct).ConfigureAwait(false);
                Notify(new CatalogReadEnded(p.Id, p.Name, kept, changes, null));
                return kept;
            }
            catch (Exception e) when (e is not MsfpException { Code: MsfpError.BackingOff })
            {
                Notify(new CatalogReadEnded(p.Id, p.Name, kept, changes, e));
                throw;
            }
        }
        finally
        {
            l.Release();
        }
    }

    private async Task<int> ReadTitlesAsync(PeerRecord p, bool background, Action<int> counted, Action<bool> sawChanges, CancellationToken ct)
    {
        var id = p.Id;
        var libs = new List<string>();
        // What is read: what this server shows of it individually, while the
        // individual relation holds, and what its groups show of it.
        var individual = Individual(p);
        var (shows, grouped) = await ShownByAsync(p, ct).ConfigureAwait(false);
        if (grouped || (individual && (p.Wants ?? []).Any(w => w.Wanted)))
        {
            foreach (var w in await TheirLibrariesAsync(p, background, ct).ConfigureAwait(false))
            {
                if (!w.Gone && ((w.Wanted && individual) || (grouped && shows(w.Id))))
                {
                    libs.Add(w.Id);
                }
            }
        }

        libs.Sort(StringComparer.Ordinal);
        var reading = NewReading();
        if (libs.Count == 0)
        {
            // Nothing wanted that it offers: nothing of it is kept.
            await DropTitlesAsync(id, reading, ct).ConfigureAwait(false);
            return 0;
        }

        var since = (p.CatalogRead.Wants ?? []).SequenceEqual(libs, StringComparer.Ordinal) ? p.CatalogRead.Seq : 0;
        long seq = 0;
        var whole = new List<Title>(); // a whole list's titles, kept once read to its end
        var first = true;
        var changes = false;
        var kept = 0;
        for (var after = ""; ;)
        {
            var body = JsonOut.Object(o => o.Strings("libraries", libs).String("after", after.Length > 0 ? after : null)
                .Integer("since", since > 0 ? since : null));
            var r = await PageCallAsync(p, body, background, ct).ConfigureAwait(false);
            if (r.Status != 200)
            {
                throw Refused(p, r);
            }

            var ans = ReadTitlesAnswer(r.Body);
            if (first)
            {
                changes = ans.Changes;
                sawChanges(changes);
                first = false;
                if (ans.Seq is > 0)
                {
                    seq = ans.Seq.Value;
                }
            }
            else if (ans.Changes != changes)
            {
                throw new MsfpException(MsfpError.Unreachable, "a reading mixing changes and the whole list");
            }

            if (ans.Titles.Count + (ans.Gone?.Count ?? 0) > TitlesPage)
            {
                throw new MsfpException(MsfpError.Unreachable, $"a page of more than {TitlesPage} keys");
            }

            var page = new List<Title>();
            foreach (var raw in ans.Titles)
            {
                if (ReadTitle(p, raw, libs) is { } t)
                {
                    page.Add(t);
                }
            }

            var gone = new List<string>();
            if (changes)
            {
                foreach (var raw in ans.Gone ?? [])
                {
                    if (JsonMembers.AsString(raw) is { Length: <= MaxGoneKey } k)
                    {
                        gone.Add(k);
                    }
                }
            }

            kept += page.Count;
            counted(kept);
            if (kept > MaxPeerTitles)
            {
                throw new MsfpException(MsfpError.Unreachable, $"more than {MaxPeerTitles} titles");
            }

            if (!changes)
            {
                // A whole list is kept only once read to its end: until then
                // the previous one stays (catalog section 3.2).
                whole.AddRange(page);
            }
            else
            {
                await _store.UpdateAsync(async tx =>
                {
                    if (page.Count > 0)
                    {
                        await tx.PutPeerTitlesAsync(id, reading, page).ConfigureAwait(false);
                    }

                    if (gone.Count > 0)
                    {
                        await tx.DropPeerTitlesAsync(id, gone).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(ans.Next) || string.CompareOrdinal(ans.Next, after) <= 0)
            {
                break;
            }

            after = ans.Next;
        }

        await _store.UpdateAsync(async tx =>
        {
            var q = await tx.PeerAsync(id).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.NotFound);
            if (!changes)
            {
                foreach (var chunk in whole.Chunk(TitlesPage))
                {
                    await tx.PutPeerTitlesAsync(id, reading, chunk).ConfigureAwait(false);
                }

                await tx.EndReadingAsync(id, reading).ConfigureAwait(false);
            }

            q.CatalogRead = new CatalogRead { Seq = seq, Wants = libs.Count > 0 ? [.. libs] : null };
            await tx.PutPeerAsync(q).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return kept;
    }

    // Asks for a page; asked to wait a moment (a signed 429 or 503 with
    // retryAfter of a minute at most), the same page once it's time.
    private async Task<Reply> PageCallAsync(PeerRecord p, byte[] body, bool background, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var r = await CallAsync(p, CallTitles, body, ct, background).ConfigureAwait(false);
            if (r.Status == 200 || attempt == 2 || r.RetryAfter <= 0 || TimeSpan.FromSeconds(r.RetryAfter) > PageWait)
            {
                return r;
            }

            await Task.Delay(TimeSpan.FromSeconds(r.RetryAfter), ct).ConfigureAwait(false);
        }
    }

    // Reads a title of a peer's answer as catalog section 3.3 and its
    // content part say; null to skip it.
    private Title? ReadTitle(PeerRecord p, JsonNode? raw, List<string> libs)
    {
        if (raw is not JsonObject obj || JsonMembers.AsString(obj["key"]) is not { } key || !KeyOn(p, key, out var k))
        {
            return null;
        }

        if (k.Part == Versions.PartVideo)
        {
            if (Video.ReadTitle(raw.DeepClone()) is not { } v || !libs.Contains(v.Library))
            {
                return null;
            }

            return new VideoTitle
            {
                Key = v.Key, Kind = v.Kind, Libraries = [v.Library],
                Copies = v.Copies.Count > 0 ? [.. v.Copies] : null,
                With = v.With.Count > 0 ? [.. v.With] : null,
                Ids = v.Ids.Count > 0 ? new Dictionary<string, string>(v.Ids) : null,
                Place = v.Place is { } pl ? new Place { Season = pl.Season, Episode = pl.Episode } : null,
                ExtraType = v.ExtraType.Length > 0 ? v.ExtraType : null,
                Name = v.Description?.Name is { Length: > 0 } name ? name : null,
                Year = v.Description?.Year ?? 0,
                Image = v.Description?.Image ?? false,
            };
        }

        if (k.Part == Versions.PartMusic)
        {
            if (Music.ReadTitle(raw.DeepClone()) is not { } m || !libs.Contains(m.Library))
            {
                return null;
            }

            return new MusicTitle
            {
                Key = m.Key, Kind = m.Kind, Libraries = [m.Library], Name = m.Name,
                Artists = m.Artists.Count > 0 ? [.. m.Artists] : null,
                ArtistNames = m.ArtistNames.Length > 0 ? m.ArtistNames : null,
                PrimaryType = m.PrimaryType.Length > 0 ? m.PrimaryType : null,
                SecondaryTypes = m.SecondaryTypes.Count > 0 ? [.. m.SecondaryTypes] : null,
                Compilation = m.Compilation,
                Date = m.Date.Length > 0 ? m.Date : null,
                OriginalDate = m.OriginalDate.Length > 0 ? m.OriginalDate : null,
                Genres = m.Genres.Count > 0 ? [.. m.Genres] : null,
                Album = m.Album.Length > 0 ? m.Album : null,
                Disc = m.Disc, Number = m.Number,
                Copies = m.Copies.Count > 0 ? [.. m.Copies] : null,
                Image = m.Image == true,
                Ids = m.Ids.Count > 0 ? new Dictionary<string, string>(m.Ids) : null,
            };
        }

        return null;
    }

    // Drops what is kept of a peer's titles: nothing wanted, it can't be
    // called, or isn't paired, any more.
    private async Task DropTitlesAsync(string id, long reading, CancellationToken ct) =>
        await _store.UpdateAsync(async tx =>
        {
            if (await tx.PeerAsync(id).ConfigureAwait(false) is not { } q)
            {
                return;
            }

            q.CatalogRead = new CatalogRead();
            await tx.PutPeerAsync(q).ConfigureAwait(false);
            await tx.EndReadingAsync(id, reading).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    private async Task DropCatalogAsync(string id, CancellationToken ct)
    {
        try
        {
            await DropTitlesAsync(id, NewReading(), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: a peer's titles couldn't be dropped: {Peer}", id);
        }
    }

    /// <summary>
    /// Whether this server's people may be offered a peer's titles of a kind
    /// ("movie", "episode", "track"…) now: the peer is paired, not suspended,
    /// can be called, and playback, media and the title's content part are on
    /// with it (engine.md section 3.3).
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="kind">The title's kind.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, and the store's: an error is never a plain false.</exception>
    public async Task<bool> PlayableAsync(string peer, string kind, CancellationToken ct = default)
    {
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        var part = kind is "artist" or "album" or "track" ? Versions.PartMusic : Versions.PartVideo;
        return Usable(p, Versions.PartPlayback) && On(p, Versions.PartMedia) && On(p, part);
    }
}
