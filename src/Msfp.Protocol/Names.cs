using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// The one rule for unknown values (MSFP core section 8.5): the open fields
/// of every part take tokens, 1 to <see cref="MaxToken"/> characters of
/// a–z 0–9 . _ -. In a fact (titles, play's answer) a token is kept as it
/// is, known or not, and never turned into another value; a value that
/// isn't a token is malformed: an optional member is dropped alone, a
/// required one takes the entry that holds it. In a request, an unknown
/// token is ignored where the field is a hint and refused with 409
/// delivery-refused where it names what to serve; only a value that isn't a
/// token gets 400. No open field has a value meaning "other". The values a
/// part lists beside an open field are examples, not the list: a copy's
/// container, codecs and subtitle format take ffmpeg's names (video section
/// 3.5), its audio roles the DASH role scheme's (video section 3.3).
/// </summary>
public static class Names
{
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
