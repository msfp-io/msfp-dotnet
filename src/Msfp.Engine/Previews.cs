using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// A peer's copy's seek-bar previews, fetched before a first play: their
/// layout, and each sheet, checked and re-encoded as a JPEG (sheet n at
/// index n).
/// </summary>
/// <param name="Trickplay">The layout.</param>
/// <param name="Sheets">The sheets, re-encoded.</param>
public sealed record PreviewSheets(Trickplay Trickplay, IReadOnlyList<byte[]> Sheets);

// The previews call (MSFP video section 5.6) and the lyrics call (music
// section 6), both ways: a copy's seek-bar previews before a first play,
// under a short ticket of their own that is no playback; a track's lyrics,
// with no ticket at all.
public sealed partial class MsfpEngine
{
    internal const string CallPreviews = "previews";
    internal const string CallLyrics = "lyrics";

    // Answers a paired peer's previews call: the copy's trickplay object, its
    // sheets under a ticket that opens them only, for 10 minutes.
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

        if (c is not { Previews: true })
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        CopyInfo info;
        try
        {
            info = await _cfg.Playback.DescribeAsync(c.Handle, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HostRefusal(e, "describe");
        }

        // Sheets are re-encoded before they leave: without a SheetEncoder,
        // there are none to give.
        if (TrickplayOf(info.Trickplay) is not { } t || _cfg.SheetEncoder is null)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        var ticket = Base64UrlText.Encode(RandomBytes(32));
        AddImageTicket(ticket, new ImageTicket(TicketPreviews, p.Id, @in.Title, Now() + ImageTicketTime, c.Handle, t.Sheets,
            t.Columns * t.TileWidth, t.Rows * t.TileHeight));
        var streamBase = StreamBase(req) + ticket + "/";
        return Answer.Ok(JsonOut.Object(o => o.Object("trickplay", w => WriteSheets(w, t, streamBase))));
    }

    // The copy of that ID of a title the peer may play here, as the catalog
    // sends it; null for none.
    private async Task<OfferedCopy?> OfferedCopyAsync(PeerRecord p, string key, string id, CancellationToken ct)
    {
        var (copies, _) = await OfferedCopiesAsync(p, key, ct).ConfigureAwait(false);
        return copies.FirstOrDefault(c => c.Id == id);
    }

    // Serves a sheet under a previews ticket: only its copy's sheets, while
    // the title is still sent to the peer (media section 4).
    private async Task<StreamOutcome> ServePreviewSheetAsync(MsfpRequest request, ImageTicket it, string name, IResponseSink output,
        CancellationToken ct)
    {
        if (!PlainMediaRequest(request) || !MediaPath.TryParse(name, out var path) || path.Kind != MediaKind.Sheet || path.Sheet >= it.Sheets ||
            _cfg.Media is null)
        {
            return StreamOutcome.NotFound;
        }

        try
        {
            var p = await PeerRecordAsync(it.Peer, ct).ConfigureAwait(false);
            if (p.State != PeerState.Paired || p.Suspended is not null || !On(p, Versions.PartVideo))
            {
                return StreamOutcome.NotFound;
            }

            var (copies, _) = await OfferedCopiesAsync(p, it.Key, ct).ConfigureAwait(false);
            if (!copies.Any(c => c.Handle == it.Handle && c.Previews))
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
            return await ServeSheetAsync(it.Handle, new MediaPath { Kind = MediaKind.Sheet, Sheet = path.Sheet, OfCopy = true },
                it.SheetWidth, it.SheetHeight, output, ct).ConfigureAwait(false);
        }
        finally
        {
            free();
        }
    }

    // Answers a paired peer's lyrics call (music section 6): the host's
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
            sets = await host.LyricsAsync(c.Handle, ct).ConfigureAwait(false);
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

    // The lock of the image and previews calls made, by peer.
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
    /// Asks a peer for the seek-bar previews of one of its copies kept here
    /// whose description says it has some (MSFP video section 5.6), and
    /// fetches every sheet at once under the answer's ticket, each checked
    /// and re-encoded, so that a viewer sees them from the first play. The
    /// host keeps them while the copy's etag holds, and asks again only when
    /// it changes. One call at a time per peer.
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="key">The title's key.</param>
    /// <param name="versionId">The copy's ID.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException">
    /// <see cref="MsfpError.NotFound"/> (no such copy kept, none with
    /// previews, or the peer's refusal), <see cref="MsfpError.NotPaired"/>,
    /// <see cref="MsfpError.Uncallable"/>, <see cref="MsfpError.Suspended"/>,
    /// <see cref="MsfpError.NotSupported"/> (a part off, or no
    /// <see cref="MsfpConfig.SheetEncoder"/>), <see cref="MsfpError.Unreachable"/>
    /// (an answer or a sheet refused), <see cref="MsfpError.RateLimited"/>,
    /// <see cref="MsfpError.BackingOff"/>, <see cref="MsfpError.ServerBusy"/>.
    /// </exception>
    public async Task<PreviewSheets> PreviewsAsync(string peer, string key, string versionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(versionId);
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        UsablePeer(p, Versions.PartCatalog);
        if (!On(p, Versions.PartMedia) || !On(p, Versions.PartVideo) || _cfg.SheetEncoder is not { } encoder)
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        // P17b: the layout is the catalog copy's previewLayout since proposal 17.
        if (await KeptTitleAsync(peer, key, ct).ConfigureAwait(false) is not VideoTitle v ||
            (v.Copies ?? []).FirstOrDefault(c => c.Id == versionId)?.PreviewLayout is not { } tp)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var gate = ImageLock(peer);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await CallAsync(p, CallPreviews, JsonOut.Object(o => o.String("title", key).String("versionId", versionId)), ct)
                .ConfigureAwait(false);
            if (r.Status != 200)
            {
                throw Refused(p, r);
            }

            PreviewsAnswer pv;
            try
            {
                pv = Video.ReadPreviewsAnswer(r.Body); // P17b: {"ticket", "etag"} since proposal 17
            }
            catch (ProtocolException e)
            {
                throw new MsfpException(MsfpError.Unreachable, e.Message, e);
            }

            var root = PeerStreamRoot(p);
            int w = tp.Columns * tp.TileWidth, h = tp.Rows * tp.TileHeight;
            var sheets = new List<byte[]>();
            foreach (var u in Enumerable.Range(0, tp.Sheets).Select(n => MediaUrls.Sheet(root, pv.Ticket, n)))
            {
                var sheet = await FetchWholeAsync(p, u, PreviewLayout.MaxSheetBytes, ct).ConfigureAwait(false);
                if (!Jpeg.Fits(sheet, w, h))
                {
                    throw new MsfpException(MsfpError.Unreachable, "a sheet that isn't a JPEG within its layout");
                }

                try
                {
                    sheets.Add(await encoder.ReencodeAsync(sheet, w, h, ct).ConfigureAwait(false));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new MsfpException(MsfpError.Unreachable, e.Message, e);
                }
            }

            return new PreviewSheets(new Trickplay
            {
                TileWidth = tp.TileWidth, TileHeight = tp.TileHeight, Columns = tp.Columns, Rows = tp.Rows, IntervalMs = tp.IntervalMs,
                TileCount = tp.TileCount, Sheets = tp.Sheets,
            }, sheets);
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
    /// description says it has some (MSFP music section 6), cleaned as music
    /// section 6 says (<see cref="Music.ReadLyrics"/>). The host keeps them
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
