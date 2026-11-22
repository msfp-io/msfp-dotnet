using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// Media, on the viewer's side (MSFP media sections 2 and 3, video section
// 6): the relay. The host's viewer route maps its own URL to a MediaPath
// and calls FetchAsync; the engine fetches from the peer's stream root with
// the playback's ticket, which never leaves the engine, and checks
// everything before a byte reaches the viewer.
public sealed partial class MsfpEngine
{
    // The reading whole of a playlist, a subtitle part, a PGS part or a
    // sheet gets 2 minutes; 4 of them (and 4 sheets) are held in memory at
    // once across the server (media section 2.4). A media fetch waits a
    // minute at most for the peer's headers.
    private static readonly TimeSpan SmallReadTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MediaHeaderTime = TimeSpan.FromSeconds(60);

    private readonly Slots _relaySlots = new(FetchesPerPlayback, FetchesPerPlayback, FetchWait);
    private readonly Slots _peerRelaySlots = new(FetchesPerPeer, FetchesPerPeer, FetchWait);
    private readonly SemaphoreSlim _smallGate = new(4, 4);
    private readonly SemaphoreSlim _sheetGate = new(4, 4);

    [GeneratedRegex(@"^bytes ([0-9]{1,15})-([0-9]{1,15})/([0-9]{1,15}|\*)\z")]
    private static partial Regex ContentRangeRe();

    /// <summary>
    /// Serves a piece of a playback on a peer, for the viewer playing it: the
    /// same <see cref="MediaPath"/> the owning side serves; range is the
    /// viewer's Range header as received ("" for none), sent on for the file
    /// and segments only, once well-formed. Content-Type is set by the
    /// engine. A fetch is cut when its playback ends.
    /// </summary>
    /// <param name="id">The engine's ID of the playback.</param>
    /// <param name="path">What to fetch.</param>
    /// <param name="range">The viewer's Range header; "" for none.</param>
    /// <param name="output">Where the answer goes.</param>
    /// <param name="ct">Cancelled when the viewer goes.</param>
    /// <exception cref="MsfpException">Nothing written then: <see cref="MsfpError.NotFound"/> (any failed
    /// fetch: the peer's body and headers never pass with a failure), <see cref="MsfpError.RangeNotSatisfiable"/>
    /// (a malformed Range: nothing is fetched; or the peer's 416), <see cref="MsfpError.ServerBusy"/> (the bounds of
    /// fetches at once, or the peer's 503).</exception>
    public async Task FetchAsync(string id, MediaPath path, string range, IResponseSink output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(output);
        range ??= "";
        var rm = LiveRemote(id) ?? throw new MsfpException(MsfpError.NotFound);
        if (path.Kind == MediaKind.Segment && IsPlaylist(rm, path.Name))
        {
            path = path with { Kind = MediaKind.Playlist };
        }

        var name = RelayName(rm, path) ?? throw new MsfpException(MsfpError.NotFound);
        var media = path.Kind is MediaKind.File or MediaKind.Segment;
        var want = new ByteRange(0, 0, 0);
        if (!media)
        {
            range = "";
        }
        else if (range.Length > 0 && !ByteRange.TryParse(range, out want))
        {
            throw new MsfpException(MsfpError.RangeNotSatisfiable);
        }

        PeerRecord p;
        try
        {
            p = await PeerRecordAsync(rm.Peer, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            EndRemote(rm, EndReason.PairingEnded, true);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new MsfpException(MsfpError.NotFound, "the peer couldn't be read", e);
        }

        if (!Usable(p, Versions.PartMedia))
        {
            // Never to a peer that can't be called, or is suspended (core
            // sections 7.5 and 12.6), whatever ended its playbacks.
            EndRemote(rm, EndReason.PairingEnded, true);
            throw new MsfpException(MsfpError.NotFound);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, rm.Ended.Token);
        if (!media)
        {
            cts.CancelAfter(SmallReadTime);
        }

        var token = cts.Token;
        var freeOne = await _relaySlots.TakeAsync(rm.View.Id, token).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.ServerBusy);
        try
        {
            var freeAll = await _peerRelaySlots.TakeAsync(rm.Peer, token).ConfigureAwait(false) ?? throw new MsfpException(MsfpError.ServerBusy);
            try
            {
                SemaphoreSlim? gate = null;
                if (!media)
                {
                    gate = path.Kind == MediaKind.Sheet ? _sheetGate : _smallGate;
                    try
                    {
                        await gate.WaitAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new MsfpException(MsfpError.ServerBusy);
                    }
                }

                try
                {
                    await RelayAsync(rm, p, path, rm.Base + name, range, want, media, output, token).ConfigureAwait(false);
                }
                finally
                {
                    gate?.Release();
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

    private async Task RelayAsync(RemotePlay rm, PeerRecord p, MediaPath path, string url, string range, ByteRange want, bool media,
        IResponseSink output, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await RelayGetAsync(p, url, range, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or MsfpException or OperationCanceledException or IOException)
        {
            if (!ct.IsCancellationRequested)
            {
                _log.LogInformation("msfp: a peer's media couldn't be fetched: {Peer} {Error}", rm.Peer, e.Message);
            }

            throw new MsfpException(MsfpError.NotFound);
        }

        using (res)
        {
            var headers = new ResponseHeaders(res);
            if (headers.GetValues("Content-Encoding").Any(c => !c.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                throw new MsfpException(MsfpError.NotFound);
            }

            switch ((int)res.StatusCode)
            {
                case 200:
                    break;
                case 206 when range.Length > 0:
                    break;
                case 416 when range.Length > 0:
                    throw new MsfpException(MsfpError.RangeNotSatisfiable);
                case 503:
                    throw new MsfpException(MsfpError.ServerBusy);
                default:
                    throw new MsfpException(MsfpError.NotFound);
            }

            if (media)
            {
                await RelayMediaAsync(rm, res, headers, want, output, ct).ConfigureAwait(false);
            }
            else
            {
                await RelaySmallAsync(rm, path, res, output, ct).ConfigureAwait(false);
            }
        }
    }

    // The name fetched under the peer's stream base for path; null for what
    // the playback doesn't have.
    private static string? RelayName(RemotePlay rm, MediaPath path)
    {
        var v = rm.View;
        switch (path.Kind)
        {
            case MediaKind.File:
                return v.Protocol == "file" ? "file" : null;
            case MediaKind.Master:
                return v.Protocol == "hls" ? "master.m3u8" : null;
            case MediaKind.Playlist or MediaKind.Segment:
                return v.Protocol == "hls" && Media.FileName(path.Name) && path.Name != "master.m3u8" ? path.Name : null;
            case MediaKind.Subtitles or MediaKind.Pictures:
                if (!rm.Subtitles.TryGetValue(path.Track, out var pictures) || pictures != (path.Kind == MediaKind.Pictures) ||
                    path.Part is < 0 or > 9999)
                {
                    return null;
                }

                return Playback.SubtitleUrl("", path.Track, pictures) + "/" + path.Part.ToString(CultureInfo.InvariantCulture);
            case MediaKind.Sheet:
                return v.Trickplay is { } tp && path.Sheet >= 0 && path.Sheet < tp.Sheets ? Playback.SheetUrl("", path.Sheet) : null;
        }

        return null;
    }

    // The client for media fetches from a peer: a trusted or named
    // certificate only (media section 1), no time bound on the body.
    private HttpClient MediaClientFor(IReadOnlyCollection<string>? pins)
    {
        var key = "media " + string.Join(" ", pins ?? []);
        lock (_clients)
        {
            if (_clients.TryGetValue(key, out var c))
            {
                return c;
            }

            c = _cfg.Transport is { } transport
                ? new HttpClient(transport, disposeHandler: false)
                : new HttpClient(PeerHandler.Create(_cfg.Dialer, _cfg.AllowLoopback, pins ?? [], false), disposeHandler: true);
            c.Timeout = Timeout.InfiniteTimeSpan;
            _clients[key] = c;
            return c;
        }
    }

    // Fetches a URL of a peer's stream: a plain GET, nothing but Range, over
    // a connection that needs a trusted or named certificate; refused for its
    // certificate, a hello, then once more (media section 2). The URL holds
    // the ticket: it is never logged.
    private async Task<HttpResponseMessage> RelayGetAsync(PeerRecord p, string url, string range, CancellationToken ct)
    {
        async Task<HttpResponseMessage> Get(PeerRecord q)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            req.Version = System.Net.HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            if (range.Length > 0)
            {
                req.Headers.TryAddWithoutValidation("Range", range);
            }

            using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headers.CancelAfter(MediaHeaderTime);
            return await MediaClientFor(q.Pins).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
        }

        try
        {
            return await Get(p).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException && !ct.IsCancellationRequested)
        {
            await CheckAsync(p.Id, background: false, ct).ConfigureAwait(false);
            var q = await PeerRecordAsync(p.Id, ct).ConfigureAwait(false);
            UsablePeer(q, Versions.PartMedia);
            return await Get(q).ConfigureAwait(false);
        }
    }

    // Whether a 206 answers the range asked (media section 2.1), and its
    // length and its Content-Range.
    private static (long Length, string ContentRange)? PartialLength(ResponseHeaders h, ByteRange want)
    {
        var ranges = h.GetValues("Content-Range");
        if (ranges.Count != 1 || ContentRangeRe().Match(ranges[0]) is not { Success: true } m)
        {
            return null;
        }

        var start = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var end = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var total = m.Groups[3].Value == "*" ? -1 : long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (start > end || (total >= 0 && end >= total))
        {
            return null;
        }

        if (want.Suffix > 0)
        {
            if (total < 0 || start != Math.Max(total - want.Suffix, 0))
            {
                return null;
            }
        }
        else if (start != want.First || (want.Last >= 0 && end > want.Last))
        {
            return null;
        }

        var n = end - start + 1;
        var cl = h.GetValues("Content-Length");
        if (cl.Count > 1 || (cl.Count == 1 && cl[0] != n.ToString(CultureInfo.InvariantCulture)))
        {
            return null;
        }

        return (n, ranges[0]);
    }

    // Relays the file or a segment: a 206 only for the range asked, never
    // more than its length, never a playlist under a media name, its type
    // kept only when it is a plain video/ or audio/ one.
    private async Task RelayMediaAsync(RemotePlay rm, HttpResponseMessage res, ResponseHeaders headers, ByteRange want, IResponseSink output,
        CancellationToken ct)
    {
        var status = (int)res.StatusCode;
        var length = res.Content.Headers.ContentLength ?? -1;
        var contentRange = "";
        if (status == 206)
        {
            if (PartialLength(headers, want) is not { } partial)
            {
                _log.LogInformation("msfp: a peer's part of a stream wasn't the range asked: {Peer}", rm.Peer);
                throw new MsfpException(MsfpError.NotFound);
            }

            (length, contentRange) = partial;
        }
        else if (headers.GetValues("Content-Length").Count > 1)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Players read a playlist by what it holds: none passes unchecked.
        var head = new byte[512];
        var got = 0;
        while (got < 7)
        {
            var n = await body.ReadAsync(head.AsMemory(got, head.Length - got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            got += n;
        }

        if (got >= 7 && head.AsSpan(0, 7).SequenceEqual("#EXTM3U"u8))
        {
            _log.LogInformation("msfp: a peer sent a playlist under a media name: {Peer}", rm.Peer);
            throw new MsfpException(MsfpError.NotFound);
        }

        var contentType = Playback.RelayedMediaType(headers.GetValues("Content-Type").FirstOrDefault());
        output.Start(status, contentType, length, contentRange);
        var left = length >= 0 ? length : long.MaxValue;
        try
        {
            var first = (int)Math.Min(got, left);
            if (first > 0)
            {
                await output.WriteAsync(head.AsMemory(0, first), ct).ConfigureAwait(false);
                left -= first;
            }

            var buffer = new byte[64 << 10];
            while (left > 0)
            {
                var n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                left -= n;
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException && !rm.Ended.IsCancellationRequested)
        {
            throw new IOException("msfp: a relayed stream was cut", e);
        }
        catch (OperationCanceledException)
        {
            // The playback ended: the answer stops there.
        }
    }

    // Relays what is read whole: playlists checked and kept to the subset,
    // WebVTT rebuilt, PGS checked, sheets checked and re-encoded (media
    // section 3.1, video sections 6.2 to 6.4).
    private async Task RelaySmallAsync(RemotePlay rm, MediaPath path, HttpResponseMessage res, IResponseSink output, CancellationToken ct)
    {
        var limit = path.Kind switch
        {
            MediaKind.Subtitles => WebVtt.MaxWebVtt,
            MediaKind.Pictures => Pgs.MaxPgs,
            MediaKind.Sheet => Protocol.Trickplay.MaxSheetBytes,
            _ => Media.MaxPlaylist,
        };
        if (res.Content.Headers.ContentLength > limit)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        byte[]? body;
        try
        {
            body = await ReadAtMostAsync(await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        if (body is null)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var contentType = "application/vnd.apple.mpegurl";
        switch (path.Kind)
        {
            case MediaKind.Master or MediaKind.Playlist:
                try
                {
                    body = Media.FilterPlaylist(body, path.Kind == MediaKind.Master ? Media.MasterSubset : Media.MediaSubset, null); // B2: the playback's audio tracks, for a master's #EXT-X-MEDIA renditions
                }
                catch (ProtocolException e)
                {
                    _log.LogInformation("msfp: a peer's playlist was refused: {Peer} {Error}", rm.Peer, e.Message);
                    throw new MsfpException(MsfpError.NotFound);
                }

                if (path.Kind == MediaKind.Master)
                {
                    // What it names is fetched as a playlist, whatever its name.
                    lock (_remotes)
                    {
                        rm.Playlists.UnionWith(Media.UriLines(body));
                    }
                }

                break;
            case MediaKind.Subtitles:
                if (!WebVtt.TryRebuild(body, out var vtt))
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                body = vtt;
                contentType = "text/vtt; charset=utf-8";
                break;
            case MediaKind.Pictures:
                // An empty part is a stretch with no subtitles: relayed as such.
                if (!Pgs.Valid(body))
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                contentType = "application/octet-stream";
                break;
            case MediaKind.Sheet:
                var tp = rm.View.Trickplay!;
                int w = tp.Columns * tp.TileWidth, h = tp.Rows * tp.TileHeight;
                if (!Jpeg.Fits(body, w, h) || _cfg.SheetEncoder is not { } encoder)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                try
                {
                    body = await encoder.ReencodeAsync(body, w, h, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                contentType = "image/jpeg";
                break;
        }

        output.Start(200, contentType, body.Length, "");
        await output.WriteAsync(body, ct).ConfigureAwait(false);
    }

    // Whether a name of a playback was named by its master playlist, or
    // ends in .m3u8: fetched and checked as a playlist.
    private bool IsPlaylist(RemotePlay rm, string name)
    {
        lock (_remotes)
        {
            return rm.Playlists.Contains(name) || name.EndsWith(".m3u8", StringComparison.Ordinal);
        }
    }
}

/// <summary>Reads a JPEG's size from its header, before anything decodes it.</summary>
internal static class Jpeg
{
    /// <summary>
    /// Whether a sheet is a JPEG (by its content, whatever its Content-Type)
    /// no larger than the layout's full sheet (MSFP video section 6.2): its
    /// first frame header read, nothing decoded.
    /// </summary>
    public static bool Fits(ReadOnlySpan<byte> b, int maxWidth, int maxHeight) =>
        Size(b) is var (w, h) && w >= 1 && h >= 1 && w <= maxWidth && h <= maxHeight;

    /// <summary>The width and height of a JPEG's frame; null when it isn't one.</summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8)
        {
            return null;
        }

        var i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF)
            {
                return null;
            }

            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return null; // no frame header before the image data
            }

            var len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2 || i + 2 + len > b.Length)
            {
                return null;
            }

            // Start of frame: baseline, extended, progressive or lossless (not
            // DHT C4, JPG C8, DAC CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (len < 7)
                {
                    return null;
                }

                var height = (b[i + 5] << 8) | b[i + 6];
                var width = (b[i + 7] << 8) | b[i + 8];
                return (width, height);
            }

            i += 2 + len;
        }

        return null;
    }
}
