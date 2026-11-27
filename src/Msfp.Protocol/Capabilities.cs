using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// A capability list (MSFP Media playing 2.8): what a device plays, sent
/// instead of delivery by a player to its own source, which then picks the
/// delivery. MSFP refuses it (MSFP playback section 2): a server never
/// describes a device to a peer, and the engine never sends one. This is
/// the list's shape and the reading of its forms, for a host that serves
/// its own players with MSFP Media (<see cref="PlayReader.Capabilities"/>);
/// the matching of a copy against the list (playing 2.8, "Matching" and
/// "What the source picks") is the host's. Values are hints: one of the
/// right form a source doesn't know is kept, and never matches a copy.
/// </summary>
public sealed record Capabilities
{
    /// <summary>The most entries of files.</summary>
    public const int MaxFiles = 64;

    /// <summary>The most entries of segments.</summary>
    public const int MaxSegments = 4;

    /// <summary>The most video entries, audio entries, and subtitles.</summary>
    public const int MaxEntries = 32;

    /// <summary>The most codecs strings of an entry.</summary>
    public const int MaxCodecs = 16;

    /// <summary>The most transfers of a video entry.</summary>
    public const int MaxTransfers = 16;

    /// <summary>The most dv profiles of a video entry.</summary>
    public const int MaxDv = 16;

    /// <summary>The most tokens of hdr, threeD and objects.</summary>
    public const int MaxTokens = 8;

    /// <summary>The containers played as a file (media types).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The HLS segment formats beyond the baseline's (media types).</summary>
    public IReadOnlyList<string> Segments { get; init; } = [];

    /// <summary>The video codecs decoded, with their limits.</summary>
    public IReadOnlyList<VideoCapability> Video { get; init; } = [];

    /// <summary>The audio codecs decoded, with their limits.</summary>
    public IReadOnlyList<AudioCapability> Audio { get; init; } = [];

    /// <summary>The subtitle formats drawn from inside a file (registry tokens).</summary>
    public IReadOnlyList<string> Subtitles { get; init; } = [];

    /// <summary>
    /// Reads a capability list as MSFP Media playing 2.8 says ("Reading the
    /// list"), refusing (400) what isn't an object, a member of the wrong
    /// JSON type, a list past its bound, an entry without codec, a number
    /// out of its range, a value not of its form (a token, a media type, a
    /// codecs string, an fps fraction), and an entry of a codec whose
    /// streams require codecs (<see cref="Rfc6381.RequiredFor"/>) without
    /// them. Unknown members are ignored.
    /// </summary>
    /// <exception cref="PlayRefusal">The list is refused (400 invalid-request).</exception>
    public static Capabilities Read(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            throw Playback.BadPlay("capabilities isn't an object");
        }

        var video = new List<VideoCapability>();
        foreach (var e in Objects(o, "video", MaxEntries))
        {
            var (codec, codecs) = CodecEntry(e);
            if (e.ContainsKey("maxRefFrames") && codec != "h264")
            {
                throw Playback.BadPlay("maxRefFrames on an entry that isn't h264");
            }

            var fps = "";
            if (e.TryGetPropertyValue("maxFps", out var raw))
            {
                fps = Copies.FpsOf(raw, reduced: false) ?? throw Playback.BadPlay("maxFps isn't a fraction");
            }

            video.Add(new VideoCapability
            {
                Codec = codec,
                Codecs = codecs,
                MaxWidth = Bounded(e, "maxWidth", 1, 100_000),
                MaxHeight = Bounded(e, "maxHeight", 1, 100_000),
                MaxBitDepth = Bounded(e, "maxBitDepth", 1, 16),
                MaxRefFrames = Bounded(e, "maxRefFrames", 1, 16),
                MaxFps = fps,
                Transfers = Integers(e, "transfers", MaxTransfers, 0, 255),
                Dv = Integers(e, "dv", MaxDv, 0, 127),
                Hdr = List(e, "hdr", MaxTokens, Names.IsToken),
                ThreeD = List(e, "threeD", MaxTokens, Names.IsToken),
                Interlaced = Playback.Boolean(e, "interlaced") ?? false,
            });
        }

        var audio = new List<AudioCapability>();
        foreach (var e in Objects(o, "audio", MaxEntries))
        {
            var (codec, codecs) = CodecEntry(e);
            audio.Add(new AudioCapability
            {
                Codec = codec,
                Codecs = codecs,
                MaxChannels = Bounded(e, "maxChannels", 1, 64),
                MaxSampleRate = Bounded(e, "maxSampleRate", 8_000, 768_000),
                MaxBitDepth = Bounded(e, "maxBitDepth", 8, 32),
                Objects = List(e, "objects", MaxTokens, Names.IsToken),
            });
        }

        return new Capabilities
        {
            Files = List(o, "files", MaxFiles, Names.IsMediaType),
            Segments = List(o, "segments", MaxSegments, Names.IsMediaType),
            Subtitles = List(o, "subtitles", MaxEntries, Names.IsToken),
            Video = video,
            Audio = audio,
        };
    }

    // An entry's codec, required, and its codecs strings, required for the
    // codecs whose streams require them.
    private static (string Codec, IReadOnlyList<string> Codecs) CodecEntry(JsonObject e)
    {
        if (Playback.Text(e, "codec") is not { } codec || !Names.IsToken(codec))
        {
            throw Playback.BadPlay("an entry without a codec token");
        }

        var codecs = List(e, "codecs", MaxCodecs, Copies.CodecsForm);
        if (Rfc6381.RequiredFor.Contains(codec, StringComparer.Ordinal) && codecs.Count == 0)
        {
            throw Playback.BadPlay("an entry of " + codec + " without codecs");
        }

        return (codec, codecs);
    }

    // An array of strings of at most most entries, each of its form.
    private static List<string> List(JsonObject o, string name, int most, Func<string, bool> form)
    {
        if (!o.TryGetPropertyValue(name, out var v))
        {
            return [];
        }

        if (v is not JsonArray a || a.Count > most)
        {
            throw Playback.BadPlay(name + " isn't an array within its bound");
        }

        var out_ = new List<string>(a.Count);
        foreach (var e in a)
        {
            if (JsonMembers.AsString(e) is not { } s || !form(s))
            {
                throw Playback.BadPlay(name + " holds a value not of its form");
            }

            out_.Add(s);
        }

        return out_;
    }

    // An array of at most most integers from lo to hi.
    private static List<int> Integers(JsonObject o, string name, int most, long lo, long hi)
    {
        if (!o.TryGetPropertyValue(name, out var v))
        {
            return [];
        }

        if (v is not JsonArray a || a.Count > most)
        {
            throw Playback.BadPlay(name + " isn't an array within its bound");
        }

        var out_ = new List<int>(a.Count);
        foreach (var e in a)
        {
            if (JsonMembers.AsInteger(e) is not { } n || n < lo || n > hi)
            {
                throw Playback.BadPlay(name + " holds a number out of its range");
            }

            out_.Add((int)n);
        }

        return out_;
    }

    // An array of at most most objects.
    private static List<JsonObject> Objects(JsonObject o, string name, int most)
    {
        if (!o.TryGetPropertyValue(name, out var v))
        {
            return [];
        }

        if (v is not JsonArray a || a.Count > most)
        {
            throw Playback.BadPlay(name + " isn't an array within its bound");
        }

        return a.Select(e => e as JsonObject ?? throw Playback.BadPlay(name + " holds an entry that isn't an object")).ToList();
    }

    // An integer member from lo to hi; 0 when absent.
    private static int Bounded(JsonObject o, string name, long lo, long hi)
    {
        if (!o.TryGetPropertyValue(name, out var v))
        {
            return 0;
        }

        return JsonMembers.AsInteger(v) is { } n && n >= lo && n <= hi ? (int)n : throw Playback.BadPlay(name + " is out of its range");
    }
}

/// <summary>
/// A capability list's video entry (MSFP Media playing 2.8): one codec and
/// how far the device plays it. Zero numbers are absent: no bound, but
/// MaxBitDepth, 8 when absent; no Transfers is standard range only (1, 6,
/// 14, 15).
/// </summary>
public sealed record VideoCapability
{
    /// <summary>The codec, a registry token.</summary>
    public string Codec { get; init; } = "";

    /// <summary>The codecs strings played (RFC 6381).</summary>
    public IReadOnlyList<string> Codecs { get; init; } = [];

    /// <summary>The widest picture; 0 for no bound.</summary>
    public int MaxWidth { get; init; }

    /// <summary>The tallest picture; 0 for no bound.</summary>
    public int MaxHeight { get; init; }

    /// <summary>The highest frame rate, a fraction; "" for no bound.</summary>
    public string MaxFps { get; init; } = "";

    /// <summary>The deepest bit depth; 0 when absent (8).</summary>
    public int MaxBitDepth { get; init; }

    /// <summary>The H.273 transfers played; none: standard range only.</summary>
    public IReadOnlyList<int> Transfers { get; init; } = [];

    /// <summary>The HDR formats played (tokens).</summary>
    public IReadOnlyList<string> Hdr { get; init; } = [];

    /// <summary>The Dolby Vision profiles played.</summary>
    public IReadOnlyList<int> Dv { get; init; } = [];

    /// <summary>Whether interlaced pictures play.</summary>
    public bool Interlaced { get; init; }

    /// <summary>The most reference frames, for h264; 0 for no bound.</summary>
    public int MaxRefFrames { get; init; }

    /// <summary>The 3D layouts played (tokens).</summary>
    public IReadOnlyList<string> ThreeD { get; init; } = [];
}

/// <summary>
/// A capability list's audio entry (MSFP Media playing 2.8): one codec and
/// how far the device plays it. Zero numbers are absent: no bound, but
/// MaxChannels, 2 when absent.
/// </summary>
public sealed record AudioCapability
{
    /// <summary>The codec, a registry token.</summary>
    public string Codec { get; init; } = "";

    /// <summary>The codecs strings played (RFC 6381).</summary>
    public IReadOnlyList<string> Codecs { get; init; } = [];

    /// <summary>The most channels; 0 when absent (2).</summary>
    public int MaxChannels { get; init; }

    /// <summary>The object formats played (tokens).</summary>
    public IReadOnlyList<string> Objects { get; init; } = [];

    /// <summary>The highest sample rate; 0 for no bound.</summary>
    public int MaxSampleRate { get; init; }

    /// <summary>The deepest bit depth; 0 for no bound.</summary>
    public int MaxBitDepth { get; init; }
}
