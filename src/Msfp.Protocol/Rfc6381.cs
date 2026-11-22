using System.Globalization;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// What a viewer's server decides by in a stream's codecs string (video
/// section 3.5): the parts it reads (profile, tier, level, chroma, HEVC's
/// constraint bytes, AAC's mp4a part), the parts it ignores left out (H.264
/// constraint flags, HEVC compatibility flags, VP9 level, AV1 colour),
/// bitDepth from the stream's own field when it has one (the authoritative
/// one), else from codecs, and the Dolby Vision string rebuilt from the base
/// 4CC and the dv object. Absent parts are null.
/// </summary>
/// <param name="Codec">The stream's codec token.</param>
/// <param name="Codecs">Its codecs string, as kept; null when absent (optional for its codec).</param>
public sealed record CodecsFacts(string Codec, string? Codecs)
{
    /// <summary>The profile: H.264's profile_idc, HEVC's general_profile_idc, AV1's and VP9's profile.</summary>
    public int? Profile { get; init; }

    /// <summary>The tier: HEVC's L (Main or unknown) or H (High); AV1's M or H.</summary>
    public string? Tier { get; init; }

    /// <summary>The level: H.264's level_idc, HEVC's general_level_idc, AV1's seq_level_idx.</summary>
    public int? Level { get; init; }

    /// <summary>The bit depth: the stream's bitDepth when it has one, else AV1's or VP9's in codecs.</summary>
    public int? BitDepth { get; init; }

    /// <summary>The chroma format of AV1 and VP9: 4:2:0, 4:2:2, 4:4:4 or 4:0:0.</summary>
    public string? Chroma { get; init; }

    /// <summary>HEVC's constraint bytes as written (B0, 9D.08); null when none.</summary>
    public string? Constraints { get; init; }

    /// <summary>AAC's part after mp4a. (40.2, 40.29, 67).</summary>
    public string? Mp4a { get; init; }

    /// <summary>The Dolby Vision string rebuilt from the base 4CC and dv (dvh1.08.06); null without dv.</summary>
    public string? DolbyVision { get; init; }
}

/// <summary>
/// RFC 6381 codecs strings as MSFP carries them (video section 3.5): which
/// codecs require one, how a viewer's server reads one, and the Dolby
/// Vision sample entries. Decisions go by codecs when it is present;
/// profile, level, tier and chroma are its facts, bit depth the stream's
/// own field's, colour colour's.
/// </summary>
public static partial class Rfc6381
{
    /// <summary>The codecs for which codecs is required: h264, hevc, av1 and vp9 (video), aac and dts (audio).</summary>
    public static readonly IReadOnlyList<string> RequiredFor = ["h264", "hevc", "av1", "vp9", "aac", "dts"];

    // The Dolby Vision sample entry of each base 4CC (video section 3.5).
    private static readonly Dictionary<string, string> DvEntry = new(StringComparer.Ordinal)
    {
        ["hvc1"] = "dvh1", ["hev1"] = "dvhe", ["av01"] = "dav1", ["avc1"] = "dva1", ["avc3"] = "dvav",
    };

    private static readonly Dictionary<string, string> Av1Chroma = new(StringComparer.Ordinal)
    {
        ["110"] = "4:2:0", ["111"] = "4:2:0", ["112"] = "4:2:0", ["113"] = "4:2:0", ["100"] = "4:2:2", ["000"] = "4:4:4",
    };

    [GeneratedRegex(@"^(avc1|avc3)\.([0-9A-Fa-f]{2})[0-9A-Fa-f]{2}([0-9A-Fa-f]{2})\z")]
    private static partial Regex AvcRe();

    [GeneratedRegex(@"^(hvc1|hev1)\.[ABC]?([0-9]{1,2})\.[0-9A-Fa-f]{1,8}\.([LH])([0-9]{1,3})((?:\.[0-9A-Fa-f]{1,2}){0,6})\z")]
    private static partial Regex HevcRe();

    [GeneratedRegex(@"^(av01)\.([0-7])\.([0-9]{2})([MH])\.([0-9]{2})(?:\.([01])\.([01][01][0-3])\.[0-9]{2}\.[0-9]{2}\.[0-9]{2}\.[01])?\z")]
    private static partial Regex Av1Re();

    [GeneratedRegex(@"^(vp09)\.([0-9]{2})\.[0-9]{2}\.([0-9]{2})(?:\.([0-9]{2})(?:\.[0-9]{2}){0,4})?\z")]
    private static partial Regex Vp9Re();

    [GeneratedRegex(@"^mp4a\.(40\.[1-9][0-9]?|66|67|68)\z")]
    private static partial Regex AacRe();

    [GeneratedRegex(@"^(dtsc|dtsh|dtsl|dtse)\z")]
    private static partial Regex DtsRe();

    /// <summary>Whether a stream of <paramref name="codec"/> must carry codecs (<see cref="RequiredFor"/>).</summary>
    public static bool Required(string codec) => RequiredFor.Contains(codec, StringComparer.Ordinal);

    /// <summary>The Dolby Vision sample entry of a base 4CC (hvc1 → dvh1, hev1 → dvhe, av01 → dav1, avc1 → dva1, avc3 → dvav); null for another.</summary>
    public static string? DolbyVisionEntry(string baseFourCC) => DvEntry.GetValueOrDefault(baseFourCC);

    /// <summary>
    /// Reads a stream's or a track's codecs as a viewer's server does (video
    /// section 3.5), given the stream as kept: its codec, its codecs
    /// (<paramref name="codecs"/>, null when absent), its own bitDepth and its
    /// dv. Null when the viewer's server treats the copy as one it can't play
    /// as it is, <paramref name="why"/> saying why: codecs missing where it is
    /// required, a Dolby Vision 4CC instead of the base codec's, or a string
    /// that isn't of its codec's form (an HEVC profile of 4 or more without
    /// its constraint bytes, AV1 profile 1 or VP9 profile 1 or 3 without the
    /// long form among them). A codec with no rule here (eac3, vvc, mpeg4…)
    /// has its codecs passed as it is.
    /// </summary>
    public static CodecsFacts? Read(string codec, string? codecs, int? bitDepth, VideoDv? dv, out string why)
    {
        ArgumentNullException.ThrowIfNull(codec);
        why = "";
        if (!Copies.CodecsForm(codecs))
        {
            if (Required(codec))
            {
                why = "codecs missing or malformed, required for " + codec;
                return null;
            }

            return new CodecsFacts(codec, null);
        }

        var s = codecs!;
        foreach (var entry in DvEntry.Values)
        {
            if (s == entry || s.StartsWith(entry + ".", StringComparison.Ordinal))
            {
                why = "a Dolby Vision 4CC (" + entry + "): codecs carries the base codec's";
                return null;
            }
        }

        static int Num(string v, int radix) => radix == 16
            ? int.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture);

        var f = new CodecsFacts(codec, s);
        var fourCC = "";
        Match m;
        switch (codec)
        {
            case "h264":
                if (!(m = AvcRe().Match(s)).Success)
                {
                    why = "codecs isn't avc1 or avc3 and three bytes";
                    return null;
                }

                fourCC = m.Groups[1].Value;
                f = f with { Profile = Num(m.Groups[2].Value, 16), Level = Num(m.Groups[3].Value, 16) };
                break;
            case "hevc":
                if (!(m = HevcRe().Match(s)).Success)
                {
                    why = "codecs isn't an HEVC string of hvc1 or hev1";
                    return null;
                }

                fourCC = m.Groups[1].Value;
                var constraints = m.Groups[5].Value.TrimStart('.');
                f = f with
                {
                    Profile = Num(m.Groups[2].Value, 10), Tier = m.Groups[3].Value, Level = Num(m.Groups[4].Value, 10),
                    Constraints = constraints.Length > 0 ? constraints : null,
                };
                if (f.Profile >= 4 && f.Constraints is null)
                {
                    why = "an HEVC profile of 4 or more without its constraint bytes";
                    return null;
                }

                break;
            case "av1":
                if (!(m = Av1Re().Match(s)).Success)
                {
                    why = "codecs isn't an AV1 string of av01";
                    return null;
                }

                fourCC = m.Groups[1].Value;
                f = f with
                {
                    Profile = Num(m.Groups[2].Value, 10), Level = Num(m.Groups[3].Value, 10), Tier = m.Groups[4].Value,
                    BitDepth = Num(m.Groups[5].Value, 10),
                };
                if (m.Groups[6].Value == "1")
                {
                    f = f with { Chroma = "4:0:0" };
                }
                else if (m.Groups[7].Success)
                {
                    f = f with { Chroma = Av1Chroma.GetValueOrDefault(m.Groups[7].Value) };
                }
                else if (f.Profile == 0)
                {
                    f = f with { Chroma = "4:2:0" };
                }
                else if (f.Profile == 1)
                {
                    why = "AV1 profile 1 (4:4:4) without the long form";
                    return null;
                }

                break;
            case "vp9":
                if (!(m = Vp9Re().Match(s)).Success)
                {
                    why = "codecs isn't a VP9 string of vp09";
                    return null;
                }

                fourCC = m.Groups[1].Value;
                f = f with { Profile = Num(m.Groups[2].Value, 10), BitDepth = Num(m.Groups[3].Value, 10) };
                switch (m.Groups[4].Success ? m.Groups[4].Value : "")
                {
                    case "00" or "01":
                        f = f with { Chroma = "4:2:0" };
                        break;
                    case "02":
                        f = f with { Chroma = "4:2:2" };
                        break;
                    case "03":
                        f = f with { Chroma = "4:4:4" };
                        break;
                    case "":
                        if (f.Profile is 1 or 3)
                        {
                            why = "VP9 profile 1 or 3 (not 4:2:0) without the long form";
                            return null;
                        }

                        f = f with { Chroma = "4:2:0" };
                        break;
                    default:
                        why = "a VP9 chroma subsampling other than 00 to 03";
                        return null;
                }

                break;
            case "aac":
                if (!(m = AacRe().Match(s)).Success)
                {
                    why = "codecs isn't mp4a.40.<n> or an MPEG-2 AAC mp4a";
                    return null;
                }

                f = f with { Mp4a = m.Groups[1].Value };
                break;
            case "dts":
                if (!DtsRe().IsMatch(s))
                {
                    why = "codecs isn't dtsc, dtsh, dtsl or dtse";
                    return null;
                }

                break;
        }

        if (bitDepth is { } own)
        {
            f = f with { BitDepth = own };
        }

        if (dv is not null && fourCC.Length > 0)
        {
            f = f with
            {
                DolbyVision = DvEntry[fourCC] + "." + dv.Profile.ToString("D2", CultureInfo.InvariantCulture) + "." +
                              dv.Level.ToString("D2", CultureInfo.InvariantCulture),
            };
        }

        return f;
    }
}
