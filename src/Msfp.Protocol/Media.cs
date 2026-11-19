using System.Text;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// Media (MSFP media, section 3): what a server checks of the parts of a
/// stream it fetches from another before relaying them: HLS playlists
/// here, PGS pictures in <see cref="Pgs"/>.
/// </summary>
public static partial class Media
{
    /// <summary>The most a playlist may be, as read: 2 MiB (media section 2.4).</summary>
    public const int MaxPlaylist = 2 << 20;

    /// <summary>The most URIs a paired server's playlist may hold (media section 3.1): URI lines and URI= values together.</summary>
    public const int MaxPlaylistUris = 50_000;

    /// <summary>The tags a relayed master playlist keeps (media section 3.1); other tags and comments are dropped.</summary>
    public static readonly IReadOnlyList<string> MasterSubset =
        ["#EXTM3U", "#EXT-X-VERSION", "#EXT-X-INDEPENDENT-SEGMENTS", "#EXT-X-STREAM-INF"];

    /// <summary>The tags a relayed media playlist keeps (media section 3.1); other tags and comments are dropped.</summary>
    public static readonly IReadOnlyList<string> MediaSubset =
    [
        "#EXTM3U", "#EXT-X-VERSION", "#EXT-X-TARGETDURATION", "#EXT-X-MEDIA-SEQUENCE",
        "#EXT-X-PLAYLIST-TYPE", "#EXT-X-INDEPENDENT-SEGMENTS", "#EXTINF", "#EXT-X-DISCONTINUITY", "#EXT-X-ENDLIST", "#EXT-X-MAP",
    ];

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex FileNameRe();

    /// <summary>
    /// Whether <paramref name="s"/> may name a file of a stream (media
    /// section 1): 1 to 64 characters of A–Z a–z 0–9 . _ -, never "." or
    /// "..", which a URL resolves as a folder, not a file.
    /// </summary>
    public static bool FileName(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s != "." && s != ".." && FileNameRe().IsMatch(s);
    }

    /// <summary>
    /// Checks a paired server's playlist as media section 3.1 says, in its
    /// order: at most 2 MiB; lines split at line feeds, one carriage return
    /// at a line's end removed; a first line of exactly #EXTM3U; every other
    /// line empty, a tag (or comment) whose every URI=, in any case and
    /// anywhere, is a quoted file name of the stream, or a file name alone;
    /// no EXT-X-DEFINE, EXT-X-KEY or EXT-X-SESSION-KEY; at most
    /// <see cref="MaxPlaylistUris"/> URIs.
    /// </summary>
    /// <exception cref="ProtocolException">The playlist is outside what MSFP allows (<see cref="ProtocolError.Playlist"/>).</exception>
    public static void CheckPlaylist(ReadOnlySpan<byte> body) => FilterPlaylist(body, null);

    /// <summary>
    /// A paired server's playlist once checked (<see cref="CheckPlaylist"/>),
    /// keeping only the tags of <paramref name="subset"/>, comments dropped,
    /// each line ending in a line feed; with a null subset, every line is kept.
    /// </summary>
    /// <remarks>Bytes that aren't UTF-8 in a kept tag line are kept as they came.</remarks>
    /// <exception cref="ProtocolException">The playlist is outside what MSFP allows (<see cref="ProtocolError.Playlist"/>).</exception>
    public static byte[] FilterPlaylist(ReadOnlySpan<byte> body, IReadOnlyCollection<string>? subset)
    {
        if (body.Length > MaxPlaylist)
        {
            throw Refused("too large");
        }

        // Lines as bytes, one char per byte (Latin-1): offsets and bytes
        // stay the playlist's own, whatever it holds.
        var raw = Encoding.Latin1.GetString(body).Split('\n');
        var lines = new string[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i].EndsWith('\r') ? raw[i][..^1] : raw[i];
            lines[i] = line;

            // Players end lines at a carriage return, or at U+2028, too:
            // such a character inside a line would hide what follows it
            // from these checks, as another line players read.
            if (HasLineBreaking(Encoding.Latin1.GetBytes(line)))
            {
                throw Refused("a control character inside a line");
            }
        }

        if (lines[0] != "#EXTM3U")
        {
            throw Refused("not a playlist");
        }

        var out_ = new StringBuilder();
        var uris = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                var (n, ok) = QuotedUris(line);
                if (!ok)
                {
                    throw Refused("an address that isn't the stream's");
                }

                if (line == "#EXT-X-DEFINE" || line.StartsWith("#EXT-X-DEFINE:", StringComparison.Ordinal) ||
                    line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                    line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
                {
                    throw Refused("variables or keys, which MSFP leaves out");
                }

                uris += n;
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var tag = colon < 0 ? line : line[..colon];
                if (subset is not null && !subset.Contains(tag))
                {
                    continue;
                }
            }
            else if (!FileName(line))
            {
                throw Refused("an address that isn't the stream's");
            }
            else
            {
                uris++;
            }

            if (uris > MaxPlaylistUris)
            {
                throw Refused("too many addresses");
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

    private static ProtocolException Refused(string why) =>
        new(ProtocolError.Playlist, "a playlist outside what MSFP allows: " + why);

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
