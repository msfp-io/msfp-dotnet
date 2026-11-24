using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Msfp.Protocol;

namespace Msfp.Examples.ShareFolder;

/// <summary>
/// A folder of films and series, shared as one library: the engine's
/// catalog (with images), playback and media. What it reads:
/// <code>
/// Heat (1995) [tmdbid-949].mkv             a film, by its TMDB ID
/// Heat (1995) [tmdbid-949].fr.vtt          its subtitles, a file per language
/// Heat (1995) [tmdbid-949].hls/            an HLS copy made beforehand (master.m3u8 and what it names)
/// Heat (1995) [tmdbid-949].trickplay/      seek-bar previews: layout.json, 0.jpg, 1.jpg…
/// Alien (1979) [tmdbid-348]/               a film in a folder of its own (its largest video file)
///     Trailers/  Extras/                   with its extras: trailers, featurettes
/// Severance (2022) [tmdbid-95396]/         a series: its episodes S&lt;n&gt;E&lt;n&gt; anywhere inside
///     Season 1/S01E01 [tmdbid-1949537].mkv an episode by its TMDB episode ID, placed at S01E01
///     Season 1/S01E02.mkv                  one by its numbers (TMDB doesn't list it yet)
///     Trailers/  Extras/                   the series' extras
/// Lain [tvdbid-78914]/                     a series by its TheTVDB ID: episodes by their numbers
/// Holidays (2019).mp4                      a local film: no public database knows it
/// Holidays (2019).jpg                      its image (.jpg or .png, beside it), optional
/// Home videos (2020)/                      a local series (poster.jpg inside, its image)
/// </code>
/// Local titles are shared only with the peers and groups the operator
/// allows them (local, group-local). Their keys and the extras' are made with
/// the engine's OpaqueKeyPart from their paths, so a peer can't tell a path
/// from them; moving a file gives it a new key.
/// <para>
/// Files are served as they are: by byte ranges (delivery file), or as their
/// HLS copy (a remux made beforehand) when the viewer doesn't play their
/// container. A play without delivery of a copy outside the baseline is
/// transcoded by ffmpeg before the answer (Baseline.cs). A copy is described
/// from its file (Probes.cs).
/// </para>
/// </summary>
internal sealed partial class Folder(string dir) : ICatalog, IImages, IPlayback, IMedia
{
    private const string Lib = "folder";

    private readonly Probes _probes = new();

    /// <summary>The engine's OpaqueKeyPart, set once it is open.</summary>
    public Func<string, string> Opaque { get; set; } = _ => throw new InvalidOperationException("the engine isn't open yet");

    // A title the folder holds: its title; for a film, an episode or an
    // extra, its video file (its copy's handle, a path in the folder); for a
    // local film or series, its image file.
    private sealed record Item(VideoTitle Title, string Video = "", string Image = "");

    [GeneratedRegex(@"\.(mp4|m4v|mkv|webm|mov|ts|avi)$")]
    private static partial Regex VideoExt();

    // "Name (Year) [tmdbid-N]", the year and the ID optional.
    [GeneratedRegex(@"^(.*?)\s*(?:\((\d{4})\))?\s*(?:\[(tmdbid|tvdbid)-([1-9][0-9]{0,9})\])?$")]
    private static partial Regex Named();

    [GeneratedRegex(@"\bS(\d{1,4})E(\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Episode();

    [GeneratedRegex(@"\[tmdbid-([1-9][0-9]{0,9})\]")]
    private static partial Regex EpisodeId();

    private static readonly Dictionary<string, string> Extras = new(StringComparer.Ordinal)
    {
        ["Trailers"] = "trailer",
        ["Extras"] = "featurette",
    };

    private static string Stem(string path) => VideoExt().Replace(path, "");

    // Whether a folder is a video file's own (its HLS copy, its previews), not a title's.
    private static bool SideDir(string name) =>
        name.EndsWith(".hls", StringComparison.Ordinal) || name.EndsWith(".trickplay", StringComparison.Ordinal);

    // Reads the folder: every title it holds, by key.
    private Dictionary<string, Item> Scan()
    {
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        if (!Directory.Exists(dir))
        {
            return items;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(path);
            if (Directory.Exists(path))
            {
                if (!SideDir(name))
                {
                    ScanDir(items, name);
                }
            }
            else if (VideoExt().IsMatch(name) && Film(Stem(name), name) is { } t)
            {
                items[t.Key] = new Item(t, name, LocalImage(t, dir, Stem(name)));
            }
        }

        return items;
    }

    // The title of a film named name: by its TMDB ID, or local (its key made
    // from where it lies: rel); null for one named by TheTVDB, which keys no
    // films.
    private VideoTitle? Film(string name, string rel)
    {
        var m = Named().Match(name);
        var t = new VideoTitle { Kind = "movie", Libraries = [Lib] };
        switch (m.Groups[3].Value)
        {
            case "tmdbid":
                t.Key = "movie/tmdb/" + m.Groups[4].Value;
                break;
            case "tvdbid":
                return null;
            default:
                (t.Key, t.Name, t.Year) = ("movie/local/" + Opaque(rel), m.Groups[1].Value, Year(m.Groups[2].Value));
                break;
        }

        return t;
    }

    // Reads a title's folder: a series when it holds episodes, a film
    // otherwise (its largest video file); and their extras.
    private void ScanDir(Dictionary<string, Item> items, string name)
    {
        var top = Path.Combine(dir, name);
        var episodes = new List<string>();
        var videos = new List<string>();
        Walk(top);

        void Walk(string at)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(at).Order(StringComparer.Ordinal))
            {
                var entry = Path.GetFileName(path);
                if (Directory.Exists(path))
                {
                    if (!SideDir(entry) && !Extras.ContainsKey(entry))
                    {
                        Walk(path);
                    }
                }
                else if (VideoExt().IsMatch(entry))
                {
                    var rel = Path.GetRelativePath(dir, path);
                    if (Episode().IsMatch(entry))
                    {
                        episodes.Add(rel);
                    }
                    else if (at == top)
                    {
                        videos.Add(rel);
                    }
                }
            }
        }

        VideoTitle? t = null;
        if (episodes.Count > 0)
        {
            t = Series(items, name, episodes);
        }
        else if (videos.Count > 0 && Film(name, name) is { } film)
        {
            t = film;
            items[t.Key] = new Item(t, videos.MaxBy(v => new FileInfo(Path.Combine(dir, v)).Length)!);
        }

        if (t is null)
        {
            return;
        }

        items[t.Key] = items[t.Key] with { Image = LocalImage(t, top, "poster") };
        foreach (var (folderName, kind) in Extras)
        {
            var extras = Path.Combine(top, folderName);
            if (!Directory.Exists(extras))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(extras).Where(p => VideoExt().IsMatch(p)))
            {
                var rel = Path.GetRelativePath(dir, path);
                var key = t.Key + "/x/" + Opaque(rel);
                items[key] = new Item(new VideoTitle
                {
                    Key = key, Kind = "extra", Libraries = [Lib], ExtraType = kind, // an extra carries no name
                }, rel);
            }
        }
    }

    // Adds a series, its seasons and its episodes, and gives the series.
    private VideoTitle Series(Dictionary<string, Item> items, string name, List<string> episodes)
    {
        var m = Named().Match(name);
        var s = new VideoTitle { Kind = "series", Libraries = [Lib] };
        var tmdb = m.Groups[3].Value == "tmdbid";
        switch (m.Groups[3].Value)
        {
            case "tmdbid":
                s.Key = "series/tmdb/" + m.Groups[4].Value;
                break;
            case "tvdbid":
                s.Key = "series/tvdb/" + m.Groups[4].Value;
                break;
            default:
                (s.Key, s.Name, s.Year) = ("series/local/" + Opaque(name), m.Groups[1].Value, Year(m.Groups[2].Value));
                break;
        }

        items[s.Key] = new Item(s);
        foreach (var rel in episodes)
        {
            var file = Path.GetFileName(rel);
            var n = Episode().Match(file);
            int season = Number(n.Groups[1].Value), number = Number(n.Groups[2].Value);
            var seasonKey = s.Key + "/s" + season.ToString(CultureInfo.InvariantCulture);
            items[seasonKey] = new Item(new VideoTitle { Key = seasonKey, Kind = "season", Libraries = [Lib] });
            var e = new VideoTitle { Key = seasonKey + "/e" + number.ToString(CultureInfo.InvariantCulture), Kind = "episode", Libraries = [Lib] };
            if (EpisodeId().Match(file) is { Success: true } id && tmdb)
            {
                // Listed by TMDB: keyed by its episode ID, placed by its numbers.
                e.Key = s.Key + "/e" + id.Groups[1].Value;
                e.Place = new Msfp.Place { Season = season, Episode = number };
            }

            items[e.Key] = new Item(e, rel);
        }

        return s;
    }

    public ValueTask<IReadOnlyList<Library>> LibrariesAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<Library>>([new Library { Id = Lib, Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)) }]);

    public ValueTask<TitlePage> TitlesAsync(IReadOnlyList<string> libraries, string after, int limit, CancellationToken ct)
    {
        var items = Scan();
        var titles = new List<Title>();
        var more = false;
        foreach (var key in items.Keys.Order(StringComparer.Ordinal))
        {
            if (string.CompareOrdinal(key, after) <= 0)
            {
                continue;
            }

            if (titles.Count == limit)
            {
                more = true;
                break;
            }

            var it = items[key];
            var t = it.Title with { Image = it.Image.Length > 0 };
            if (it.Video.Length > 0)
            {
                if (CopyOf(it.Video) is not { } copy)
                {
                    continue;
                }

                t.Copies = [copy];
            }

            titles.Add(t);
        }

        return ValueTask.FromResult(new TitlePage(titles, more));
    }

    /// <summary>A local film's or series' image (MSFP catalog section 4.4).</summary>
    public async ValueTask<byte[]> ImageAsync(string key, CancellationToken ct) =>
        Scan().TryGetValue(key, out var it) && it.Image.Length > 0
            ? await File.ReadAllBytesAsync(it.Image, ct)
            : throw new MsfpException(MsfpError.NotFound);

    // Describes a video file as the catalog lists a copy (MSFP video section
    // 3.2): its exact size, its media type and etag, what ffprobe says of it
    // (Probes.cs, with the Msfp.Describe package), its .vtt subtitles
    // (numbered after the file's streams; the file's own tracks first, at
    // most 64 in all), its previews' layout. Its ID is its
    // handle: its path in the folder.
    private Protocol.Copy? CopyOf(string rel)
    {
        var path = Path.Combine(dir, rel);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1)
        {
            return null;
        }

        var (c, streams) = _probes.Describe(path, info);
        if (c.ETag.Length == 0)
        {
            return null;
        }

        var external = Subtitles(path).Select((s, i) => new CopySubtitle("s" + (streams + i).ToString(CultureInfo.InvariantCulture), "webvtt")
        {
            Language = Language(s),
            External = true,
        });
        return c with
        {
            Id = rel,
            Size = info.Length,
            // A copy lists at most 64 subtitle tracks (video section 3.2):
            // the file's own first.
            SubtitleTracks = [.. c.SubtitleTracks.Concat(external).Take(Copies.MaxSubtitleTracks)],
            PreviewLayout = Previews(path),
        };
    }

    // The .vtt files beside a video file, s<n> by position.
    private static List<string> Subtitles(string path)
    {
        var stem = Path.GetFileName(Stem(path)) + ".";
        var at = Path.GetDirectoryName(path)!;
        return Directory.EnumerateFiles(at).Select(Path.GetFileName).OfType<string>()
            .Where(n => n.StartsWith(stem, StringComparison.Ordinal) && n.EndsWith(".vtt", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(n => Path.Combine(at, n)).ToList();
    }

    // A subtitles file's language, from its name ("fr"); null when it isn't one.
    private static string? Language(string path) =>
        Languages.TrySentTag(Path.GetExtension(Path.GetFileNameWithoutExtension(path)).TrimStart('.'), out var tag) ? tag : null;

    // A video file's previews' layout, layout.json in its .trickplay folder:
    // {"tileWidth":320,"tileHeight":180,"columns":10,"rows":10,
    // "intervalMs":10000,"tileCount":540}, its sheets beside it named 0.jpg,
    // 1.jpg… (the engine checks and re-encodes each before it leaves); null
    // for none, or out of MSFP's bounds.
    private static PreviewLayout? Previews(string path)
    {
        try
        {
            return Copies.ReadPreviewLayout(JsonNode.Parse(File.ReadAllBytes(Stem(path) + ".trickplay/layout.json")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the folder's one library is among those offered to the peer a
    /// call is made for: a copy's handle (its file's path) is resolved only
    /// then.
    /// </summary>
    private static bool OfferedHere(PeerRef peer) => peer.Libraries.Contains(Lib);

    /// <summary>
    /// Tells what the catalog doesn't: no fonts; every .vtt file is served
    /// apart (as text: video section 5.4), the subtitle tracks inside the file
    /// aren't (they play with the file as it is).
    /// </summary>
    public ValueTask<CopyInfo> DescribeAsync(PeerRef peer, string copy, CancellationToken ct) =>
        !OfferedHere(peer) ? throw new MsfpException(MsfpError.NotFound) : ValueTask.FromResult(new CopyInfo
        {
            NotApart = Local(copy) && CopyOf(copy) is { } c ? c.SubtitleTracks.Where(s => !s.External).Select(s => s.Id).ToList() : null,
        });

    /// <summary>
    /// Serves what the engine decided (the request's Served), or refuses: the
    /// file when the viewer's server asks it (delivery file, always that of a
    /// download or a scan); its HLS copy, made beside it, for a remux to its
    /// segments' media type (video/mp2t for .ts segments, video/mp4
    /// otherwise), and without delivery when the engine found the copy within
    /// the baseline (H.264 High 4.1 8-bit SDR BT.709 and AAC-LC stereo, the
    /// HLS copy then taken to be the same streams); without delivery outside
    /// it, a transcode ffmpeg makes (Baseline.cs). It makes no transcode asked
    /// by delivery, and no audio renditions, which the answer then says
    /// (<see cref="Started"/>'s AudioRenditions false: MSFP playback section
    /// 2.3). The handle is the protocol and the file's path.
    /// </summary>
    public ValueTask<Started> StartAsync(StartRequest request, CancellationToken ct)
    {
        if (!Local(request.Copy) || !OfferedHere(request.Peer))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var path = Path.Combine(dir, request.Copy);
        var hlsDir = Stem(path) + ".hls";
        var hls = File.Exists(Path.Combine(hlsDir, "master.m3u8"));
        var (method, protocol) = ("direct", "file");
        switch (request.Delivery)
        {
            case Playback.DeliveryFile:
                break;
            case Playback.DeliveryTranscode:
                throw new MsfpException(MsfpError.DeliveryRefused);
            case Playback.DeliveryRemux:
                var segments = hls && Directory.EnumerateFiles(hlsDir, "*.ts").Any() ? "mp2t" : "mp4";
                (method, protocol) = hls && request.Served.Segments == segments ? ("remux", "hls") : throw new MsfpException(MsfpError.DeliveryRefused);
                break;
            case "" when request.Served.Method == Playback.MethodTranscode:
                return BaselineAsync(request, path, ct);
            default:
                (method, protocol) = hls ? ("remux", "hls") : throw new MsfpException(MsfpError.NotPlayable);
                break;
        }

        request.Admit?.Invoke(method);
        return ValueTask.FromResult(new Started
        {
            Handle = protocol + " " + request.Copy,
            Method = method,
            Protocol = protocol,
            PositionMs = request.StartMs,
        });
    }

    public ValueTask ProgressAsync(Report report, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>
    /// Answers what a playback's ticket opens (or, for a sheet under a
    /// previews ticket, OfCopy, a copy's: its handle is then the file's path).
    /// </summary>
    public async ValueTask ServeAsync(PeerRef peer, string handle, MediaPath path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        var rel = path.OfCopy ? handle : handle[(handle.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        if (!Local(rel) || path.Entry != 0 || !OfferedHere(peer))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var file = Path.Combine(dir, rel);
        var stem = Stem(file);
        var hlsDir = (path.OfCopy ? null : Made(handle)) ?? stem + ".hls";
        switch (path.Kind)
        {
            case MediaKind.File:
                await ServeFileAsync(file, range, output, ct);
                return;
            case MediaKind.Master:
                await ServeFileAsync(Path.Combine(hlsDir, "master.m3u8"), null, output, ct);
                return;
            case MediaKind.Playlist or MediaKind.Segment:
                await ServeFileAsync(Path.Combine(hlsDir, path.Name), range, output, ct);
                return;
            case MediaKind.Sheet:
                await ServeFileAsync(Path.Combine(stem + ".trickplay", path.Sheet.ToString(CultureInfo.InvariantCulture) + ".jpg"), null, output, ct);
                return;
            case MediaKind.Subtitles:
                var subs = Subtitles(file);
                var (_, streams) = _probes.Describe(file, new FileInfo(file));
                var n = path.Track.StartsWith('s') && int.TryParse(path.Track.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                    ? i - streams : -1;
                if (n < 0 || n >= subs.Count)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                var vtt = await File.ReadAllBytesAsync(subs[n], ct);
                if (!path.Whole && !WebVtt.TryPart(vtt, path.Part, out vtt))
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                output.Start(200, "text/vtt; charset=utf-8", vtt.Length, "");
                await output.WriteAsync(vtt, ct);
                return;
            default:
                throw new MsfpException(MsfpError.NotFound);
        }
    }

    // Whether a handle is a path inside the folder: never absolute, never "..".
    private static bool Local(string rel) =>
        rel.Length > 0 && !Path.IsPathRooted(rel) && !rel.Split('/', '\\').Contains("..");

    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        [".m3u8"] = "application/vnd.apple.mpegurl",
        [".ts"] = "video/mp2t",
        [".m4s"] = "video/iso.segment",
        [".jpg"] = "image/jpeg",
        [".mp4"] = "video/mp4",
    };

    // Answers a file, or the range of it asked. For the copy's file the
    // engine gives an exact range, and writes the headers itself.
    private static async Task ServeFileAsync(string path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        FileStream file;
        try
        {
            file = File.OpenRead(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        await using (file)
        {
            var size = file.Length;
            var (first, last) = (0L, size - 1);
            var (status, contentRange) = (200, "");
            if (range is not null)
            {
                (first, last) = (range.First, Math.Min(range.Last, size - 1));
                if (range.Suffix > 0)
                {
                    (first, last) = (Math.Max(size - range.Suffix, 0), size - 1);
                }
                else if (range.Last < 0)
                {
                    last = size - 1;
                }

                if (first >= size)
                {
                    throw new MsfpException(MsfpError.RangeNotSatisfiable);
                }

                (status, contentRange) = (206, $"bytes {first}-{last}/{size}");
            }

            output.Start(status, Types.GetValueOrDefault(Path.GetExtension(path)) ?? "application/octet-stream", last - first + 1, contentRange);
            file.Position = first;
            var buffer = new byte[64 * 1024];
            for (var left = last - first + 1; left > 0;)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                left -= read;
            }
        }
    }

    // A local film's or series' image: name.jpg or name.png in at; "" for
    // none, and for every other title.
    private static string LocalImage(VideoTitle t, string at, string name)
    {
        if (!t.Key.Contains("/local/", StringComparison.Ordinal))
        {
            return "";
        }

        foreach (var ext in new[] { ".jpg", ".png" })
        {
            var p = Path.Combine(at, name + ext);
            if (new FileInfo(p) is { Exists: true, Length: > 0 })
            {
                return p;
            }
        }

        return "";
    }

    private static int Number(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static int Year(string s) => Number(s) is var y and >= 1800 and <= 9999 ? y : 0;
}
