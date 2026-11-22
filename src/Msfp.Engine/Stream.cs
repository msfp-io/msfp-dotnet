using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>What <see cref="MsfpEngine.HandleStreamAsync"/> did with a request.</summary>
public enum StreamOutcome
{
    /// <summary>The engine answered it, through the sink.</summary>
    Served,

    /// <summary>
    /// Refused, nothing written: the host answers its own not-found page, as
    /// for any path that doesn't exist (MSFP core section 3).
    /// </summary>
    NotFound,

    /// <summary>
    /// Not a ticket of the engine's (one the host gave its own people, or one
    /// that ended), nothing written: the host's to answer, as its own route
    /// would (Go's fallback handler).
    /// </summary>
    Fallback,
}

/// <summary>
/// What an <see cref="IResponseSink"/> answer was, kept in memory up to a
/// bound: for a playlist the engine checks before it goes.
/// </summary>
internal sealed class BoundedSink(int max) : IResponseSink
{
    public bool Started { get; private set; }

    public bool Over { get; private set; }

    public int Status { get; private set; }

    public string ContentType { get; private set; } = "";

    public MemoryStream Body { get; } = new();

    public void Start(int status, string contentType, long length, string contentRange)
    {
        if (!Started)
        {
            Started = true;
            Status = status;
            ContentType = contentType;
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!Started)
        {
            Start(200, "", -1, "");
        }

        if (Body.Length + data.Length > max)
        {
            Over = true;
            throw new IOException("msfp: an answer over its bound");
        }

        Body.Write(data.Span);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A sink that notes whether it started, writes nothing once its token is
/// cancelled, and paces a download's body.
/// </summary>
internal sealed class GuardedSink(IResponseSink inner, CancellationToken ct, Pacer? pace = null) : IResponseSink
{
    // How many bytes of a download go at a time.
    internal const int PaceChunk = 16 << 10;

    public bool Started { get; private set; }

    public void Start(int status, string contentType, long length, string contentRange)
    {
        if (Started)
        {
            return;
        }

        Started = true;
        inner.Start(status, contentType, length, contentRange);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        // The playback ended, or the peer left: nothing more goes.
        ct.ThrowIfCancellationRequested();
        if (!Started)
        {
            Start(200, "", -1, "");
        }

        if (pace is null)
        {
            await inner.WriteAsync(data, ct).ConfigureAwait(false);
            return;
        }

        while (data.Length > 0)
        {
            var n = Math.Min(data.Length, PaceChunk);
            await pace.TakeAsync(n, ct).ConfigureAwait(false);
            await inner.WriteAsync(data[..n], ct).ConfigureAwait(false);
            data = data[n..];
        }
    }
}

/// <summary>
/// The sink of the file: the engine's status and headers (the host's content
/// type alone kept), at most left bytes, paced for a download.
/// </summary>
internal sealed class FileSink(IResponseSink inner, CancellationToken ct, string etag, long left, Pacer? pace, int status, string contentRange)
    : IResponseSink
{
    private string _contentType = "";

    public bool Started { get; private set; }

    public long Left { get; private set; } = left;

    public void Start(int status_, string contentType, long length, string contentRange_)
    {
        if (!Started)
        {
            _contentType = contentType;
            Begin();
        }
    }

    // Writes the status and headers, once.
    public void Begin()
    {
        if (Started)
        {
            return;
        }

        Started = true;
        (inner as IETagSink)?.SetETag(etag);
        inner.Start(status, Playback.RelayedMediaType(_contentType), Left, contentRange);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        ct.ThrowIfCancellationRequested();
        Begin();
        while (data.Length > 0)
        {
            if (Left == 0)
            {
                // A host writing past the bytes asked: what follows isn't sent.
                throw new IOException("msfp: the host wrote past the range");
            }

            var n = (int)Math.Min(Math.Min(data.Length, Left), GuardedSink.PaceChunk);
            if (pace is not null)
            {
                await pace.TakeAsync(n, ct).ConfigureAwait(false);
            }

            await inner.WriteAsync(data[..n], ct).ConfigureAwait(false);
            Left -= n;
            data = data[n..];
        }
    }
}

// Media, on the owning side (MSFP media sections 1 to 4, video section 6):
// the stream routes of peers' tickets. Every request is checked before the
// host serves a byte: the ticket, live and of a pairing still paired, and of
// a kind that opens that URL; GET only (HEAD and the rest 405); no query; no
// browser's headers; a Range header of one of the three forms, on the file
// and segments only; the fetches at once. The file is served by byte ranges
// against the copy's exact size and etag (a scan's within its cap, a
// download's paced); the host's playlists are checked against MSFP's
// subset, its subtitles and fonts rebuilt or checked, before they leave.
public sealed partial class MsfpEngine
{
    // Bounds of the fetches at once on a peer's tickets (media section 2.4):
    // per ticket and per peer, each with a queue as long, waited in for 10
    // seconds at most.
    private const int FetchesPerPlayback = 8;
    private const int FetchesPerPeer = 64;
    private static readonly TimeSpan FetchWait = TimeSpan.FromSeconds(10);

    /// <summary>Where <see cref="HandleStreamAsync"/> is mounted, after Config.Prefix.</summary>
    internal const string StreamRoute = "/api/v1/stream/";

    private readonly Slots _fetchSlots = new(FetchesPerPlayback, FetchesPerPlayback, FetchWait);
    private readonly Slots _peerFetchSlots = new(FetchesPerPeer, FetchesPerPeer, FetchWait);

    /// <summary>
    /// Serves the media of peers' playbacks here, under
    /// <c>&lt;Prefix&gt;/api/v1/stream/</c> (Go's StreamHandler): give every
    /// request there to this method, whatever its method, on the raw target,
    /// with no authentication in front and no compression of its answers. The
    /// engine writes the answer through output: its status and the headers
    /// MSFP needs (Content-Type, Content-Length, Content-Range), then its
    /// body. A host's sink adds Accept-Ranges: bytes to a 200 or a 206, the
    /// ETag the engine gives it (<see cref="IETagSink"/>, which a sink must
    /// implement: the file's answers carry their copy's ETag), and
    /// Allow: GET to a 405 (HEAD and every method but GET get one).
    /// The 405 is the engine's on every media URL form
    /// (<see cref="MediaPath.TryParse"/>, and <c>&lt;ticket&gt;/image</c>),
    /// whatever the ticket: live, ended or never given (MSFP media section
    /// 2.2); Fallback is given GETs only there. A host that answers its own
    /// clients' HEAD (a copy presented as a file) does so on a route of its
    /// own.
    /// </summary>
    /// <param name="request">The request, its body unread.</param>
    /// <param name="output">Where the answer goes.</param>
    /// <param name="ct">Cancelled when the request goes.</param>
    /// <returns>
    /// Served once answered; NotFound for the host's not-found answer;
    /// Fallback for a ticket the engine doesn't know (one the host gave its
    /// own people): the host's own route answers it.
    /// </returns>
    public async Task<StreamOutcome> HandleStreamAsync(MsfpRequest request, IResponseSink output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        var target = request.Target;
        var root = _cfg.Prefix + StreamRoute;
        var path = target.Split('?', 2)[0];
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            return StreamOutcome.Fallback;
        }

        var rest = path[root.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var ticket = slash < 0 ? rest : rest[..slash];
        var name = slash < 0 ? "" : rest[(slash + 1)..];
        if (!Playback.Id(ticket))
        {
            return StreamOutcome.Fallback;
        }

        // HEAD and the rest get 405 on every media URL, whatever its ticket:
        // live, ended, or never given (media section 2.2).
        if ((name == "image" || MediaPath.TryParse(name, out _)) && MediaMethod(request, output) is { } refused)
        {
            return refused;
        }

        if (await ServeImageAsync(request, ticket, name, output, ct).ConfigureAwait(false) is { } served)
        {
            return served;
        }

        LivePlayback? pb;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
        lock (_plays)
        {
            _plays.ByTicket.TryGetValue(hash, out pb);
        }

        if (pb is null)
        {
            // A ticket of the host's own people, or one that ended: the
            // host's to answer (it answers 404 to one it doesn't know).
            return StreamOutcome.Fallback;
        }

        if (MediaMethod(request, output) is { } other)
        {
            return other;
        }

        if (!PlainMediaRequest(request) || !MediaPathOf(name, pb, out var media) || _cfg.Media is null ||
            !await PlayAllowedAsync(pb, ct).ConfigureAwait(false))
        {
            return StreamOutcome.NotFound;
        }

        ByteRange? range = null;
        var ranges = request.Headers.GetValues("Range");
        if (ranges.Count > 0 && media.Kind == MediaKind.Segment)
        {
            if (ranges.Count > 1 || !ByteRange.TryParse(ranges[0], out var parsed))
            {
                output.Start(416, "", 0, "");
                return StreamOutcome.Served;
            }

            range = parsed;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, pb.Ended.Token);
        var token = cts.Token;
        var freeOne = await _fetchSlots.TakeAsync(pb.Id, token).ConfigureAwait(false);
        if (freeOne is null)
        {
            return Busy(output, token);
        }

        try
        {
            var freeAll = await _peerFetchSlots.TakeAsync(pb.Peer, token).ConfigureAwait(false);
            if (freeAll is null)
            {
                return Busy(output, token);
            }

            try
            {
                Used(pb);
                lock (_plays)
                {
                    pb.Open++;
                }

                try
                {
                    switch (media.Kind)
                    {
                        case MediaKind.Master or MediaKind.Playlist:
                            return await ServePlaylistAsync(pb, media, output, token).ConfigureAwait(false);
                        case MediaKind.File:
                            return await ServeFileAsync(request, pb, media, output, token).ConfigureAwait(false);
                        case MediaKind.Subtitles or MediaKind.Ass or MediaKind.Pictures or MediaKind.Font:
                            return await ServeSmallAsync(pb, media, output, token).ConfigureAwait(false);
                        case MediaKind.Sheet:
                            return await ServeSheetAsync(pb.Handle, media, pb.SheetWidth, pb.SheetHeight, output, token).ConfigureAwait(false);
                    }

                    var sink = new GuardedSink(output, token, pb.Pace);
                    Exception? error = null;
                    try
                    {
                        await _cfg.Media.ServeAsync(pb.Handle, media, range, sink, token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }

                    if (sink.Started || token.IsCancellationRequested)
                    {
                        return StreamOutcome.Served;
                    }

                    return MediaError(output, error);
                }
                finally
                {
                    lock (_plays)
                    {
                        pb.Open--;
                    }

                    Used(pb);
                }
            }
            finally
            {
                freeAll();
            }
        }
        finally
        {
            freeOne();
        }
    }

    // Answers a media request of another method than GET with 405 (the
    // host's sink adds Allow: GET), HEAD included, never 404 (media section
    // 2.2): null when it is a GET.
    private static StreamOutcome? MediaMethod(MsfpRequest request, IResponseSink output)
    {
        if (request.Method == "GET")
        {
            return null;
        }

        output.Start(405, "", 0, "");
        return StreamOutcome.Served;
    }

    // Whether a media request is as a viewer's server sends it: no query,
    // none of a browser's headers (media section 2): a browser must never
    // use a peer's ticket.
    private static bool PlainMediaRequest(MsfpRequest request)
    {
        var h = request.Headers;
        return !request.Target.Contains('?', StringComparison.Ordinal) && h.GetValues("Origin").Count == 0 &&
            h.GetValues("Sec-Fetch-Site").Count == 0 && h.GetValues("Sec-Fetch-Mode").Count == 0;
    }

    // What a request asks of a peer's playback here: false for anything its
    // ticket doesn't open (media section 4: a scan's opens the file only; a
    // download's the stream, its subtitles and fonts) or the playback doesn't
    // serve (another protocol's names, a queue entry it hasn't, a track it
    // doesn't serve apart, a sheet or font past the count).
    private static bool MediaPathOf(string rest, LivePlayback pb, out MediaPath path)
    {
        if (!MediaPath.TryParse(rest, out path))
        {
            return false;
        }

        switch (path.Kind)
        {
            case MediaKind.File or MediaKind.Master or MediaKind.Playlist or MediaKind.Segment:
                if (!pb.Entries.TryGetValue(path.Entry, out var entry) ||
                    (pb.Purpose == Playback.PurposeScan && (path.Kind != MediaKind.File || path.Entry != 0)))
                {
                    return false;
                }

                return entry.Protocol == "file" == (path.Kind == MediaKind.File);
            case MediaKind.Sheet:
                return path.Sheet < pb.Sheets;
            case MediaKind.Font:
                return path.Font < pb.Fonts;
        }

        var how = pb.Subtitles.GetValueOrDefault(path.Track);
        return path.Kind switch
        {
            MediaKind.Subtitles => how.Text,
            MediaKind.Ass => how.Ass,
            _ => how.Pictures,
        };
    }

    // Serves the file of a playback, or of a queue entry, by byte ranges
    // (media sections 2.1, 3.2, 4.1 and 4.2): its exact size and etag, 206
    // for a range it can satisfy, 416 with bytes */<size> for one it can't or
    // a malformed one; under a scan's ticket, a range only, at most 2 at
    // once, cut at the cap; a download's paced.
    private async Task<StreamOutcome> ServeFileAsync(MsfpRequest request, LivePlayback pb, MediaPath path, IResponseSink output,
        CancellationToken ct)
    {
        var entry = pb.Entries[path.Entry];
        var size = entry.Size;
        StreamOutcome Unsatisfied()
        {
            output.Start(416, "", 0, Media.UnsatisfiedRange(size));
            return StreamOutcome.Served;
        }

        var hs = request.Headers.GetValues("Range");
        if (hs.Count > 1)
        {
            return Unsatisfied();
        }

        var h = hs.Count == 1 ? hs[0] : "";
        long start = 0, end = size - 1;
        var scan = pb.Purpose == Playback.PurposeScan;
        if (scan)
        {
            lock (_plays)
            {
                if (pb.ScanReads >= Playback.ScanReadsAtOnce)
                {
                    output.Start(503, "", 0, "");
                    return StreamOutcome.Served;
                }

                if (!Media.TryScanSpan(pb.ScanSent, h, size, out start, out end))
                {
                    return Unsatisfied();
                }

                // The range is held against the cap while it is served, so
                // that two fetches at once never pass it together; what wasn't
                // sent (a fetch cut, a host's failure) is given back after:
                // the cap counts the bytes sent (media section 4.2).
                pb.ScanSent += end - start + 1;
                pb.ScanReads++;
            }
        }
        else if (h.Length > 0)
        {
            if (!Media.TryParseRange(h, out var br) || !br.TrySpan(size, out start, out end))
            {
                return Unsatisfied();
            }
        }

        long unsent = 0; // of a scan's range held, what wasn't sent
        try
        {
            var ranged = h.Length > 0;
            var sink = new FileSink(output, ct, entry.Etag, end - start + 1, pb.Pace, ranged ? 206 : 200,
                ranged ? Media.ContentRange(start, end, size) : "");
            Exception? error = null;
            try
            {
                await _cfg.Media!.ServeAsync(pb.Handle, path, ranged ? new ByteRange(start, end, 0) : null, sink, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                error = e;
            }

            unsent = sink.Left;

            if (sink.Started || ct.IsCancellationRequested)
            {
                return StreamOutcome.Served;
            }

            if (error is null && sink.Left == 0)
            {
                sink.Begin();
                return StreamOutcome.Served;
            }

            return MediaError(output, error);
        }
        finally
        {
            if (scan)
            {
                lock (_plays)
                {
                    pb.ScanReads--;
                    pb.ScanSent -= unsent;
                }
            }
        }
    }

    // A 503 to a fetch over the bounds, unless it was cut.
    private static StreamOutcome Busy(IResponseSink output, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
        {
            output.Start(503, "", 0, "");
        }

        return StreamOutcome.Served;
    }

    // A media request the host failed: its NotFound, RangeNotSatisfiable and
    // ServerBusy as 404, 416 and 503; anything else 500, logged.
    private StreamOutcome MediaError(IResponseSink output, Exception? e)
    {
        switch (e)
        {
            case null:
                _log.LogWarning("msfp: the host's media answered nothing");
                output.Start(500, "", 0, "");
                return StreamOutcome.Served;
            case MsfpException { Code: MsfpError.NotFound }:
                return StreamOutcome.NotFound;
            case MsfpException { Code: MsfpError.RangeNotSatisfiable }:
                output.Start(416, "", 0, "");
                return StreamOutcome.Served;
            case MsfpException { Code: MsfpError.ServerBusy }:
                output.Start(503, "", 0, "");
                return StreamOutcome.Served;
        }

        _log.LogWarning(e, "msfp: the host's media failed");
        output.Start(500, "", 0, "");
        return StreamOutcome.Served;
    }

    // Serves a playlist the host made, once checked against MSFP's subset
    // (media section 3.1): complete, and in a master playlist audio
    // renditions named by the copy's audio track IDs only. One the host made
    // outside it is refused, with a warning, rather than refused by every
    // peer.
    //
    // The first served of each name is kept and served again on every fetch
    // under the ticket: the same playlist on every fetch (section 3.1),
    // whatever the host makes the next time. At most PlaylistsKept are kept,
    // of PlaylistBytesKept bytes together; past that, each is checked only.
    private async Task<StreamOutcome> ServePlaylistAsync(LivePlayback pb, MediaPath path, IResponseSink output, CancellationToken ct)
    {
        var key = path.Entry.ToString(CultureInfo.InvariantCulture) + "/" + path.Kind + "/" + path.Name;
        byte[]? first;
        lock (_plays)
        {
            first = pb.Playlists.GetValueOrDefault(key);
        }

        if (first is not null)
        {
            return await WritePlaylistAsync(output, first, ct).ConfigureAwait(false);
        }

        var buffer = new BoundedSink(Media.MaxPlaylist);
        Exception? error = null;
        try
        {
            await _cfg.Media!.ServeAsync(pb.Handle, path, null, buffer, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = e;
        }

        if (!buffer.Started)
        {
            return MediaError(output, error);
        }

        if (error is not null || buffer.Over || buffer.Status != 200)
        {
            _log.LogWarning(error, "msfp: the host's playlist couldn't be served: {Status}", buffer.Status);
            output.Start(500, "", 0, "");
            return StreamOutcome.Served;
        }

        var body = buffer.Body.ToArray();
        byte[] kept;
        try
        {
            kept = Media.FilterPlaylist(body, path.Kind == MediaKind.Master ? Media.MasterSubset : Media.MediaSubset, pb.Audio.Contains);
            if (!kept.AsSpan().SequenceEqual(NormalLines(body)))
            {
                throw new ProtocolException(ProtocolError.Json, "tags outside MSFP's subset");
            }
        }
        catch (ProtocolException e)
        {
            _log.LogWarning(e, "msfp: the host made a playlist outside what MSFP allows; it isn't sent");
            output.Start(500, "", 0, "");
            return StreamOutcome.Served;
        }

        lock (_plays)
        {
            if (pb.Playlists.TryGetValue(key, out var other))
            {
                kept = other; // another fetch kept it meanwhile
            }
            else if (pb.Playlists.Count < PlaylistsKept && pb.PlaylistBytes + kept.Length <= PlaylistBytesKept)
            {
                pb.Playlists[key] = kept;
                pb.PlaylistBytes += kept.Length;
            }
        }

        return await WritePlaylistAsync(output, kept, ct).ConfigureAwait(false);
    }

    // Bounds of the playlists a playback keeps, to serve them the same.
    private const int PlaylistsKept = 16;
    private const int PlaylistBytesKept = 4 << 20;

    // Answers with a checked playlist.
    private static async Task<StreamOutcome> WritePlaylistAsync(IResponseSink output, byte[] kept, CancellationToken ct)
    {
        output.Start(200, Playback.HlsType, kept.Length, "");
        await output.WriteAsync(kept, ct).ConfigureAwait(false);
        return StreamOutcome.Served;
    }

    // Serves a preview sheet the host made, checked (a JPEG within the
    // layout, video section 6.2) and re-encoded with the SheetEncoder, so
    // that only its pixels leave: whatever else the host's file holds
    // (metadata, trailing bytes) stays here. One outside the layout isn't
    // sent. Sheets are offered only with a SheetEncoder (Register, previews).
    private async Task<StreamOutcome> ServeSheetAsync(string handle, MediaPath path, int width, int height, IResponseSink output,
        CancellationToken ct)
    {
        if (_cfg.SheetEncoder is not { } encoder)
        {
            return StreamOutcome.NotFound;
        }

        await _sheetGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var buffer = new BoundedSink(Protocol.Trickplay.MaxSheetBytes);
            Exception? error = null;
            try
            {
                await _cfg.Media!.ServeAsync(handle, path, null, buffer, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                error = e;
            }

            if (!buffer.Started)
            {
                return MediaError(output, error);
            }

            if (error is not null || buffer.Over || buffer.Status != 200)
            {
                _log.LogWarning(error, "msfp: the host's preview sheet couldn't be served: {Status}", buffer.Status);
                output.Start(500, "", 0, "");
                return StreamOutcome.Served;
            }

            var body = buffer.Body.ToArray();
            byte[]? sheet = null;
            if (Jpeg.Fits(body, width, height))
            {
                try
                {
                    sheet = await encoder.ReencodeAsync(body, width, height, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    error = e;
                }
            }

            if (sheet is null)
            {
                _log.LogWarning(error, "msfp: the host's preview sheet isn't a JPEG within its layout; it isn't sent");
                output.Start(500, "", 0, "");
                return StreamOutcome.Served;
            }

            output.Start(200, "image/jpeg", sheet.Length, "");
            await output.WriteAsync(sheet, ct).ConfigureAwait(false);
            return StreamOutcome.Served;
        }
        finally
        {
            _sheetGate.Release();
        }
    }

    // For what is read whole: its bound as read, how it is rebuilt or
    // checked, and its type.
    internal static (int Most, Func<byte[], (byte[]? Body, string ContentType)> Rebuild) SmallRules(MediaPath p)
    {
        static Func<byte[], (byte[]?, string)> Text(TryRebuildFn f, string type) => b => (f(b, out var output) ? output : null, type);
        const string vtt = "text/vtt; charset=utf-8";
        const string ass = "text/x-ass; charset=utf-8";
        return p.Kind switch
        {
            MediaKind.Subtitles when p.Whole => (WebVtt.MaxWholeText, Text(WebVtt.TryRebuildWhole, vtt)),
            MediaKind.Subtitles => (WebVtt.MaxWebVtt, Text(WebVtt.TryRebuild, vtt)),
            MediaKind.Ass when p.Whole => (WebVtt.MaxWholeText, Text(Ass.TryRebuildWhole, ass)),
            MediaKind.Ass => (Ass.MaxAss, Text(Ass.TryRebuild, ass)),
            MediaKind.Pictures => (Pgs.MaxPgs, b => (Pgs.Valid(b) ? b : null, "application/octet-stream")),
            _ => (Font.MaxFont, b => Font.TryCheck(b, out var type, out _) ? (b, type) : (null, "")),
        };
    }

    internal delegate bool TryRebuildFn(ReadOnlySpan<byte> input, out byte[] output);

    // Serves what is read whole: a stretch or a whole text track, rebuilt as
    // WebVTT or ASS; a stretch of a picture track, checked as PGS; a font,
    // checked (video sections 6.3 to 6.6). What the host made outside MSFP's
    // rules isn't sent.
    private async Task<StreamOutcome> ServeSmallAsync(LivePlayback pb, MediaPath path, IResponseSink output, CancellationToken ct)
    {
        var (most, rebuild) = SmallRules(path);
        var buffer = new BoundedSink(most);
        Exception? error = null;
        try
        {
            await _cfg.Media!.ServeAsync(pb.Handle, path, null, buffer, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = e;
        }

        if (!buffer.Started)
        {
            return MediaError(output, error);
        }

        if (error is not null || buffer.Over || buffer.Status != 200)
        {
            _log.LogWarning(error, "msfp: the host's subtitles or font couldn't be served: {Status}", buffer.Status);
            output.Start(500, "", 0, "");
            return StreamOutcome.Served;
        }

        var (body, contentType) = rebuild(buffer.Body.ToArray());
        if (body is null)
        {
            _log.LogWarning("msfp: the host's subtitles or font are outside what MSFP allows; they aren't sent: {Kind}", path.Kind);
            output.Start(500, "", 0, "");
            return StreamOutcome.Served;
        }

        output.Start(200, contentType, body.Length, "");
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        return StreamOutcome.Served;
    }

    // A playlist with its empty lines dropped, each line ending in a line
    // feed, as FilterPlaylist writes what it keeps.
    private static byte[] NormalLines(byte[] b)
    {
        var output = new StringBuilder();
        foreach (var raw in Encoding.UTF8.GetString(b).Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length > 0)
            {
                output.Append(line).Append('\n');
            }
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }
}

/// <summary>What a media request asks for.</summary>
public sealed partial record MediaPath
{
    /// <summary>
    /// Reads what a URL asks of a playback, after its ticket (or, at the
    /// host's own viewer route, after its own ID for the playback):
    /// "master.m3u8", "file", "subtitles/&lt;track&gt;/&lt;part&gt;" or
    /// ".../whole", "ass/&lt;track&gt;/&lt;part&gt;" or ".../whole",
    /// "pictures/&lt;track&gt;/&lt;part&gt;", "fonts/&lt;n&gt;",
    /// "trickplay/&lt;n&gt;", "queue/&lt;n&gt;/file",
    /// "queue/&lt;n&gt;/master.m3u8" or "queue/&lt;n&gt;/&lt;name&gt;", or a
    /// bare name, a playlist when it ends in .m3u8 and a segment otherwise.
    /// Each part is checked as MSFP media section 1 says, compared as sent:
    /// nothing decoded; false for anything else. A host's viewer route
    /// serves playlists unchanged this way: their bare names resolve under
    /// it. Go's ParseMediaPath.
    /// </summary>
    /// <param name="rest">The path after the ticket.</param>
    /// <param name="path">What it asks; an empty path when false.</param>
    public static bool TryParse(string rest, out MediaPath path)
    {
        ArgumentNullException.ThrowIfNull(rest);
        path = new MediaPath();
        var parts = rest.Split('/');
        if (!parts.All(Media.FileName))
        {
            return false;
        }

        static bool Number(string s, int most, out int n)
        {
            n = 0;
            var ok = s.Length is >= 1 and <= 4 && s.All(char.IsAsciiDigit) && (s == "0" || s[0] != '0');
            if (ok)
            {
                n = int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
            }

            return ok && n <= most;
        }

        var entry = 0;
        if (parts.Length == 3 && parts[0] == "queue")
        {
            if (!Number(parts[1], Music.MaxQueue - 1, out var q))
            {
                return false;
            }

            entry = q + 1;
            parts = parts[2..];
        }

        switch (parts.Length)
        {
            case 1 when parts[0] == "master.m3u8":
                path = new MediaPath { Kind = MediaKind.Master, Entry = entry };
                return true;
            case 1 when parts[0] == "file":
                path = new MediaPath { Kind = MediaKind.File, Entry = entry };
                return true;
            case 1 when parts[0].EndsWith(".m3u8", StringComparison.Ordinal):
                path = new MediaPath { Kind = MediaKind.Playlist, Name = parts[0], Entry = entry };
                return true;
            case 1:
                path = new MediaPath { Kind = MediaKind.Segment, Name = parts[0], Entry = entry };
                return true;
        }

        if (entry > 0)
        {
            return false;
        }

        switch (parts.Length)
        {
            case 2 when parts[0] == "trickplay":
                if (!Number(parts[1], Protocol.Trickplay.MaxSheets - 1, out var sheet))
                {
                    return false;
                }

                path = new MediaPath { Kind = MediaKind.Sheet, Sheet = sheet };
                return true;
            case 2 when parts[0] == "fonts":
                if (!Number(parts[1], 7, out var font))
                {
                    return false;
                }

                path = new MediaPath { Kind = MediaKind.Font, Font = font };
                return true;
            case 3 when parts[0] is "subtitles" or "ass" or "pictures":
                var kind = parts[0] switch
                {
                    "subtitles" => MediaKind.Subtitles,
                    "ass" => MediaKind.Ass,
                    _ => MediaKind.Pictures,
                };
                if (parts[2] == "whole" && kind != MediaKind.Pictures)
                {
                    path = new MediaPath { Kind = kind, Track = parts[1], Whole = true };
                    return true;
                }

                if (!Number(parts[2], 9999, out var part))
                {
                    return false;
                }

                path = new MediaPath { Kind = kind, Track = parts[1], Part = part };
                return true;
        }

        return false;
    }
}

/// <summary>Parsing of a Range header.</summary>
public sealed partial record ByteRange
{
    /// <summary>
    /// Reads a Range header of MSFP media section 2.1: one range of bytes in
    /// one of three forms, each number 1 to 15 digits; never several ranges,
    /// "bytes=-" or "bytes=-0", nor a first byte past the last. Go's
    /// ParseRange.
    /// </summary>
    /// <param name="header">The header's value.</param>
    /// <param name="range">The range; null when false.</param>
    public static bool TryParse(string header, out ByteRange range)
    {
        ArgumentNullException.ThrowIfNull(header);
        range = new ByteRange(0, 0, 0);
        if (!header.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return false;
        }

        var spec = header["bytes=".Length..];
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return false;
        }

        var from = spec[..dash];
        var to = spec[(dash + 1)..];
        static bool Digits(string s) => s.Length is >= 1 and <= 15 && s.All(char.IsAsciiDigit);
        if ((from.Length == 0 && to.Length == 0) || (from.Length > 0 && !Digits(from)) || (to.Length > 0 && !Digits(to)))
        {
            return false;
        }

        var a = from.Length > 0 ? long.Parse(from, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        var b = to.Length > 0 ? long.Parse(to, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        if (from.Length == 0)
        {
            range = new ByteRange(0, 0, b);
            return b >= 1;
        }

        if (to.Length == 0)
        {
            range = new ByteRange(a, -1, 0);
            return true;
        }

        range = new ByteRange(a, b, 0);
        return a <= b;
    }
}
