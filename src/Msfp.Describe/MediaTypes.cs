using System.Text;

namespace Msfp.Describe;

/// <summary>
/// Media types (MSFP video section 3.5, the registry's section 4): read
/// from a file's first bytes where they tell it, else mapped from a
/// container's name. Only the types sniffed here are probed: a file whose
/// first bytes say none (a playlist, a script, a concat list, an SDP file
/// renamed .mkv) is never handed to ffprobe.
/// </summary>
public static class MediaTypes
{
    /// <summary>How many of a file's first bytes <see cref="Sniff"/> needs.</summary>
    public const int SniffSize = 8192;

    // The registry's brand table (section 4.5).
    private static readonly Dictionary<string, string> BrandTypes = new(StringComparer.Ordinal)
    {
        ["qt  "] = "video/quicktime", ["mp41"] = "video/mp4", ["mp42"] = "video/mp4", ["avc1"] = "video/mp4",
        ["av01"] = "video/mp4", ["M4V "] = "video/mp4", ["MSNV"] = "video/mp4", ["dby1"] = "video/mp4",
        ["M4A "] = "audio/mp4", ["M4B "] = "audio/mp4", ["M4P "] = "audio/mp4", ["iamf"] = "audio/mp4",
        ["3g2a"] = "video/3gpp2", ["3g2b"] = "video/3gpp2", ["mjp2"] = "video/mj2", ["mj2s"] = "video/mj2",
        ["f4v "] = "video/mp4", ["isml"] = "video/mp4", ["XAVC"] = "video/mp4",
    };

    // The subtypes whose audio/ and video/ forms name the same container
    // (the registry's section 4.1).
    private static readonly HashSet<string> PairedSubtypes = new(StringComparer.Ordinal) { "mp4", "matroska", "webm", "ogg", "3gpp", "3gpp2", "mp2t" };

    // The registry's other spellings of its media types (sections 4.3 and 4.4).
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["video/x-msvideo"] = "video/avi", ["video/vnd.avi"] = "video/avi", ["video/msvideo"] = "video/avi",
        ["audio/wav"] = "audio/wave", ["audio/x-wav"] = "audio/wave", ["audio/vnd.wave"] = "audio/wave",
        ["audio/x-aiff"] = "audio/aiff", ["video/x-matroska"] = "video/matroska", ["audio/x-matroska"] = "audio/matroska",
        ["video/mp2ts"] = "video/mp2t", ["video/m2ts"] = "video/vnd.dlna.mpeg-tts", ["audio/x-flac"] = "audio/flac",
        ["audio/mp3"] = "audio/mpeg", ["audio/x-aac"] = "audio/aac",
    };

    // A container's name, as ffprobe's format_name or a media server
    // (Jellyfin's Container) reports it, to the registry's media type.
    private static readonly Dictionary<string, string> Containers = new(StringComparer.Ordinal)
    {
        ["mov,mp4,m4a,3gp,3g2,mj2"] = "video/mp4", ["mp4"] = "video/mp4", ["m4v"] = "video/mp4",
        ["m4a"] = "audio/mp4", ["m4b"] = "audio/mp4", ["mov"] = "video/quicktime", ["3gp"] = "video/3gpp", ["3g2"] = "video/3gpp2",
        ["matroska,webm"] = "video/matroska", ["matroska"] = "video/matroska", ["mkv"] = "video/matroska", ["mka"] = "audio/matroska",
        ["mk3d"] = "video/matroska", ["webm"] = "video/webm", ["mpegts"] = "video/mp2t", ["ts"] = "video/mp2t", ["mts"] = "video/vnd.dlna.mpeg-tts",
        ["m2ts"] = "video/vnd.dlna.mpeg-tts", ["avi"] = "video/avi", ["wav"] = "audio/wave", ["aiff"] = "audio/aiff", ["ogg"] = "video/ogg",
        ["ogv"] = "video/ogg", ["oga"] = "audio/ogg", ["opus"] = "audio/ogg", ["flac"] = "audio/flac", ["mp3"] = "audio/mpeg",
        ["aac"] = "audio/aac", ["mpeg"] = "video/mpeg", ["mpg"] = "video/mpeg", ["vob"] = "video/mpeg", ["flv"] = "video/x-flv",
        ["mj2"] = "video/mj2", ["ac3"] = "audio/ac3", ["eac3"] = "audio/eac3", ["dts"] = "audio/vnd.dts", ["truehd"] = "audio/vnd.dolby.mlp",
    };

    // ffmpeg's demuxer for each sniffed type: ffprobe is run with -f, never left to guess.
    private static readonly Dictionary<string, string> Demuxers = new(StringComparer.Ordinal)
    {
        ["video/mp4"] = "mov", ["audio/mp4"] = "mov", ["video/quicktime"] = "mov", ["video/3gpp"] = "mov",
        ["video/3gpp2"] = "mov", ["video/mj2"] = "mov", ["video/matroska"] = "matroska", ["video/webm"] = "matroska",
        ["video/mp2t"] = "mpegts", ["video/vnd.dlna.mpeg-tts"] = "mpegts", ["video/avi"] = "avi", ["audio/wave"] = "wav",
        ["audio/aiff"] = "aiff", ["video/ogg"] = "ogg", ["audio/flac"] = "flac", ["video/mpeg"] = "mpeg", ["video/x-flv"] = "flv",
        ["audio/mpeg"] = "mp3", ["audio/aac"] = "aac",
    };

    /// <summary>
    /// The media type a file's first bytes say, the registry's spelling,
    /// before the audio-or-video rule (<see cref="AudioOrVideo"/>): an ISO
    /// BMFF file by its brands (the major brand, else the compatible ones in
    /// order, else video/mp4), Matroska or WebM by its EBML DocType, an MPEG
    /// transport stream by its packets (188 bytes video/mp2t, 192
    /// video/vnd.dlna.mpeg-tts), AVI, WAV (RIFF, RF64, BW64), AIFF, Ogg,
    /// FLAC, MPEG-PS, FLV, MP3 and ADTS AAC by their headers; "" when they say
    /// none of those.
    /// </summary>
    public static string Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8))
        {
            return IsoType(b);
        }

        if (b.StartsWith((ReadOnlySpan<byte>)[0x1a, 0x45, 0xdf, 0xa3]))
        {
            return EbmlType(b);
        }

        if (Packets(b, 0, 188))
        {
            return "video/mp2t";
        }

        if (Packets(b, 4, 192))
        {
            return "video/vnd.dlna.mpeg-tts";
        }

        if (Riff(b, "AVI "u8))
        {
            return "video/avi";
        }

        if (Riff(b, "WAVE"u8))
        {
            return "audio/wave";
        }

        if (b.Length >= 12 && b[..4].SequenceEqual("FORM"u8) && (b[8..12].SequenceEqual("AIFF"u8) || b[8..12].SequenceEqual("AIFC"u8)))
        {
            return "audio/aiff";
        }

        if (b.StartsWith("OggS\0"u8))
        {
            return "video/ogg";
        }

        if (b.StartsWith("fLaC"u8))
        {
            return "audio/flac";
        }

        if (b.StartsWith((ReadOnlySpan<byte>)[0, 0, 1, 0xba]))
        {
            return "video/mpeg";
        }

        if (b.StartsWith("FLV\x01"u8))
        {
            return "video/x-flv";
        }

        if (b.StartsWith("ID3"u8) && b.Length >= 10)
        {
            return Id3Type(b);
        }

        return ElementaryAudio(b);
    }

    private static bool Riff(ReadOnlySpan<byte> b, ReadOnlySpan<byte> form) =>
        b.Length >= 12 && (b[..4].SequenceEqual("RIFF"u8) || b[..4].SequenceEqual("RF64"u8) || b[..4].SequenceEqual("BW64"u8)) &&
        b[8..12].SequenceEqual(form);

    private static string IsoType(ReadOnlySpan<byte> b)
    {
        if (BrandType(Encoding.Latin1.GetString(b[8..12])) is { Length: > 0 } t)
        {
            return t;
        }

        var size = (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        for (var i = 16; i + 4 <= Math.Min(size, b.Length); i += 4)
        {
            if (BrandType(Encoding.Latin1.GetString(b[i..(i + 4)])) is { Length: > 0 } c)
            {
                return c;
            }
        }

        return "video/mp4"; // an unknown brand
    }

    private static string BrandType(string b)
    {
        if (BrandTypes.TryGetValue(b, out var t))
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

    // An EBML file's media type by its DocType: video/webm for "webm",
    // video/matroska for any other (less precise for an unknown DocType,
    // never wrong).
    private static string EbmlType(ReadOnlySpan<byte> b)
    {
        var head = b[..Math.Min(b.Length, 64)];
        var i = head.IndexOf((ReadOnlySpan<byte>)[0x42, 0x82]);
        if (i >= 0 && i + 3 < head.Length && (head[i + 2] & 0x80) != 0)
        {
            var n = head[i + 2] & 0x7f;
            if (i + 3 + n <= head.Length && head.Slice(i + 3, n).SequenceEqual("webm"u8))
            {
                return "video/webm";
            }
        }

        return "video/matroska";
    }

    // Whether b holds transport stream packets of size n, their sync byte at
    // offset from each packet's start: as many in a row as b holds, at
    // least three.
    private static bool Packets(ReadOnlySpan<byte> b, int offset, int n)
    {
        var count = 0;
        for (var i = offset; i < b.Length; i += n)
        {
            if (b[i] != 0x47)
            {
                return false;
            }

            count++;
        }

        return count >= 3;
    }

    // The media type of a file that starts with an ID3v2 tag: what follows
    // it when b holds it, else audio/mpeg.
    private static string Id3Type(ReadOnlySpan<byte> b)
    {
        var size = 10 + (((b[6] & 0x7f) << 21) | ((b[7] & 0x7f) << 14) | ((b[8] & 0x7f) << 7) | (b[9] & 0x7f));
        if ((b[5] & 0x10) != 0)
        {
            size += 10; // a footer
        }

        if (size < b.Length && ElementaryAudio(b[size..]) is { Length: > 0 } t)
        {
            return t;
        }

        return "audio/mpeg";
    }

    private static readonly int[] Mp3Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mp3Hz = [44100, 48000, 32000];

    // An MPEG audio file's type by its first frame header: ADTS audio/aac,
    // MPEG layers I to III audio/mpeg; two frame headers in a row are
    // required for MPEG audio, whose sync is short.
    private static string ElementaryAudio(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xff || (b[1] & 0xe0) != 0xe0)
        {
            return "";
        }

        if ((b[1] & 0xf6) == 0xf0 && b.Length >= 7)
        {
            var n = ((b[3] & 3) << 11) | (b[4] << 3) | (b[5] >> 5); // the frame's length
            return n >= 7 && n + 2 <= b.Length && b[n] == 0xff && (b[n + 1] & 0xf6) == 0xf0 ? "audio/aac" : "";
        }

        int version = (b[1] >> 3) & 3, layer = (b[1] >> 1) & 3;
        int rate = (b[2] >> 2) & 3, index = b[2] >> 4;
        if (version == 1 || layer == 0 || rate == 3 || index == 0 || index == 15)
        {
            return "";
        }

        // Frame length, for layer III of MPEG-1 (the common case); other
        // layers and versions are taken on one header.
        if (version == 3 && layer == 1)
        {
            var n = (144000 * Mp3Kbps[index] / Mp3Hz[rate]) + ((b[2] >> 1) & 1);
            if (n + 2 > b.Length || b[n] != 0xff || (b[n + 1] & 0xe0) != 0xe0)
            {
                return "";
            }
        }

        return "audio/mpeg";
    }

    /// <summary>
    /// The audio-or-video rule (video section 3.5) on a media type of the
    /// pairs the registry lists: audio/&lt;sub&gt; for a file with no picture,
    /// video/&lt;sub&gt; for one with a picture. Other types are kept as they
    /// are (video/quicktime stays video/quicktime; audio/mpeg isn't video/mpeg).
    /// </summary>
    public static string AudioOrVideo(string t, bool picture)
    {
        ArgumentNullException.ThrowIfNull(t);
        var slash = t.IndexOf('/', StringComparison.Ordinal);
        var top = slash < 0 ? t : t[..slash];
        var sub = slash < 0 ? "" : t[(slash + 1)..];
        if (!PairedSubtypes.Contains(sub) || (top != "audio" && top != "video"))
        {
            return t;
        }

        return (picture ? "video/" : "audio/") + sub;
    }

    /// <summary>A media type as the registry spells it: lowercase, without parameters, an alias as its registered type.</summary>
    public static string CanonicalType(string t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var semi = t.IndexOf(';', StringComparison.Ordinal);
        if (semi >= 0)
        {
            t = t[..semi];
        }

        t = t.Trim().ToLowerInvariant();
        return Aliases.TryGetValue(t, out var c) ? c : t;
    }

    /// <summary>
    /// The media type of a container named as ffprobe's format_name
    /// ("matroska,webm", "mov,mp4,m4a,3gp,3g2,mj2", "mpegts") or a media
    /// server's container name ("mkv", "mp4", "m2ts"; several, comma
    /// separated, read in order), before the audio-or-video rule; "" for one
    /// the registry doesn't name. Sniff is more precise where the file is at hand.
    /// </summary>
    public static string ContainerType(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim().ToLowerInvariant();
        if (Containers.TryGetValue(name, out var t))
        {
            return t;
        }

        foreach (var n in name.Split(','))
        {
            if (Containers.TryGetValue(n.Trim(), out var u))
            {
                return u;
            }
        }

        return "";
    }

    /// <summary>The ffmpeg demuxer for a type <see cref="Sniff"/> gives; "" for another.</summary>
    public static string Demuxer(string mediaType) => Demuxers.GetValueOrDefault(mediaType ?? "", "");
}
