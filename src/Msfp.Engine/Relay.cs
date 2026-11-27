using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

// Media, on the viewer's side (MSFP Media fetching 2 and 3, MSFP
// Media video 6): the relay. The host's viewer route maps its own URL to a MediaPath
// and calls FetchAsync; the engine fetches from the peer's stream root with
// the playback's ticket, which never leaves the engine, and checks
// everything before a byte reaches the viewer.
public sealed partial class MsfpEngine
{
    // The reading whole of a playlist, a subtitle part, a PGS part, a font
    // or a sheet gets 2 minutes; 4 of them (and 4 sheets) are held in memory at
    // once across the server (MSFP Media fetching 2.4). A media fetch waits a
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
    /// same <see cref="MediaPath"/> the owning side serves
    /// (<see cref="MediaPath.Entry"/> for a queue entry's stream); range is
    /// the viewer's Range header as received ("" for none), sent on for the
    /// file and segments only, once well-formed. Content-Type is set by the
    /// engine; an output that implements <see cref="IETagSink"/> is given the
    /// file's ETag first. The file's answers are checked against the copy's
    /// etag and exact size (a different one: the copy changed, NotFound, and
    /// RestartAsync won't join it). A fetch is cut when its playback ends.
    /// </summary>
    /// <param name="id">The engine's ID of the playback.</param>
    /// <param name="path">What to fetch.</param>
    /// <param name="range">The viewer's Range header; "" for none.</param>
    /// <param name="output">Where the answer goes.</param>
    /// <param name="ct">Cancelled when the viewer goes.</param>
    /// <exception cref="MsfpException">Nothing written then: <see cref="MsfpError.NotFound"/> (any failed
    /// fetch: the peer's body and headers never pass with a failure), <see cref="MsfpError.RangeNotSatisfiable"/>
    /// (a malformed Range: nothing is fetched; or the peer's 416, a scan's past its cap among them), <see cref="MsfpError.ServerBusy"/> (the bounds of
    /// fetches at once, or the peer's 503).</exception>
    public async Task FetchAsync(string id, MediaPath path, string range, IResponseSink output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(output);
        range ??= "";
        var rm = LiveRemote(id) ?? throw new MsfpException(MsfpError.NotFound);
        if (path.Kind == MediaKind.Segment && IsPlaylist(rm, path.Entry, path.Name))
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
            // Never to a peer that can't be called, or is suspended (MSFP
            // core sections 7.5 and 12.6), whatever ended its playbacks.
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

            if (path.Kind == MediaKind.File)
            {
                await RelayFileAsync(rm, path.Entry, res, headers, range, want, output, ct).ConfigureAwait(false);
            }
            else if (media)
            {
                await RelayMediaAsync(rm, res, headers, want, output, ct).ConfigureAwait(false);
            }
            else
            {
                await RelaySmallAsync(rm, path, res, output, ct).ConfigureAwait(false);
            }
        }
    }

    // The folder of a stream of a playback on a peer, under its base: its
    // own, or a queue entry's (MSFP Media fetching 1).
    private static string EntryBase(int entry) => entry == 0 ? "" : "queue/" + (entry - 1).ToString(CultureInfo.InvariantCulture) + "/";

    // The name fetched under the peer's stream base for path; null for what
    // the playback doesn't have.
    private string? RelayName(RemotePlay rm, MediaPath path)
    {
        RemoteStream? entry;
        lock (_remotes)
        {
            entry = rm.Entries.GetValueOrDefault(path.Entry);
        }

        var v = rm.View;
        switch (path.Kind)
        {
            case MediaKind.File:
                return entry is { Protocol: "file" } ? EntryBase(path.Entry) + "file" : null;
            case MediaKind.Master:
                return entry is { Protocol: "hls" } ? EntryBase(path.Entry) + "master.m3u8" : null;
            case MediaKind.Playlist or MediaKind.Segment:
                return entry is { Protocol: "hls" } && Media.FileName(path.Name) && path.Name != "master.m3u8" ? EntryBase(path.Entry) + path.Name : null;
            case MediaKind.Subtitles or MediaKind.Ass or MediaKind.Pictures:
                if (!rm.Subtitles.TryGetValue(path.Track, out var how) || path.Entry != 0)
                {
                    return null;
                }

                var served = path.Kind switch
                {
                    MediaKind.Subtitles => how.Text,
                    MediaKind.Ass => how.Ass,
                    _ => how.Pictures,
                };
                string part;
                if (path.Whole)
                {
                    if (path.Kind == MediaKind.Pictures)
                    {
                        return null;
                    }

                    part = "whole";
                }
                else if (path.Part is < 0 or > 9999)
                {
                    return null;
                }
                else
                {
                    part = path.Part.ToString(CultureInfo.InvariantCulture);
                }

                var folder = path.Kind switch
                {
                    MediaKind.Subtitles => "subtitles/",
                    MediaKind.Ass => "ass/",
                    _ => "pictures/",
                };
                return served ? folder + path.Track + "/" + part : null;
            case MediaKind.Font:
                return path.Font >= 0 && path.Font < v.Fonts && path.Entry == 0 ? "fonts/" + path.Font.ToString(CultureInfo.InvariantCulture) : null;
            case MediaKind.Sheet:
                return rm.Layout is not null && path.Sheet >= 0 && path.Sheet < v.Sheets && path.Entry == 0
                    ? "trickplay/" + path.Sheet.ToString(CultureInfo.InvariantCulture) : null;
        }

        return null;
    }

    // The client for media fetches from a peer: a trusted or named
    // certificate only (MSFP media section 1), no time bound on the body.
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
    // certificate, a hello, then once more (MSFP Media fetching 2). The URL holds
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

    // Whether a 206 answers the range asked (MSFP Media fetching 2.1), and its
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

    // An ETag header's value, when it is a strong entity tag of an etag's
    // form; "" otherwise.
    private static string StrongETag(string? h) =>
        h is { Length: >= 2 } && h[0] == '"' && h[^1] == '"' && Playback.Id(h[1..^1]) ? h[1..^1] : "";

    // Relays the file of a playback, or of a queue entry (MSFP
    // Media fetching 3.2): its answer checked against the copy's etag and exact size (learnt
    // from the first answer when the catalog didn't give them), a 206 only
    // for the range asked, never more than its length.
    private async Task RelayFileAsync(RemotePlay rm, int entry, HttpResponseMessage res, ResponseHeaders headers, string range, ByteRange want,
        IResponseSink output, CancellationToken ct)
    {
        RemoteStream ent;
        lock (_remotes)
        {
            ent = rm.Entries[entry];
        }

        if (headers.GetValues("ETag").Count > 1 || headers.GetValues("Content-Length").Count > 1 || headers.GetValues("Content-Range").Count > 1)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var status = (int)res.StatusCode;
        var a = new MediaAnswer(status, headers.GetValues("ETag").FirstOrDefault(), headers.GetValues("Content-Range").FirstOrDefault(),
            headers.GetValues("Content-Length").FirstOrDefault());
        if (ent.Etag.Length == 0 || ent.Size < 0)
        {
            // Not known from the catalog: learnt from this first answer, then
            // held to.
            if (ent.Etag.Length == 0)
            {
                ent = ent with { Etag = StrongETag(a.ETag) };
            }

            if (ent.Size < 0)
            {
                if (status == 200 && long.TryParse(a.ContentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    ent = ent with { Size = n };
                }
                else if (a.ContentRange is { } cr && ContentRangeRe().Match(cr) is { Success: true } m && m.Groups[3].Value != "*")
                {
                    ent = ent with { Size = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) };
                }
            }

            if (ent.Etag.Length == 0 || ent.Size < 0)
            {
                throw new MsfpException(MsfpError.NotFound);
            }
        }

        if (Media.CheckFileAnswer(range, ent.Size, ent.Etag, a) is { } why)
        {
            _log.LogInformation("msfp: a peer's file answer was refused: {Peer} {Why}", rm.Peer, why);
            throw new MsfpException(MsfpError.NotFound);
        }

        lock (_remotes)
        {
            rm.Entries[entry] = ent;
            if (entry == 0)
            {
                rm.View = rm.View with { Size = ent.Size };
            }
        }

        long length = ent.Size;
        var contentRange = "";
        if (status == 206)
        {
            if (PartialLength(headers, want) is not { } partial)
            {
                throw new MsfpException(MsfpError.NotFound);
            }

            (length, contentRange) = partial;
        }

        (output as IETagSink)?.SetETag(ent.Etag);
        // The copy's own mediaType, from the catalog (MSFP Media fetching 3).
        output.Start(status, Media.RelayFileType(ent.MediaType), length, contentRange);
        await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            var buffer = new byte[64 << 10];
            for (var left = length; left > 0;)
            {
                var n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    // Fewer bytes than its length: cut, never a whole file.
                    throw new IOException("msfp: a relayed file ended short");
                }

                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                left -= n;
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException && !rm.Ended.IsCancellationRequested)
        {
            throw new IOException("msfp: a relayed file was cut", e);
        }
        catch (OperationCanceledException)
        {
            // The playback ended: the answer stops there.
        }
    }

    // Relays a segment: a 206 only for the range asked, never more than its
    // length, never a playlist under a media name, its type kept only when it
    // is a plain video/ or audio/ one.
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
    // complete, their renditions the playback's audio tracks; WebVTT and ASS
    // rebuilt; PGS checked; fonts checked; sheets checked and re-encoded
    // (MSFP Media fetching 3.1, MSFP Media video 6.2 to 6.6).
    private async Task RelaySmallAsync(RemotePlay rm, MediaPath path, HttpResponseMessage res, IResponseSink output, CancellationToken ct)
    {
        var limit = Media.MaxPlaylist;
        Func<byte[], (byte[]? Body, string ContentType)>? rebuild = null;
        switch (path.Kind)
        {
            case MediaKind.Subtitles or MediaKind.Ass or MediaKind.Pictures or MediaKind.Font:
                (limit, rebuild) = SmallRules(path);
                break;
            case MediaKind.Sheet:
                limit = PreviewLayout.MaxSheetBytes;
                break;
        }

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

        var contentType = Playback.HlsType;
        switch (path.Kind)
        {
            case MediaKind.Master or MediaKind.Playlist:
                try
                {
                    body = Media.FilterPlaylist(body, path.Kind == MediaKind.Master ? Media.MasterSubset : Media.MediaSubset, rm.Audio.Contains);
                }
                catch (ProtocolException e)
                {
                    _log.LogInformation("msfp: a peer's playlist was refused: {Peer} {Error}", rm.Peer, e.Message);
                    throw new MsfpException(MsfpError.NotFound);
                }

                if (path.Kind == MediaKind.Master)
                {
                    // What it names is fetched as a playlist, whatever its name.
                    var entry = path.Entry.ToString(CultureInfo.InvariantCulture) + "/";
                    lock (_remotes)
                    {
                        rm.Playlists.UnionWith(Media.UriLines(body).Select(n => entry + n));
                    }
                }

                break;
            case MediaKind.Sheet:
                var tp = rm.Layout!;
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
            default:
                // An empty PGS part is a stretch with no subtitles: relayed as such.
                var (again, type) = rebuild!(body);
                if (again is not null && path.Kind == MediaKind.Font)
                {
                    lock (_remotes)
                    {
                        again = FontsWithin(rm.FontBytes, path.Font, again.Length) ? again : null;
                    }
                }

                body = again ?? throw new MsfpException(MsfpError.NotFound);
                contentType = type;
                break;
        }

        output.Start(200, contentType, body.Length, "");
        await output.WriteAsync(body, ct).ConfigureAwait(false);
    }

    // Whether a name of a playback's stream was named by its master
    // playlist, or ends in .m3u8: fetched and checked as a playlist.
    private bool IsPlaylist(RemotePlay rm, int entry, string name)
    {
        lock (_remotes)
        {
            return rm.Playlists.Contains(entry.ToString(CultureInfo.InvariantCulture) + "/" + name) || name.EndsWith(".m3u8", StringComparison.Ordinal);
        }
    }
}
/// <summary>Reads a JPEG's size from its header, before anything decodes it.</summary>
internal static class Jpeg
{
    /// <summary>
    /// Whether a sheet is a JPEG (by its content, whatever its Content-Type)
    /// no larger than the layout's full sheet (MSFP Media video 6.2): its
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
