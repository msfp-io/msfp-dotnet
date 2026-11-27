using System.Collections.Frozen;

namespace Msfp.Protocol;

/// <summary>
/// Language tags as a server sends them (MSFP Media listing 3, MSFP
/// Media base 2.3): media servers often give a file's ISO 639-2 code, /B
/// ("fre", "ger", "chi") or /T ("fra", "deu", "zho"); MSFP sends the ISO
/// 639-1 code ("fr", "de", "zh"), with the rest of the tag kept, in BCP
/// 47's case conventions ("fre-ca" is sent "fr-CA"); und, mul, zxx and
/// mis are not sent.
/// </summary>
public static class Languages
{
    // Each ISO 639-2 code, /B and /T, of a language that has an ISO 639-1
    // code, to that code.
    private static readonly FrozenDictionary<string, string> Iso639 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["aar"] = "aa", ["abk"] = "ab", ["afr"] = "af", ["aka"] = "ak", ["alb"] = "sq", ["amh"] = "am", ["ara"] = "ar",
        ["arg"] = "an", ["arm"] = "hy", ["asm"] = "as", ["ava"] = "av", ["ave"] = "ae", ["aym"] = "ay", ["aze"] = "az",
        ["bak"] = "ba", ["bam"] = "bm", ["baq"] = "eu", ["bel"] = "be", ["ben"] = "bn", ["bih"] = "bh", ["bis"] = "bi",
        ["bod"] = "bo", ["bos"] = "bs", ["bre"] = "br", ["bul"] = "bg", ["bur"] = "my", ["cat"] = "ca", ["ces"] = "cs",
        ["cha"] = "ch", ["che"] = "ce", ["chi"] = "zh", ["chu"] = "cu", ["chv"] = "cv", ["cor"] = "kw", ["cos"] = "co",
        ["cre"] = "cr", ["cym"] = "cy", ["cze"] = "cs", ["dan"] = "da", ["deu"] = "de", ["div"] = "dv", ["dut"] = "nl",
        ["dzo"] = "dz", ["ell"] = "el", ["eng"] = "en", ["epo"] = "eo", ["est"] = "et", ["eus"] = "eu", ["ewe"] = "ee",
        ["fao"] = "fo", ["fas"] = "fa", ["fij"] = "fj", ["fin"] = "fi", ["fra"] = "fr", ["fre"] = "fr", ["fry"] = "fy",
        ["ful"] = "ff", ["geo"] = "ka", ["ger"] = "de", ["gla"] = "gd", ["gle"] = "ga", ["glg"] = "gl", ["glv"] = "gv",
        ["gre"] = "el", ["grn"] = "gn", ["guj"] = "gu", ["hat"] = "ht", ["hau"] = "ha", ["heb"] = "he", ["her"] = "hz",
        ["hin"] = "hi", ["hmo"] = "ho", ["hrv"] = "hr", ["hun"] = "hu", ["hye"] = "hy", ["ibo"] = "ig", ["ice"] = "is",
        ["ido"] = "io", ["iii"] = "ii", ["iku"] = "iu", ["ile"] = "ie", ["ina"] = "ia", ["ind"] = "id", ["ipk"] = "ik",
        ["isl"] = "is", ["ita"] = "it", ["jav"] = "jv", ["jpn"] = "ja", ["kal"] = "kl", ["kan"] = "kn", ["kas"] = "ks",
        ["kat"] = "ka", ["kau"] = "kr", ["kaz"] = "kk", ["khm"] = "km", ["kik"] = "ki", ["kin"] = "rw", ["kir"] = "ky",
        ["kom"] = "kv", ["kon"] = "kg", ["kor"] = "ko", ["kua"] = "kj", ["kur"] = "ku", ["lao"] = "lo", ["lat"] = "la",
        ["lav"] = "lv", ["lim"] = "li", ["lin"] = "ln", ["lit"] = "lt", ["ltz"] = "lb", ["lub"] = "lu", ["lug"] = "lg",
        ["mac"] = "mk", ["mah"] = "mh", ["mal"] = "ml", ["mao"] = "mi", ["mar"] = "mr", ["may"] = "ms", ["mkd"] = "mk",
        ["mlg"] = "mg", ["mlt"] = "mt", ["mon"] = "mn", ["mri"] = "mi", ["msa"] = "ms", ["mya"] = "my", ["nau"] = "na",
        ["nav"] = "nv", ["nbl"] = "nr", ["nde"] = "nd", ["ndo"] = "ng", ["nep"] = "ne", ["nld"] = "nl", ["nno"] = "nn",
        ["nob"] = "nb", ["nor"] = "no", ["nya"] = "ny", ["oci"] = "oc", ["oji"] = "oj", ["ori"] = "or", ["orm"] = "om",
        ["oss"] = "os", ["pan"] = "pa", ["per"] = "fa", ["pli"] = "pi", ["pol"] = "pl", ["por"] = "pt", ["pus"] = "ps",
        ["que"] = "qu", ["roh"] = "rm", ["ron"] = "ro", ["rum"] = "ro", ["run"] = "rn", ["rus"] = "ru", ["sag"] = "sg",
        ["san"] = "sa", ["sin"] = "si", ["slk"] = "sk", ["slo"] = "sk", ["slv"] = "sl", ["sme"] = "se", ["smo"] = "sm",
        ["sna"] = "sn", ["snd"] = "sd", ["som"] = "so", ["sot"] = "st", ["spa"] = "es", ["sqi"] = "sq", ["srd"] = "sc",
        ["srp"] = "sr", ["ssw"] = "ss", ["sun"] = "su", ["swa"] = "sw", ["swe"] = "sv", ["tah"] = "ty", ["tam"] = "ta",
        ["tat"] = "tt", ["tel"] = "te", ["tgk"] = "tg", ["tgl"] = "tl", ["tha"] = "th", ["tib"] = "bo", ["tir"] = "ti",
        ["ton"] = "to", ["tsn"] = "tn", ["tso"] = "ts", ["tuk"] = "tk", ["tur"] = "tr", ["twi"] = "tw", ["uig"] = "ug",
        ["ukr"] = "uk", ["urd"] = "ur", ["uzb"] = "uz", ["ven"] = "ve", ["vie"] = "vi", ["vol"] = "vo", ["wel"] = "cy",
        ["wln"] = "wa", ["wol"] = "wo", ["xho"] = "xh", ["yid"] = "yi", ["yor"] = "yo", ["zha"] = "za", ["zho"] = "zh",
        ["zul"] = "zu",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="s"/> is a valid tag (MSFP Media listing 3): one
    /// already in the form a server sends, which <see cref="TrySentTag"/>
    /// gives back unchanged ("fr", "fr-CA", "yue", "zh-Hant-TW", "es-419");
    /// "fre" (not yet converted), "fr-ca" or "FR" (another case), "e", "und"
    /// and "fr_CA" aren't. It is the one rule wherever a track or a lyrics
    /// set carries a tag: a receiver checks it and never converts the tag; an
    /// invalid one counts as absent in what it keeps. Valid tags compare
    /// the same byte for byte and without case.
    /// </summary>
    public static bool ValidTag(string? s) => s is not null && TrySentTag(s, out var t) && t == s;

    // Whether s has a tag's form: 2 to 12 characters of A–Z a–z 0–9 -.
    private static bool TagForm(string s) =>
        s.Length is >= 2 and <= 12 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>
    /// The tag sent for <paramref name="code"/>, a media server's language
    /// code ("fre" and "fra" give "fr", "fre-ca" "fr-CA"); false for one MSFP
    /// doesn't send: not of a tag's form (2 to 12 characters of A–Z a–z 0–9 -), or
    /// und, mul, zxx or mis. Test cases: languageCodes in
    /// vectors/v0/strict.json.
    /// </summary>
    /// <param name="code">The media server's code.</param>
    /// <param name="tag">The tag sent; empty when false.</param>
    public static bool TrySentTag(string code, out string tag)
    {
        ArgumentNullException.ThrowIfNull(code);
        tag = "";
        if (!TagForm(code))
        {
            return false;
        }

        var subtags = code.Split('-');
        for (var i = 0; i < subtags.Length; i++)
        {
            var s = subtags[i];
            if (s.Length == 0)
            {
                return false;
            }

            s = s.ToLowerInvariant();
            if (i == 0)
            {
                if (s.Length > 8 || !LettersOnly(s))
                {
                    return false;
                }

                if (Iso639.TryGetValue(s, out var shorter))
                {
                    s = shorter;
                }

                if (s is "und" or "mul" or "zxx" or "mis")
                {
                    return false;
                }
            }
            else if ((s.Length == 2 && LettersOnly(s)) || (s.Length == 3 && s.All(char.IsAsciiDigit)))
            {
                s = s.ToUpperInvariant();
            }
            else if (s.Length == 4 && LettersOnly(s))
            {
                s = char.ToUpperInvariant(s[0]) + s[1..];
            }

            subtags[i] = s;
        }

        tag = string.Join('-', subtags);
        return true;
    }

    /// <summary>
    /// A list of a media server's language codes as sent: each as
    /// <see cref="TrySentTag"/> gives it, those it refuses skipped, each once
    /// (compared without case), at most <see cref="Video.MaxTags"/>.
    /// </summary>
    public static IReadOnlyList<string> SentTags(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var out_ = new List<string>();
        foreach (var c in codes)
        {
            if (out_.Count == Video.MaxTags)
            {
                break;
            }

            if (TrySentTag(c, out var t) && !out_.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                out_.Add(t);
            }
        }

        return out_;
    }

    private static bool LettersOnly(string s) => s.All(char.IsAsciiLetterLower);
}
