using System.Text;

namespace Msfp.Examples.ShareFolder;

/// <summary>
/// A file's media type (MSFP video section 3.5), from its first bytes where it
/// can (an ISO BMFF brand, a Matroska DocType, a transport stream's packets),
/// else from its extension; audio/&lt;sub&gt; for a file with no picture, of
/// the pairs the registry lists.
/// </summary>
internal static class Sniff
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.Ordinal)
    {
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mkv"] = "video/matroska", [".webm"] = "video/webm",
        [".mov"] = "video/quicktime", [".ts"] = "video/mp2t", [".m2ts"] = "video/vnd.dlna.mpeg-tts",
    };

    /// <summary>The file's media type: what its first 4,096 bytes say, else its extension's, else video/mp4.</summary>
    public static string MediaTypeOf(string path, bool picture)
    {
        byte[] head = [];
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[4096];
            var n = f.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
            head = b[..n];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        var t = Of(head);
        if (t.Length == 0)
        {
            t = ByExtension.GetValueOrDefault(Path.GetExtension(path).ToLowerInvariant(), "video/mp4");
        }

        return AudioOrVideo(t, picture);
    }

    /// <summary>video/&lt;sub&gt; as audio/&lt;sub&gt; for a file with no picture, for the pairs the registry lists (section 4.1).</summary>
    public static string AudioOrVideo(string t, bool picture) =>
        !picture && t.StartsWith("video/", StringComparison.Ordinal) && t[6..] is "mp4" or "matroska" or "webm" or "ogg" or "3gpp" or "3gpp2" or "mp2t"
            ? "audio/" + t[6..]
            : t;

    // The registry's brand table (section 4.5): the media type an ISO BMFF
    // brand chooses.
    private static readonly Dictionary<string, string> Brands = new(StringComparer.Ordinal)
    {
        ["qt  "] = "video/quicktime", ["mp41"] = "video/mp4", ["mp42"] = "video/mp4", ["avc1"] = "video/mp4", ["av01"] = "video/mp4",
        ["M4V "] = "video/mp4", ["MSNV"] = "video/mp4", ["dby1"] = "video/mp4",
        ["M4A "] = "audio/mp4", ["M4B "] = "audio/mp4", ["M4P "] = "audio/mp4", ["iamf"] = "audio/mp4",
        ["3g2a"] = "video/3gpp2", ["3g2b"] = "video/3gpp2", ["mjp2"] = "video/mj2", ["mj2s"] = "video/mj2",
    };

    private static string Brand(string b)
    {
        if (Brands.TryGetValue(b, out var t))
        {
            return t;
        }

        if (b == "isom" || (b.Length == 4 && b.StartsWith("iso", StringComparison.Ordinal)) ||
            b.StartsWith("dash", StringComparison.Ordinal) || b.StartsWith("cmf", StringComparison.Ordinal) || b is "msdh" or "msix")
        {
            return "video/mp4";
        }

        return b.StartsWith("3g", StringComparison.Ordinal) ? "video/3gpp" : "";
    }

    /// <summary>The media type a file's first bytes say; "" when they say none.</summary>
    public static string Of(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8))
        {
            if (Brand(Encoding.Latin1.GetString(b[8..12])) is { Length: > 0 } major)
            {
                return major;
            }

            var size = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
            for (var i = 16; i + 4 <= Math.Min(size, b.Length); i += 4)
            {
                if (Brand(Encoding.Latin1.GetString(b[i..(i + 4)])) is { Length: > 0 } compatible)
                {
                    return compatible;
                }
            }

            return "video/mp4"; // an unknown brand
        }

        if (b.StartsWith((ReadOnlySpan<byte>)[0x1a, 0x45, 0xdf, 0xa3]))
        {
            // The DocType element (0x4282), its size, its string.
            var i = b.IndexOf((ReadOnlySpan<byte>)[0x42, 0x82]);
            if (i >= 0 && i + 3 < b.Length && (b[i + 2] & 0x80) != 0)
            {
                var n = b[i + 2] & 0x7f;
                if (i + 3 + n <= b.Length && b.Slice(i + 3, n).SequenceEqual("webm"u8))
                {
                    return "video/webm";
                }
            }

            return "video/matroska"; // less precise for an unknown DocType, never wrong
        }

        if (Packets(b, 0, 188))
        {
            return "video/mp2t";
        }

        return Packets(b, 4, 192) ? "video/vnd.dlna.mpeg-tts" : "";
    }

    // Whether b holds transport stream packets of size n, their sync byte at
    // offset from each packet's start: three in a row.
    private static bool Packets(ReadOnlySpan<byte> b, int offset, int n)
    {
        for (var k = 0; k < 3; k++)
        {
            var i = offset + (k * n);
            if (i >= b.Length || b[i] != 0x47)
            {
                return false;
            }
        }

        return true;
    }
}
