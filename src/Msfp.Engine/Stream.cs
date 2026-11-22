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

/// <summary>A sink that notes whether it started, and writes nothing once its token is cancelled.</summary>
internal sealed class GuardedSink(IResponseSink inner, CancellationToken ct) : IResponseSink
{
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

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        // The playback ended, or the peer left: nothing more goes.
        ct.ThrowIfCancellationRequested();
        if (!Started)
        {
            Start(200, "", -1, "");
        }

        return inner.WriteAsync(data, ct);
    }
}

// Media, on the owning side (MSFP media sections 1 to 4, video section 6):
// the stream routes of peers' tickets. Every request is checked before the
// host serves a byte: the ticket, live and of a pairing still paired; GET
// only; no query; no browser's headers; a Range header of one of the three
// forms, on the file and segments only; the fetches at once. The host's
// playlists are checked against media 1's subset before they leave.
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
    /// GET there to this method, on the raw target, with no authentication in
    /// front and no compression of its answers. The engine writes the answer
    /// through output: its status and the headers MSFP needs
    /// (Content-Type, Content-Length, Content-Range; a host's sink adds
    /// Accept-Ranges: bytes to a 200 or a 206), then its body.
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

        // GET only (HEAD refused too), no query, no browser: a browser must
        // never use a peer's ticket (media section 2).
        var h = request.Headers;
        if (request.Method != "GET" || target.Contains('?', StringComparison.Ordinal) ||
            h.GetValues("Origin").Count > 0 || h.GetValues("Sec-Fetch-Site").Count > 0 || h.GetValues("Sec-Fetch-Mode").Count > 0)
        {
            return StreamOutcome.NotFound;
        }

        if (!MediaPathOf(name, pb, out var media) || _cfg.Media is null || !await PlayAllowedAsync(pb, ct).ConfigureAwait(false))
        {
            return StreamOutcome.NotFound;
        }

        ByteRange? range = null;
        var ranges = h.GetValues("Range");
        if (ranges.Count > 0 && media.Kind is MediaKind.File or MediaKind.Segment)
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
                    if (media.Kind is MediaKind.Master or MediaKind.Playlist)
                    {
                        return await ServePlaylistAsync(pb, media, output, token).ConfigureAwait(false);
                    }

                    var sink = new GuardedSink(output, token);
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

    // What a request asks of a peer's playback here: false for anything the
    // playback doesn't serve (another protocol's names, a track it hasn't, a
    // sheet past the count).
    private static bool MediaPathOf(string rest, LivePlayback pb, out MediaPath path)
    {
        if (!MediaPath.TryParse(rest, out path))
        {
            return false;
        }

        switch (path.Kind)
        {
            case MediaKind.File:
                return pb.Protocol == "file";
            case MediaKind.Master or MediaKind.Playlist or MediaKind.Segment:
                return pb.Protocol == "hls";
            case MediaKind.Sheet:
                return path.Sheet < pb.Sheets;
        }

        return pb.Subtitles.TryGetValue(path.Track, out var pictures) && pictures == (path.Kind == MediaKind.Pictures);
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

    // Serves a playlist the host made, once checked against media 1's subset
    // (media section 3.1): one the host made outside it is refused, with a
    // warning, rather than refused by every peer.
    private async Task<StreamOutcome> ServePlaylistAsync(LivePlayback pb, MediaPath path, IResponseSink output, CancellationToken ct)
    {
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
            kept = Media.FilterPlaylist(body, path.Kind == MediaKind.Master ? Media.MasterSubset : Media.MediaSubset, null); // B2: the playback's audio tracks, for a master's #EXT-X-MEDIA renditions
            if (!kept.AsSpan().SequenceEqual(NormalLines(body)))
            {
                throw new ProtocolException(ProtocolError.Json, "tags outside media 1's subset");
            }
        }
        catch (ProtocolException e)
        {
            _log.LogWarning(e, "msfp: the host made a playlist outside what MSFP allows; it isn't sent");
            output.Start(500, "", 0, "");
            return StreamOutcome.Served;
        }

        output.Start(200, "application/vnd.apple.mpegurl", kept.Length, "");
        await output.WriteAsync(kept, ct).ConfigureAwait(false);
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
    /// "master.m3u8", "file", "subtitles/&lt;track&gt;/&lt;part&gt;",
    /// "pictures/&lt;track&gt;/&lt;part&gt;", "trickplay/&lt;n&gt;", or a bare
    /// name, a playlist when it ends in .m3u8 and a segment otherwise. Each
    /// part is checked as MSFP media section 1 says, compared as sent:
    /// nothing decoded; false for anything else. Go's ParseMediaPath.
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

        static bool Number(string s, out int n)
        {
            n = 0;
            var ok = s.Length is >= 1 and <= 4 && s.All(char.IsAsciiDigit) && (s == "0" || s[0] != '0');
            if (ok)
            {
                n = int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
            }

            return ok;
        }

        switch (parts.Length)
        {
            case 1 when parts[0] == "master.m3u8":
                path = new MediaPath { Kind = MediaKind.Master };
                return true;
            case 1 when parts[0] == "file":
                path = new MediaPath { Kind = MediaKind.File };
                return true;
            case 1 when parts[0].EndsWith(".m3u8", StringComparison.Ordinal):
                path = new MediaPath { Kind = MediaKind.Playlist, Name = parts[0] };
                return true;
            case 1:
                path = new MediaPath { Kind = MediaKind.Segment, Name = parts[0] };
                return true;
            case 2 when parts[0] == "trickplay":
                if (!Number(parts[1], out var sheet))
                {
                    return false;
                }

                path = new MediaPath { Kind = MediaKind.Sheet, Sheet = sheet };
                return true;
            case 3 when parts[0] is "subtitles" or "pictures":
                if (!Number(parts[2], out var part))
                {
                    return false;
                }

                path = new MediaPath { Kind = parts[0] == "pictures" ? MediaKind.Pictures : MediaKind.Subtitles, Track = parts[1], Part = part };
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
