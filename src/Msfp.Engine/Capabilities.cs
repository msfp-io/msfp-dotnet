using Msfp.Protocol;

namespace Msfp;

// P17: proposal 17 removed play's capabilities, the names lists and their
// coarsening from the protocol (Msfp.Protocol). The engine still describes
// a device this way until its own revision step; what it sends of them is
// an unknown member of play's request now, which an owning server ignores.
// This file goes with that step.

/// <summary>
/// What a viewer's device plays, as the engine still describes it (MSFP
/// before proposal 17). Each list holds only values of its names list, each
/// once, sorted (DvProfiles in increasing order); 0 is not said.
/// </summary>
public sealed record PlayCapabilities
{
    /// <summary>The containers it plays.</summary>
    public IReadOnlyList<string> Containers { get; init; } = [];

    /// <summary>The video codecs it plays.</summary>
    public IReadOnlyList<string> VideoCodecs { get; init; } = [];

    /// <summary>The audio codecs it plays.</summary>
    public IReadOnlyList<string> AudioCodecs { get; init; } = [];

    /// <summary>The audio profiles it plays in their codec.</summary>
    public IReadOnlyList<string> AudioProfiles { get; init; } = [];

    /// <summary>The HDR formats it shows.</summary>
    public IReadOnlyList<string> Hdr { get; init; } = [];

    /// <summary>The Dolby Vision profiles it shows.</summary>
    public IReadOnlyList<int> DvProfiles { get; init; } = [];

    /// <summary>The tallest picture, in pixels; 0: not said.</summary>
    public int MaxHeight { get; init; }

    /// <summary>The most audio channels; 0: not said.</summary>
    public int MaxAudioChannels { get; init; }

    /// <summary>The highest sample rate, in Hz; 0: not said.</summary>
    public int MaxAudioSampleRate { get; init; }

    /// <summary>The deepest bit depth; 0: not said.</summary>
    public int MaxAudioBitDepth { get; init; }

    /// <summary>The highest bitrate, in kbit/s; 0: not said.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>Whether it says nothing, as capabilities left out.</summary>
    public bool Empty =>
        Containers.Count + VideoCodecs.Count + AudioCodecs.Count + AudioProfiles.Count + Hdr.Count + DvProfiles.Count == 0 &&
        MaxHeight == 0 && MaxAudioChannels == 0 && MaxAudioSampleRate == 0 && MaxAudioBitDepth == 0 && MaxBitrateKbps == 0;

    /// <summary>
    /// What the engine sends of a device's capabilities: the lists
    /// lowercased, only their listed values, each once, sorted; maxHeight,
    /// maxAudioChannels, maxAudioSampleRate and maxAudioBitDepth stepped
    /// down; maxBitrateKbps rounded down to a whole 1,000, below 1,000 left
    /// out; 0 left out.
    /// </summary>
    public PlayCapabilities Coarsen()
    {
        static IReadOnlyList<string> Lower(IReadOnlyList<string> l, IReadOnlyList<string> names) =>
            OldNames.Listed(l.Select(e => e.ToLowerInvariant()), names);
        return new PlayCapabilities
        {
            Containers = Lower(Containers, OldNames.Containers),
            VideoCodecs = Lower(VideoCodecs, OldNames.VideoCodecs),
            AudioCodecs = Lower(AudioCodecs, OldNames.AudioCodecs),
            AudioProfiles = Lower(AudioProfiles, OldNames.AudioProfiles),
            Hdr = Lower(Hdr, OldNames.HdrFormats),
            DvProfiles = [.. DvProfiles.Where(OldNames.DvProfiles.Contains).Distinct().Order()],
            MaxHeight = Playback.StepDown(MaxHeight, Protocol.Video.Heights),
            MaxAudioChannels = Playback.StepDown(MaxAudioChannels, Playback.ChannelSteps),
            MaxAudioSampleRate = Playback.StepDown(MaxAudioSampleRate, OldNames.SampleRateSteps),
            MaxAudioBitDepth = Playback.StepDown(MaxAudioBitDepth, OldNames.BitDepthSteps),
            MaxBitrateKbps = MaxBitrateKbps >= 1000 ? Math.Min(MaxBitrateKbps, Playback.MaxKbps) / 1000 * 1000 : 0,
        };
    }

    /// <summary>Writes its members, those empty or 0 left out.</summary>
    public void Write(JsonOut w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Strings("containers", Some(Containers)).Strings("videoCodecs", Some(VideoCodecs))
            .Strings("audioCodecs", Some(AudioCodecs)).Strings("audioProfiles", Some(AudioProfiles)).Strings("hdr", Some(Hdr))
            .Integers("dvProfiles", DvProfiles.Count > 0 ? DvProfiles.Select(p => (long)p) : null)
            .Integer("maxHeight", NonZero(MaxHeight)).Integer("maxAudioChannels", NonZero(MaxAudioChannels))
            .Integer("maxAudioSampleRate", NonZero(MaxAudioSampleRate)).Integer("maxAudioBitDepth", NonZero(MaxAudioBitDepth))
            .Integer("maxBitrateKbps", NonZero(MaxBitrateKbps));
    }

    private static IReadOnlyList<string>? Some(IReadOnlyList<string> l) => l.Count > 0 ? l : null;

    private static long? NonZero(long v) => v != 0 ? v : null;
}

// P17: the names lists the protocol dropped, as the engine still uses them.
internal static class OldNames
{
    internal const string Other = "other";

    internal static readonly IReadOnlyList<string> Containers =
    [
        "mp4", "m4v", "mov", "webm", "mkv", "ts", "mp3", "aac", "m4a", "flac", "ogg", "opus", "wav",
        "avi", "m2ts", "wmv", "flv", "3gp",
    ];

    internal static readonly IReadOnlyList<string> VideoCodecs =
        ["h264", "hevc", "av1", "vp8", "vp9", "mpeg2video", "mpeg4", "vc1", "wmv3", "h263", "theora"];

    internal static readonly IReadOnlyList<string> AudioCodecs =
    [
        "aac", "mp3", "opus", "vorbis", "flac", "ac3", "eac3", "alac", "pcm_s16le",
        "dts", "truehd", "mp2", "wmav2", "pcm_s24le", "pcm_bluray",
    ];

    internal static readonly IReadOnlyList<string> HdrFormats = ["hdr10", "hdr10plus", "hlg", "dv"];

    internal static readonly IReadOnlyList<int> DvProfiles = [4, 5, 7, 8, 9, 10];

    internal static readonly IReadOnlyList<string> AudioProfiles = ["atmos", "dtsx", "dts-hd-ma", "dts-hd-hra", "dts-es", "he-aac", "lc"];

    internal static readonly IReadOnlyList<string> ExtraTypes =
        ["trailer", "featurette", "behindthescenes", "deletedscene", "interview", "scene", "short", "clip"];

    internal static readonly IReadOnlyList<int> SampleRateSteps = [44_100, 48_000, 96_000, 192_000];

    internal static readonly IReadOnlyList<int> BitDepthSteps = [16, 24, 32];

    internal static IReadOnlyList<string> Listed(IEnumerable<string> entries, IReadOnlyList<string> names) =>
        [.. entries.Where(e => names.Contains(e, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    internal static string NameOrOther(IReadOnlyList<string> list, string? s) =>
        s is not null && list.Contains(s, StringComparer.Ordinal) ? s : Other;

    // P17: the request's maxBitrateKbps as the engine still sends it,
    // rounded down to a whole 1,000 from 1,000 up.
    internal static int CoarsenBitrate(int kbps) => kbps >= 1000 ? Math.Min(kbps, Playback.MaxKbps) / 1000 * 1000 : Math.Max(kbps, 0);
}
