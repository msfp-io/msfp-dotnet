using System.Buffers.Binary;
using System.Text;

namespace Msfp.Protocol;

/// <summary>
/// Fonts (MSFP video, section 6.6): the fonts a copy's ASS tracks use,
/// served apart, at most 8 a copy. Both sides check a font's structure
/// before serving or relaying it; a viewer's server may refuse fonts
/// entirely and never fetch them.
/// </summary>
public static class Font
{
    /// <summary>The most a font may be, in bytes: 10 MiB (video section 6.6, media section 2.4).</summary>
    public const int MaxFont = 10 << 20;

    /// <summary>The media type a TrueType font is served as (media section 3.4).</summary>
    public const string Ttf = "font/ttf";

    /// <summary>The media type an OpenType font with CFF is served as (media section 3.4).</summary>
    public const string Otf = "font/otf";

    private const int MaxTables = 64;

    // The tables every font has; it has glyf and loca too, or CFF or CFF2.
    private static readonly string[] Required = ["cmap", "head", "hhea", "hmtx", "maxp", "name", "post"];

    /// <summary>
    /// Checks a font as video section 6.6 says, in its steps: at most
    /// <see cref="MaxFont"/> bytes (1); TrueType (00 01 00 00, or Apple's
    /// "true") or OpenType with CFF ("OTTO"), never a collection, WOFF or
    /// another kind (2); 1 to 64 tables, the directory within the file (3),
    /// every table within the file, each tag of printable ASCII and once
    /// (4); the required tables (5).
    /// </summary>
    /// <param name="font">The font's bytes.</param>
    /// <param name="type">The type the font is served as, <see cref="Ttf"/> or <see cref="Otf"/>; "" when false.</param>
    /// <param name="reason">Why it is refused, "step N: …"; "" when true.</param>
    public static bool TryCheck(ReadOnlySpan<byte> font, out string type, out string reason)
    {
        type = "";
        reason = Check(font, out var t) ?? "";
        if (reason.Length != 0)
        {
            return false;
        }

        type = t;
        return true;
    }

    private static string? Check(ReadOnlySpan<byte> b, out string type)
    {
        type = "";
        if (b.Length > MaxFont)
        {
            return "step 1: over 10 MiB";
        }

        if (b.Length < 4)
        {
            return "step 2: under 4 bytes";
        }

        if (b[..4].SequenceEqual((ReadOnlySpan<byte>)[0, 1, 0, 0]) || b[..4].SequenceEqual("true"u8))
        {
            type = Ttf;
        }
        else if (b[..4].SequenceEqual("OTTO"u8))
        {
            type = Otf;
        }
        else
        {
            return "step 2: neither TrueType nor OpenType";
        }

        if (b.Length < 6)
        {
            return "step 3: no table count";
        }

        int n = BinaryPrimitives.ReadUInt16BigEndian(b[4..]);
        if (n is < 1 or > MaxTables)
        {
            return $"step 3: {n} tables";
        }

        if (12 + (16 * n) > b.Length)
        {
            return "step 3: the table directory past the end";
        }

        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var rec = b[(12 + (16 * i))..];
            foreach (var c in rec[..4])
            {
                if (c is < 0x20 or > 0x7e)
                {
                    return "step 4: a tag byte outside 0x20 to 0x7E";
                }
            }

            var tag = Encoding.ASCII.GetString(rec[..4]);
            if (!tags.Add(tag))
            {
                return $"step 4: the tag \"{tag}\" twice";
            }

            // In 64 bits: an offset and a length can't wrap.
            ulong off = BinaryPrimitives.ReadUInt32BigEndian(rec[8..]), length = BinaryPrimitives.ReadUInt32BigEndian(rec[12..]);
            if (off + length > (ulong)b.Length)
            {
                return $"step 4: the table \"{tag}\" past the end";
            }
        }

        foreach (var t in Required)
        {
            if (!tags.Contains(t))
            {
                return $"step 5: no {t} table";
            }
        }

        if (!(tags.Contains("glyf") && tags.Contains("loca")) && !tags.Contains("CFF ") && !tags.Contains("CFF2"))
        {
            return "step 5: no outlines: neither glyf and loca, nor CFF or CFF2";
        }

        return null;
    }
}
