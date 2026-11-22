using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// Text subtitles as WebVTT (MSFP video, section 6.3): a part is rebuilt,
/// not filtered. Cue timings survive, the settings that place a cue (line,
/// position, size, align), plain cue text, and the italic, bold and colour
/// tags, balanced; identifiers, styles, regions, notes and every other tag
/// go. The owning server rebuilds what it serves, the viewer's server again
/// what it relays.
/// </summary>
public static partial class WebVtt
{
    /// <summary>The most a WebVTT part may be, in bytes, as read: 5 MiB (video section 6.3, media section 2.4).</summary>
    public const int MaxWebVtt = 5 << 20;

    /// <summary>The most a whole text track may be, in bytes, as read, WebVTT or ASS: 16 MiB.</summary>
    public const int MaxWholeText = 16 << 20;

    /// <summary>How long a part of subtitles covers, in seconds (media section 3.4): part n covers n × 120 to (n + 1) × 120 seconds, from 10 seconds early.</summary>
    public const int StretchSeconds = 120;

    private const int MaxCues = 50_000;       // a part's cues, once rebuilt
    private const int MaxWholeCues = 200_000; // a whole track's
    private const int MaxCueLine = 500;       // bytes of a cue text line's text once cleaned, tags not counted
    private const int MaxCueTags = 32;        // tags a cue text line keeps
    private const int MaxCueDepth = 8;        // tags open at once in a cue
    private const long StretchEarly = 10_000; // how early a stretch starts, in milliseconds

    private static readonly UTF8Encoding Utf8 = new(false, false);

    // WebVTT's built-in text colour classes; bg_ before one is its
    // background colour.
    private static readonly string[] CueColours = ["white", "lime", "cyan", "red", "yellow", "magenta", "blue", "black"];

    // Cue settings (video section 6.3): each kept in this order, when it is
    // the first of its name and valid.
    private static readonly string[] CueSettingNames = ["line", "position", "size", "align"];

    // A timing line, once white space at its ends is removed: two
    // timestamps ([hours:]minutes:seconds.milliseconds) around "-->", with
    // spaces, tabs or form feeds between, then the cue settings.
    [GeneratedRegex(@"^((?:[0-9]{1,3}:)?[0-9]{2}:[0-9]{2}\.[0-9]{3})[ \t\f]+-->[ \t\f]+((?:[0-9]{1,3}:)?[0-9]{2}:[0-9]{2}\.[0-9]{3})([ \t\f].*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CueTiming();

    [GeneratedRegex(@"^([0-9]{1,3}(?:\.[0-9]{1,3})?%|-?[0-9]+)(?:,(?:start|center|end))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CueLineValue();

    [GeneratedRegex(@"^([0-9]{1,3}(?:\.[0-9]{1,3})?%)(?:,(?:line-left|center|line-right))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CuePosition();

    [GeneratedRegex(@"^([0-9]{1,3}(?:\.[0-9]{1,3})?%)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CueSize();

    /// <summary>
    /// Rebuilds a WebVTT part (a stretch) as video section 6.3 says: false
    /// when it is over <see cref="MaxWebVtt"/>, doesn't start with WEBVTT, or
    /// holds more than 50,000 cues once rebuilt.
    /// </summary>
    /// <param name="input">The part, as fetched or read.</param>
    /// <param name="output">The part rebuilt; empty when false.</param>
    public static bool TryRebuild(ReadOnlySpan<byte> input, out byte[] output) => Rebuild(input, MaxWebVtt, MaxCues, out output);

    /// <summary>
    /// Rebuilds a whole WebVTT track (a subtitles/&lt;track&gt;/whole answer)
    /// under the same rules as <see cref="TryRebuild"/>, with a whole
    /// track's bounds: <see cref="MaxWholeText"/> bytes, 200,000 cues.
    /// </summary>
    /// <param name="input">The track, as fetched or read.</param>
    /// <param name="output">The track rebuilt; empty when false.</param>
    public static bool TryRebuildWhole(ReadOnlySpan<byte> input, out byte[] output) => Rebuild(input, MaxWholeText, MaxWholeCues, out output);

    /// <summary>
    /// Part <paramref name="n"/> of a whole WebVTT track, for an owning
    /// server that holds its subtitles as one file: the track rebuilt as
    /// <see cref="TryRebuildWhole"/> does, keeping the cues that show during
    /// the part's stretch (media section 3.4). A part with no cue is
    /// "WEBVTT" and a line feed. False when the track isn't WebVTT within a
    /// whole track's bounds, or its part holds more cues than a part may.
    /// </summary>
    /// <param name="track">The whole track.</param>
    /// <param name="n">The part's number, from 0.</param>
    /// <param name="output">The part; empty when false.</param>
    public static bool TryPart(ReadOnlySpan<byte> track, int n, out byte[] output)
    {
        output = [];
        if (!TryRebuildWhole(track, out var all))
        {
            return false;
        }

        var out_ = new StringBuilder("WEBVTT\n");
        var cues = 0;
        foreach (var cue in Utf8.GetString(all).Split("\n\n").Skip(1))
        {
            var newline = cue.IndexOf('\n', StringComparison.Ordinal);
            var m = CueTiming().Match(newline < 0 ? cue : cue[..newline]);
            if (!m.Success || !InStretch(CueMs(m.Groups[1].Value), CueMs(m.Groups[2].Value), n))
            {
                continue;
            }

            if (++cues > MaxCues)
            {
                return false;
            }

            out_.Append('\n').Append(cue.EndsWith('\n') ? cue[..^1] : cue).Append('\n');
        }

        output = Utf8.GetBytes(out_.ToString());
        return true;
    }

    // Whether what shows from start to end (milliseconds) belongs in part
    // n: it ends after the part's start, 10 seconds early, and starts
    // before its end.
    internal static bool InStretch(long start, long end, int n) =>
        end > ((long)n * StretchSeconds * 1000) - StretchEarly && start < (long)(n + 1) * StretchSeconds * 1000;

    private static bool Rebuild(ReadOnlySpan<byte> input, int most, int mostCues, out byte[] output)
    {
        output = [];
        if (input.Length > most)
        {
            return false;
        }

        // Bytes that aren't UTF-8 read as U+FFFD, which cue text drops.
        var text = Utf8.GetString(input).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (text.StartsWith('\ufeff'))
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
                m = CueTiming().Match(lines[i].Trim());
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

            var body = new List<List<CuePiece>>();
            foreach (var l in lines.AsSpan(timing + 1))
            {
                if (CleanCueLine(l) is { } c)
                {
                    body.Add(c);
                }
            }

            if (body.Count == 0)
            {
                continue;
            }

            if (++cues > mostCues)
            {
                return false;
            }

            out_.Append('\n').Append(m!.Groups[1].Value).Append(" --> ").Append(m.Groups[2].Value)
                .Append(CueSettings(m.Groups[3].Value)).Append('\n');
            foreach (var l in BalanceCue(body))
            {
                out_.Append(l).Append('\n');
            }
        }

        output = Utf8.GetBytes(out_.ToString());
        return true;
    }

    // Keeps a timing line's settings, each written after a space.
    private static string CueSettings(string s)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in s.Split([' ', '\t', '\f'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = f.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            // The first of its name, valid or not: an invalid one keeps a
            // later one of its name out too.
            var name = f[..colon];
            if (!seen.Add(name))
            {
                continue;
            }

            if (CueSetting(name, f[(colon + 1)..]))
            {
                kept[name] = f;
            }
        }

        var out_ = new StringBuilder();
        foreach (var n in CueSettingNames)
        {
            if (kept.TryGetValue(n, out var f))
            {
                out_.Append(' ').Append(f);
            }
        }

        return out_.ToString();
    }

    private static bool CueSetting(string name, string value)
    {
        switch (name)
        {
            case "line":
            {
                var m = CueLineValue().Match(value);
                if (!m.Success)
                {
                    return false;
                }

                var v = m.Groups[1].Value;
                return v.EndsWith('%') ? CuePercentIn(v) : Media.Int(v, -1000, 1000);
            }

            case "position":
            {
                var m = CuePosition().Match(value);
                return m.Success && CuePercentIn(m.Groups[1].Value);
            }

            case "size":
            {
                var m = CueSize().Match(value);
                return m.Success && CuePercentIn(m.Groups[1].Value);
            }

            case "align":
                return value is "start" or "center" or "end" or "left" or "right";
            default:
                return false;
        }
    }

    // Whether a percentage of the cue settings' form is 0 to 100.
    private static bool CuePercentIn(string s) =>
        double.TryParse(s[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 100;

    // A piece of a cleaned cue text line: a kept tag, or text (escaped once
    // the line is cleaned).
    private readonly record struct CuePiece(bool Tag, string Text);

    // Whether a tag is kept (video section 6.3, step 3): <i>, <b>, their
    // end tags, </c>, or <c.> with one colour class, or one text colour and
    // one background colour.
    private static bool KeptCueTag(string t)
    {
        if (t is "<i>" or "</i>" or "<b>" or "</b>" or "</c>")
        {
            return true;
        }

        if (!t.StartsWith("<c.", StringComparison.Ordinal) || !t.EndsWith('>'))
        {
            return false;
        }

        var classes = t[3..^1].Split('.');
        if (classes.Length > 2)
        {
            return false;
        }

        var backgrounds = 0;
        foreach (var c in classes)
        {
            var bg = c.StartsWith("bg_", StringComparison.Ordinal);
            if (!CueColours.Contains(bg ? c[3..] : c))
            {
                return false;
            }

            if (bg)
            {
                backgrounds++;
            }
        }

        return classes.Length == 1 || backgrounds == 1;
    }

    // Whether s[i] is a "<" that starts a tag: one followed by a letter
    // (any script), "/", "!" or "?".
    private static bool CueTagAt(string s, int i)
    {
        if (s[i] != '<' || i + 1 >= s.Length ||
            Rune.DecodeFromUtf16(s.AsSpan(i + 1), out var r, out _) != System.Buffers.OperationStatus.Done)
        {
            return false;
        }

        return Rune.IsLetter(r) || r.Value is '/' or '!' or '?';
    }

    // Turns what is shaped like a tag into one space; one without its ">"
    // removes the rest of the text.
    private static string UntagCue(string s)
    {
        var b = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (!CueTagAt(s, i))
            {
                b.Append(s[i]);
                continue;
            }

            var j = s.IndexOf('>', i);
            if (j < 0)
            {
                break;
            }

            b.Append(' ');
            i = j;
        }

        return b.ToString();
    }

    // Cleans a cue text line, in video section 6.3's steps: its kept tags
    // and its text, escaped, or null when no text is left.
    private static List<CuePiece>? CleanCueLine(string s)
    {
        s = DropInvisible(s);

        // Steps 2 and 3: tags (kept, or a space) and text.
        var pieces = new List<CuePiece>();
        for (var i = 0; i < s.Length;)
        {
            if (CueTagAt(s, i))
            {
                var j = s.IndexOf('>', i);
                if (j < 0)
                {
                    break; // the rest of the line goes
                }

                var t = s[i..(j + 1)];
                pieces.Add(KeptCueTag(t) ? new CuePiece(true, t) : new CuePiece(false, " "));
                i = j + 1;
                continue;
            }

            var k = i + 1;
            while (k < s.Length && !CueTagAt(s, k))
            {
                k++;
            }

            pieces.Add(new CuePiece(false, s[i..k]));
            i = k;
        }

        // Steps 4 to 6: references decoded, spaces collapsed across tags,
        // the text cut to 500 bytes and the tags to 32.
        var out_ = new List<CuePiece>();
        void Write(string t)
        {
            if (out_.Count > 0 && !out_[^1].Tag)
            {
                out_[^1] = out_[^1] with { Text = out_[^1].Text + t };
            }
            else
            {
                out_.Add(new CuePiece(false, t));
            }
        }

        int size = 0, tags = 0;

        // space: white space seen since the last character written; text: a
        // character written; spaced: the last thing written is a space.
        bool space = false, text = false, spaced = false, full = false;
        foreach (var p in pieces)
        {
            if (full)
            {
                break;
            }

            if (p.Tag)
            {
                if (tags == MaxCueTags)
                {
                    continue;
                }

                // A run of spaces before a tag is written before it.
                if (space && text && !spaced)
                {
                    if (size + 1 > MaxCueLine)
                    {
                        break;
                    }

                    Write(" ");
                    size++;
                    spaced = true;
                }

                space = false;
                tags++;
                out_.Add(p);
                continue;
            }

            foreach (var r in UntagCue(DropInvisible(UnescapeHtml(p.Text))).EnumerateRunes())
            {
                if (Rune.IsControl(r) || Rune.IsWhiteSpace(r))
                {
                    space = true;
                    continue;
                }

                if (space && text && !spaced)
                {
                    if (size + 1 > MaxCueLine)
                    {
                        full = true;
                        break;
                    }

                    Write(" ");
                    size++;
                }

                space = false;
                var n = r.Utf8SequenceLength;
                if (size + n > MaxCueLine)
                {
                    full = true;
                    break;
                }

                Write(r.ToString());
                size += n;
                text = true;
                spaced = false;
            }
        }

        if (!text)
        {
            return null;
        }

        // Spaces at the end of the text go, tags after them or not.
        for (var i = out_.Count - 1; i >= 0; i--)
        {
            if (!out_[i].Tag)
            {
                var trimmed = out_[i].Text.TrimEnd(' ');
                if (trimmed.Length == 0)
                {
                    out_.RemoveAt(i);
                }
                else
                {
                    out_[i] = out_[i] with { Text = trimmed };
                }

                break;
            }
        }

        for (var i = 0; i < out_.Count; i++)
        {
            if (!out_[i].Tag)
            {
                out_[i] = out_[i] with { Text = Escape(out_[i].Text) };
            }
        }

        return out_;
    }

    // Plain text escaped for a cue: in WebVTT, "<" opens a tag and "&" a
    // character reference.
    private static string Escape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    // Balances the tags of a cue's lines and writes each line: an end tag
    // only closing the innermost open tag of its name, at most 8 open (one
    // past them removed with its end tag), those still open closed at the
    // cue's end, innermost first.
    private static List<string> BalanceCue(List<List<CuePiece>> lines)
    {
        var stack = new List<(string Name, bool Removed)>();
        var depth = 0;
        var out_ = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            var b = new StringBuilder();
            foreach (var p in l)
            {
                if (!p.Tag)
                {
                    b.Append(p.Text);
                    continue;
                }

                var name = p.Text.Trim('<', '/', '>');
                if (name.StartsWith("c.", StringComparison.Ordinal))
                {
                    name = "c";
                }

                if (!p.Text.StartsWith("</", StringComparison.Ordinal))
                {
                    if (depth == MaxCueDepth)
                    {
                        stack.Add((name, true));
                        continue;
                    }

                    stack.Add((name, false));
                    depth++;
                    b.Append(p.Text);
                    continue;
                }

                if (stack.Count > 0 && stack[^1].Name == name)
                {
                    if (!stack[^1].Removed)
                    {
                        depth--;
                        b.Append(p.Text);
                    }

                    stack.RemoveAt(stack.Count - 1);
                }
            }

            out_.Add(b.ToString());
        }

        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (!stack[i].Removed)
            {
                out_[^1] += "</" + stack[i].Name + ">";
            }
        }

        return out_;
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

    // Whether r is one of the direction controls (U+061C, U+200E, U+200F,
    // U+202A to U+202E, U+2066 to U+2069).
    internal static bool DirectionControl(int r) =>
        r is 0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);

    // Removes U+FFFD (bytes that weren't UTF-8 among them) and the
    // direction controls.
    private static string DropInvisible(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '�' || DirectionControl(c))
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
