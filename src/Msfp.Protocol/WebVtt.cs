using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// Text subtitles (MSFP video, section 6.3): a WebVTT part is rebuilt, not
/// filtered. Only cue timings and plain cue text survive; cue settings,
/// identifiers, styles, regions, notes and every tag go. The owning server
/// rebuilds what it serves, the viewer's server again what it relays.
/// </summary>
public static partial class WebVtt
{
    /// <summary>The most a WebVTT part may be, in bytes, as read.</summary>
    public const int MaxWebVtt = 5 << 20;

    /// <summary>How long a part of subtitles covers, in seconds (video section 6.2): part n covers n × 120 to (n + 1) × 120 seconds.</summary>
    public const int StretchSeconds = 120;

    private const int MaxCues = 50_000;
    private const int MaxCueLine = 500; // bytes, a cue text line once cleaned

    private static readonly UTF8Encoding Utf8 = new(false, false);

    // A timing line, once white space at its ends is removed: two
    // timestamps ([hours:]minutes:seconds.milliseconds) around "-->", with
    // spaces, tabs or form feeds between, then the cue settings.
    [GeneratedRegex(@"^((?:[0-9]{1,3}:)?[0-9]{2}:[0-9]{2}\.[0-9]{3})[ \t\f]+-->[ \t\f]+((?:[0-9]{1,3}:)?[0-9]{2}:[0-9]{2}\.[0-9]{3})(?:[ \t\f].*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CueTiming();

    /// <summary>
    /// Rebuilds a WebVTT part as video section 6.3 says: false when it is
    /// over <see cref="MaxWebVtt"/>, doesn't start with WEBVTT, or holds
    /// more than 50,000 cues once rebuilt.
    /// </summary>
    /// <param name="input">The part, as fetched or read.</param>
    /// <param name="output">The part rebuilt; empty when false.</param>
    public static bool TryRebuild(ReadOnlySpan<byte> input, out byte[] output)
    {
        output = [];
        if (input.Length > MaxWebVtt)
        {
            return false;
        }

        // Bytes that aren't UTF-8 read as U+FFFD, which cue text drops.
        var text = Utf8.GetString(input).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (text.StartsWith('﻿'))
        {
            text = text[1..]; // one byte order mark
        }

        if (!text.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            return false;
        }

        var out_ = new StringBuilder("WEBVTT\n");
        var cues = 0;
        foreach (var block in text.Split("\n\n"))
        {
            var lines = block.Trim('\n').Split('\n');

            // The first timing line starts the cue; what's before it (the
            // header, an identifier) goes. A block without one goes whole.
            var timing = -1;
            Match? m = null;
            for (var i = 0; i < lines.Length; i++)
            {
                m = CueTiming().Match(TrimSpace(lines[i]));
                if (m.Success)
                {
                    timing = i;
                    break;
                }
            }

            if (timing < 0)
            {
                continue;
            }

            var body = new List<string>();
            foreach (var l in lines.AsSpan(timing + 1))
            {
                var c = CleanCueLine(l);
                if (c.Length > 0)
                {
                    body.Add(c);
                }
            }

            if (body.Count == 0)
            {
                continue;
            }

            if (++cues > MaxCues)
            {
                return false;
            }

            out_.Append('\n').Append(m!.Groups[1].Value).Append(" --> ").Append(m.Groups[2].Value).Append('\n')
                .AppendJoin('\n', body).Append('\n');
        }

        output = Utf8.GetBytes(out_.ToString());
        return true;
    }

    /// <summary>
    /// Part <paramref name="n"/> of a whole WebVTT track, for an owning
    /// server that holds its subtitles as one file: the track rebuilt as
    /// <see cref="TryRebuild"/> does, keeping the cues that show during the
    /// part's stretch, from 10 seconds before it (video section 6.2). A part
    /// with no cue is "WEBVTT" and a line feed. False when the track isn't
    /// WebVTT.
    /// </summary>
    /// <param name="track">The whole track.</param>
    /// <param name="n">The part's number, from 0.</param>
    /// <param name="output">The part; empty when false.</param>
    public static bool TryPart(ReadOnlySpan<byte> track, int n, out byte[] output)
    {
        output = [];
        if (!TryRebuild(track, out var all))
        {
            return false;
        }

        var from = (long)((n * StretchSeconds) - 10) * 1000;
        var to = (long)(n + 1) * StretchSeconds * 1000;
        var out_ = new StringBuilder("WEBVTT\n");
        foreach (var cue in Utf8.GetString(all).Split("\n\n").Skip(1))
        {
            var newline = cue.IndexOf('\n', StringComparison.Ordinal);
            var timing = newline < 0 ? cue : cue[..newline];
            var m = CueTiming().Match(timing);
            if (m.Success && CueMs(m.Groups[2].Value) > from && CueMs(m.Groups[1].Value) < to)
            {
                out_.Append('\n').Append(cue);
                if (!cue.EndsWith('\n'))
                {
                    out_.Append('\n');
                }
            }
        }

        output = Utf8.GetBytes(out_.ToString());
        return true;
    }

    // A cue timestamp, [hours:]minutes:seconds.milliseconds, in milliseconds.
    private static long CueMs(string ts)
    {
        var dot = ts.IndexOf('.', StringComparison.Ordinal);
        var clock = dot < 0 ? ts : ts[..dot];
        var frac = dot < 0 ? "" : ts[(dot + 1)..];
        long ms = 0;
        foreach (var part in clock.Split(':'))
        {
            _ = long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var p);
            ms = (ms * 60) + p;
        }

        _ = long.TryParse(frac, NumberStyles.None, CultureInfo.InvariantCulture, out var f);
        return (ms * 1000) + f;
    }

    // Cleans a cue text line, in video section 6.3's steps: invalid UTF-8,
    // U+FFFD and direction controls removed; tags removed; character
    // references decoded; both again; controls and white space made
    // spaces, collapsed and trimmed; cut to 500 bytes; then escaped.
    private static string CleanCueLine(string s)
    {
        s = StripCueTags(DropInvisible(s));
        s = UnescapeHtml(s);
        s = StripCueTags(DropInvisible(s));
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(f => SplitControls(f)));
        if (Encoding.UTF8.GetByteCount(s) > MaxCueLine)
        {
            var cut = new StringBuilder();
            var bytes = 0;
            foreach (var r in s.EnumerateRunes())
            {
                if (bytes + r.Utf8SequenceLength > MaxCueLine)
                {
                    break;
                }

                bytes += r.Utf8SequenceLength;
                cut.Append(r.ToString());
            }

            s = cut.ToString().TrimEnd(' ');
        }

        return s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    // The fields of a word split again at control characters (white space
    // is split already).
    private static IEnumerable<string> SplitControls(string word)
    {
        var start = 0;
        for (var i = 0; i <= word.Length; i++)
        {
            if (i == word.Length || char.IsControl(word[i]))
            {
                if (i > start)
                {
                    yield return word[start..i];
                }

                start = i + 1;
            }
        }
    }

    // White space at both ends removed (Unicode's White_Space, as Go's
    // unicode.IsSpace).
    private static string TrimSpace(string s) => s.Trim();

    // Removes tags: from a "<" followed by a letter (any script), "/", "!"
    // or "?", to the next ">", the tag becoming one space; one without a
    // ">" removes the rest of the line. Any other "<" stays.
    private static string StripCueTags(string s)
    {
        var b = new StringBuilder(s.Length);
        var inTag = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inTag)
            {
                if (c == '>')
                {
                    inTag = false;
                    b.Append(' ');
                }
            }
            else if (c == '<' && StartsTag(s, i + 1))
            {
                inTag = true;
            }
            else
            {
                b.Append(c);
            }
        }

        return b.ToString();
    }

    private static bool StartsTag(string s, int at)
    {
        if (at >= s.Length || Rune.DecodeFromUtf16(s.AsSpan(at), out var r, out _) != System.Buffers.OperationStatus.Done)
        {
            return false;
        }

        return Rune.IsLetter(r) || r.Value is '/' or '!' or '?';
    }

    // Removes U+FFFD (bytes that weren't UTF-8 among them) and the
    // direction controls (U+061C, U+200E, U+200F, U+202A to U+202E, U+2066
    // to U+2069).
    private static string DropInvisible(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '�' or '؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))
            {
                continue;
            }

            b.Append(c);
        }

        return b.ToString();
    }

    // Windows-1252's characters for the numeric references 0x80 to 0x9F.
    private static readonly int[] Windows1252 =
    [
        0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008D, 0x017D, 0x008F,
        0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178,
    ];

    // Decodes character references as HTML text does (the WHATWG
    // tokenizer, outside attributes): named ones, the longest that matches,
    // legacy ones without a semicolon too; numeric ones, decimal or hex,
    // the semicolon optional; anything else kept as it is.
    internal static string UnescapeHtml(string s)
    {
        var amp = s.IndexOf('&', StringComparison.Ordinal);
        if (amp < 0)
        {
            return s;
        }

        var b = new StringBuilder(s.Length);
        b.Append(s, 0, amp);
        for (var src = amp; src < s.Length;)
        {
            if (s[src] != '&')
            {
                var next = s.IndexOf('&', src);
                if (next < 0)
                {
                    b.Append(s, src, s.Length - src);
                    break;
                }

                b.Append(s, src, next - src);
                src = next;
            }

            src += Reference(s.AsSpan(src), b);
        }

        return b.ToString();
    }

    // Reads the reference at s[0] == '&' into b; how many characters it took.
    private static int Reference(ReadOnlySpan<char> s, StringBuilder b)
    {
        if (s.Length <= 1)
        {
            b.Append('&');
            return 1;
        }

        int i;
        if (s[1] == '#')
        {
            if (s.Length <= 3)
            {
                b.Append('&');
                return 1;
            }

            i = 2;
            var hex = false;
            if (s[i] is 'x' or 'X')
            {
                hex = true;
                i++;
            }

            var x = 0; // wraps as Go's rune does
            while (i < s.Length)
            {
                var c = s[i];
                i++;
                if (hex)
                {
                    if (c is >= '0' and <= '9')
                    {
                        x = unchecked((16 * x) + c - '0');
                        continue;
                    }

                    if (c is >= 'a' and <= 'f')
                    {
                        x = unchecked((16 * x) + c - 'a' + 10);
                        continue;
                    }

                    if (c is >= 'A' and <= 'F')
                    {
                        x = unchecked((16 * x) + c - 'A' + 10);
                        continue;
                    }
                }
                else if (c is >= '0' and <= '9')
                {
                    x = unchecked((10 * x) + c - '0');
                    continue;
                }

                if (c != ';')
                {
                    i--;
                }

                break;
            }

            if (i <= 3)
            {
                b.Append('&');
                return 1;
            }

            if (x is >= 0x80 and <= 0x9F)
            {
                x = Windows1252[x - 0x80];
            }
            else if (x <= 0 || x is >= 0xD800 and <= 0xDFFF || x > 0x10FFFF)
            {
                x = 0xFFFD;
            }

            b.Append(char.ConvertFromUtf32(x));
            return i;
        }

        i = 1;
        while (i < s.Length)
        {
            var c = s[i];
            i++;
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                continue;
            }

            if (c != ';')
            {
                i--;
            }

            break;
        }

        var name = s[1..i].ToString();
        if (name.Length > 0)
        {
            if (HtmlEntities.Get(name) is { } text)
            {
                b.Append(text);
                return i;
            }

            for (var j = Math.Min(name.Length - 1, HtmlEntities.LongestWithoutSemicolon); j > 1; j--)
            {
                if (HtmlEntities.Get(name[..j]) is { } shorter && shorter.EnumerateRunes().Count() == 1)
                {
                    b.Append(shorter);
                    return j + 1;
                }
            }
        }

        b.Append(s[..i]);
        return i;
    }
}
