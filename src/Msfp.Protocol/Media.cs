using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// Media (MSFP media, section 3): what a server checks of the parts of a
/// stream it fetches from another before relaying them: HLS playlists
/// here; the file's ranges and answers, and the scan cap, in MediaFile.cs;
/// WebVTT, ASS, PGS and fonts in <see cref="WebVtt"/>, <see cref="Ass"/>,
/// <see cref="Pgs"/> and <see cref="Font"/>.
/// </summary>
public static partial class Media
{
    /// <summary>The most a playlist may be, as read: 2 MiB (MSFP Media fetching 2.4).</summary>
    public const int MaxPlaylist = 2 << 20;

    /// <summary>The most URIs a paired server's playlist may hold (MSFP Media fetching 3.1): URI lines and URI= values together.</summary>
    public const int MaxPlaylistUris = 50_000;

    /// <summary>The most #EXT-X-MEDIA lines a master playlist may hold (MSFP Media fetching 3.1).</summary>
    public const int MaxRenditions = 32;

    /// <summary>The longest target duration a media playlist may give, in seconds (MSFP Media fetching 3.1).</summary>
    public const int MaxTargetDuration = 60;

    /// <summary>The tags a relayed master playlist keeps (MSFP Media fetching 3.1); other tags and comments are dropped.</summary>
    public static readonly IReadOnlyList<string> MasterSubset =
        ["#EXTM3U", "#EXT-X-VERSION", "#EXT-X-INDEPENDENT-SEGMENTS", "#EXT-X-STREAM-INF", "#EXT-X-MEDIA"];

    /// <summary>The tags a relayed media playlist keeps (MSFP Media fetching 3.1); other tags and comments are dropped.</summary>
    public static readonly IReadOnlyList<string> MediaSubset =
    [
        "#EXTM3U", "#EXT-X-VERSION", "#EXT-X-TARGETDURATION", "#EXT-X-MEDIA-SEQUENCE",
        "#EXT-X-PLAYLIST-TYPE", "#EXT-X-INDEPENDENT-SEGMENTS", "#EXTINF", "#EXT-X-DISCONTINUITY", "#EXT-X-ENDLIST", "#EXT-X-MAP",
    ];

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex FileNameRe();

    [GeneratedRegex(@"^#EXTINF:([0-9]+(?:\.[0-9]+)?)(?:,.*)?\z")]
    private static partial Regex ExtinfForm();

    [GeneratedRegex(@"^#EXT-X-TARGETDURATION:([0-9]+)\z")]
    private static partial Regex TargetForm();

    // One attribute of an attribute list (RFC 8216 section 4.2), and the
    // comma after it.
    [GeneratedRegex("^([A-Z0-9-]+)=(\"[^\"\\r\\n]*\"|[^\",]*)(?:,|\\z)")]
    private static partial Regex HlsAttribute();

    [GeneratedRegex(@"^-?[0-9]+\z")]
    private static partial Regex IntForm();

    /// <summary>
    /// Whether <paramref name="s"/> may name a file of a stream (MSFP
    /// Media fetching 1): 1 to 64 characters of A–Z a–z 0–9 . _ -, never "." or
    /// "..", which a URL resolves as a folder, not a file.
    /// </summary>
    public static bool FileName(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s != "." && s != ".." && FileNameRe().IsMatch(s);
    }

    /// <summary>
    /// The folder of queue entry <paramref name="n"/>'s stream (MSFP
    /// Media fetching 1 and 3), under a playback's base (&lt;stream root&gt;&lt;ticket&gt;/):
    /// its file and master.m3u8 are there, and the bare names its playlists
    /// give resolve there.
    /// </summary>
    /// <param name="baseUrl">The playback's base, ending in "/".</param>
    /// <param name="n">The queue entry's number.</param>
    public static string QueueStreamBase(string baseUrl, int n) =>
        baseUrl + "queue/" + n.ToString(CultureInfo.InvariantCulture) + "/";

    /// <summary>
    /// Checks a paired server's playlist as MSFP Media fetching 3.1 says, in its
    /// order (<see cref="FilterPlaylist"/>).
    /// </summary>
    /// <param name="body">The playlist, as fetched.</param>
    /// <param name="audio">Whether an ID is one of the playback's audio tracks; null for a playback without any.</param>
    /// <exception cref="ProtocolException">The playlist is outside what MSFP allows (<see cref="ProtocolError.Playlist"/>); its message names the step.</exception>
    public static void CheckPlaylist(ReadOnlySpan<byte> body, Func<string, bool>? audio) => FilterPlaylist(body, null, audio);

    /// <summary>
    /// A paired server's playlist once checked as MSFP Media fetching 3.1 says, in
    /// its order, keeping only the tags of <paramref name="subset"/>, comments
    /// dropped, each line ending in a line feed; with a null subset, every
    /// line is kept. The checks: at most 2 MiB (1); lines split at line
    /// feeds, one carriage return at a line's end removed, and no character
    /// players end a line at inside one (2); a first line of exactly #EXTM3U
    /// (3); every URI line a file name of the stream (4), and every URI=, in
    /// any case and anywhere in a tag line, a quoted one (5); no
    /// EXT-X-DEFINE (6), EXT-X-KEY or EXT-X-SESSION-KEY (7); at most
    /// <see cref="MaxPlaylistUris"/> URIs (8); a media playlist (one with
    /// #EXTINF) complete, of type VOD, its durations within its target
    /// duration, and a playlist either a media or a master one (9); a
    /// master's #EXT-X-MEDIA audio renditions, at most
    /// <see cref="MaxRenditions"/>, each NAME an audio track ID that
    /// <paramref name="audio"/> says the playback has (10).
    /// </summary>
    /// <param name="body">The playlist, as fetched.</param>
    /// <param name="subset">The tags kept (<see cref="MasterSubset"/> or <see cref="MediaSubset"/>); null keeps every line.</param>
    /// <param name="audio">Whether an ID is one of the playback's audio tracks; null for a playback without any.</param>
    /// <remarks>Bytes that aren't UTF-8 in a kept tag line are kept as they came.</remarks>
    /// <exception cref="ProtocolException">The playlist is outside what MSFP allows (<see cref="ProtocolError.Playlist"/>); its message names the step.</exception>
    public static byte[] FilterPlaylist(ReadOnlySpan<byte> body, IReadOnlyCollection<string>? subset, Func<string, bool>? audio)
    {
        if (body.Length > MaxPlaylist)
        {
            throw Step(1, "over 2 MiB");
        }

        // Lines as bytes, one char per byte (Latin-1): offsets and bytes
        // stay the playlist's own, whatever it holds.
        var lines = Encoding.Latin1.GetString(body).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }

            // Players end lines at a carriage return, or at U+2028, too:
            // such a character inside a line would hide what follows it
            // from these checks, as another line players read.
            if (HasLineBreaking(Encoding.Latin1.GetBytes(lines[i])))
            {
                throw Step(2, "a control character inside a line");
            }
        }

        if (lines[0] != "#EXTM3U")
        {
            throw Step(3, "not a playlist");
        }

        int uris = 0, lastUri = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!FileName(line))
            {
                throw Step(4, "a URI line that isn't a file of the stream");
            }

            uris++;
            lastUri = i;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                var (n, ok) = QuotedUris(line);
                if (!ok)
                {
                    throw Step(5, "a URI attribute that isn't a quoted file of the stream");
                }

                uris += n;
            }
        }

        if (lines.Any(l => HlsTag(l, "#EXT-X-DEFINE")))
        {
            throw Step(6, "variables, which MSFP leaves out");
        }

        if (lines.Any(l => l.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) || l.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal)))
        {
            throw Step(7, "keys, which MSFP leaves out");
        }

        if (uris > MaxPlaylistUris)
        {
            throw Step(8, "too many addresses");
        }

        var media = lines.Any(l => HlsTag(l, "#EXTINF"));
        var master = lines.Any(l => HlsTag(l, "#EXT-X-STREAM-INF"));
        if (!media && !master)
        {
            throw Step(9, "neither a master nor a media playlist");
        }

        if (media && CheckMediaPlaylist(lines, lastUri) is { } why)
        {
            throw Step(9, why);
        }

        if (master && CheckRenditions(lines, audio) is { } why10)
        {
            throw Step(10, why10);
        }

        var out_ = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#') && subset is not null)
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (!subset.Contains(colon < 0 ? line : line[..colon]))
                {
                    continue;
                }
            }

            out_.Append(line).Append('\n');
        }

        return Encoding.Latin1.GetBytes(out_.ToString());
    }

    /// <summary>A checked playlist's URI lines.</summary>
    public static IReadOnlyList<string> UriLines(ReadOnlySpan<byte> playlist)
    {
        var out_ = new List<string>();
        foreach (var line in Encoding.Latin1.GetString(playlist).Split('\n'))
        {
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                out_.Add(line);
            }
        }

        return out_;
    }

    private static ProtocolException Step(int step, string why) =>
        new(ProtocolError.Playlist, $"msfp: a playlist outside what MSFP allows, step {step}: {why}");

    // Whether line is the tag name, alone or with a value.
    private static bool HlsTag(string line, string name) =>
        line == name || (line.Length > name.Length && line.StartsWith(name, StringComparison.Ordinal) && line[name.Length] == ':');

    // Step 9 for a media playlist: complete from the first fetch (one
    // #EXT-X-PLAYLIST-TYPE:VOD, one #EXT-X-ENDLIST after the last URI
    // line), a target duration of 1 to 60 seconds, every #EXTINF above 0
    // and, rounded, at most the first target duration. Null when it passes.
    private static string? CheckMediaPlaylist(string[] lines, int lastUri)
    {
        int vod = 0, ends = 0, endAt = -1;
        long target = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l == "#EXT-X-PLAYLIST-TYPE:VOD")
            {
                vod++;
            }
            else if (HlsTag(l, "#EXT-X-PLAYLIST-TYPE"))
            {
                return "a playlist that grows, of a type other than VOD";
            }
            else if (l == "#EXT-X-ENDLIST")
            {
                ends++;
                endAt = i;
            }
            else if (HlsTag(l, "#EXT-X-TARGETDURATION"))
            {
                var m = TargetForm().Match(l);
                if (!m.Success || !Int(m.Groups[1].Value, 1, MaxTargetDuration))
                {
                    return "a target duration that isn't 1 to 60 seconds";
                }

                if (target < 0)
                {
                    target = long.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                }
            }
        }

        if (vod != 1)
        {
            return "not exactly one #EXT-X-PLAYLIST-TYPE:VOD";
        }

        if (ends != 1)
        {
            return "a playlist that grows, without exactly one #EXT-X-ENDLIST";
        }

        if (endAt < lastUri)
        {
            return "#EXT-X-ENDLIST before the last URI line";
        }

        if (target < 0)
        {
            return "no #EXT-X-TARGETDURATION";
        }

        foreach (var l in lines)
        {
            if (!HlsTag(l, "#EXTINF"))
            {
                continue;
            }

            var m = ExtinfForm().Match(l);
            if (!m.Success)
            {
                return "a segment duration that isn't a decimal";
            }

            if (!double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ||
                double.IsInfinity(d) || d <= 0)
            {
                return "a segment duration not above 0";
            }

            if (Math.Round(d, MidpointRounding.AwayFromZero) > target)
            {
                return "a segment longer than the target duration";
            }
        }

        return null;
    }

    // Reads an attribute list: each name once, quoted values with their
    // quotes; null when malformed.
    private static Dictionary<string, string>? HlsAttributes(string s)
    {
        var out_ = new Dictionary<string, string>(StringComparer.Ordinal);
        while (s.Length > 0)
        {
            var m = HlsAttribute().Match(s);
            if (!m.Success || !out_.TryAdd(m.Groups[1].Value, m.Groups[2].Value))
            {
                return null;
            }

            s = s[m.Length..];
        }

        return out_;
    }

    // Step 10 for a master playlist: at most 32 #EXT-X-MEDIA lines, each
    // TYPE=AUDIO and a quoted NAME that is one of the playback's audio
    // track IDs. Null when it passes.
    private static string? CheckRenditions(string[] lines, Func<string, bool>? audio)
    {
        var n = 0;
        foreach (var l in lines)
        {
            if (!HlsTag(l, "#EXT-X-MEDIA"))
            {
                continue;
            }

            if (++n > MaxRenditions)
            {
                return "over 32 renditions";
            }

            var list = l["#EXT-X-MEDIA".Length..];
            var a = HlsAttributes(list.StartsWith(':') ? list[1..] : list);
            if (a is null)
            {
                return "a rendition's malformed attribute list";
            }

            if (a.GetValueOrDefault("TYPE") != "AUDIO")
            {
                return "a rendition that isn't audio";
            }

            var name = a.GetValueOrDefault("NAME", "");
            if (name.Length < 2 || name[0] != '"' || name[^1] != '"' || audio is null || !audio(name[1..^1]))
            {
                return "a rendition whose NAME isn't an audio track of the playback";
            }
        }

        return null;
    }

    // Whether s is an integer (an optional "-", then digits) from lo to hi.
    internal static bool Int(string s, long lo, long hi) =>
        IntForm().IsMatch(s) &&
        long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v >= lo && v <= hi;

    // Whether a line holds a character that may end a line for some
    // player, or hide what follows it: control characters but tabs, and
    // Unicode's line breaks. Bytes that aren't UTF-8 are none of them.
    private static bool HasLineBreaking(ReadOnlySpan<byte> line)
    {
        while (!line.IsEmpty)
        {
            Rune.DecodeFromUtf8(line, out var r, out var n);
            var v = r.Value;
            if ((v < 0x20 && v != '\t') || v == 0x7f || v == 0x85 || v == 0x2028 || v == 0x2029)
            {
                return true;
            }

            line = line[n..];
        }

        return false;
    }

    // Counts a tag line's URI= values, false when one isn't a quoted file
    // name of the stream: every URI=, in any case and anywhere in the line
    // (inside another attribute's quoted string too), followed by a quote,
    // a file name and a quote. HLS quoted strings have no escapes.
    private static (int Count, bool Ok) QuotedUris(string line)
    {
        // Upper case, ASCII letters only: offsets stay the line's.
        var chars = line.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'a' and <= 'z')
            {
                chars[i] = (char)(chars[i] - 'a' + 'A');
            }
        }

        var upper = new string(chars);
        var n = 0;
        for (var from = 0; ;)
        {
            var i = upper.IndexOf("URI=", from, StringComparison.Ordinal);
            if (i < 0)
            {
                return (n, true);
            }

            var rest = line[(i + 4)..];
            if (!rest.StartsWith('"'))
            {
                return (n, false);
            }

            var close = rest.IndexOf('"', 1);
            if (close < 0 || !FileName(rest[1..close]))
            {
                return (n, false);
            }

            n++;
            from = i + 4;
        }
    }
}
