using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// The shared vocabulary of MSFP's content parts: the names a copy's
/// container and codecs take (video section 3.5, music section 3.3), which
/// are also the values of play's capabilities lists (playback section 2.1,
/// video section 5.1), and the other closed lists of video's copies and
/// extras (video sections 3.2, 3.3 and 3.9). Every name is lowercase and
/// compared byte for byte. The lists are the protocol's: callers read
/// them, never change them.
/// </summary>
public static class Names
{
    /// <summary>
    /// The name of anything outside a names list: a container or a codec a
    /// viewer's server reads as "convert it", an edition, a subtitle format
    /// outside its list.
    /// </summary>
    public const string Other = "other";

    /// <summary>
    /// The names list's containers (video section 3.5): first those a
    /// viewer's device commonly plays, then those a viewer's server must
    /// know of to convert them; play's capabilities take both. Anything else
    /// is sent, and kept, as <see cref="Other"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> Containers =
    [
        "mp4", "m4v", "mov", "webm", "mkv", "ts", "mp3", "aac", "m4a", "flac", "ogg", "opus", "wav",
        "avi", "m2ts", "wmv", "flv", "3gp",
    ];

    /// <summary>The names list's video codecs (video section 3.5), in the same two groups as <see cref="Containers"/>.</summary>
    public static readonly IReadOnlyList<string> VideoCodecs =
    [
        "h264", "hevc", "av1", "vp8", "vp9",
        "mpeg2video", "mpeg4", "vc1", "wmv3", "h263", "theora",
    ];

    /// <summary>The names list's audio codecs (video section 3.5), in the same two groups as <see cref="Containers"/>.</summary>
    public static readonly IReadOnlyList<string> AudioCodecs =
    [
        "aac", "mp3", "opus", "vorbis", "flac", "ac3", "eac3", "alac", "pcm_s16le",
        "dts", "truehd", "mp2", "wmav2", "pcm_s24le", "pcm_bluray",
    ];

    /// <summary>
    /// The high dynamic range formats a video stream's hdr and the hdr
    /// capability take, in the order a copy lists them (video sections 3.3
    /// and 5.1): a format sits beside its base, HDR10+ as ["hdr10",
    /// "hdr10plus"], Dolby Vision on HDR10 as ["hdr10", "dv"].
    /// </summary>
    public static readonly IReadOnlyList<string> HdrFormats = ["hdr10", "hdr10plus", "hlg", "dv"];

    /// <summary>
    /// The Dolby Vision profiles play's dvProfiles capability takes, in
    /// increasing order (video section 5.1); another is ignored. A copy's own
    /// dv object takes any profile from 0 to 10 (video section 3.3).
    /// </summary>
    public static readonly IReadOnlyList<int> DvProfiles = [4, 5, 7, 8, 9, 10];

    /// <summary>
    /// An audio track's profile values, and those of the audioProfiles
    /// capability (video section 3.3, playback section 2.1): a profile listed
    /// in capabilities means the device plays it in its codec (atmos in eac3
    /// or truehd, dts-hd-ma in dts), one not listed only the codec's core.
    /// </summary>
    public static readonly IReadOnlyList<string> AudioProfiles = ["atmos", "dtsx", "dts-hd-ma", "dts-hd-hra", "dts-es", "he-aac", "lc"];

    /// <summary>A video copy's edition values (video section 3.2); an unknown string is kept as <see cref="Other"/>.</summary>
    public static readonly IReadOnlyList<string> Editions =
        ["theatrical", "directors", "extended", "unrated", "remastered", "special", "imax", Other];

    /// <summary>An extra's extraType values (video section 3.9); an unknown or missing one is kept as "clip", not <see cref="Other"/>.</summary>
    public static readonly IReadOnlyList<string> ExtraTypes =
        ["trailer", "featurette", "behindthescenes", "deletedscene", "interview", "scene", "short", "clip"];

    /// <summary>A subtitle track's source formats (video section 3.3); an unknown or missing one is kept as <see cref="Other"/>.</summary>
    public static readonly IReadOnlyList<string> SubtitleFormats =
        ["subrip", "ass", "ssa", "webvtt", "mov_text", "ttml", "pgs", "vobsub", "dvb", Other];

    /// <summary>Whether <paramref name="s"/> is a container of the names list.</summary>
    public static bool IsContainer(string? s) => In(Containers, s);

    /// <summary>Whether <paramref name="s"/> is a video codec of the names list.</summary>
    public static bool IsVideoCodec(string? s) => In(VideoCodecs, s);

    /// <summary>Whether <paramref name="s"/> is an audio codec of the names list.</summary>
    public static bool IsAudioCodec(string? s) => In(AudioCodecs, s);

    /// <summary>
    /// <paramref name="s"/> when <paramref name="list"/> holds it,
    /// <see cref="Other"/> otherwise: how a sender names a container, a codec,
    /// an edition or a subtitle format, and how a receiver keeps one it
    /// doesn't know (NameOrOther(Containers, "matroska") is "other": a sender
    /// maps its media server's names to the list's first).
    /// </summary>
    public static string NameOrOther(IReadOnlyList<string> list, string? s)
    {
        ArgumentNullException.ThrowIfNull(list);
        return In(list, s) ? s! : Other;
    }

    private static bool In(IReadOnlyList<string> list, string? s) =>
        s is not null && list.Contains(s, StringComparer.Ordinal);

    /// <summary>The most characters of a token.</summary>
    public const int MaxToken = 32;

    /// <summary>
    /// The containers a remux names (playback section 2.7), as ffmpeg names
    /// them: mpegts (MPEG-TS segments) and mp4 (fragmented MP4 segments),
    /// the two segment formats of HLS. Another token is refused with 409
    /// delivery-refused.
    /// </summary>
    public static readonly IReadOnlyList<string> RemuxContainers = ["mpegts", "mp4"];

    /// <summary>
    /// The baseline's video codec (playback section 2.7): what the owning
    /// server serves without delivery, and a transcode's videoCodec left out.
    /// </summary>
    public const string BaselineVideoCodec = "h264";

    /// <summary>The baseline's audio codec (playback section 2.7), and a transcode's audioCodec left out.</summary>
    public const string BaselineAudioCodec = "aac";

    /// <summary>The baseline's most audio channels (playback section 2.7), and a transcode's audioChannels left out.</summary>
    public const int BaselineChannels = 2;

    /// <summary>Whether <paramref name="s"/> is a token: 1 to <see cref="MaxToken"/> characters of a–z 0–9 . _ -.</summary>
    public static bool IsToken(string? s)
    {
        if (s is null || s.Length is < 1 or > MaxToken)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The value of an open field as a fact keeps it (core section 8.5): the
    /// string when it is a token, known or not; null, malformed, for any
    /// other value (another JSON type included).
    /// </summary>
    public static string? Token(JsonNode? node) => JsonMembers.AsString(node) is { } s && IsToken(s) ? s : null;
}
