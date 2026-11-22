using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// One version of a film, an episode or an extra (video section 3.2): what
/// an owning server sends (<see cref="Copies.Write(Copy)"/>), and what a
/// viewer's server keeps (<see cref="Copies.Read(JsonNode?)"/>), in bounds,
/// written again unchanged. A null member, an empty string or list and a
/// false flag are absent; <see cref="AudioTracks"/> is required, and may be
/// empty.
/// </summary>
/// <param name="Id">The copy's ID, of a library ID's form (<see cref="Playback.Id"/>).</param>
/// <param name="DurationMs">Its duration, 0 to <see cref="Copies.MaxDurationMs"/>.</param>
/// <param name="Container">Its container, of <see cref="Names.Containers"/> or <see cref="Names.Other"/>.</param>
/// <param name="Size">Its size in bytes, 1 to <see cref="Copies.MaxSize"/>.</param>
/// <param name="ETag">Its entity tag, of an ID's form.</param>
/// <param name="AudioTracks">Its audio tracks.</param>
public sealed record Copy(string Id, long DurationMs, string Container, long Size, string ETag, IReadOnlyList<CopyAudio> AudioTracks)
{
    /// <summary>Its name, plain text of at most <see cref="Copies.MaxName"/> code points.</summary>
    public string? Name { get; init; }

    /// <summary>Its edition, of <see cref="Names.Editions"/>.</summary>
    public string? Edition { get; init; }

    /// <summary>Where play starts, 0 to <see cref="DurationMs"/>.</summary>
    public long? StartMs { get; init; }

    /// <summary>Its files, when it is made of several: 1 to <see cref="Copies.MaxParts"/>, their sizes adding up to <see cref="Size"/>.</summary>
    public IReadOnlyList<CopyPart> Parts { get; init; } = [];

    /// <summary>Its bitrate, 500 to <see cref="Copies.MaxBitrateKbps"/>, rounded down to a multiple of 500.</summary>
    public long? BitrateKbps { get; init; }

    /// <summary>Its picture.</summary>
    public VideoStream? Video { get; init; }

    /// <summary>Its subtitle tracks.</summary>
    public IReadOnlyList<CopySubtitle> SubtitleTracks { get; init; } = [];

    /// <summary>Its chapters, in order.</summary>
    public IReadOnlyList<CopyChapter> Chapters { get; init; } = [];

    /// <summary>Its skip markers.</summary>
    public IReadOnlyList<CopySegment> Segments { get; init; } = [];

    /// <summary>Whether it has seek-bar previews (video section 5.5).</summary>
    public bool Previews { get; init; }

    /// <summary>The day it was added, YYYY-MM-DD.</summary>
    public string? Added { get; init; }

    /// <summary>Its 3D mode, of <see cref="Copies.ThreeDModes"/>.</summary>
    public string? ThreeD { get; init; }
}

/// <summary>One file of a copy made of several (video section 3.2).</summary>
public sealed record CopyPart(long Size, long DurationMs);

/// <summary>A copy's picture (video section 3.3).</summary>
/// <param name="Codec">Its codec, of <see cref="Names.VideoCodecs"/> or <see cref="Names.Other"/>.</param>
/// <param name="Width">Its width, 1 to <see cref="Copies.MaxVideoSide"/>.</param>
/// <param name="Height">Its height, 1 to <see cref="Copies.MaxVideoSide"/>.</param>
public sealed record VideoStream(string Codec, int Width, int Height)
{
    /// <summary>Its codec tag, 1 to 8 letters and digits.</summary>
    public string? CodecTag { get; init; }

    /// <summary>Its profile, 1 to 32 code points.</summary>
    public string? Profile { get; init; }

    /// <summary>Its level, 0 to 1000.</summary>
    public double? Level { get; init; }

    /// <summary>Its bit depth: 8, 10 or 12.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Its HDR formats, in <see cref="Names.HdrFormats"/>' order; empty for standard range.</summary>
    public IReadOnlyList<string> Hdr { get; init; } = [];

    /// <summary>Its Dolby Vision profile, with dv in <see cref="Hdr"/>.</summary>
    public VideoDv? Dv { get; init; }

    /// <summary>Its frame rate, above 0 and at most 1000, to two decimals.</summary>
    public double? Fps { get; init; }

    /// <summary>Whether it is interlaced.</summary>
    public bool Interlaced { get; init; }

    /// <summary>Its colour description.</summary>
    public VideoColour? Colour { get; init; }
}

/// <summary>A Dolby Vision picture's profile (0 to 10), level (0 to 13) and base-layer compatibility ID (0 to 15).</summary>
public sealed record VideoDv(int Profile, int Level, int Compatibility);

/// <summary>A picture's colour description, as ffmpeg names it (1 to 32 characters of a-z 0-9 _ -); <see cref="Range"/> is tv or pc. At least one member is set.</summary>
public sealed record VideoColour(string? Primaries, string? Transfer, string? Matrix, string? Range);

/// <summary>A copy's audio track (video section 3.3), with the a&lt;n&gt; ID play gives it.</summary>
/// <param name="Id">Its ID (<see cref="Playback.TryTrackId"/>, a).</param>
/// <param name="Codec">Its codec, of <see cref="Names.AudioCodecs"/> or <see cref="Names.Other"/>.</param>
public sealed record CopyAudio(string Id, string Codec)
{
    /// <summary>Its language, a valid tag (<see cref="Languages.ValidTag"/>).</summary>
    public string? Language { get; init; }

    /// <summary>Its channels, 1 to 64.</summary>
    public int? Channels { get; init; }

    /// <summary>Its role, of <see cref="Copies.AudioRoles"/>.</summary>
    public string? Role { get; init; }

    /// <summary>Whether it is the default track.</summary>
    public bool Default { get; init; }

    /// <summary>Whether it is forced.</summary>
    public bool Forced { get; init; }

    /// <summary>Its index among the file's streams, 0 to 999.</summary>
    public int? FileIndex { get; init; }

    /// <summary>Its profile, of <see cref="Names.AudioProfiles"/>.</summary>
    public string? Profile { get; init; }

    /// <summary>Its channel layout, 1 to 32 characters of A-Z a-z 0-9 ( ) . _ -.</summary>
    public string? Layout { get; init; }

    /// <summary>Its sample rate, 8,000 to 768,000.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Its bit depth, 8 to 32.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Its title, plain text of at most <see cref="Copies.MaxName"/> code points.</summary>
    public string? Title { get; init; }

    /// <summary>Whether it is in the title's original language.</summary>
    public bool Original { get; init; }
}

/// <summary>A copy's subtitle track (video section 3.3), with the s&lt;n&gt; ID play gives it.</summary>
/// <param name="Id">Its ID (<see cref="Playback.TryTrackId"/>, s).</param>
/// <param name="Format">Its source format, of <see cref="Names.SubtitleFormats"/>.</param>
public sealed record CopySubtitle(string Id, string Format)
{
    /// <summary>Its language, a valid tag (<see cref="Languages.ValidTag"/>).</summary>
    public string? Language { get; init; }

    /// <summary>Whether it is the default track.</summary>
    public bool Default { get; init; }

    /// <summary>Whether it is forced.</summary>
    public bool Forced { get; init; }

    /// <summary>Whether it is for the deaf and hard of hearing.</summary>
    public bool HearingImpaired { get; init; }

    /// <summary>Whether it is a file beside the copy's.</summary>
    public bool External { get; init; }

    /// <summary>Its index among the file's streams, 0 to 999.</summary>
    public int? FileIndex { get; init; }

    /// <summary>Its title, plain text of at most <see cref="Copies.MaxName"/> code points.</summary>
    public string? Title { get; init; }
}

/// <summary>A chapter of a copy (video section 3.4), its name plain text of at most <see cref="Copies.MaxChapterName"/> code points.</summary>
public sealed record CopyChapter(long StartMs, string? Name = null);

/// <summary>A skip marker of a copy (video section 3.4): <paramref name="Kind"/> is one of <see cref="Copies.SegmentKinds"/>, before <paramref name="EndMs"/>.</summary>
public sealed record CopySegment(string Kind, long StartMs, long EndMs, bool Final = false);

/// <summary>
/// The copies of a film, an episode or an extra (MSFP video sections 3.2 to
/// 3.8): their bounds, what a viewer's server keeps of them, how an owning
/// server writes them, and their order.
/// </summary>
public static partial class Copies
{
    /// <summary>How many copies a title keeps at most, the first good ones (video section 3.8).</summary>
    public const int MaxCopies = 8;

    /// <summary>The longest a copy may last: 100 days.</summary>
    public const long MaxDurationMs = 8_640_000_000;

    /// <summary>The greatest size of a copy, and of a part.</summary>
    public const long MaxSize = 999_999_999_999_999;

    /// <summary>How many parts a copy may have.</summary>
    public const int MaxParts = 16;

    /// <summary>How many audio tracks a copy keeps at most.</summary>
    public const int MaxAudioTracks = 32;

    /// <summary>How many subtitle tracks a copy keeps at most.</summary>
    public const int MaxSubtitleTracks = 64;

    /// <summary>How many chapters a copy keeps at most.</summary>
    public const int MaxChapters = 256;

    /// <summary>How many skip markers a copy keeps at most.</summary>
    public const int MaxSegments = 32;

    /// <summary>The length of a copy's name and of a track's title, in code points (video section 3.6).</summary>
    public const int MaxName = 32;

    /// <summary>The length of a chapter's name, in code points.</summary>
    public const int MaxChapterName = 64;

    /// <summary>The greatest bitrate of a copy.</summary>
    public const long MaxBitrateKbps = 10_000_000;

    /// <summary>The greatest width or height of a picture.</summary>
    public const int MaxVideoSide = 100_000;

    /// <summary>A copy's 3D modes (video section 3.2).</summary>
    public static readonly IReadOnlyList<string> ThreeDModes = ["sbs", "tab", "mvc"];

    /// <summary>An audio track's roles (video section 3.3).</summary>
    public static readonly IReadOnlyList<string> AudioRoles = ["commentary", "description"];

    /// <summary>The kinds of skip markers (video section 3.4).</summary>
    public static readonly IReadOnlyList<string> SegmentKinds = ["intro", "outro", "recap", "preview", "commercial"];

    private static readonly IReadOnlyList<string> ColourRanges = ["tv", "pc"];

    [GeneratedRegex(@"^[A-Za-z0-9]{1,8}\z")]
    private static partial Regex CodecTagRe();

    [GeneratedRegex(@"^[A-Za-z0-9()._-]{1,32}\z")]
    private static partial Regex LayoutRe();

    [GeneratedRegex(@"^[a-z0-9_-]{1,32}\z")]
    private static partial Regex ColourRe();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex DateRe();

    /// <summary>
    /// Reads one entry of a title's copies as video section 3.8 says: null,
    /// to skip it, when it isn't an object, when its id or etag isn't of an
    /// ID's form, when durationMs or size isn't an integer in range, when
    /// container isn't a string, or when audioTracks isn't an array. Its other
    /// members, out of range or of the wrong type, count as absent alone; a
    /// bad entry of one of its lists is skipped alone. Whether its id repeats
    /// an earlier copy's is the title's to check (<see cref="Video.ReadTitle(JsonNode?)"/> does).
    /// </summary>
    public static Copy? Read(JsonNode? entry)
    {
        if (entry is not JsonObject o ||
            JsonMembers.AsString(o["id"]) is not { } id || !Playback.Id(id) ||
            !Integer(o["durationMs"], 0, MaxDurationMs, out var duration) ||
            JsonMembers.AsString(o["container"]) is not { } container ||
            !Integer(o["size"], 1, MaxSize, out var size) ||
            JsonMembers.AsString(o["etag"]) is not { } etag || !Playback.Id(etag) ||
            o["audioTracks"] is not JsonArray audio)
        {
            return null;
        }

        return new Copy(id, duration, Names.NameOrOther(Names.Containers, container), size, etag, ReadAudioTracks(audio))
        {
            Name = Text(o["name"], MaxName),
            Edition = JsonMembers.AsString(o["edition"]) is { } edition ? Names.NameOrOther(Names.Editions, edition) : null,
            StartMs = Integer(o["startMs"], 0, duration, out var start) ? start : null,
            Parts = ReadParts(o["parts"], size),
            BitrateKbps = Integer(o["bitrateKbps"], 500, MaxBitrateKbps, out var bitrate) ? bitrate / 500 * 500 : null,
            Video = ReadVideoStream(o["video"]),
            SubtitleTracks = ReadSubtitleTracks(o["subtitleTracks"]),
            Chapters = ReadChapters(o["chapters"], duration),
            Segments = ReadSegments(o["segments"], duration),
            Previews = Video.True(o["previews"]),
            Added = JsonMembers.AsString(o["added"]) is { } added && DateRe().IsMatch(added) &&
                    DateOnly.TryParseExact(added, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? added
                : null,
            ThreeD = OneOf(o["threeD"], ThreeDModes),
        };
    }

    /// <summary>Reads one copy from its bytes, as <see cref="Read(JsonNode?)"/>; null too when they aren't I-JSON.</summary>
    public static Copy? Read(ReadOnlySpan<byte> raw)
    {
        try
        {
            return Read(StrictJson.Parse(raw, nulls: true));
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>Keeps of a title's copies the first <see cref="MaxCopies"/> good ones (<see cref="Read(JsonNode?)"/>), each id once (video section 3.8); none when the list is null.</summary>
    public static IReadOnlyList<Copy> ReadList(JsonArray? list)
    {
        var out_ = new List<Copy>();
        foreach (var e in list ?? [])
        {
            if (out_.Count == MaxCopies)
            {
                break;
            }

            if (Read(e) is { } c && !out_.Exists(o => o.Id == c.Id))
            {
                out_.Add(c);
            }
        }

        return out_;
    }

    /// <summary>A copy's JSON, as an owning server sends it and the vectors write what is kept: absent members left out, audioTracks always written.</summary>
    public static byte[] Write(Copy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return JsonOut.Object(o => Write(o, copy));
    }

    /// <summary>Writes a copy's members into an object being written (for <see cref="JsonOut.Objects{T}"/>), in the order of the table of video section 3.2.</summary>
    public static void Write(JsonOut o, Copy c)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(c);
        o.String("id", c.Id).String("name", Absent(c.Name)).String("edition", Absent(c.Edition))
            .Integer("durationMs", c.DurationMs).Integer("startMs", c.StartMs).String("container", c.Container)
            .Integer("size", c.Size).String("etag", c.ETag)
            .Objects("parts", Absent(c.Parts), (p, part) => p.Integer("size", part.Size).Integer("durationMs", part.DurationMs))
            .Integer("bitrateKbps", c.BitrateKbps)
            .Object("video", c.Video is { } v ? w => WriteVideo(w, v) : null)
            .Objects("audioTracks", c.AudioTracks, WriteAudio)
            .Objects("subtitleTracks", Absent(c.SubtitleTracks), WriteSubtitle)
            .Objects("chapters", Absent(c.Chapters), (w, ch) => w.Integer("startMs", ch.StartMs).String("name", Absent(ch.Name)))
            .Objects("segments", Absent(c.Segments), (w, s) => w.String("kind", s.Kind).Integer("startMs", s.StartMs)
                .Integer("endMs", s.EndMs).Bool("final", Flag(s.Final)))
            .Bool("previews", Flag(c.Previews)).String("added", Absent(c.Added)).String("threeD", Absent(c.ThreeD));
    }

    /// <summary>
    /// Whether <paramref name="a"/> comes before <paramref name="b"/> in a
    /// title's copies (video section 3.7): of a greater height, a picture
    /// wider than 16:9 counted as a 16:9 one as wide (3840 × 1600 as 2160),
    /// then with HDR when b has none, then with more hdr formats; a copy
    /// without video after those with one.
    /// </summary>
    public static bool Better(Copy a, Copy b) => Compare(a, b) < 0;

    /// <summary>
    /// Orders two copies as video section 3.7 does: negative when
    /// <paramref name="a"/> comes first, 0 when they are equal on all three
    /// (the owning server's order then stands).
    /// </summary>
    public static int Compare(Copy a, Copy b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Video is not { } va || b.Video is not { } vb)
        {
            return BoolRank(a.Video is not null, b.Video is not null);
        }

        if (CountedHeight(va) != CountedHeight(vb))
        {
            return CountedHeight(vb).CompareTo(CountedHeight(va));
        }

        if (va.Hdr.Count > 0 != vb.Hdr.Count > 0)
        {
            return BoolRank(va.Hdr.Count > 0, vb.Hdr.Count > 0);
        }

        return vb.Hdr.Count - va.Hdr.Count;
    }

    /// <summary>A title's copies in the order of video section 3.7, keeping the owning server's own order between equal ones.</summary>
    public static IReadOnlyList<Copy> Sorted(IEnumerable<Copy> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);
        return copies.OrderBy(c => c, Comparer<Copy>.Create(Compare)).ToList(); // OrderBy is stable
    }

    // The height a picture counts as in the order of copies.
    private static long CountedHeight(VideoStream v) =>
        (long)v.Width * 9 > (long)v.Height * 16 ? (long)v.Width * 9 / 16 : v.Height;

    // Orders true before false.
    private static int BoolRank(bool a, bool b) => a == b ? 0 : a ? -1 : 1;

    // Parts: none unless there are 1 to MaxParts, each in range, whose
    // sizes add up to size.
    private static IReadOnlyList<CopyPart> ReadParts(JsonNode? node, long size)
    {
        if (node is not JsonArray list || list.Count == 0 || list.Count > MaxParts)
        {
            return [];
        }

        var out_ = new List<CopyPart>();
        long sum = 0;
        foreach (var e in list)
        {
            if (e is not JsonObject o || !Integer(o["size"], 1, MaxSize, out var s) ||
                !Integer(o["durationMs"], 0, MaxDurationMs, out var d))
            {
                return [];
            }

            sum += s;
            out_.Add(new CopyPart(s, d));
        }

        return sum == size ? out_ : [];
    }

    // The picture: null, absent, unless it is an object whose codec is a
    // string and whose width and height are integers in range; its other
    // members count as absent alone.
    private static VideoStream? ReadVideoStream(JsonNode? node)
    {
        if (node is not JsonObject o || JsonMembers.AsString(o["codec"]) is not { } codec ||
            !Integer(o["width"], 1, MaxVideoSide, out var w) || !Integer(o["height"], 1, MaxVideoSide, out var h))
        {
            return null;
        }

        var hdr = o["hdr"] is JsonArray ? Video.CleanHdr(Strings(o["hdr"])) : [];
        VideoDv? dv = null;
        if (o["dv"] is JsonObject d && hdr.Contains("dv") &&
            Integer(d["profile"], 0, 10, out var p) && Integer(d["level"], 0, 13, out var l) &&
            Integer(d["compatibility"], 0, 15, out var c))
        {
            dv = new VideoDv((int)p, (int)l, (int)c);
        }

        double? fps = null;
        if (Number(o["fps"], out var f))
        {
            var r = Math.Round(f * 100, MidpointRounding.AwayFromZero) / 100;
            if (r > 0 && r <= 1000)
            {
                fps = r;
            }
        }

        VideoColour? colour = null;
        if (o["colour"] is JsonObject col)
        {
            var kept = new VideoColour(Form(col["primaries"], ColourRe()), Form(col["transfer"], ColourRe()),
                Form(col["matrix"], ColourRe()), OneOf(col["range"], ColourRanges));
            if (kept != new VideoColour(null, null, null, null))
            {
                colour = kept;
            }
        }

        return new VideoStream(Names.NameOrOther(Names.VideoCodecs, codec), (int)w, (int)h)
        {
            CodecTag = Form(o["codecTag"], CodecTagRe()),
            Profile = JsonMembers.AsString(o["profile"]) is { Length: > 0 } profile && profile.EnumerateRunes().Count() <= 32
                ? profile
                : null,
            Level = Number(o["level"], out var level) && level is >= 0 and <= 1000 ? level : null,
            BitDepth = Integer(o["bitDepth"], 8, 12, out var b) && b % 2 == 0 ? (int)b : null,
            Hdr = hdr,
            Dv = dv,
            Fps = fps,
            Interlaced = Video.True(o["interlaced"]),
            Colour = colour,
        };
    }

    // The first MaxAudioTracks good tracks, each ID once.
    private static IReadOnlyList<CopyAudio> ReadAudioTracks(JsonArray list)
    {
        var out_ = new List<CopyAudio>();
        foreach (var e in list)
        {
            if (out_.Count == MaxAudioTracks)
            {
                break;
            }

            if (e is not JsonObject o || JsonMembers.AsString(o["id"]) is not { } id || !Playback.TryTrackId(id, 'a', out _) ||
                JsonMembers.AsString(o["codec"]) is not { } codec || out_.Exists(t => t.Id == id))
            {
                continue;
            }

            out_.Add(new CopyAudio(id, Names.NameOrOther(Names.AudioCodecs, codec))
            {
                Language = Tag(o["language"]),
                Channels = Integer(o["channels"], 1, 64, out var ch) ? (int)ch : null,
                Role = OneOf(o["role"], AudioRoles),
                Default = Video.True(o["default"]),
                Forced = Video.True(o["forced"]),
                FileIndex = Index(o["fileIndex"]),
                Profile = OneOf(o["profile"], Names.AudioProfiles),
                Layout = Form(o["layout"], LayoutRe()),
                SampleRate = Integer(o["sampleRate"], 8_000, 768_000, out var rate) ? (int)rate : null,
                BitDepth = Integer(o["bitDepth"], 8, 32, out var depth) ? (int)depth : null,
                Title = Text(o["title"], MaxName),
                Original = Video.True(o["original"]),
            });
        }

        return out_;
    }

    // The first MaxSubtitleTracks good tracks, each ID once; none when node
    // isn't an array.
    private static IReadOnlyList<CopySubtitle> ReadSubtitleTracks(JsonNode? node)
    {
        var out_ = new List<CopySubtitle>();
        if (node is not JsonArray list)
        {
            return out_;
        }

        foreach (var e in list)
        {
            if (out_.Count == MaxSubtitleTracks)
            {
                break;
            }

            if (e is not JsonObject o || JsonMembers.AsString(o["id"]) is not { } id || !Playback.TryTrackId(id, 's', out _) ||
                out_.Exists(t => t.Id == id))
            {
                continue;
            }

            out_.Add(new CopySubtitle(id, Names.NameOrOther(Names.SubtitleFormats, JsonMembers.AsString(o["format"])))
            {
                Language = Tag(o["language"]),
                Default = Video.True(o["default"]),
                Forced = Video.True(o["forced"]),
                HearingImpaired = Video.True(o["hearingImpaired"]),
                External = Video.True(o["external"]),
                FileIndex = Index(o["fileIndex"]),
                Title = Text(o["title"], MaxName),
            });
        }

        return out_;
    }

    // In order, the chapters whose start is in the copy and after the one
    // before, the first MaxChapters.
    private static IReadOnlyList<CopyChapter> ReadChapters(JsonNode? node, long duration)
    {
        var out_ = new List<CopyChapter>();
        if (node is not JsonArray list)
        {
            return out_;
        }

        foreach (var e in list)
        {
            if (out_.Count == MaxChapters)
            {
                break;
            }

            if (e is not JsonObject o || !Integer(o["startMs"], 0, duration, out var start) ||
                (out_.Count > 0 && start <= out_[^1].StartMs))
            {
                continue;
            }

            out_.Add(new CopyChapter(start, Text(o["name"], MaxChapterName)));
        }

        return out_;
    }

    // The segments of a known kind whose times are in the copy and in
    // order, the first MaxSegments.
    private static IReadOnlyList<CopySegment> ReadSegments(JsonNode? node, long duration)
    {
        var out_ = new List<CopySegment>();
        if (node is not JsonArray list)
        {
            return out_;
        }

        foreach (var e in list)
        {
            if (out_.Count == MaxSegments)
            {
                break;
            }

            if (e is not JsonObject o || OneOf(o["kind"], SegmentKinds) is not { } kind ||
                !Integer(o["startMs"], 0, duration, out var start) || !Integer(o["endMs"], 0, duration, out var end) ||
                start >= end)
            {
                continue;
            }

            out_.Add(new CopySegment(kind, start, end, Video.True(o["final"])));
        }

        return out_;
    }

    private static void WriteVideo(JsonOut w, VideoStream v)
    {
        w.String("codec", v.Codec).String("codecTag", Absent(v.CodecTag)).String("profile", Absent(v.Profile))
            .Node("level", v.Level is { } level ? JsonValue.Create(level) : null)
            .Integer("width", v.Width).Integer("height", v.Height).Integer("bitDepth", v.BitDepth)
            .Strings("hdr", Absent(v.Hdr))
            .Object("dv", v.Dv is { } dv ? d => d.Integer("profile", dv.Profile).Integer("level", dv.Level)
                .Integer("compatibility", dv.Compatibility) : null)
            .Node("fps", v.Fps is { } fps ? JsonValue.Create(fps) : null)
            .Bool("interlaced", Flag(v.Interlaced))
            .Object("colour", v.Colour is { } c ? o => o.String("primaries", Absent(c.Primaries))
                .String("transfer", Absent(c.Transfer)).String("matrix", Absent(c.Matrix)).String("range", Absent(c.Range)) : null);
    }

    /// <summary>Writes an audio track's members into an object being written, as a copy's audioTracks carries them.</summary>
    public static void WriteAudio(JsonOut w, CopyAudio t)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(t);
        w.String("id", t.Id).String("language", Absent(t.Language)).Integer("channels", t.Channels)
            .String("role", Absent(t.Role)).Bool("default", Flag(t.Default)).Bool("forced", Flag(t.Forced))
            .Integer("fileIndex", t.FileIndex).String("codec", t.Codec).String("profile", Absent(t.Profile))
            .String("layout", Absent(t.Layout)).Integer("sampleRate", t.SampleRate).Integer("bitDepth", t.BitDepth)
            .String("title", Absent(t.Title)).Bool("original", Flag(t.Original));
    }

    /// <summary>Writes a subtitle track's members into an object being written, as a copy's subtitleTracks carries them.</summary>
    public static void WriteSubtitle(JsonOut w, CopySubtitle t)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(t);
        w.String("id", t.Id).String("language", Absent(t.Language)).Bool("default", Flag(t.Default))
            .Bool("forced", Flag(t.Forced)).Bool("hearingImpaired", Flag(t.HearingImpaired)).String("format", t.Format)
            .Bool("external", Flag(t.External)).Integer("fileIndex", t.FileIndex).String("title", Absent(t.Title));
    }

    private static string? Absent(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static IEnumerable<T>? Absent<T>(IReadOnlyList<T> list) => list.Count > 0 ? list : null;

    private static bool? Flag(bool b) => b ? true : null;

    // An MSFP integer (core section 2.3) from lo to hi; false for any other
    // value.
    internal static bool Integer(JsonNode? node, long lo, long hi, out long n)
    {
        if (JsonMembers.AsInteger(node) is { } i && i >= lo && i <= hi)
        {
            n = i;
            return true;
        }

        n = 0;
        return false;
    }

    // Any JSON number, as a double.
    private static bool Number(JsonNode? node, out double f)
    {
        f = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number &&
               double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) && double.IsFinite(f);
    }

    // A list's strings: none when it isn't a list, an entry that isn't a
    // string skipped.
    internal static List<string> Strings(JsonNode? node)
    {
        var out_ = new List<string>();
        if (node is JsonArray list)
        {
            foreach (var e in list)
            {
                if (JsonMembers.AsString(e) is { } s)
                {
                    out_.Add(s);
                }
            }
        }

        return out_;
    }

    // A track's fileIndex, 0 to 999.
    private static int? Index(JsonNode? node) => Integer(node, 0, 999, out var n) ? (int)n : null;

    // The string when it matches re.
    private static string? Form(JsonNode? node, Regex re) => JsonMembers.AsString(node) is { } s && re.IsMatch(s) ? s : null;

    // The string when list holds it.
    private static string? OneOf(JsonNode? node, IReadOnlyList<string> list) =>
        JsonMembers.AsString(node) is { } s && list.Contains(s, StringComparer.Ordinal) ? s : null;

    // A valid tag (Languages.ValidTag), never converted.
    private static string? Tag(JsonNode? node) => JsonMembers.AsString(node) is { } s && Languages.ValidTag(s) ? s : null;

    // A name or a title made plain text and cut (video section 3.6); null
    // when it isn't a string or nothing is left.
    private static string? Text(JsonNode? node, int max) =>
        JsonMembers.AsString(node) is { } s && Music.CleanText(s, max) is { Length: > 0 } t ? t : null;
}
