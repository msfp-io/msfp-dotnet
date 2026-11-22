using System.Text.RegularExpressions;
using Msfp.Protocol;

namespace Msfp.Examples.ShareFolder;

/// <summary>
/// A folder of films, shared: the engine's catalog, playback and media. A
/// film is a file named with its TMDB ID, "Title (Year) [tmdbid-949].mp4";
/// beside it, optionally, "&lt;same name&gt;.hls/" holding an HLS copy made
/// beforehand (master.m3u8 and what it names, as ffmpeg writes them), and
/// "&lt;same name&gt;.&lt;language&gt;.vtt" subtitles. Files are served as
/// they are: direct when the viewer plays their container, the HLS copy
/// otherwise. Nothing is transcoded.
/// </summary>
internal sealed partial class Folder(string dir) : ICatalog, IPlayback, IMedia
{
    // What the folder holds of one title; subtitles are s<n> by position.
    private sealed record Film(string File, string? Hls, List<string> Subs);

    [GeneratedRegex(@"\[tmdbid-([1-9][0-9]{0,9})\]\.(mp4|m4v|mkv|webm|mov|ts)$")]
    private static partial Regex TmdbId();

    // Reads the folder: its films by title key.
    private Dictionary<string, Film> Scan()
    {
        var names = Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>().ToList()
            : [];
        var films = new Dictionary<string, Film>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            var m = TmdbId().Match(n);
            if (!m.Success)
            {
                continue;
            }

            var stem = n[..^(m.Groups[2].Length + 1)];
            var hls = Path.Combine(dir, stem + ".hls");
            var subs = names.Where(s => s.StartsWith(stem + ".", StringComparison.Ordinal) && s.EndsWith(".vtt", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Select(s => Path.Combine(dir, s)).ToList();
            films["movie/tmdb/" + m.Groups[1].Value] = new Film(Path.Combine(dir, n),
                File.Exists(Path.Combine(hls, "master.m3u8")) ? hls : null, subs);
        }

        return films;
    }

    // Describes the film's one copy, its file, as the catalog lists it (MSFP
    // video section 3.2): its size, an etag from its size and its time, its
    // container from its extension; its length and streams are left unknown
    // (the example reads no file), its subtitles from the .vtt files beside
    // it. Its ID is the handle Playback and Media are given.
    private static Protocol.Copy? CopyOf(Film film, string handle)
    {
        var info = new FileInfo(film.File);
        if (!info.Exists || info.Length < 1)
        {
            return null;
        }

        var etag = Convert.ToString(info.Length, 16) + "-" + Convert.ToString(info.LastWriteTimeUtc.Ticks, 16);
        return new Protocol.Copy(handle, 0, Names.NameOrOther(Names.Containers, Path.GetExtension(film.File).TrimStart('.')),
            info.Length, etag, [new CopyAudio("a0", Names.Other) { Default = true }])
        {
            SubtitleTracks = film.Subs.Select((s, i) => new CopySubtitle("s" + i, "webvtt") { Language = Language(s), External = true }).ToList(),
        };
    }

    // A subtitles file's language, from its name.
    private static string Language(string path) => Path.GetExtension(Path.GetFileNameWithoutExtension(path)).TrimStart('.');

    public ValueTask<IReadOnlyList<Library>> LibrariesAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<Library>>([new Library { Id = "films", Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), Kind = "movies" }]);

    public ValueTask<TitlePage> TitlesAsync(IReadOnlyList<string> libraries, string after, int limit, CancellationToken ct)
    {
        var films = Scan();
        var titles = new List<Title>();
        var more = false;
        foreach (var key in films.Keys.Order(StringComparer.Ordinal))
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

            if (CopyOf(films[key], key) is not { } copy)
            {
                continue;
            }

            titles.Add(new VideoTitle { Key = key, Kind = "movie", Libraries = ["films"], Copies = [copy] });
        }

        return ValueTask.FromResult(new TitlePage(titles, more));
    }

    /// <summary>Tells what the catalog doesn't: no previews, no fonts; its .vtt files are served apart.</summary>
    public ValueTask<CopyInfo> DescribeAsync(string copy, CancellationToken ct) => ValueTask.FromResult(new CopyInfo());

    /// <summary>
    /// Serves the file when the viewer plays its container, the HLS copy
    /// otherwise; the handle is the protocol and the key.
    /// </summary>
    public ValueTask<Started> StartAsync(StartRequest request, CancellationToken ct)
    {
        if (!Scan().TryGetValue(request.Copy, out var film))
        {
            throw new MsfpException(MsfpError.NotPlayable);
        }

        var ext = Path.GetExtension(film.File).TrimStart('.');
        var (method, protocol) = ("direct", "file");
        switch (request.Delivery)
        {
            case Playback.DeliveryTranscode:
                throw new MsfpException(MsfpError.DeliveryRefused);
            case Playback.DeliveryRemux:
                if (film.Hls is null)
                {
                    throw new MsfpException(MsfpError.DeliveryRefused);
                }

                (method, protocol) = ("remux", "hls");
                break;
        }

        if (request.Delivery.Length == 0 && !request.Capabilities.Containers.Contains(ext))
        {
            if (film.Hls is null)
            {
                throw new MsfpException(MsfpError.NotPlayable);
            }

            (method, protocol) = ("remux", "hls");
        }

        request.Admit?.Invoke(method);
        return ValueTask.FromResult(new Started
        {
            Handle = protocol + " " + request.Copy,
            Method = method,
            Protocol = protocol,
            MimeType = "video/" + ext.Replace("mkv", "x-matroska", StringComparison.Ordinal),
            PositionMs = request.StartMs,
        });
    }

    public ValueTask ProgressAsync(Report report, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StopAsync(string handle, EndReason reason, CancellationToken ct) => ValueTask.CompletedTask;

    public async ValueTask ServeAsync(string handle, MediaPath path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        var key = handle[(handle.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        if (!Scan().TryGetValue(key, out var film))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        switch (path.Kind)
        {
            case MediaKind.File:
                await ServeFileAsync(film.File, range, output, ct);
                return;
            case MediaKind.Master when film.Hls is not null:
                await ServeFileAsync(Path.Combine(film.Hls, "master.m3u8"), null, output, ct);
                return;
            case MediaKind.Playlist or MediaKind.Segment when film.Hls is not null:
                await ServeFileAsync(Path.Combine(film.Hls, path.Name), range, output, ct);
                return;
            case MediaKind.Subtitles:
                var n = int.TryParse(path.Track.AsSpan(1), out var i) && path.Track.StartsWith('s') ? i : -1;
                if (n < 0 || n >= film.Subs.Count || !WebVtt.TryPart(await File.ReadAllBytesAsync(film.Subs[n], ct), path.Part, out var part))
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                output.Start(200, "text/vtt; charset=utf-8", part.Length, "");
                await output.WriteAsync(part, ct);
                return;
            default:
                throw new MsfpException(MsfpError.NotFound);
        }
    }

    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        [".m3u8"] = "application/vnd.apple.mpegurl",
        [".ts"] = "video/mp2t",
        [".m4s"] = "video/iso.segment",
    };

    // Answers a file, or the range of it asked.
    private static async Task ServeFileAsync(string path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        FileStream file;
        try
        {
            file = File.OpenRead(path);
        }
        catch (IOException)
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

            var ext = Path.GetExtension(path);
            output.Start(status, Types.GetValueOrDefault(ext) ?? "video/" + ext.TrimStart('.'), last - first + 1, contentRange);
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
}
