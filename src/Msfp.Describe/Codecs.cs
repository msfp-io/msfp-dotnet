using System.Globalization;
using Msfp.Protocol;

namespace Msfp.Describe;

/// <summary>
/// Codecs strings (MSFP Media video 3.5, implementers' notes "Building
/// codecs"): built from the stream's record where it has one (exact), else
/// from ffprobe's fields under the spec's tolerance: profile, level and bit
/// depth always the stream's own; H.264 constraint flags zero, HEVC
/// compatibility flags the profile's own bit and tier L, VP9's level guessed
/// from size and frame rate, when the record doesn't give them. A string
/// that can't be made whole (an HEVC of profile 4 or more without its
/// constraint bytes, an AAC of unknown object type) is "": the copy is then
/// one a viewer's server doesn't play as it is. On Matroska or MPEG-TS the
/// string is the one the stream would have in ISO BMFF; on Dolby Vision,
/// the base codec's, with the base 4CC of the file's Dolby Vision entry.
/// </summary>
public static class CodecsStrings
{
    // A sample entry ffprobe reports (codec_tag_string) to the 4CC a codecs
    // string starts with: avc3 and hev1 kept, Dolby Vision's entries to
    // their base (the registry's table).
    private static readonly Dictionary<string, string> BaseFourCC = new(StringComparer.Ordinal)
    {
        ["avc1"] = "avc1", ["avc3"] = "avc3", ["hvc1"] = "hvc1", ["hev1"] = "hev1",
        ["dva1"] = "avc1", ["dvav"] = "avc3", ["dvh1"] = "hvc1", ["dvhe"] = "hev1", ["dav1"] = "av01", ["av01"] = "av01",
    };

    // ffprobe's H.264 profile names: profile_idc and the constraint flags the name implies.
    private static readonly Dictionary<string, (int Profile, int Constraints)> AvcProfiles = new(StringComparer.Ordinal)
    {
        ["Baseline"] = (66, 0), ["Constrained Baseline"] = (66, 0x40), ["Main"] = (77, 0), ["Extended"] = (88, 0), ["High"] = (100, 0),
        ["Constrained High"] = (100, 0x0c), ["Progressive High"] = (100, 0x08), ["High 10"] = (110, 0), ["High 10 Intra"] = (110, 0x10),
        ["High 4:2:2"] = (122, 0), ["High 4:2:2 Intra"] = (122, 0x10), ["High 4:4:4"] = (244, 0), ["High 4:4:4 Predictive"] = (244, 0),
        ["High 4:4:4 Intra"] = (244, 0x10), ["CAVLC 4:4:4"] = (44, 0), ["Multiview High"] = (118, 0), ["Stereo High"] = (128, 0),
    };

    private static readonly Dictionary<string, int> HevcProfiles = new(StringComparer.Ordinal) { ["Main"] = 1, ["Main 10"] = 2, ["Main Still Picture"] = 3 };

    private static readonly Dictionary<string, int> Av1Profiles = new(StringComparer.Ordinal) { ["Main"] = 0, ["High"] = 1, ["Professional"] = 2 };

    // ffprobe's AAC profile names to the audio object type.
    private static readonly Dictionary<string, int> AacProfiles = new(StringComparer.Ordinal)
    {
        ["Main"] = 1, ["LC"] = 2, ["SSR"] = 3, ["LTP"] = 4, ["HE-AAC"] = 5, ["HE-AACv2"] = 29, ["LD"] = 23, ["ELD"] = 39, ["xHE-AAC"] = 42, ["USAC"] = 42,
    };

    private static readonly Dictionary<string, string> FixedAudio = new(StringComparer.Ordinal)
    {
        ["ac3"] = "ac-3", ["eac3"] = "ec-3", ["flac"] = "fLaC", ["opus"] = "Opus", ["truehd"] = "mlpa", ["alac"] = "alac",
    };

    // VP9's levels: the most luma samples a picture and a second may hold.
    private static readonly (int Level, long Picture, long PerSecond)[] Vp9Levels =
    [
        (10, 36864, 829440), (11, 73728, 2764800), (20, 122880, 4608000), (21, 245760, 9216000),
        (30, 552960, 20736000), (31, 983040, 36864000), (40, 2228224, 83558400), (41, 2228224, 160432128),
        (50, 8912896, 311951360), (51, 8912896, 588251136), (52, 8912896, 1176502272), (60, 35651584, 1176502272),
        (61, 35651584, 2353004544), (62, 35651584, 4706009088),
    ];

    private static string X2(int n) => n.ToString("X2", CultureInfo.InvariantCulture);

    private static string D2(int n) => n.ToString("D2", CultureInfo.InvariantCulture);

    private static string D(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A video stream's codecs string, from its record where it can be read,
    /// else from ffprobe's fields; bitDepth is the picture's
    /// (<see cref="Shaper.VideoBitDepth"/>). "" for a codec it doesn't build
    /// (h264, hevc, av1, vp9 only), or when it can't be made whole.
    /// </summary>
    public static string Video(Stream s, int bitDepth)
    {
        ArgumentNullException.ThrowIfNull(s);
        var out_ = s.CodecName switch
        {
            "h264" => Avc(s),
            "hevc" => Hevc(s),
            "av1" => Av1(s, bitDepth),
            "vp9" => Vp9(s, bitDepth),
            _ => "",
        };
        return out_.Length == 0 || !Copies.CodecsForm(out_) ? "" : out_;
    }

    private static string Avc(Stream s)
    {
        var fourCC = BaseFourCC.GetValueOrDefault(s.CodecTag) == "avc3" ? "avc3" : "avc1";
        if (s.Record is { } rec && Records.TryParseAvc(rec, out var r) && r.Profile > 0 && r.Level > 0)
        {
            return fourCC + "." + X2(r.Profile) + X2(r.Constraints) + X2(r.Level);
        }

        if (!AvcProfiles.TryGetValue(s.Profile, out var p) || s.Level <= 0 || s.Level > 255)
        {
            return "";
        }

        return fourCC + "." + X2(p.Profile) + X2(p.Constraints) + X2(s.Level);
    }

    private static string Hevc(Stream s)
    {
        var fourCC = BaseFourCC.GetValueOrDefault(s.CodecTag) == "hev1" ? "hev1" : "hvc1";
        if (s.Record is { } rec && Records.TryParseHevc(rec, out var r) && r.Profile > 0 && r.Level > 0)
        {
            return HevcString(fourCC, r);
        }

        var profile = HevcProfiles.GetValueOrDefault(s.Profile);
        if (profile == 0 || s.Level <= 0 || s.Level > 255)
        {
            return ""; // from profile 4 the constraint bytes are required, and only the record has them
        }

        return fourCC + "." + D(profile) + "." + (1 << profile).ToString("X", CultureInfo.InvariantCulture) + ".L" + D(s.Level);
    }

    /// <summary>
    /// An HEVC record's codecs string (ISO/IEC 14496-15 annex E): the profile
    /// space as a letter (none for 0), the profile, the compatibility flags in
    /// reverse bit order in hex, the tier and level, the constraint bytes,
    /// trailing zero bytes left out.
    /// </summary>
    public static string HevcString(string fourCC, HevcRecord r)
    {
        var space = new[] { "", "A", "B", "C" }[r.ProfileSpace & 3];
        var out_ = fourCC + "." + space + D(r.Profile) + "." + Reverse(r.Compatibility).ToString("X", CultureInfo.InvariantCulture) + "." +
            (r.HighTier ? "H" : "L") + D(r.Level);
        var c = r.Constraints ?? [];
        var last = c.Length;
        while (last > 0 && c[last - 1] == 0)
        {
            last--;
        }

        for (var i = 0; i < last; i++)
        {
            out_ += "." + c[i].ToString("X", CultureInfo.InvariantCulture);
        }

        return out_;
    }

    private static uint Reverse(uint v)
    {
        uint r = 0;
        for (var i = 0; i < 32; i++)
        {
            r = (r << 1) | ((v >> i) & 1);
        }

        return r;
    }

    private static string Av1(Stream s, int bitDepth)
    {
        if (s.Record is not { } rec || !Records.TryParseAv1(rec, out var r))
        {
            if (!Av1Profiles.TryGetValue(s.Profile, out var profile) || s.Level < 0 || s.Level > 31 || bitDepth == 0)
            {
                return "";
            }

            var (mono, sx, sy) = Shaper.Chroma(s.PixFmt) switch
            {
                "400" => (true, true, true),
                "420" => (false, true, true),
                "422" => (false, true, false),
                _ => (false, false, false),
            };
            r = new Av1Record(profile, s.Level, false, bitDepth, mono, sx, sy, 0);
        }

        var out_ = "av01." + D(r.Profile) + "." + D2(r.Level) + (r.HighTier ? "H" : "M") + "." + D2(r.BitDepth);
        if (r.Monochrome || !r.SubX || !r.SubY)
        {
            // The long form: monochrome, chroma subsampling and position,
            // colour (1, BT.709, where unknown) and the full-range flag.
            var (p, t, m, full) = ColourDigits(s);
            out_ += "." + (r.Monochrome ? "1" : "0") + "." + (r.SubX ? "1" : "0") + (r.SubY ? "1" : "0") + D(r.ChromaPosition) +
                "." + D2(p) + "." + D2(t) + "." + D2(m) + "." + D(full);
        }

        return out_;
    }

    private static string Vp9(Stream s, int bitDepth)
    {
        Vp9Record r;
        if (s.Record is not { } rec || !Records.TryParseVp9(rec, out r))
        {
            r = new Vp9Record(-1, 0, 0, -1);
        }

        if (r.Profile < 0)
        {
            var name = s.Profile.StartsWith("Profile ", StringComparison.Ordinal) ? s.Profile[8..] : s.Profile;
            if (!int.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n < 0 || n > 3)
            {
                return "";
            }

            r = r with { Profile = n };
        }

        if (r.BitDepth == 0)
        {
            r = r with { BitDepth = bitDepth };
        }

        if (r.BitDepth == 0)
        {
            return "";
        }

        if (r.Level < 10 || r.Level > 62)
        {
            r = r with { Level = Vp9Level(s) };
        }

        var out_ = "vp09." + D2(r.Profile) + "." + D2(r.Level) + "." + D2(r.BitDepth);
        var ch = r.Chroma;
        if (ch < 0 || ch > 3)
        {
            ch = Shaper.Chroma(s.PixFmt) switch { "444" => 3, "422" => 2, "420" => 1, "400" => 1, _ => 0 };
        }

        if (r.Profile == 1 || r.Profile == 3 || ch >= 2)
        {
            var (p, t, m, full) = ColourDigits(s);
            out_ += "." + D2(ch) + "." + D2(p) + "." + D2(t) + "." + D2(m) + "." + D2(full);
        }

        return out_;
    }

    // A VP9 stream's level guessed from its size and frame rate (a consumer
    // ignores VP9's level: MSFP Media video 3.5).
    private static int Vp9Level(Stream s)
    {
        var picture = (long)s.Width * s.Height;
        var rate = 30.0;
        if (Shaper.Fps(s) is { Length: > 0 } fps)
        {
            var parts = fps.Split('/');
            rate = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        foreach (var l in Vp9Levels)
        {
            if (picture <= l.Picture && picture * rate <= l.PerSecond)
            {
                return l.Level;
            }
        }

        return 62;
    }

    // The colour parts of an AV1 or VP9 long form: H.273 primaries,
    // transfer and matrix, 1 (BT.709) where unknown, and the full-range
    // flag, 0 where unknown.
    private static (int P, int T, int M, int Full) ColourDigits(Stream s) =>
        (H273.Primaries.GetValueOrDefault(s.ColorPrimaries, 1), H273.Transfer.GetValueOrDefault(s.ColorTransfer, 1),
            H273.Matrix.GetValueOrDefault(s.ColorSpace, 1), s.ColorRange == "pc" ? 1 : 0);

    /// <summary>
    /// An audio track's codecs string: AAC's object type, from its
    /// AudioSpecificConfig (the stream's Record) and ffprobe's profile (which
    /// tells an implicit HE-AAC the record doesn't), DTS's flavour from its
    /// profile, the registry's fixed codes for AC-3, E-AC-3, TrueHD, FLAC,
    /// Opus and ALAC; "" when not known.
    /// </summary>
    public static string Audio(Stream s)
    {
        ArgumentNullException.ThrowIfNull(s);
        switch (s.CodecName)
        {
            case "aac":
                var ot = s.Record is { } rec && Records.TryParseAudioConfig(rec, out var o) ? o : 0;

                // ffprobe's decoder finds SBR and PS the record may not signal.
                var p = AacProfiles.GetValueOrDefault(s.Profile);
                if (ot == 0 || p == 29 || (p == 5 && ot == 2))
                {
                    ot = p;
                }

                return ot == 0 ? "" : "mp4a.40." + D(ot);
            case "dts":
                var dp = s.Profile;
                if (dp.StartsWith("DTS-HD", StringComparison.Ordinal))
                {
                    return "dtsh";
                }

                return dp switch
                {
                    "DTS Express" => "dtse",
                    "DTS" or "DTS-ES" or "DTS 96/24" => "dtsc",
                    _ => "",
                };
        }

        return FixedAudio.GetValueOrDefault(s.CodecName, "");
    }
}

/// <summary>ffmpeg's colour names as ITU-T H.273 code points (the implementers' notes' table), the other spellings ffmpeg reads included.</summary>
public static class H273
{
    /// <summary>color_primaries to ColourPrimaries.</summary>
    public static readonly IReadOnlyDictionary<string, int> Primaries = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["bt709"] = 1, ["bt470m"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7, ["film"] = 8,
        ["bt2020"] = 9, ["smpte428"] = 10, ["smpte428_1"] = 10, ["smpte431"] = 11, ["smpte432"] = 12, ["jedec-p22"] = 22, ["ebu3213"] = 22,
    };

    /// <summary>color_transfer to TransferCharacteristics.</summary>
    public static readonly IReadOnlyDictionary<string, int> Transfer = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["bt709"] = 1, ["bt470m"] = 4, ["gamma22"] = 4, ["bt470bg"] = 5, ["gamma28"] = 5, ["smpte170m"] = 6,
        ["smpte240m"] = 7, ["linear"] = 8, ["log100"] = 9, ["log"] = 9, ["log316"] = 10, ["log_sqrt"] = 10, ["iec61966-2-4"] = 11,
        ["bt1361e"] = 12, ["iec61966-2-1"] = 13, ["srgb"] = 13, ["bt2020-10"] = 14, ["bt2020-12"] = 15, ["smpte2084"] = 16,
        ["smpte428"] = 17, ["smpte428_1"] = 17, ["arib-std-b67"] = 18,
    };

    /// <summary>color_space to MatrixCoefficients.</summary>
    public static readonly IReadOnlyDictionary<string, int> Matrix = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["gbr"] = 0, ["rgb"] = 0, ["bt709"] = 1, ["fcc"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7,
        ["ycgco"] = 8, ["bt2020nc"] = 9, ["bt2020c"] = 10, ["smpte2085"] = 11, ["chroma-derived-nc"] = 12, ["chroma-derived-c"] = 13,
        ["ictcp"] = 14, ["ipt-c2"] = 15, ["ycgco-re"] = 16, ["ycgco-ro"] = 17,
    };
}
