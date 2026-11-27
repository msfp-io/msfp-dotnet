using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// One version of a film, an episode or an extra (MSFP Media video 3.2): what
/// an owning server sends (<see cref="Copies.Write(Copy)"/>), and what a
/// viewer's server keeps (<see cref="Copies.Read(JsonNode?)"/>), in bounds,
/// written again unchanged. A null member, an empty string or list and a
/// false flag are absent; <see cref="AudioTracks"/> is required, and may be
/// empty. A copy carries no name, no bitrate (size × 8 / durationMs is its
/// overall rate) and no day it was added: a viewer's server builds labels
/// from its facts.
/// </summary>
/// <param name="Id">The copy's ID, of a library ID's form (<see cref="Playback.Id"/>).</param>
/// <param name="DurationMs">Its length, all its parts: the length of what <paramref name="Size"/> measures, 0 to <see cref="Copies.MaxDurationMs"/>, exact.</param>
/// <param name="MediaType">The file's media type (<see cref="Names.IsMediaType"/>): video/mp4, video/matroska, video/mp2t…, known or not.</param>
/// <param name="Size">Its size in bytes, all its parts, 1 to <see cref="Copies.MaxSize"/>.</param>
/// <param name="ETag">Its entity tag, of an ID's form.</param>
/// <param name="AudioTracks">Its audio tracks.</param>
public sealed record Copy(string Id, long DurationMs, string MediaType, long Size, string ETag, IReadOnlyList<CopyAudio> AudioTracks)
{
    /// <summary>Its edition, a token (theatrical, directors, extended, unrated, remastered, special, imax…), known or not.</summary>
    public string? Edition { get; init; }

    /// <summary>For an episode in a copy holding several: where it starts in the copy, 0 to <see cref="DurationMs"/>.</summary>
    public long? StartMs { get; init; }

    /// <summary>For an episode in a copy holding several: where it ends in the copy, above <see cref="StartMs"/> (0 when absent) and at most <see cref="DurationMs"/>.</summary>
    public long? EndMs { get; init; }

    /// <summary>The number of files it is made of, 2 to <see cref="Copies.MaxParts"/>; null for one file. A copy of several parts is never served as file.</summary>
    public int? Parts { get; init; }

    /// <summary>Its picture.</summary>
    public VideoStream? Video { get; init; }

    /// <summary>Its subtitle tracks.</summary>
    public IReadOnlyList<CopySubtitle> SubtitleTracks { get; init; } = [];

    /// <summary>Its chapters, in order.</summary>
    public IReadOnlyList<CopyChapter> Chapters { get; init; } = [];

    /// <summary>Its skip markers.</summary>
    public IReadOnlyList<CopySegment> Segments { get; init; } = [];

    /// <summary>The layout of its seek-bar previews (MSFP Media video 5.5), when the owning server has them.</summary>
    public PreviewLayout? PreviewLayout { get; init; }

    /// <summary>How a 3D picture is laid out, a token (sbs, tab, sbs-rl, tab-rl, mvc…), known or not; null for 2D.</summary>
    public string? ThreeD { get; init; }

    /// <summary>Whether it is made of several files (<see cref="Parts"/>).</summary>
    public bool Several => Parts is > 1;
}

/// <summary>A copy's picture (MSFP Media video 3.3).</summary>
/// <param name="Codec">Its codec, a registry token (h264, hevc, av1, vp9, vvc, prores…), known or not.</param>
/// <param name="Width">Its width, 1 to <see cref="Copies.MaxVideoSide"/>.</param>
/// <param name="Height">Its height, 1 to <see cref="Copies.MaxVideoSide"/>.</param>
public sealed record VideoStream(string Codec, int Width, int Height)
{
    /// <summary>
    /// Its codecs string of RFC 6381 (avc1.640028, hvc1.2.4.L153.B0,
    /// av01.0.08M.10), 1 to 64 characters of A-Z a-z 0-9 . + -: required for
    /// h264, hevc, av1 and vp9 (<see cref="Rfc6381.Required"/>); on Dolby
    /// Vision the base codec's string, <see cref="Dv"/> holding the rest.
    /// </summary>
    public string? Codecs { get; init; }

    /// <summary>Its sample (pixel) aspect ratio, "&lt;w&gt;:&lt;h&gt;" (32:27), each side 1 to 65,535; null for square pixels.</summary>
    public string? SampleAspect { get; init; }

    /// <summary>The degrees clockwise the picture must be turned to show upright: 0, 90, 180 or 270; null for 0.</summary>
    public int? Rotation { get; init; }

    /// <summary>Its bit depth, 1 to 16: the authoritative one, where codecs holds one too.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Its colour, as ITU-T H.273 code points (MSFP Media video 3.6): the base fact of its dynamic range.</summary>
    public VideoColour? Colour { get; init; }

    /// <summary>
    /// Its enhancement layers and dynamic metadata (hdr10plus…), tokens
    /// sorted in byte order, each once, at most <see cref="Copies.MaxHdr"/>;
    /// HDR10 and HLG are said by <see cref="Colour"/>, Dolby Vision by
    /// <see cref="Dv"/>.
    /// </summary>
    public IReadOnlyList<string> Hdr { get; init; } = [];

    /// <summary>Its Dolby Vision configuration record, whenever the copy has Dolby Vision.</summary>
    public VideoDv? Dv { get; init; }

    /// <summary>Its frame rate, "&lt;num&gt;/&lt;den&gt;" (24000/1001, 25/1), each 1 to 1,000,000, the fraction reduced: the nominal rate, the average for a variable one.</summary>
    public string? Fps { get; init; }

    /// <summary>On an h264 stream only: its declared maximum number of reference frames (max_num_ref_frames), 1 to 16.</summary>
    public int? RefFrames { get; init; }

    /// <summary>Whether it is interlaced.</summary>
    public bool Interlaced { get; init; }
}

/// <summary>
/// A Dolby Vision configuration record (MSFP Media video 3.6): its profile (0
/// to 127), level (0 to 63) and base-layer compatibility ID (0 to 15), and
/// whether an enhancement layer is present (null when not said).
/// </summary>
public sealed record VideoDv(int Profile, int Level, int Compatibility)
{
    /// <summary>Whether an enhancement layer is present; null when not said.</summary>
    public bool? El { get; init; }
}

/// <summary>
/// A picture's colour (MSFP Media video 3.6): ITU-T H.273 code points, 0 to
/// 255, each null when unknown (2, unspecified, is never kept), and H.273's
/// full-range flag. At least one member is set.
/// </summary>
/// <param name="Primaries">Its colour primaries (1: BT.709, 9: BT.2020).</param>
/// <param name="Transfer">Its preferred transfer characteristics (1: BT.709, 16: PQ, 18: HLG; 14 and 15: standard range in BT.2020).</param>
/// <param name="Matrix">Its matrix coefficients (1: BT.709, 9: BT.2020 non-constant).</param>
/// <param name="FullRange">H.273's full-range flag; null when unknown.</param>
public sealed record VideoColour(int? Primaries, int? Transfer, int? Matrix, bool? FullRange = null)
{
    /// <summary>H.273's transfer of PQ, HDR10's (SMPTE ST 2084).</summary>
    public const int TransferPq = 16;

    /// <summary>H.273's transfer of HLG (ARIB STD-B67).</summary>
    public const int TransferHlg = 18;

    /// <summary>Whether its transfer is PQ or HLG: a high dynamic range.</summary>
    public bool HighRange => Transfer is TransferPq or TransferHlg;
}

/// <summary>A copy's audio track (MSFP Media video 3.3), with the a&lt;n&gt; ID play takes.</summary>
/// <param name="Id">Its ID (<see cref="Playback.TryTrackId"/>, a): for a copy of one file, the stream's index in the file.</param>
/// <param name="Codec">Its codec, a registry token (aac, ac3, eac3, truehd, dts, opus, flac, pcm_s24le…), known or not.</param>
public sealed record CopyAudio(string Id, string Codec)
{
    /// <summary>Its language, a valid tag (<see cref="Languages.ValidTag"/>).</summary>
    public string? Language { get; init; }

    /// <summary>Its channels, 1 to 64.</summary>
    public int? Channels { get; init; }

    /// <summary>Its role, a token of the DASH role scheme (main, alternate, supplementary, commentary, dub, description…), known or not; an unknown one is never the title's own sound.</summary>
    public string? Role { get; init; }

    /// <summary>Whether it is the default track.</summary>
    public bool Default { get; init; }

    /// <summary>Whether it is forced.</summary>
    public bool Forced { get; init; }

    /// <summary>Its codecs string of RFC 6381 (mp4a.40.2, ec-3, dtsh), 1 to 64 characters of A-Z a-z 0-9 . + -: required for aac and dts.</summary>
    public string? Codecs { get; init; }

    /// <summary>The object-based format it carries, a token (dolby-atmos, dts-x…), known or not.</summary>
    public string? Objects { get; init; }

    /// <summary>Its sample rate, 8,000 to 768,000.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Its bit depth, 8 to 32.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Whether it is in the title's original language, by the file's own flag only.</summary>
    public bool Original { get; init; }
}

/// <summary>A copy's subtitle track (MSFP Media video 3.3), with its s&lt;n&gt; ID.</summary>
/// <param name="Id">Its ID (<see cref="Playback.TryTrackId"/>, s): for a copy of one file, the stream's index in the file, a track beside it (external) above the file's streams.</param>
/// <param name="Format">Its source format, a registry token (subrip, ass, webvtt, hdmv_pgs_subtitle, dvd_subtitle…), known or not; "" when the owning server doesn't know it.</param>
public sealed record CopySubtitle(string Id, string Format)
{
    /// <summary>Its language, a valid tag (<see cref="Languages.ValidTag"/>).</summary>
    public string? Language { get; init; }

    /// <summary>Its role, a token of the DASH role scheme (subtitle, commentary, description, easyreader…), known or not; never forced-subtitle or caption, which are the flags.</summary>
    public string? Role { get; init; }

    /// <summary>Whether it is the default track.</summary>
    public bool Default { get; init; }

    /// <summary>Whether it is forced: the only forced flag.</summary>
    public bool Forced { get; init; }

    /// <summary>Whether it is for the deaf and hard of hearing (SDH): the only such flag.</summary>
    public bool HearingImpaired { get; init; }

    /// <summary>Whether it is a file beside the copy's.</summary>
    public bool External { get; init; }
}

/// <summary>A chapter of a copy (MSFP Media video 3.4), its name plain text of at most <see cref="Copies.MaxChapterName"/> code points.</summary>
public sealed record CopyChapter(long StartMs, string? Name = null);

/// <summary>A skip marker of a copy (MSFP Media video 3.4): <paramref name="Kind"/> is a token (intro, outro, recap, preview, commercial…), known or not, <paramref name="StartMs"/> before <paramref name="EndMs"/>. The last outro is the final credits.</summary>
public sealed record CopySegment(string Kind, long StartMs, long EndMs);

/// <summary>
/// A copy's seek-bar previews (MSFP Media video 5.5), in the catalog: tiles of
/// <paramref name="TileWidth"/> × <paramref name="TileHeight"/> pixels, every
/// <paramref name="IntervalMs"/>, laid out <paramref name="Columns"/> ×
/// <paramref name="Rows"/> on each sheet, <paramref name="TileCount"/> in
/// all. Read in bounds only (<see cref="Copies.ReadPreviewLayout"/>).
/// </summary>
public sealed record PreviewLayout(int TileWidth, int TileHeight, int Columns, int Rows, int IntervalMs, int TileCount)
{
    /// <summary>The most sheets a copy's previews may have.</summary>
    public const int MaxSheets = 1000;

    /// <summary>The most a sheet may be, as read (MSFP Media fetching 2.4): 8 MiB.</summary>
    public const int MaxSheetBytes = 8 << 20;

    /// <summary>How many sheets the tiles fill: ⌈tileCount / (columns × rows)⌉, at most <see cref="MaxSheets"/>.</summary>
    public int Sheets => (int)((TileCount + ((long)Columns * Rows) - 1) / ((long)Columns * Rows));
}

/// <summary>
/// The copies of a film, an episode or an extra (MSFP
/// Media video 3.2 to 3.8): their bounds, what a viewer's server keeps of them, how an owning
/// server writes them, and the order the implementers' notes advise.
/// </summary>
public static partial class Copies
{
    /// <summary>How many copies a title keeps at most, the first good ones (MSFP Media video 3.8).</summary>
    public const int MaxCopies = 16;

    /// <summary>The longest a copy may last: 100 days.</summary>
    public const long MaxDurationMs = 8_640_000_000;

    /// <summary>The greatest size of a copy.</summary>
    public const long MaxSize = 999_999_999_999_999;

    /// <summary>How many files a copy may be made of.</summary>
    public const int MaxParts = 16;

    /// <summary>How many audio tracks a copy keeps at most.</summary>
    public const int MaxAudioTracks = 32;

    /// <summary>How many subtitle tracks a copy keeps at most.</summary>
    public const int MaxSubtitleTracks = 64;

    /// <summary>How many chapters a copy keeps at most.</summary>
    public const int MaxChapters = 256;

    /// <summary>How many skip markers a copy keeps at most.</summary>
    public const int MaxSegments = 32;

    /// <summary>How many entries a video stream's hdr keeps at most (MSFP Media video 3.8).</summary>
    public const int MaxHdr = 8;

    /// <summary>The length of a chapter's name, in code points.</summary>
    public const int MaxChapterName = 64;

    /// <summary>The greatest width or height of a picture.</summary>
    public const int MaxVideoSide = 100_000;

    [GeneratedRegex(@"^[A-Za-z0-9.+-]{1,64}\z")]
    private static partial Regex CodecsRe();

    [GeneratedRegex(@"^([1-9][0-9]{0,6})/([1-9][0-9]{0,6})\z")]
    private static partial Regex FpsRe();

    [GeneratedRegex(@"^([1-9][0-9]{0,4}):([1-9][0-9]{0,4})\z")]
    private static partial Regex AspectRe();

    // An fps fraction of the copy's form, each number at most 1,000,000;
    // reduced: in lowest terms. Null otherwise.
    internal static string? FpsOf(JsonNode? node, bool reduced) => Ratio(node, FpsRe(), 1_000_000, reduced);

    /// <summary>Whether <paramref name="s"/> has the form of a codecs string (MSFP Media video 3.5): 1 to 64 characters of A-Z a-z 0-9 . + -.</summary>
    public static bool CodecsForm(string? s) => s is not null && CodecsRe().IsMatch(s);

    /// <summary>
    /// Reads one entry of a title's copies as MSFP Media video 3.8 says: null,
    /// to skip it, when it isn't an object, when its id or etag isn't of an
    /// ID's form, when durationMs or size isn't an integer in range, when
    /// mediaType isn't a media type (MSFP Media base 3), or when audioTracks
    /// isn't an array. Its open values are kept as they are when they are
    /// well formed, known or not; its other members, out of range, of the
    /// wrong type or out of their form, count as absent alone; a bad entry of
    /// one of its lists is skipped alone; members of earlier drafts (name,
    /// container, bitrateKbps, added, previews, a track's title, profile,
    /// layout, fileIndex, codecTag, level, dv's bl and rpu, a segment's
    /// final) are ignored. Whether its id repeats an earlier copy's is the
    /// title's to check (<see cref="ReadList"/> does).
    /// </summary>
    public static Copy? Read(JsonNode? entry)
    {
        if (entry is not JsonObject o ||
            JsonMembers.AsString(o["id"]) is not { } id || !Playback.Id(id) ||
            !Integer(o["durationMs"], 0, MaxDurationMs, out var duration) ||
            Names.MediaType(o["mediaType"]) is not { } mediaType ||
            !Integer(o["size"], 1, MaxSize, out var size) ||
            JsonMembers.AsString(o["etag"]) is not { } etag || !Playback.Id(etag) ||
            o["audioTracks"] is not JsonArray audio)
        {
            return null;
        }

        long? start = Integer(o["startMs"], 0, duration, out var st) ? st : null;
        return new Copy(id, duration, mediaType, size, etag, ReadAudioTracks(audio))
        {
            Edition = Names.Token(o["edition"]),
            StartMs = start,
            EndMs = Integer(o["endMs"], (start ?? 0) + 1, duration, out var end) ? end : null,
            Parts = Integer(o["parts"], 2, MaxParts, out var parts) ? (int)parts : null,
            Video = ReadVideoStream(o["video"]),
            SubtitleTracks = ReadSubtitleTracks(o["subtitleTracks"]),
            Chapters = ReadChapters(o["chapters"], duration),
            Segments = ReadSegments(o["segments"], duration),
            PreviewLayout = ReadPreviewLayout(o["previewLayout"]),
            ThreeD = Names.Token(o["threeD"]),
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

    /// <summary>Keeps of a title's copies the first <see cref="MaxCopies"/> good ones (<see cref="Read(JsonNode?)"/>), each id once (MSFP Media video 3.8); none when the list is null.</summary>
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

    /// <summary>
    /// Reads a copy's previewLayout as MSFP Media video 5.5 says: null, dropped
    /// alone, unless it is an object whose six members are integers within
    /// their bounds (tiles 1 to 2,048 pixels each way, 1 to 100 columns and
    /// rows, a full sheet at most 4,096 pixels each way, intervalMs 100 to
    /// 3,600,000, tileCount 1 to 1,000 × columns × rows). Other members (sheet
    /// URLs of earlier drafts) are ignored.
    /// </summary>
    public static PreviewLayout? ReadPreviewLayout(JsonNode? node)
    {
        if (node is not JsonObject o ||
            !Integer(o["tileWidth"], 1, 2048, out var w) || !Integer(o["tileHeight"], 1, 2048, out var h) ||
            !Integer(o["columns"], 1, 100, out var c) || !Integer(o["rows"], 1, 100, out var r) ||
            !Integer(o["intervalMs"], 100, 3_600_000, out var iv) || c * w > 4096 || r * h > 4096 ||
            !Integer(o["tileCount"], 1, PreviewLayout.MaxSheets * c * r, out var n))
        {
            return null;
        }

        return new PreviewLayout((int)w, (int)h, (int)c, (int)r, (int)iv, (int)n);
    }

    /// <summary>A copy's JSON, as an owning server sends it and the vectors write what is kept: absent members left out, audioTracks always written.</summary>
    public static byte[] Write(Copy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return JsonOut.Object(o => Write(o, copy));
    }

    /// <summary>Writes a copy's members into an object being written (for <see cref="JsonOut.Objects{T}"/>), in the order of the table of MSFP Media video 3.2.</summary>
    public static void Write(JsonOut o, Copy c)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(c);
        o.String("id", c.Id).String("edition", Absent(c.Edition))
            .Integer("durationMs", c.DurationMs).Integer("startMs", c.StartMs).Integer("endMs", c.EndMs)
            .String("mediaType", c.MediaType).Integer("size", c.Size).String("etag", c.ETag)
            .Integer("parts", c.Parts)
            .Object("video", c.Video is { } v ? w => WriteVideo(w, v) : null)
            .Objects("audioTracks", c.AudioTracks, WriteAudio)
            .Objects("subtitleTracks", Absent(c.SubtitleTracks), WriteSubtitle)
            .Objects("chapters", Absent(c.Chapters), (w, ch) => w.Integer("startMs", ch.StartMs).String("name", Absent(ch.Name)))
            .Objects("segments", Absent(c.Segments), (w, s) => w.String("kind", s.Kind).Integer("startMs", s.StartMs)
                .Integer("endMs", s.EndMs))
            .Object("previewLayout", c.PreviewLayout is { } l ? w => WritePreviewLayout(w, l) : null)
            .String("threeD", Absent(c.ThreeD));
    }

    /// <summary>Writes a previewLayout's members into an object being written.</summary>
    public static void WritePreviewLayout(JsonOut w, PreviewLayout l)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(l);
        w.Integer("tileWidth", l.TileWidth).Integer("tileHeight", l.TileHeight).Integer("columns", l.Columns)
            .Integer("rows", l.Rows).Integer("intervalMs", l.IntervalMs).Integer("tileCount", l.TileCount);
    }

    /// <summary>
    /// Whether <paramref name="a"/> comes before <paramref name="b"/> in the
    /// order of copies the implementers' notes advise (not a rule: the order
    /// is the owning server's preference): of a greater height, a picture
    /// wider than 16:9 counted as a 16:9 one as wide (3840 × 1600 as 2160),
    /// then with a high dynamic range when b has none (<see cref="HighRange"/>),
    /// then with more hdr entries; a copy without video after those with one.
    /// </summary>
    public static bool Better(Copy a, Copy b) => Compare(a, b) < 0;

    /// <summary>
    /// Orders two copies as the notes' order does: negative when
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

        if (HighRange(va) != HighRange(vb))
        {
            return BoolRank(HighRange(va), HighRange(vb));
        }

        return vb.Hdr.Count - va.Hdr.Count;
    }

    /// <summary>
    /// Whether a picture has a high dynamic range, as the notes' order counts
    /// it: an H.273 transfer of 16 (PQ) or 18 (HLG), a dv object, or a
    /// non-empty hdr.
    /// </summary>
    public static bool HighRange(VideoStream v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Colour is { HighRange: true } || v.Dv is not null || v.Hdr.Count > 0;
    }

    /// <summary>A title's copies in the notes' order, keeping the owning server's own order between equal ones.</summary>
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

    // The picture: null, absent, unless it is an object whose codec is a
    // token and whose width and height are integers in range; its other
    // members count as absent alone.
    private static VideoStream? ReadVideoStream(JsonNode? node)
    {
        if (node is not JsonObject o || Names.Token(o["codec"]) is not { } codec ||
            !Integer(o["width"], 1, MaxVideoSide, out var w) || !Integer(o["height"], 1, MaxVideoSide, out var h))
        {
            return null;
        }

        var hdr = o["hdr"] is JsonArray ? Video.CleanHdr(Strings(o["hdr"])) : [];
        VideoDv? dv = null;
        if (o["dv"] is JsonObject d &&
            Integer(d["profile"], 0, 127, out var p) && Integer(d["level"], 0, 63, out var l) &&
            Integer(d["compatibility"], 0, 15, out var c))
        {
            dv = new VideoDv((int)p, (int)l, (int)c) { El = Music.BoolOf(d["el"]) };
        }

        VideoColour? colour = null;
        if (o["colour"] is JsonObject col)
        {
            var kept = new VideoColour(H273(col["primaries"]), H273(col["transfer"]), H273(col["matrix"]), Music.BoolOf(col["fullRange"]));
            if (kept != new VideoColour(null, null, null))
            {
                colour = kept;
            }
        }

        return new VideoStream(codec, (int)w, (int)h)
        {
            Codecs = Form(o["codecs"], CodecsRe()),
            SampleAspect = Ratio(o["sampleAspect"], AspectRe(), 65_535, false),
            Rotation = Integer(o["rotation"], 0, 270, out var r) && r % 90 == 0 ? (int)r : null,
            BitDepth = Integer(o["bitDepth"], 1, 16, out var b) ? (int)b : null,
            Colour = colour,
            Hdr = hdr,
            Dv = dv,
            Fps = Ratio(o["fps"], FpsRe(), 1_000_000, true),
            RefFrames = codec == "h264" && Integer(o["refFrames"], 1, 16, out var rf) ? (int)rf : null,
            Interlaced = Video.True(o["interlaced"]),
        };
    }

    // An H.273 code point, 0 to 255; 2 (unspecified) counts as absent.
    private static int? H273(JsonNode? node) => Integer(node, 0, 255, out var n) && n != 2 ? (int)n : null;

    // A ratio of two numbers in its form, each at most top; reduced: its
    // two numbers have no common divisor but 1. Null otherwise.
    private static string? Ratio(JsonNode? node, Regex re, long top, bool reduced)
    {
        if (JsonMembers.AsString(node) is not { } s || re.Match(s) is not { Success: true } m)
        {
            return null;
        }

        var a = long.Parse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        var b = long.Parse(m.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        return a <= top && b <= top && (!reduced || (long)BigInteger.GreatestCommonDivisor(a, b) == 1) ? s : null;
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
                Names.Token(o["codec"]) is not { } codec || out_.Exists(t => t.Id == id))
            {
                continue;
            }

            out_.Add(new CopyAudio(id, codec)
            {
                Language = Tag(o["language"]),
                Channels = Integer(o["channels"], 1, 64, out var ch) ? (int)ch : null,
                Role = Names.Token(o["role"]),
                Default = Video.True(o["default"]),
                Forced = Video.True(o["forced"]),
                Codecs = Form(o["codecs"], CodecsRe()),
                Objects = Names.Token(o["objects"]),
                SampleRate = Integer(o["sampleRate"], 8_000, 768_000, out var rate) ? (int)rate : null,
                BitDepth = Integer(o["bitDepth"], 8, 32, out var depth) ? (int)depth : null,
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

            out_.Add(new CopySubtitle(id, Names.Token(o["format"]) ?? "")
            {
                Language = Tag(o["language"]),
                Role = Names.Token(o["role"]),
                Default = Video.True(o["default"]),
                Forced = Video.True(o["forced"]),
                HearingImpaired = Video.True(o["hearingImpaired"]),
                External = Video.True(o["external"]),
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

    // The segments whose kind is a token and whose times are in the copy
    // and in order, the first MaxSegments.
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

            if (e is not JsonObject o || Names.Token(o["kind"]) is not { } kind ||
                !Integer(o["startMs"], 0, duration, out var start) || !Integer(o["endMs"], 0, duration, out var end) ||
                start >= end)
            {
                continue;
            }

            out_.Add(new CopySegment(kind, start, end));
        }

        return out_;
    }

    private static void WriteVideo(JsonOut w, VideoStream v)
    {
        w.String("codec", v.Codec).String("codecs", Absent(v.Codecs))
            .Integer("width", v.Width).Integer("height", v.Height).String("sampleAspect", Absent(v.SampleAspect))
            .Integer("rotation", v.Rotation).Integer("bitDepth", v.BitDepth)
            .Object("colour", v.Colour is { } c ? o => o.Integer("primaries", c.Primaries)
                .Integer("transfer", c.Transfer).Integer("matrix", c.Matrix).Bool("fullRange", c.FullRange) : null)
            .Strings("hdr", Absent(v.Hdr))
            .Object("dv", v.Dv is { } dv ? d => d.Integer("profile", dv.Profile).Integer("level", dv.Level)
                .Integer("compatibility", dv.Compatibility).Bool("el", dv.El) : null)
            .String("fps", Absent(v.Fps)).Integer("refFrames", v.RefFrames)
            .Bool("interlaced", Flag(v.Interlaced));
    }

    /// <summary>Writes an audio track's members into an object being written, as a copy's audioTracks carries them.</summary>
    public static void WriteAudio(JsonOut w, CopyAudio t)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(t);
        w.String("id", t.Id).String("language", Absent(t.Language)).Integer("channels", t.Channels)
            .String("role", Absent(t.Role)).Bool("default", Flag(t.Default)).Bool("forced", Flag(t.Forced))
            .String("codec", t.Codec).String("codecs", Absent(t.Codecs)).String("objects", Absent(t.Objects))
            .Integer("sampleRate", t.SampleRate).Integer("bitDepth", t.BitDepth).Bool("original", Flag(t.Original));
    }

    /// <summary>Writes a subtitle track's members into an object being written, as a copy's subtitleTracks carries them.</summary>
    public static void WriteSubtitle(JsonOut w, CopySubtitle t)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(t);
        w.String("id", t.Id).String("language", Absent(t.Language)).String("role", Absent(t.Role))
            .Bool("default", Flag(t.Default)).Bool("forced", Flag(t.Forced)).Bool("hearingImpaired", Flag(t.HearingImpaired))
            .String("format", Absent(t.Format)).Bool("external", Flag(t.External));
    }

    private static string? Absent(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static IEnumerable<T>? Absent<T>(IReadOnlyList<T> list) => list.Count > 0 ? list : null;

    private static bool? Flag(bool b) => b ? true : null;

    // An MSFP integer (MSFP Media base 2.2) from lo to hi; false for any other
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

    // The string when it matches re.
    private static string? Form(JsonNode? node, Regex re) => JsonMembers.AsString(node) is { } s && re.IsMatch(s) ? s : null;

    // A valid tag (Languages.ValidTag), never converted.
    private static string? Tag(JsonNode? node) => JsonMembers.AsString(node) is { } s && Languages.ValidTag(s) ? s : null;

    // A chapter's name made plain text and cut; null when it isn't a
    // string or nothing is left.
    private static string? Text(JsonNode? node, int max) =>
        JsonMembers.AsString(node) is { } s && Music.CleanText(s, max) is { Length: > 0 } t ? t : null;
}
