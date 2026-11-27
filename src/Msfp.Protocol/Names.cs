using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// The one rule for unknown values (MSFP Media base 3): the open fields
/// of every part take tokens, 1 to <see cref="MaxToken"/> characters of
/// a–z 0–9 . _ -, or media types (<see cref="IsMediaType"/>). In a fact
/// (titles, an answer) a value is kept as it is, known or not, and never
/// turned into another value; a malformed one is dropped: an optional
/// member alone, a required one with the entry that holds it. In a request,
/// an unknown value is ignored where the field is a hint and refused with
/// 409 delivery-refused where it names what to serve; only a malformed one
/// gets 400. No list has a value meaning "other". The lists are the MSFP
/// registry's (spec/registry.md) or an outside vocabulary's (IANA media
/// types, RFC 6381, ITU-T H.273, the DASH role scheme, MusicBrainz); the
/// values the parts give beside an open field are examples.
/// </summary>
public static class Names
{
    /// <summary>The most characters of a token.</summary>
    public const int MaxToken = 32;

    /// <summary>The most characters of a media type (RFC 6838's bound).</summary>
    public const int MaxMediaType = 127;

    /// <summary>
    /// The subtypes a remux's media type names (MSFP Media playing 2.7): mp2t
    /// (MPEG-TS segments) and mp4 (fragmented MP4 segments), the two segment
    /// formats of HLS, as video/… or, for a copy with no video, audio/….
    /// Another well-formed media type is refused with 409 delivery-refused.
    /// </summary>
    public static readonly IReadOnlyList<string> RemuxSubtypes = ["mp2t", "mp4"];

    /// <summary>
    /// The value of a transcode's videoCodec or audioCodec that serves that
    /// stream unchanged (MSFP Media playing 2.7): a step of MSFP's own, like
    /// delivery, not a registry token.
    /// </summary>
    public const string Copy = "copy";

    /// <summary>A transcode's range tone-mapped to SDR, 8-bit, BT.709 (MSFP Media playing 2.7): the default.</summary>
    public const string RangeSdr = "sdr";

    /// <summary>A transcode's range keeping the copy's transfer and primaries, 10-bit (MSFP Media playing 2.7).</summary>
    public const string RangeKeep = "keep";

    /// <summary>
    /// The baseline's video codec (MSFP Media playing 2.7): what the owning
    /// server serves without delivery, and a transcode's videoCodec left out.
    /// </summary>
    public const string BaselineVideoCodec = "h264";

    /// <summary>The baseline's audio codec (MSFP Media playing 2.7), and a transcode's audioCodec left out.</summary>
    public const string BaselineAudioCodec = "aac";

    /// <summary>The baseline's most audio channels (MSFP Media playing 2.7), and a transcode's audioChannels left out.</summary>
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
    /// The value of an open field as a fact keeps it (MSFP Media base 3): the
    /// string when it is a token, known or not; null, malformed, for any
    /// other value (another JSON type included).
    /// </summary>
    public static string? Token(JsonNode? node) => JsonMembers.AsString(node) is { } s && IsToken(s) ? s : null;

    /// <summary>
    /// Whether <paramref name="s"/> is a media type (MSFP Media base 3): an
    /// IANA-form media type, lowercase, exactly one /, no parameters, each
    /// half starting with a letter or a digit, at most
    /// <see cref="MaxMediaType"/> characters of a–z 0–9 . + - _ /
    /// (video/mp4, application/ttml+xml).
    /// </summary>
    public static bool IsMediaType(string? s)
    {
        if (s is null || s.Length is < 1 or > MaxMediaType)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '+' or '-' or '_' or '/'))
            {
                return false;
            }
        }

        var slash = s.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || s.IndexOf('/', slash + 1) >= 0)
        {
            return false;
        }

        static bool Starts(ReadOnlySpan<char> h) => h.Length > 0 && h[0] is (>= 'a' and <= 'z') or (>= '0' and <= '9');
        return Starts(s.AsSpan(0, slash)) && Starts(s.AsSpan(slash + 1));
    }

    /// <summary>The value of a media-type field as a fact keeps it (MSFP Media base 3): the string when it is a media type, known or not; null otherwise.</summary>
    public static string? MediaType(JsonNode? node) => JsonMembers.AsString(node) is { } s && IsMediaType(s) ? s : null;
}
