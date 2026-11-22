using System.Globalization;
using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>An IResponseSink in memory (msfp-go's sink).</summary>
internal sealed class MemorySink : IResponseSink, IETagSink
{
    /// <summary>The ETag the engine gave, "" for none.</summary>
    public string ETag { get; private set; } = "";

    public void SetETag(string etag) => ETag = etag;

    public int Status { get; private set; }

    public string ContentType { get; private set; } = "";

    public long Length { get; private set; }

    public string ContentRange { get; private set; } = "";

    public bool Started { get; private set; }

    private readonly MemoryStream _body = new();

    public byte[] Body
    {
        get
        {
            lock (_body)
            {
                return _body.ToArray();
            }
        }
    }

    public string Text => System.Text.Encoding.UTF8.GetString(Body);

    public void Start(int status, string contentType, long length, string contentRange)
    {
        if (Started)
        {
            return;
        }

        Started = true;
        (Status, ContentType, Length, ContentRange) = (status, contentType, length, contentRange);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        lock (_body)
        {
            _body.Write(data.Span);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A host's catalog, playback and media, in memory (msfp-go's fakeHost): its
/// libraries and titles, each title's copies, each copy's tracks; Start
/// admits the method set (direct by default) and serves media from files
/// set by name.
/// </summary>
internal sealed class FakeHost : ICatalog, IPlayback, IMedia, IImages, ILyrics
{
    private readonly object _lock = new();

    public List<Library> Libs { get; set; } = [];

    public Dictionary<string, Title> Titles { get; } = new(StringComparer.Ordinal);

    /// <summary>What Describe gives, by copy handle.</summary>
    public Dictionary<string, CopyInfo> Info { get; } = new(StringComparer.Ordinal);

    public string Method { get; set; } = "direct";

    public string Protocol { get; set; } = "hls";

    public Exception? StartError { get; set; }

    public bool NoAdmit { get; set; }

    /// <summary>Start prepares every queued track.</summary>
    public bool Queue { get; set; }

    public List<StartRequest> StartedList { get; } = [];

    public Dictionary<string, EndReason> Stopped { get; } = new(StringComparer.Ordinal);

    public List<long> ProgressList { get; } = [];

    public List<Report> ReportList { get; } = [];

    /// <summary>What LyricsAsync gives, by copy handle.</summary>
    public Dictionary<string, IReadOnlyList<LyricsSet>> Lyrics { get; } = new(StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<LyricsSet>> LyricsAsync(string copy, CancellationToken ct)
    {
        lock (_lock)
        {
            return Lyrics.TryGetValue(copy, out var l) ? ValueTask.FromResult(l) : throw new MsfpException(MsfpError.NotFound);
        }
    }

    public Dictionary<string, byte[]> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What ImageAsync gives, by title key.</summary>
    public Dictionary<string, byte[]> Images { get; } = new(StringComparer.Ordinal);

    public ValueTask<byte[]> ImageAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            return Images.TryGetValue(key, out var img) ? ValueTask.FromResult(img) : throw new MsfpException(MsfpError.NotFound);
        }
    }

    /// <summary>A title's one copy, of the least a copy carries (msfp-go's oneCopy).</summary>
    public static List<Msfp.Protocol.Copy> OneCopy(string handle) => [new Msfp.Protocol.Copy(handle, 60_000, "mp4", 100, "e1", [])];

    /// <summary>Answers in place of the files, when set.</summary>
    public Func<MediaPath, ByteRange?, IResponseSink, CancellationToken, ValueTask>? Serve { get; set; }

    /// <summary>Makes Start wait until it completes, so that plays cross; Entered is told each Start that waits.</summary>
    public Task? Hold { get; set; }

    public SemaphoreSlim? Entered { get; set; }

    private int _handles;

    public object Lock => _lock;

    /// <summary>
    /// A film in library lib, with one copy per audio language given (handle
    /// "&lt;key&gt;#&lt;n&gt;", size 20, the size of Files["file"]), each with
    /// an audio track, a French text subtitle track and a French PGS one.
    /// </summary>
    public void Film(string lib, string key, params string[] audio)
    {
        lock (_lock)
        {
            var t = new VideoTitle { Key = key, Kind = "movie", Libraries = [lib], Copies = [] };
            Titles[key] = t;
            for (var i = 0; i < audio.Length; i++)
            {
                var a = audio[i];
                var handle = key + "#" + i.ToString(CultureInfo.InvariantCulture);
                t.Copies.Add(new Msfp.Protocol.Copy(handle, 5_400_000, "mkv", 20, "e" + i.ToString(CultureInfo.InvariantCulture),
                    [new CopyAudio("a0", "aac") { Language = a, Channels = 2, Default = true }])
                {
                    Name = "Copy " + a,
                    SubtitleTracks = [new CopySubtitle("s0", "subrip") { Language = "fr" }, new CopySubtitle("s1", "pgs") { Language = "fr" }],
                });
            }
        }
    }

    /// <summary>Changes copy n of a video title (msfp-go's copyOf).</summary>
    public void EditCopy(string key, int n, Func<Msfp.Protocol.Copy, Msfp.Protocol.Copy> edit)
    {
        lock (_lock)
        {
            var copies = ((VideoTitle)Titles[key]).Copies!;
            copies[n] = edit(copies[n]);
        }
    }

    public List<Report> Reports()
    {
        lock (_lock)
        {
            return [.. ReportList];
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            Titles.Remove(key);
        }
    }

    public Dictionary<string, EndReason> Stops()
    {
        lock (_lock)
        {
            return new Dictionary<string, EndReason>(Stopped);
        }
    }

    public List<long> Progresses()
    {
        lock (_lock)
        {
            return [.. ProgressList];
        }
    }

    public List<StartRequest> Starts()
    {
        lock (_lock)
        {
            return [.. StartedList];
        }
    }

    public ValueTask<IReadOnlyList<Library>> LibrariesAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<Library>>(Libs.Select(l => l with { }).ToList());
        }
    }

    public ValueTask<TitlePage> TitlesAsync(IReadOnlyList<string> libraries, string after, int limit, CancellationToken ct)
    {
        lock (_lock)
        {
            var page = new List<Title>();
            var more = false;
            foreach (var k in Titles.Keys.Order(StringComparer.Ordinal))
            {
                var t = Titles[k];
                if (string.CompareOrdinal(k, after) <= 0 || !(t.Libraries ?? []).Any(libraries.Contains))
                {
                    continue;
                }

                if (page.Count == limit)
                {
                    more = true;
                    break;
                }

                page.Add(TitleJson.Unmarshal(TitleJson.Marshal(t)));
            }

            return ValueTask.FromResult(new TitlePage(page, more));
        }
    }

    public ValueTask<CopyInfo> DescribeAsync(string copy, CancellationToken ct)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(Info.GetValueOrDefault(copy) ?? new CopyInfo());
        }
    }

    public async ValueTask<Started> StartAsync(StartRequest request, CancellationToken ct)
    {
        string method, protocol;
        Exception? error;
        bool noAdmit;
        Task? hold;
        SemaphoreSlim? entered;
        bool queue;
        lock (_lock)
        {
            (method, protocol, error, noAdmit, hold, entered, queue) = (Method, Protocol, StartError, NoAdmit, Hold, Entered, Queue);
        }

        if (hold is not null)
        {
            entered?.Release();
            await hold;
        }

        if (error is not null)
        {
            throw error;
        }

        // A delivery asked is served as asked.
        switch (request.Delivery)
        {
            case Playback.DeliveryFile:
                (method, protocol) = ("direct", "file");
                break;
            case Playback.DeliveryRemux:
                (method, protocol) = ("remux", "hls");
                break;
            case Playback.DeliveryTranscode:
                (method, protocol) = ("transcode", "hls");
                break;
        }

        if (!noAdmit)
        {
            request.Admit!(method);
        }

        lock (_lock)
        {
            _handles++;
            StartedList.Add(request);
            return new Started
            {
                Handle = "h" + _handles.ToString(CultureInfo.InvariantCulture), Method = method, Protocol = protocol, MimeType = "video/mp4",
                PositionMs = request.StartMs, Info = new PlaybackInfo { Container = "mp4" },
                Queue = queue ? request.Queue.Select(q => new StartedEntry(q.Place, method, protocol, "audio/flac")).ToList() : null,
            };
        }
    }

    public ValueTask ProgressAsync(Report report, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!report.Stop && !report.Late)
            {
                ProgressList.Add(report.PositionMs);
            }

            ReportList.Add(report);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(string handle, EndReason reason, CancellationToken ct)
    {
        lock (_lock)
        {
            Stopped[handle] = reason;
        }

        return ValueTask.CompletedTask;
    }

    // A MediaPath's name under the stream, as Files keys it.
    public static string MediaName(MediaPath p) => p.Kind switch
    {
        MediaKind.Master => EntryOf(p) + "master.m3u8",
        MediaKind.File => EntryOf(p) + "file",
        MediaKind.Subtitles => $"subtitles/{p.Track}/{Part(p)}",
        MediaKind.Ass => $"ass/{p.Track}/{Part(p)}",
        MediaKind.Pictures => $"pictures/{p.Track}/{Part(p)}",
        MediaKind.Font => $"fonts/{p.Font}",
        MediaKind.Sheet => $"trickplay/{p.Sheet}",
        _ => EntryOf(p) + p.Name,
    };

    private static string Part(MediaPath p) => p.Whole ? "whole" : p.Part.ToString(CultureInfo.InvariantCulture);

    // A queue entry's folder ("" for the playback's own stream).
    private static string EntryOf(MediaPath p) => p.Entry == 0 ? "" : $"queue/{p.Entry - 1}/";

    private static string TypeOf(MediaPath p) => p.Kind switch
    {
        MediaKind.Master or MediaKind.Playlist => "application/vnd.apple.mpegurl",
        MediaKind.Subtitles => "text/vtt; charset=utf-8",
        MediaKind.Ass => "text/x-ass",
        MediaKind.Font => "font/ttf",
        MediaKind.Sheet => "image/jpeg",
        MediaKind.Pictures => "application/octet-stream",
        _ => "video/mp4",
    };

    async ValueTask IMedia.ServeAsync(string handle, MediaPath path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        Func<MediaPath, ByteRange?, IResponseSink, CancellationToken, ValueTask>? serve;
        byte[]? body;
        lock (_lock)
        {
            serve = Serve;
            Files.TryGetValue(MediaName(path), out body);
        }

        if (serve is not null)
        {
            await serve(path, range, output, ct);
            return;
        }

        if (body is null)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        await ServeBytesAsync(body, range, output, TypeOf(path), ct);
    }

    // Answers body, or the range of it asked.
    private static async ValueTask ServeBytesAsync(byte[] body, ByteRange? range, IResponseSink output, string contentType, CancellationToken ct)
    {
        long size = body.Length;
        if (range is null)
        {
            output.Start(200, contentType, size, "");
            await output.WriteAsync(body, ct);
            return;
        }

        long first = range.First, last = range.Last;
        if (range.Suffix > 0)
        {
            first = Math.Max(size - range.Suffix, 0);
            last = size - 1;
        }

        if (last < 0 || last >= size)
        {
            last = size - 1;
        }

        if (first >= size)
        {
            throw new MsfpException(MsfpError.RangeNotSatisfiable);
        }

        output.Start(206, contentType, last - first + 1, $"bytes {first}-{last}/{size}");
        await output.WriteAsync(body.AsMemory((int)first, (int)(last - first + 1)), ct);
    }
}

/// <summary>
/// A sheet encoder for the tests, which have no JPEG codec: it checks the
/// size from the header as the engine does, and gives a fresh JPEG of its own.
/// </summary>
internal sealed class TestSheets : ISheetEncoder
{
    public ValueTask<byte[]> ReencodeAsync(ReadOnlyMemory<byte> jpeg, int maxWidth, int maxHeight, CancellationToken ct) =>
        Jpeg.Fits(jpeg.Span, maxWidth, maxHeight)
            ? ValueTask.FromResult((byte[])Sheets.Jpeg20x10.Clone())
            : throw new MsfpException(MsfpError.NotAnImage);
}

/// <summary>
/// Two engines paired, A sharing h's libraries with B, B wanting them and
/// having read them (msfp-go's sharing).
/// </summary>
internal sealed class Sharing : IDisposable
{
    public required Network Net { get; init; }

    public required TestServer A { get; set; }

    public required TestServer B { get; set; }

    public required FakeHost H { get; init; }

    /// <summary>B as A knows it.</summary>
    public required string OnA { get; init; }

    /// <summary>A as B knows it.</summary>
    public required string OnB { get; init; }

    /// <summary>The first library's public ID.</summary>
    public required string LibraryPub { get; init; }

    public static async Task<Sharing> StartAsync(FakeHost h, Func<MsfpConfig, MsfpConfig>? cfgA = null, Func<MsfpConfig, MsfpConfig>? cfgB = null,
        FakeTime? clock = null)
    {
        if (h.Libs.Count == 0)
        {
            h.Libs = [new Library { Id = "films", Name = "Films", Kind = "movies" }];
        }

        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A", clock: clock,
            with: c => (cfgA ?? (x => x))(c with { Catalog = h, Playback = h, Media = h, SheetEncoder = new TestSheets() }));
        var b = await TestServer.StartAsync(net, "https://b.example.org", "Server B", clock: clock,
            with: c => (cfgB ?? (x => x))(c with { SheetEncoder = new TestSheets() }));
        var (onA, onB) = await Kit.LinkAsync(a, b);
        await a.Engine.CheckAsync(onA);
        await b.Engine.CheckAsync(onB);
        await a.Engine.SetOffersAsync(onA, h.Libs.Select(l => l.Id));
        var libs = await b.Engine.TheirLibrariesAsync(onB);
        Assert.Equal(h.Libs.Count, libs.Count);
        await b.Engine.SetWantsAsync(onB, libs.Select(l => l.Id));
        await b.Engine.ReadNowAsync(onB);
        return new Sharing { Net = net, A = a, B = b, H = h, OnA = onA, OnB = onB, LibraryPub = libs[0].Id };
    }

    /// <summary>What B keeps of A's titles, by key.</summary>
    public async Task<Dictionary<string, Title>> PeerTitlesAsync()
    {
        IReadOnlyList<Title> titles = [];
        await B.Store.ReadAsync(async tx => titles = await tx.PeerTitlesAsync(OnB, "", 10_000_000), CancellationToken.None);
        return titles.ToDictionary(t => t.Key, StringComparer.Ordinal);
    }

    /// <summary>The exchanges A served whose path ends with /call.</summary>
    public List<Seen> Exchanges(string call) => Net.SeenBy(A.Host).Where(x => x.Target.EndsWith("/" + call, StringComparison.Ordinal)).ToList();

    /// <summary>Makes A hostile: rewrite changes its answers, signed again.</summary>
    public void Hostile(Func<string, HeaderList, byte[], byte[]?>? rewrite)
    {
        var key = Kit.KeyAsync(A, r => r.Current, "msfp current key").GetAwaiter().GetResult();
        Net.Rewrite(A.Host, rewrite, () => key);
    }

    public Task WaitAsync() => Task.WhenAll(A.WaitAsync(), B.WaitAsync());

    public void Dispose()
    {
        A.Dispose();
        B.Dispose();
    }
}

internal static class HostKit
{
    /// <summary>An engine's stream route, as a host mounts it: its not-found and fallback answers a plain 404.</summary>
    public static async Task<MsfpResponse> StreamAsync(MsfpEngine engine, MsfpRequest r)
    {
        var sink = new MemorySink();
        var outcome = await engine.HandleStreamAsync(r, sink);
        if (outcome != StreamOutcome.Served)
        {
            return new MsfpResponse { Status = 404, Headers = [new("Content-Type", "text/plain")], Body = "not found"u8.ToArray() };
        }

        var headers = new List<KeyValuePair<string, string>>();
        if (sink.ContentType.Length > 0)
        {
            headers.Add(new("Content-Type", sink.ContentType));
        }

        if (sink.Length >= 0)
        {
            headers.Add(new("Content-Length", sink.Length.ToString(CultureInfo.InvariantCulture)));
        }

        if (sink.ContentRange.Length > 0)
        {
            headers.Add(new("Content-Range", sink.ContentRange));
        }

        if (sink.Status is 200 or 206)
        {
            headers.Add(new("Accept-Ranges", "bytes"));
            if (sink.ETag.Length > 0)
            {
                headers.Add(new("ETag", "\"" + sink.ETag + "\""));
            }
        }

        if (sink.Status == 405)
        {
            headers.Add(new("Allow", "GET"));
        }

        return new MsfpResponse { Status = sink.Status, Headers = headers, Body = sink.Body };
    }

    /// <summary>A GET on a server's stream route, as sent (nothing decoded), with headers in pairs: its status.</summary>
    public static async Task<int> GetAsync(TestServer s, string target, string method = "GET", params string[] headers) =>
        (await GetFullAsync(s, target, method, headers)).Status;

    /// <summary>A request on a server's stream route, as sent (nothing decoded), with headers in pairs: its answer.</summary>
    public static async Task<MsfpResponse> GetFullAsync(TestServer s, string target, string method = "GET", params string[] headers)
    {
        var h = new HeaderList();
        for (var i = 0; i + 1 < headers.Length; i += 2)
        {
            h.Add(headers[i], headers[i + 1]);
        }

        return await StreamAsync(s.Engine, new MsfpRequest { Method = method, Target = target, Headers = h, Body = Stream.Null });
    }

    public const string Master = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-STREAM-INF:BANDWIDTH=5000000\nv0.m3u8\n";
    public const string MediaList = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.0,\nseg0.m4s\n#EXT-X-ENDLIST\n";

    /// <summary>A host with one film, movie/tmdb/949, in English and French, and its media.</summary>
    public static FakeHost FilmHost()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/949", "en", "fr");
        h.Files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["master.m3u8"] = System.Text.Encoding.UTF8.GetBytes(Master),
            ["v0.m3u8"] = System.Text.Encoding.UTF8.GetBytes(MediaList),
            ["init.mp4"] = "init"u8.ToArray(),
            ["seg0.m4s"] = "0123456789abcdefghij"u8.ToArray(),
            ["file"] = "0123456789abcdefghij"u8.ToArray(),
            ["subtitles/s0/0"] = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<i>Bonjour</i>\n"u8.ToArray(),
        };
        return h;
    }

    public static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    /// <summary>A track of the album 0c…0002, with one FLAC copy of handle copy, 20 bytes long (msfp-go's musicTrack).</summary>
    public static MusicTitle MusicTrack(string key, string copy, string lyrics = "") => new()
    {
        Key = key, Kind = "track", Libraries = ["music"], Name = "x", Album = "album/mb/0b000000-0000-4000-8000-000000000002", Disc = 1, Number = 1,
        Copies = [new MusicCopy { Id = copy, Name = "FLAC", DurationMs = 200_000, Container = "flac", Codec = "flac", Size = 20, Etag = "m-" + copy, Lyrics = lyrics }],
    };
}
