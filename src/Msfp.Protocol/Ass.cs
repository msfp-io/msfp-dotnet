using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// Text subtitles as ASS (MSFP video, section 6.4): an ASS or SSA script
/// served at a track's assUrl is rebuilt, not filtered: known sections,
/// known lines and known override tags only, anything else dropped, sizes
/// capped, and written back as an ASS (v4.00+) script in one form. The
/// owning server rebuilds what it serves (converting SSA to ASS on the
/// way), the viewer's server again what it relays.
/// </summary>
public static partial class Ass
{
    /// <summary>The most an ASS part may be, in bytes, as read: 5 MiB (MSFP Media video 6.4, MSFP Media fetching 2.4). A whole track is at most <see cref="WebVtt.MaxWholeText"/>.</summary>
    public const int MaxAss = 5 << 20;

    private const int MaxStyles = 1024;
    private const int MaxEvents = 50_000;       // a part's events, once rebuilt
    private const int MaxWholeEvents = 200_000; // a whole track's
    private const int MaxEventText = 65_536;    // bytes of an event's text, once rebuilt
    private const int MaxBlocks = 256;          // override blocks an event
    private const int MaxTags = 1024;           // kept tags an event
    private const int MaxTransforms = 16;       // \t an event
    private const int MaxNumbers = 10_000;      // numbers a drawing
    private const int MaxName = 64;             // bytes of a style's name or font name

    private static readonly UTF8Encoding Utf8 = new(false, false);

    [GeneratedRegex(@"^-?[0-9]{1,7}(?:\.[0-9]{1,3})?\z")]
    private static partial Regex NumberForm();

    [GeneratedRegex(@"^&H([0-9A-Fa-f]{1,8})&?\z")]
    private static partial Regex StyleHex();

    [GeneratedRegex(@"^[0-9]+\z")]
    private static partial Regex DecimalForm();

    [GeneratedRegex(@"^&H([0-9A-Fa-f]{1,6})&?\z")]
    private static partial Regex TagColour();

    [GeneratedRegex(@"^&H([0-9A-Fa-f]{1,2})&?\z")]
    private static partial Regex TagAlpha();

    [GeneratedRegex(@"^[0-9]{1,3}:[0-9]{2}:[0-9]{2}\.[0-9]{2}\z")]
    private static partial Regex Time();

    [GeneratedRegex(@"^-?[0-9]+\z")]
    private static partial Regex IntForm();

    /// <summary>
    /// Rebuilds an ASS or SSA part (a stretch) as MSFP Media video 6.4 says:
    /// false when it is over <see cref="MaxAss"/>, its first line isn't
    /// [Script Info], or it holds more than 50,000 events once rebuilt.
    /// </summary>
    /// <param name="input">The part, as fetched or read.</param>
    /// <param name="output">The part rebuilt; empty when false.</param>
    public static bool TryRebuild(ReadOnlySpan<byte> input, out byte[] output) => Rebuild(input, MaxAss, MaxEvents, out output);

    /// <summary>
    /// Rebuilds a whole ASS or SSA track (an ass/&lt;track&gt;/whole answer)
    /// under the same rules as <see cref="TryRebuild"/>, with a whole track's
    /// bounds: <see cref="WebVtt.MaxWholeText"/> bytes, 200,000 events.
    /// </summary>
    /// <param name="input">The track, as fetched or read.</param>
    /// <param name="output">The track rebuilt; empty when false.</param>
    public static bool TryRebuildWhole(ReadOnlySpan<byte> input, out byte[] output) =>
        Rebuild(input, WebVtt.MaxWholeText, MaxWholeEvents, out output);

    /// <summary>
    /// Part <paramref name="n"/> of a whole ASS or SSA track, for an owning
    /// server that holds its subtitles as one file: the track rebuilt as
    /// <see cref="TryRebuildWhole"/> does, its header and the events that
    /// show during the part's stretch (MSFP Media fetching 3.4, MSFP
    /// Media video 6.4). A part with no event is valid. False when the track is refused,
    /// or its part holds more events than a part may.
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

        var s = Utf8.GetString(all);
        var at = s.IndexOf("\nDialogue: ", StringComparison.Ordinal);
        if (at < 0)
        {
            output = all;
            return true;
        }

        var out_ = new StringBuilder(s[..at]).Append('\n');
        var kept = 0;
        var events = "Dialogue: " + s[(at + "\nDialogue: ".Length)..];
        foreach (var e in (events.EndsWith('\n') ? events[..^1] : events).Split('\n'))
        {
            // Rebuilt: layer, start, end first, in one form.
            var f = e["Dialogue: ".Length..].Split(',', 4);
            if (!WebVtt.InStretch(Ms(f[1]), Ms(f[2]), n))
            {
                continue;
            }

            if (++kept > MaxEvents)
            {
                return false;
            }

            out_.Append(e).Append('\n');
        }

        output = Utf8.GetBytes(out_.ToString());
        return true;
    }

    // An event's time, hours:minutes:seconds.centiseconds, in milliseconds.
    private static long Ms(string t)
    {
        var dot = t.IndexOf('.', StringComparison.Ordinal);
        var clock = dot < 0 ? t : t[..dot];
        var cs = dot < 0 ? "" : t[(dot + 1)..];
        long ms = 0;
        foreach (var part in clock.Split(':'))
        {
            _ = long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var p);
            ms = (ms * 60) + p;
        }

        _ = long.TryParse(cs, NumberStyles.None, CultureInfo.InvariantCulture, out var c);
        return (ms * 1000) + (c * 10);
    }

    // A line of a known section: the section's header, or a name and its
    // value. Section: "info", "v4+", "v4" or "events".
    private readonly record struct Line(string Section, bool Header, string Name, string Value);

    private static bool Rebuild(ReadOnlySpan<byte> input, int most, int mostEvents, out byte[] output)
    {
        output = [];
        if (input.Length > most)
        {
            return false;
        }

        var text = Utf8.GetString(input).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (text.StartsWith('\ufeff'))
        {
            text = text[1..];
        }

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith(';') || l.StartsWith("!:", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(l);
        }

        if (lines.Count == 0 || lines[0] != "[Script Info]")
        {
            return false;
        }

        // The lines of the known sections; every other section's go whole.
        var known = new List<Line>();
        var section = "";
        bool v4 = false, v4plus = false;
        foreach (var l in lines)
        {
            if (l.StartsWith('[') && l.EndsWith(']'))
            {
                if (Fold(l, "[Script Info]"))
                {
                    section = "info";
                }
                else if (Fold(l, "[V4+ Styles]"))
                {
                    section = "v4+";
                    v4plus = true;
                }
                else if (Fold(l, "[V4 Styles]"))
                {
                    section = "v4";
                    v4 = true;
                }
                else if (Fold(l, "[Events]"))
                {
                    section = "events";
                }
                else
                {
                    section = "";
                }

                if (section.Length > 0)
                {
                    known.Add(new Line(section, true, "", ""));
                }

                continue;
            }

            var colon = l.IndexOf(':', StringComparison.Ordinal);
            if (section.Length == 0 || colon < 0)
            {
                continue;
            }

            known.Add(new Line(section, false, l[..colon], l[(colon + 1)..].Trim()));
        }

        // Script information: the first line of each name, kept when valid.
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in known)
        {
            if (l.Section != "info" || l.Header)
            {
                continue;
            }

            foreach (var (name, ok) in Info)
            {
                if (Fold(l.Name, name) && seen.Add(name) && ok(l.Value))
                {
                    info[name] = l.Value;
                }
            }
        }

        // Styles.
        var styles = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        string[] format = [];
        foreach (var l in known)
        {
            var ssa = l.Section == "v4";
            if (!ssa && l.Section != "v4+")
            {
                continue;
            }

            if (l.Header)
            {
                format = ssa ? V4Style : V4PlusStyle;
            }
            else if (l.Name == "Format")
            {
                format = Format(l.Value);
            }
            else if (l.Name == "Style")
            {
                if (Style(l.Value, format, ssa) is not { } s || names.Contains(s.Name) || styles.Count == MaxStyles)
                {
                    continue;
                }

                names.Add(s.Name);
                styles.Add(s.Written);
            }
        }

        // Events: an SSA script's take SSA's default format.
        var events = new List<string>();
        foreach (var l in known)
        {
            if (l.Section != "events")
            {
                continue;
            }

            if (l.Header)
            {
                format = v4 && !v4plus ? SsaEvent : EventFormat;
            }
            else if (l.Name == "Format")
            {
                format = Format(l.Value);
            }
            else if (l.Name == "Dialogue")
            {
                if (Event(l.Value, format, names) is not { } e)
                {
                    continue;
                }

                if (events.Count == mostEvents)
                {
                    return false;
                }

                events.Add(e);
            }
        }

        var out_ = new StringBuilder("[Script Info]\nScriptType: v4.00+\n");
        foreach (var (name, _) in Info)
        {
            if (info.TryGetValue(name, out var v))
            {
                out_.Append(name).Append(": ").Append(v).Append('\n');
            }
        }

        out_.Append("\n[V4+ Styles]\nFormat: ").AppendJoin(", ", V4PlusStyle).Append('\n');
        if (!names.Contains("Default"))
        {
            out_.Append("Style: ").Append(DefaultStyle()).Append('\n');
        }

        foreach (var s in styles)
        {
            out_.Append("Style: ").Append(s).Append('\n');
        }

        out_.Append("\n[Events]\nFormat: ").AppendJoin(", ", EventFormat).Append('\n');
        foreach (var e in events)
        {
            out_.Append("Dialogue: ").Append(e).Append('\n');
        }

        output = Utf8.GetBytes(out_.ToString());
        return true;
    }

    // Whether a and b are equal without ASCII case (and only ASCII's: "K",
    // the Kelvin sign, isn't "k").
    private static bool Fold(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (Lower(a[i]) != Lower(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static char Lower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 'a' - 'A') : c;

    // The script information kept, in the order it is written.
    private static readonly (string Name, Func<string, bool> Ok)[] Info =
    [
        ("PlayResX", Resolution),
        ("PlayResY", Resolution),
        ("LayoutResX", Resolution),
        ("LayoutResY", Resolution),
        ("WrapStyle", OneOf("0", "1", "2", "3")),
        ("ScaledBorderAndShadow", OneOf("yes", "no")),
        ("Collisions", OneOf("Normal", "Reverse")),
        ("Kerning", OneOf("yes", "no")),
        ("YCbCr Matrix", OneOf("None", "TV.601", "PC.601", "TV.709", "PC.709", "TV.FCC", "PC.FCC", "TV.240M", "PC.240M")),
    ];

    private static bool Resolution(string s) => Media.Int(s, 1, 16384);

    private static bool Margin(string s) => Media.Int(s, 0, 16384);

    private static Func<string, bool> OneOf(params string[] set) => s => set.Contains(s);

    // ASS's number: an optional "-", 1 to 7 digits, then optionally "." and
    // 1 to 3 digits, from lo to hi.
    private static bool Number(string s, double lo, double hi) =>
        NumberForm().IsMatch(s) &&
        double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) &&
        v >= lo && v <= hi;

    private static Func<string, bool> NumIn(double lo, double hi) => s => Number(s, lo, hi);

    private static Func<string, bool> IntIn(long lo, long hi) => s => Media.Int(s, lo, hi);

    private static readonly Func<string, bool> Coord = NumIn(-100_000, 100_000);
    private static readonly Func<string, bool> Millis = IntIn(0, 3_600_000);
    private static readonly Func<string, bool> ByteValue = IntIn(0, 255);

    private static int Utf8Length(string s) => Encoding.UTF8.GetByteCount(s);

    // Makes text plain: invalid UTF-8, control characters and commas
    // removed.
    private static string Plain(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value == 0xFFFD || Rune.IsControl(r) || r.Value == ',')
            {
                continue;
            }

            b.Append(r.ToString());
        }

        return b.ToString();
    }

    // A style's colour, "&H" and 1 to 8 hexadecimal digits or a decimal
    // integer, written "&H" and 8 uppercase digits.
    private static string? Colour(string s, bool ssa)
    {
        uint v;
        var m = StyleHex().Match(s);
        if (m.Success)
        {
            v = uint.Parse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        else if (!DecimalForm().IsMatch(s) || !uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v))
        {
            return null;
        }

        return "&H" + v.ToString("X8", CultureInfo.InvariantCulture);
    }

    // A field of a written style: its name, its default, and what it is
    // written as (null when its value is bad).
    private sealed record Field(string Name, string Default, Func<string, bool, string?> Read);

    private static Func<string, bool, string?> Value(Func<string, bool> ok) => (v, _) => ok(v) ? v : null;

    private static string? ReadName(string v, bool ssa)
    {
        var p = Plain(v);
        var n = Utf8Length(p);
        return n is >= 1 and <= MaxName ? p : null;
    }

    // -1 or 0, any other integer -1.
    private static string? Flag(string v, bool ssa)
    {
        if (!IntForm().IsMatch(v))
        {
            return null;
        }

        return (v.StartsWith('-') ? v[1..] : v).Trim('0').Length == 0 ? "0" : "-1";
    }

    // How SSA's alignments become ASS's.
    private static readonly Dictionary<string, string> SsaAlignment = new(StringComparer.Ordinal)
    {
        ["1"] = "1", ["2"] = "2", ["3"] = "3", ["5"] = "7", ["6"] = "8", ["7"] = "9", ["9"] = "4", ["10"] = "5", ["11"] = "6",
    };

    // An alignment: 1 to 9, or SSA's, converted.
    private static string? Alignment(string v, bool ssa)
    {
        if (ssa)
        {
            return SsaAlignment.GetValueOrDefault(v);
        }

        return v.Length == 1 && v[0] is >= '1' and <= '9' ? v : null;
    }

    // A written style's fields, in ASS's order.
    private static readonly Field[] Fields =
    [
        new("Name", "", ReadName),
        new("Fontname", "Arial", ReadName),
        new("Fontsize", "20", Value(NumIn(0, 1000))),
        new("PrimaryColour", "&H00FFFFFF", Colour),
        new("SecondaryColour", "&H000000FF", Colour),
        new("OutlineColour", "&H00000000", Colour),
        new("BackColour", "&H00000000", Colour),
        new("Bold", "0", Value(IntIn(-1, 1000))),
        new("Italic", "0", Flag),
        new("Underline", "0", Flag),
        new("StrikeOut", "0", Flag),
        new("ScaleX", "100", Value(NumIn(0, 1000))),
        new("ScaleY", "100", Value(NumIn(0, 1000))),
        new("Spacing", "0", Value(NumIn(-1000, 1000))),
        new("Angle", "0", Value(NumIn(-360, 360))),
        new("BorderStyle", "1", Value(OneOf("1", "3", "4"))),
        new("Outline", "2", Value(NumIn(0, 100))),
        new("Shadow", "2", Value(NumIn(0, 100))),
        new("Alignment", "2", Alignment),
        new("MarginL", "10", Value(Margin)),
        new("MarginR", "10", Value(Margin)),
        new("MarginV", "10", Value(Margin)),
        new("Encoding", "1", Value(IntIn(0, 255))),
    ];

    // The standard formats: of styles, ASS's and SSA's; of events, ASS's
    // and SSA's.
    private static readonly string[] V4PlusStyle =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "OutlineColour", "BackColour",
        "Bold", "Italic", "Underline", "StrikeOut", "ScaleX", "ScaleY", "Spacing", "Angle", "BorderStyle", "Outline", "Shadow",
        "Alignment", "MarginL", "MarginR", "MarginV", "Encoding",
    ];

    private static readonly string[] V4Style =
    [
        "Name", "Fontname", "Fontsize", "PrimaryColour", "SecondaryColour", "TertiaryColour", "BackColour",
        "Bold", "Italic", "BorderStyle", "Outline", "Shadow", "Alignment", "MarginL", "MarginR", "MarginV", "AlphaLevel", "Encoding",
    ];

    private static readonly string[] EventFormat = ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    private static readonly string[] SsaEvent = ["Marked", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];

    // A Format: line's field names.
    private static string[] Format(string v) => v.Split(',').Select(f => f.Trim()).ToArray();

    // A Style: line under its format: the style as written, and its name;
    // null when it is dropped.
    private static (string Written, string Name)? Style(string value, string[] format, bool ssa)
    {
        var fields = value.Split(',');
        if (fields.Length != format.Length)
        {
            return null;
        }

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < format.Length; i++)
        {
            var name = ssa && format[i] == "TertiaryColour" ? "OutlineColour" : format[i];
            raw.TryAdd(name, fields[i].Trim());
        }

        var out_ = new string[Fields.Length];
        for (var i = 0; i < Fields.Length; i++)
        {
            var f = Fields[i];
            var w = raw.TryGetValue(f.Name, out var v) ? f.Read(v, ssa) : null;
            if (w is null)
            {
                if (f.Name == "Name")
                {
                    return null;
                }

                w = f.Default;
            }

            out_[i] = w;
        }

        return (string.Join(',', out_), out_[0]);
    }

    // The style Default, of every default.
    private static string DefaultStyle()
    {
        var out_ = Fields.Select(f => f.Default).ToArray();
        out_[0] = "Default";
        return string.Join(',', out_);
    }

    // A Dialogue: line under its format: the event as written, or null when
    // it is dropped.
    private static string? Event(string value, string[] format, HashSet<string> styles)
    {
        if (format.Length == 0)
        {
            return null;
        }

        var parts = value.Split(',', format.Length);
        if (parts.Length != format.Length)
        {
            return null;
        }

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < format.Length - 1; i++)
        {
            raw.TryAdd(format[i], parts[i].Trim());
        }

        string Get(string name, string def, Func<string, bool> ok) => raw.TryGetValue(name, out var v) && ok(v) ? v : def;

        var start = raw.GetValueOrDefault("Start", "");
        var end = raw.GetValueOrDefault("End", "");
        if (!Time().IsMatch(start) || !Time().IsMatch(end))
        {
            return null;
        }

        var text = EventText(parts[^1], styles);
        if (text.Length == 0 || Utf8Length(text) > MaxEventText)
        {
            return null;
        }

        return string.Join(',',
            Get("Layer", "0", IntIn(0, 1000)), start, end,
            Get("Style", "Default", styles.Contains), "",
            Get("MarginL", "0", Margin), Get("MarginR", "0", Margin), Get("MarginV", "0", Margin),
            "", text);
    }

    // Rebuilds an event's text: text outside braces kept (a drawing's
    // rebuilt), override blocks rebuilt, within the counts of an event.
    private static string EventText(string s, HashSet<string> styles)
    {
        var clean = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value == '\t')
            {
                clean.Append(' ');
            }
            else if (r.Value != 0xFFFD && !WebVtt.DirectionControl(r.Value) && !Rune.IsControl(r))
            {
                clean.Append(r.ToString());
            }
        }

        s = clean.ToString();
        var out_ = new StringBuilder();
        bool drawing = false, full = false;
        int blocks = 0, tags = 0, transforms = 0;
        while (true)
        {
            var open = s.IndexOf('{', StringComparison.Ordinal);
            var piece = open < 0 ? s : s[..open];
            out_.Append(drawing ? Drawing(piece) : piece);
            if (open < 0)
            {
                break;
            }

            var rest = s[(open + 1)..];
            var close = rest.IndexOf('}', StringComparison.Ordinal);
            if (close < 0)
            {
                break; // the rest of the text goes
            }

            var inner = rest[..close];
            s = rest[(close + 1)..];
            if (full)
            {
                continue;
            }

            var t = transforms;
            var (kept, p) = Block(inner, styles, ref t);
            if (kept.Count == 0)
            {
                continue;
            }

            if (blocks == MaxBlocks || tags + kept.Count > MaxTags)
            {
                full = true; // this block and every one after it go
                continue;
            }

            blocks++;
            tags += kept.Count;
            transforms = t;
            out_.Append('{').AppendJoin("", kept).Append('}');
            if (p >= 0)
            {
                drawing = p > 0;
            }
        }

        return out_.ToString();
    }

    // Cuts a block's tags (from its first "\") at each "\", but not inside
    // a \t's parentheses: each tag without its "\".
    private static List<string> Cut(string s)
    {
        var out_ = new List<string>();
        for (var i = 0; i < s.Length;)
        {
            var j = i + 1;
            var transform = string.CompareOrdinal(s, j, "t(", 0, 2) == 0;
            var depth = 0;
            for (; j < s.Length; j++)
            {
                var c = s[j];
                if (c == '\\' && depth == 0)
                {
                    break;
                }

                if (transform && c == '(')
                {
                    depth++;
                }
                else if (transform && c == ')' && depth > 0)
                {
                    depth--;
                }
            }

            out_.Add(s[(i + 1)..j]);
            i = j;
        }

        return out_;
    }

    // Rebuilds what's inside an override block: its kept tags, and the last
    // \p's value (-1 without one). What comes before the first "\" is a
    // comment, dropped.
    private static (List<string> Kept, int P) Block(string inner, HashSet<string> styles, ref int transforms)
    {
        var kept = new List<string>();
        var k = inner.IndexOf('\\', StringComparison.Ordinal);
        if (k < 0)
        {
            return (kept, -1);
        }

        var p = -1;
        foreach (var raw in Cut(inner[k..]))
        {
            if (Tag(raw, styles, false, ref transforms) is not { } t)
            {
                continue;
            }

            if (t.P >= 0)
            {
                p = t.P;
            }

            kept.Add(t.Written);
        }

        return (kept, p);
    }

    // The override tags kept; a tag is the longest of them its text starts
    // with.
    private static readonly string[] TagNames =
    [
        "b", "i", "u", "s", "bord", "xbord", "ybord", "be", "blur", "shad", "xshad", "yshad",
        "fn", "fs", "fscx", "fscy", "fsp", "frx", "fry", "frz", "fr", "fax", "fay", "fe",
        "c", "1c", "2c", "3c", "4c", "alpha", "1a", "2a", "3a", "4a", "an", "a", "k", "K", "kf", "ko", "q", "r",
        "pos", "org", "move", "fad", "fade", "t", "clip", "iclip", "p", "pbo",
    ];

    // The tags a \t never holds.
    private static readonly string[] NotInTransform = ["t", "pos", "org", "move", "fad", "fade", "an", "a", "k", "K", "kf", "ko", "q", "r", "fn", "p", "pbo"];

    // Reads "(a,b,…)": the arguments, white space around them removed; null
    // when not in parentheses.
    private static string[]? Parens(string args)
    {
        if (!args.StartsWith('(') || !args.EndsWith(')') || args.Length < 2)
        {
            return null;
        }

        return Format(args[1..^1]);
    }

    private static bool All(IEnumerable<string> parts, Func<string, bool> ok) => parts.All(ok);

    // Checks one override tag (without its "\"): the tag as written, the
    // value of a \p (-1 for another tag), or null when it is dropped.
    private static (string Written, int P)? Tag(string raw, HashSet<string> styles, bool inTransform, ref int transforms)
    {
        var name = "";
        foreach (var n in TagNames)
        {
            if (raw.StartsWith(n, StringComparison.Ordinal) && n.Length > name.Length)
            {
                name = n;
            }
        }

        if (name.Length == 0 || (inTransform && NotInTransform.Contains(name)))
        {
            return null;
        }

        var args = raw[name.Length..];
        (string, int)? As(bool ok) => ok ? ("\\" + raw, -1) : null;
        (string, int)? Written(string w, bool ok) => ok ? (w, -1) : null;
        switch (name)
        {
            case "b":
                return As(Media.Int(args, 0, 1000));
            case "i" or "u" or "s":
                return As(args is "0" or "1");
            case "bord" or "xbord" or "ybord" or "be" or "blur":
                return As(Number(args, 0, 100));
            case "shad" or "xshad" or "yshad" or "fax" or "fay":
                return As(Number(args, -100, 100));
            case "fn":
            {
                var f = Plain(args);
                return Written("\\fn" + f, Utf8Length(f) <= MaxName);
            }

            case "fs" or "fscx" or "fscy":
                return As(Number(args, 0, 1000));
            case "fsp":
                return As(Number(args, -1000, 1000));
            case "frx" or "fry" or "frz" or "fr":
                return As(Number(args, -3600, 3600));
            case "fe":
                return As(ByteValue(args));
            case "c" or "1c" or "2c" or "3c" or "4c":
            {
                var m = TagColour().Match(args);
                return m.Success
                    ? ("\\" + name + "&H" + uint.Parse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture).ToString("X6", CultureInfo.InvariantCulture) + "&", -1)
                    : null;
            }

            case "alpha" or "1a" or "2a" or "3a" or "4a":
            {
                var m = TagAlpha().Match(args);
                return m.Success
                    ? ("\\" + name + "&H" + uint.Parse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture).ToString("X2", CultureInfo.InvariantCulture) + "&", -1)
                    : null;
            }

            case "an":
                return As(Alignment(args, false) is not null);
            case "a":
            {
                var a = Alignment(args, true);
                return Written("\\an" + a, a is not null);
            }

            case "k" or "K" or "kf" or "ko":
                return As(Media.Int(args, 0, 100_000));
            case "q":
                return As(args is "0" or "1" or "2" or "3");
            case "r":
                return As(args.Length == 0 || styles.Contains(args));
            case "p":
                return Media.Int(args, 0, 16) ? ("\\p" + args, int.Parse(args, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)) : null;
            case "pbo":
                return As(Coord(args));
            case "pos" or "org":
            {
                var a = Parens(args);
                return a is not null && a.Length == 2 && All(a, Coord) ? ("\\" + name + "(" + string.Join(',', a) + ")", -1) : null;
            }

            case "move":
            {
                var a = Parens(args);
                var ok = a is not null && a.Length is 4 or 6 && All(a[..4], Coord) && All(a[4..], Millis);
                return ok ? ("\\move(" + string.Join(',', a!) + ")", -1) : null;
            }

            case "fad":
            {
                var a = Parens(args);
                return a is not null && a.Length == 2 && All(a, Millis) ? ("\\fad(" + string.Join(',', a) + ")", -1) : null;
            }

            case "fade":
            {
                var a = Parens(args);
                return a is not null && a.Length == 7 && All(a[..3], ByteValue) && All(a[3..], Millis) ? ("\\fade(" + string.Join(',', a) + ")", -1) : null;
            }

            case "clip" or "iclip":
            {
                var a = Parens(args);
                switch (a?.Length)
                {
                    case 4: // a rectangle
                        return All(a, Coord) ? ("\\" + name + "(" + string.Join(',', a) + ")", -1) : null;
                    case 2: // a scale, a drawing
                    {
                        var d = Drawing(a[1]);
                        return Media.Int(a[0], 1, 16) && d.Length > 0 ? ("\\" + name + "(" + a[0] + "," + d + ")", -1) : null;
                    }

                    case 1: // a drawing
                    {
                        var d = Drawing(a[0]);
                        return d.Length > 0 ? ("\\" + name + "(" + d + ")", -1) : null;
                    }

                    default:
                        return null;
                }
            }

            case "t":
                return Transform(args, styles, ref transforms);
            default:
                return null;
        }
    }

    // Checks a \t's arguments: optionally two times, optionally an
    // acceleration, then its tags, each checked.
    private static (string Written, int P)? Transform(string args, HashSet<string> styles, ref int transforms)
    {
        if (transforms == MaxTransforms || args.Length < 2 || !args.StartsWith('(') || !args.EndsWith(')'))
        {
            return null;
        }

        var body = args[1..^1];
        var k = body.IndexOf('\\', StringComparison.Ordinal);
        if (k < 0)
        {
            return null;
        }

        // The numbers before the tags, each before a ",".
        var nums = Format(body[..k]);
        if (nums[^1].Length != 0)
        {
            return null;
        }

        nums = nums[..^1];
        var ok = nums.Length switch
        {
            0 => true,
            1 => Number(nums[0], 0, 100),
            2 => All(nums, Millis),
            3 => All(nums[..2], Millis) && Number(nums[2], 0, 100),
            _ => false,
        };
        if (!ok)
        {
            return null;
        }

        var kept = new List<string>();
        foreach (var raw in Cut(body[k..].Trim()))
        {
            if (Tag(raw, styles, true, ref transforms) is { } t)
            {
                kept.Add(t.Written);
            }
        }

        if (kept.Count == 0)
        {
            return null;
        }

        transforms++;
        var pre = string.Join(',', nums);
        if (pre.Length > 0)
        {
            pre += ",";
        }

        return ("\\t(" + pre + string.Concat(kept) + ")", -1);
    }

    // Rebuilds a drawing: its commands and numbers (at most 10,000), single
    // spaces between them, anything else dropped.
    private static string Drawing(string s)
    {
        var out_ = new List<string>();
        var numbers = 0;
        foreach (var f in s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (f.Length == 1 && "mnlbspc".Contains(f[0], StringComparison.Ordinal))
            {
                out_.Add(f);
            }
            else if (Coord(f))
            {
                if (++numbers > MaxNumbers)
                {
                    break;
                }

                out_.Add(f);
            }
        }

        return string.Join(' ', out_);
    }
}
