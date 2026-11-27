using System.Globalization;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// The sheets fetched of peers' copies, re-encoded, keyed by peer, title,
/// copy, etag, layout and sheet (MSFP Media video 5.5: caching recommended,
/// keyed by the copy's etag and its previewLayout), within 64 MiB, the first
/// kept dropped first; and the previews tickets of copies, each used until
/// it expires. Locked on itself.
/// </summary>
internal sealed class SheetCache
{
    public const int MostBytes = 64 << 20;

    private readonly Dictionary<string, byte[]> _sheets = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private int _bytes;

    public Dictionary<string, (string Ticket, string Etag, DateTimeOffset Expires)> Tickets { get; } = new(StringComparer.Ordinal);

    public byte[]? Get(string key)
    {
        lock (this)
        {
            return _sheets.GetValueOrDefault(key);
        }
    }

    public void Put(string key, byte[] b)
    {
        lock (this)
        {
            if (_sheets.ContainsKey(key) || b.Length > MostBytes)
            {
                return;
            }

            while (_bytes + b.Length > MostBytes && _order.TryDequeue(out var old))
            {
                _bytes -= _sheets[old].Length;
                _sheets.Remove(old);
            }

            _sheets[key] = b;
            _order.Enqueue(key);
            _bytes += b.Length;
        }
    }
}

// The previews call (MSFP Media video 5.6) and the lyrics call (MSFP
// Media music 6), both ways: a ticket for a copy's seek-bar previews outside a
// playback, which is no playback; a track's lyrics, with no ticket at all.
public sealed partial class MsfpEngine
{
    internal const string CallPreviews = "previews";
    internal const string CallLyrics = "lyrics";

    private readonly SheetCache _sheets = new();

    // Answers a paired peer's previews call (MSFP Media video 5.6): a ticket that
    // opens the copy's sheets only, for 10 minutes, and the copy's etag as
    // the host holds it now; 404 for a copy without a previewLayout in the
    // catalog. The layout is the catalog's: the answer carries no URL.
    private async Task<Answer> PreviewsAsync(CallRequest req, CancellationToken ct)
    {
        var p = req.Peer;
        if (p is null || p.State != PeerState.Paired)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        if (!On(p, Versions.PartVideo))
        {
            return Answer.Refuse(ProblemType.NotSupported);
        }

        PreviewsRequest @in;
        try
        {
            @in = Video.ReadPreviewsRequest(req.Body);
        }
        catch (ProtocolException)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (_cfg.Playback is null || _cfg.Media is null)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        OfferedCopy? c;
        try
        {
            c = await OfferedCopyAsync(p, @in.Title, @in.VersionId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the host's title couldn't be read for its previews");
            return Answer.Refuse(ProblemType.Internal);
        }

        if (c?.Copy.PreviewLayout is not { } t)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        var ticket = Base64UrlText.Encode(RandomBytes(32));
        AddImageTicket(ticket, new ImageTicket(TicketPreviews, p.Id, @in.Title, Now() + ImageTicketTime, c.Handle, t.Sheets,
            t.Columns * t.TileWidth, t.Rows * t.TileHeight));
        return Answer.Ok(Video.WritePreviewsAnswer(new PreviewsAnswer(ticket, c.Copy.ETag)));
    }

    // The copy of that ID of a title the peer may play here, as the catalog
    // sends it; null for none.
    private async Task<OfferedCopy?> OfferedCopyAsync(PeerRecord p, string key, string id, CancellationToken ct)
    {
        var (copies, _) = await OfferedCopiesAsync(p, key, ct).ConfigureAwait(false);
        return copies.FirstOrDefault(c => c.Id == id);
    }

    // Serves a sheet under a previews ticket: only its copy's sheets, below
    // the sheet count of its layout, while the title is still sent to the
    // peer with that layout (MSFP Media fetching 4.2).
    private async Task<StreamOutcome> ServePreviewSheetAsync(MsfpRequest request, ImageTicket it, string name, IResponseSink output,
        CancellationToken ct)
    {
        if (!PlainMediaRequest(request) || !MediaPath.TryParse(name, out var path) || path.Kind != MediaKind.Sheet || path.Sheet >= it.Sheets ||
            _cfg.Media is null)
        {
            return StreamOutcome.NotFound;
        }

        PeerRef peer;
        try
        {
            var p = await PeerRecordAsync(it.Peer, ct).ConfigureAwait(false);
            peer = PeerRefOf(p);
            if (p.State != PeerState.Paired || p.Suspended is not null || !On(p, Versions.PartVideo))
            {
                return StreamOutcome.NotFound;
            }

            var (copies, _) = await OfferedCopiesAsync(p, it.Key, ct).ConfigureAwait(false);
            if (!copies.Any(c => c.Handle == it.Handle && c.Copy.PreviewLayout?.Sheets == it.Sheets))
            {
                return StreamOutcome.NotFound;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return StreamOutcome.NotFound;
        }

        var free = await _peerFetchSlots.TakeAsync(it.Peer, ct).ConfigureAwait(false);
        if (free is null)
        {
            return Busy(output, ct);
        }

        try
        {
            return await ServeSheetAsync(peer, it.Handle, new MediaPath { Kind = MediaKind.Sheet, Sheet = path.Sheet, OfCopy = true },
                it.SheetWidth, it.SheetHeight, output, ct).ConfigureAwait(false);
        }
        finally
        {
            free();
        }
    }

    // Answers a paired peer's lyrics call (MSFP Media music 6): the host's
    // lyrics of a copy whose catalog description says it has some, cleaned
    // as a viewer's server keeps them.
    private async Task<Answer> LyricsAsync(CallRequest req, CancellationToken ct)
    {
        var p = req.Peer;
        if (p is null || p.State != PeerState.Paired)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        if (!On(p, Versions.PartMusic))
        {
            return Answer.Refuse(ProblemType.NotSupported);
        }

        if (!Music.TryReadLyricsRequest(req.Body, out var @in) || @in is null)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (_cfg.Catalog is not ILyrics host)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        OfferedCopy? c;
        try
        {
            c = await OfferedCopyAsync(p, @in.Title, @in.VersionId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the host's title couldn't be read for its lyrics");
            return Answer.Refuse(ProblemType.Internal);
        }

        if (c is not { Lyrics.Length: > 0 })
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        IReadOnlyList<LyricsSet> sets;
        try
        {
            sets = await host.LyricsAsync(PeerRefOf(p), c.Handle, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HostRefusal(e, "lyrics");
        }

        if (Music.ReadLyrics(Music.WriteLyrics(sets)) is not { } kept)
        {
            _log.LogWarning("msfp: the host's lyrics are outside what MSFP allows; they aren't sent");
            return Answer.Refuse(ProblemType.NotFound);
        }

        return Answer.Ok(Music.WriteLyrics(kept));
    }

    // A peer's title as this server keeps it; null for none.
    private async Task<Title?> KeptTitleAsync(string peer, string key, CancellationToken ct)
    {
        Title? t = null;
        await _store.ReadAsync(async tx => t = await tx.PeerTitleAsync(peer, key).ConfigureAwait(false), ct).ConfigureAwait(false);
        return t;
    }

    // The lock of the image calls made, by peer, and of the previews calls,
    // by copy.
    private SemaphoreSlim ImageLock(string peer)
    {
        lock (_imageLocks)
        {
            if (!_imageLocks.TryGetValue(peer, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _imageLocks[peer] = gate;
            }

            return gate;
        }
    }

    /// <summary>
    /// Fetches sheet <paramref name="n"/> of the seek-bar previews of a peer's
    /// copy kept here with a previewLayout (MSFP Media video 5.5), checked
    /// against that layout and re-encoded as a JPEG. Previews are fetched on
    /// demand, for a copy someone opened (its page, its chapters, a playback,
    /// a scrub), never for a whole library nor in bulk for copies nobody
    /// opened: call it only so (fetching a copy's sheets together hides where
    /// the viewer is). During a live playback of the copy the playback's
    /// ticket opens its sheets; outside one, a previews call is made once per
    /// copy and its ticket used for all viewers until it expires, at most one
    /// call per copy at a time. Sheets fetched are kept (in memory, within 64
    /// MiB) while the copy's etag and layout hold.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="key">The title's key.</param>
    /// <param name="versionId">The copy's ID.</param>
    /// <param name="n">The sheet, from 0 to its layout's sheet count − 1.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException">
    /// <see cref="MsfpError.NotFound"/> (no such copy kept, no layout, a sheet
    /// past its count, or the peer's refusal), <see cref="MsfpError.NotPaired"/>,
    /// <see cref="MsfpError.Uncallable"/>, <see cref="MsfpError.Suspended"/>,
    /// <see cref="MsfpError.NotSupported"/> (a part off, or no
    /// <see cref="MsfpConfig.SheetEncoder"/>), <see cref="MsfpError.CopyChanged"/>
    /// (the previews answer's etag isn't the catalog's: the title is read
    /// again), <see cref="MsfpError.Unreachable"/> (an answer or a sheet
    /// refused), <see cref="MsfpError.RateLimited"/>, <see cref="MsfpError.BackingOff"/>,
    /// <see cref="MsfpError.ServerBusy"/>.
    /// </exception>
    public async Task<byte[]> PreviewSheetAsync(string peer, string key, string versionId, int n, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(versionId);
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        UsablePeer(p, Versions.PartCatalog);
        if (!On(p, Versions.PartMedia) || !On(p, Versions.PartVideo) || _cfg.SheetEncoder is not { } encoder)
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        if (await CatalogCopyAsync(peer, key, versionId, ct).ConfigureAwait(false) is not { PreviewLayout: { } l } c || n < 0 || n >= l.Sheets)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var cacheKey = string.Join('\n', peer, key, versionId, c.ETag, l.ToString(), n.ToString(CultureInfo.InvariantCulture));
        if (_sheets.Get(cacheKey) is { } kept)
        {
            return kept;
        }

        string url;
        if (PlayingCopy(peer, key, versionId) is { } rm)
        {
            url = rm.Base + "trickplay/" + n.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            var ticket = await PreviewsTicketAsync(p, key, versionId, c.ETag, ct).ConfigureAwait(false);
            url = MediaUrls.Sheet(PeerStreamRoot(p), ticket, n);
        }

        var sheet = await FetchWholeAsync(p, url, PreviewLayout.MaxSheetBytes, ct).ConfigureAwait(false);
        int w = l.Columns * l.TileWidth, h = l.Rows * l.TileHeight;
        if (!Jpeg.Fits(sheet, w, h))
        {
            throw new MsfpException(MsfpError.Unreachable, "a sheet that isn't a JPEG within its layout");
        }

        byte[] again;
        try
        {
            again = await encoder.ReencodeAsync(sheet, w, h, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new MsfpException(MsfpError.Unreachable, e.Message, e);
        }

        _sheets.Put(cacheKey, again);
        return again;
    }

    // A live playback on a peer of that copy whose ticket opens its sheets;
    // null for none.
    private RemotePlay? PlayingCopy(string peer, string key, string versionId)
    {
        lock (_remotes)
        {
            return _remotes.Values.FirstOrDefault(rm => rm.Peer == peer && rm.View.Title == key && rm.View.VersionId == versionId && rm.Layout is not null);
        }
    }

    // A previews ticket of a peer's copy: the one kept while it works, else a
    // new previews call's, one call per copy at a time (MSFP Media video 5.6).
    private async Task<string> PreviewsTicketAsync(PeerRecord p, string key, string versionId, string etag, CancellationToken ct)
    {
        var id = p.Id + "\n" + key + "\n" + versionId;
        var gate = ImageLock("previews\n" + id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sheets)
            {
                if (_sheets.Tickets.TryGetValue(id, out var t) && Now() < t.Expires && t.Etag == etag)
                {
                    return t.Ticket;
                }
            }

            var r = await CallAsync(p, CallPreviews, JsonOut.Object(o => o.String("title", key).String("versionId", versionId)), ct)
                .ConfigureAwait(false);
            if (r.Status != 200)
            {
                throw Refused(p, r);
            }

            PreviewsAnswer a;
            try
            {
                a = Video.ReadPreviewsAnswer(r.Body);
            }
            catch (ProtocolException e)
            {
                throw new MsfpException(MsfpError.Unreachable, e.Message, e);
            }

            if (a.ETag != etag)
            {
                // The copy changed: the title is read again before any sheet.
                ReadSoon(p.Id);
                throw new MsfpException(MsfpError.CopyChanged);
            }

            lock (_sheets)
            {
                var now = Now();
                foreach (var (k, old) in _sheets.Tickets.ToList())
                {
                    if (now >= old.Expires)
                    {
                        _sheets.Tickets.Remove(k);
                    }
                }

                // A little short of the ticket's 10 minutes, for the time a fetch takes.
                _sheets.Tickets[id] = (a.Ticket, a.ETag, now + ImageTicketTime - TimeSpan.FromSeconds(30));
            }

            return a.Ticket;
        }
        finally
        {
            gate.Release();
        }
    }

    // Fetches a URL of a peer's stream whole, within most bytes and the time
    // of a small read: a plain GET, nothing coded.
    private async Task<byte[]> FetchWholeAsync(PeerRecord p, string url, int most, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SmallReadTime);
        try
        {
            using var res = await RelayGetAsync(p, url, "", cts.Token).ConfigureAwait(false);
            var headers = new ResponseHeaders(res);
            if (headers.GetValues("Content-Encoding").Any(c => !c.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                throw new MsfpException(MsfpError.Unreachable, "a coded answer");
            }

            switch ((int)res.StatusCode)
            {
                case 200:
                    break;
                case 503:
                    throw new MsfpException(MsfpError.ServerBusy);
                default:
                    throw new MsfpException(MsfpError.NotFound);
            }

            if (res.Content.Headers.ContentLength > most)
            {
                throw new MsfpException(MsfpError.Unreachable, "an answer over its bound");
            }

            return await ReadAtMostAsync(await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), most, cts.Token).ConfigureAwait(false)
                ?? throw new MsfpException(MsfpError.Unreachable, "an answer over its bound");
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new MsfpException(MsfpError.Unreachable, e.Message, e);
        }
    }

    /// <summary>
    /// Asks a peer for the lyrics of one of its tracks' copies kept here whose
    /// description says it has some (MSFP Media music 6), cleaned as MSFP
    /// Media music 6 says (<see cref="Music.ReadLyrics"/>). The host keeps them
    /// while the copy's etag holds.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="key">The track's key.</param>
    /// <param name="versionId">The copy's ID.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException">
    /// <see cref="MsfpError.NotFound"/> (no such copy kept, none with lyrics,
    /// or the peer's refusal), <see cref="MsfpError.NotPaired"/>,
    /// <see cref="MsfpError.Uncallable"/>, <see cref="MsfpError.Suspended"/>,
    /// <see cref="MsfpError.NotSupported"/>, <see cref="MsfpError.Unreachable"/>
    /// (an answer refused), <see cref="MsfpError.RateLimited"/>,
    /// <see cref="MsfpError.BackingOff"/>.
    /// </exception>
    public async Task<IReadOnlyList<LyricsSet>> TrackLyricsAsync(string peer, string key, string versionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(versionId);
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        UsablePeer(p, Versions.PartCatalog);
        if (!On(p, Versions.PartMusic))
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        if (await KeptTitleAsync(peer, key, ct).ConfigureAwait(false) is not MusicTitle m ||
            !(m.Copies ?? []).Any(c => c.Id == versionId && c.Lyrics.Length > 0))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var r = await CallAsync(p, CallLyrics, new LyricsRequest(key, versionId).ToJson(), ct).ConfigureAwait(false);
        if (r.Status != 200)
        {
            throw Refused(p, r);
        }

        return Music.ReadLyrics(r.Body) ?? throw new MsfpException(MsfpError.Unreachable, "a lyrics answer outside what MSFP allows");
    }
}
