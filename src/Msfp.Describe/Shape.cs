using System.Globalization;
using System.Text.RegularExpressions;
using Msfp.Protocol;

namespace Msfp.Describe;

/// <summary>A subtitle file beside the video (video section 3.3, external): what the host knows of it. <see cref="Shaper.Shape"/> numbers it above the file's streams, in the order given.</summary>
/// <param name="Language">A media server's code; sent as <see cref="Languages.TrySentTag"/> gives it.</param>
/// <param name="Format">The registry's token (subrip, webvtt, ass…).</param>
public sealed record External(string Language, string Format)
{
    /// <summary>A DASH role (commentary, easyreader…), or "".</summary>
    public string Role { get; init; } = "";

    /// <summary>Marked default.</summary>
    public bool Default { get; init; }

    /// <summary>Marked forced.</summary>
    public bool Forced { get; init; }

    /// <summary>Marked for the hearing impaired.</summary>
    public bool HearingImpaired { get; init; }
}

/// <summary>A probe shaped into the copy it describes (video sections 3.2 to 3.6).</summary>
public static partial class Shaper
{
    /// <summary>The greatest term of an fps fraction (video section 3.2).</summary>
    public const int MaxFpsTerm = 1_000_000;

    /// <summary>The greatest term of a sample aspect.</summary>
    public const int MaxSampleAspect = 65_535;

    /// <summary>The most reference frames an H.264 stream may state.</summary>
    public const int MaxRefFrames = 16;

    /// <summary>
    /// The copy a probe describes, every value checked against its field's
    /// form and bounds, nothing else from the tags (no title, no file name,
    /// no encoder): the sniffed media type under the audio-or-video rule;
    /// size and durationMs; video (the first picture stream: codec token,
    /// codecs, bitDepth, H.273 colour with fullRange, the frame read's
    /// transfer preferred, dv, hdr10plus, fps, sampleAspect, rotation,
    /// refFrames, interlaced); threeD; audio and subtitle tracks, a&lt;n&gt; and
    /// s&lt;n&gt; by stream index, with their language, codec or format token,
    /// flags and DASH role from the dispositions, then from the track's title
    /// where a flag is missing, objects, sample rate, bit depth; external
    /// subtitles numbered above the file's streams; chapters with clean names.
    /// The copy's id and etag are left empty: the host's to set.
    /// </summary>
    public static Copy Shape(Probe pr, IReadOnlyList<External>? external)
    {
        ArgumentNullException.ThrowIfNull(pr);
        long duration = Ffprobe.Seconds(pr.Format.Duration, out var ms) && ms <= Copies.MaxDurationMs ? ms : 0;
        var picture = pr.Picture();
        var mediaType = MediaTypes.AudioOrVideo(MediaTypes.CanonicalType(pr.MediaType), picture is not null);
        VideoStream? video = null;
        string? threeD = null;
        if (picture is not null)
        {
            video = VideoOf(picture, pr.Frames);
            threeD = ThreeD(picture);
        }

        var audio = new List<CopyAudio>();
        var subs = new List<CopySubtitle>();
        var streams = pr.Format.Streams;
        foreach (var s in pr.Streams)
        {
            streams = Math.Max(streams, s.Index + 1);
            switch (s.CodecType)
            {
                case "audio" when audio.Count < Copies.MaxAudioTracks:
                    if (AudioOf(s) is { } a)
                    {
                        audio.Add(a);
                    }

                    break;
                case "subtitle" when subs.Count < Copies.MaxSubtitleTracks:
                    subs.Add(SubtitleOf(s));
                    break;
            }
        }

        var i = 0;
        foreach (var e in external ?? [])
        {
            if (subs.Count == Copies.MaxSubtitleTracks)
            {
                break;
            }

            subs.Add(new CopySubtitle("s" + (streams + i).ToString(CultureInfo.InvariantCulture), Names.IsToken(e.Format) ? e.Format : "")
            {
                Language = Tag(e.Language),
                Role = Names.IsToken(e.Role) ? e.Role : null,
                Default = e.Default,
                Forced = e.Forced,
                HearingImpaired = e.HearingImpaired,
                External = true,
            });
            i++;
        }

        return new Copy("", duration, mediaType, pr.Size, "", audio)
        {
            Video = video,
            ThreeD = threeD,
            SubtitleTracks = subs,
            Chapters = ChaptersOf(pr.Chapters, duration),
        };
    }

    private static string? Tag(string code) => Languages.TrySentTag(code ?? "", out var t) ? t : null;

    // A codec or format name as the registry's token: the producer-side
    // aliases mapped (section 4.4), "" for one that isn't a token.
    private static string CodecToken(string name)
    {
        name = name switch
        {
            "dca" => "dts",
            "srt" or "text" => "subrip",
            _ => name,
        };
        return name is "none" or "unknown" || !Names.IsToken(name) ? "" : name;
    }

    /// <summary>A picture's bit depth: ffprobe's bits_per_raw_sample, else what its pixel format says; 0 when neither does.</summary>
    public static int VideoBitDepth(Stream s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (int.TryParse(s.BitsPerRawSample, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 16)
        {
            return n;
        }

        foreach (var d in new[] { "16", "14", "12", "10", "9" })
        {
            if (s.PixFmt.Contains("p" + d, StringComparison.Ordinal))
            {
                return int.Parse(d, CultureInfo.InvariantCulture);
            }
        }

        return s.PixFmt.Length > 0 ? 8 : 0;
    }

    private static VideoStream? VideoOf(Stream s, IReadOnlyList<Frame>? frames)
    {
        var codec = CodecToken(s.CodecName);
        if (codec.Length == 0 || s.Width > Copies.MaxVideoSide || s.Height > Copies.MaxVideoSide)
        {
            return null;
        }

        var depth = VideoBitDepth(s);
        var sar = Reduced(s.SampleAspectRatio, ':', MaxSampleAspect);
        int? refFrames = null;
        if (codec == "h264" && s.Record is { } rec && Records.TryParseAvc(rec, out var avc) && avc.RefFrames is >= 1 and <= MaxRefFrames)
        {
            refFrames = avc.RefFrames;
        }

        // The frame read: the transfer the decoder applied (the alternative
        // transfer characteristics SEI's), HDR10+ in the frames' side data.
        var hdr10plus = false;
        foreach (var f in frames ?? [])
        {
            if (H273.Transfer.ContainsKey(f.ColorTransfer) && f.ColorTransfer != s.ColorTransfer)
            {
                s = s with { ColorTransfer = f.ColorTransfer };
            }

            hdr10plus = hdr10plus || f.SideData.Any(d => IsHdr10Plus(d.Type));
        }

        VideoDv? dv = null;
        int? rotation = null;
        foreach (var d in s.SideData)
        {
            if (d.Type.StartsWith("DOVI configuration", StringComparison.Ordinal))
            {
                if (d.DvProfile is >= 0 and <= 127 && d.DvLevel is >= 0 and <= 63 && d.Compatibility is >= 0 and <= 15)
                {
                    dv = new VideoDv(d.DvProfile, d.DvLevel, d.Compatibility) { El = d.ElPresent == 1 };
                }
            }
            else if (d.Type == "Display Matrix")
            {
                rotation = Rotation(d);
            }
            else if (IsHdr10Plus(d.Type))
            {
                hdr10plus = true;
            }
        }

        var v = new VideoStream(codec, s.Width, s.Height)
        {
            BitDepth = depth == 0 ? null : depth,
            Fps = Absent(Fps(s)),
            SampleAspect = sar.Length > 0 && sar != "1:1" ? sar : null,
            RefFrames = refFrames,
            Interlaced = s.FieldOrder.Length > 0 && s.FieldOrder is not ("progressive" or "unknown"),
            Colour = ColourOf(s),
            Dv = dv,
            Rotation = rotation,
            Hdr = hdr10plus ? ["hdr10plus"] : [],
        };
        var codecs = CodecsStrings.Video(s, depth);
        if (codecs.Length > 0 && Rfc6381.Read(codec, codecs, depth == 0 ? null : depth, dv, out _) is null)
        {
            codecs = "";
        }

        return v with { Codecs = Absent(codecs) };
    }

    private static string? Absent(string s) => s.Length == 0 ? null : s;

    private static bool IsHdr10Plus(string t) => t.Contains("HDR10+", StringComparison.Ordinal) || t.Contains("SMPTE2094-40", StringComparison.Ordinal);

    /// <summary>A display matrix's turn, clockwise (ffprobe gives it counterclockwise); null for none, a mirror, or an angle that isn't a multiple of 90.</summary>
    public static int? Rotation(SideData d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (Matrix(d.DisplayMatrix) is { } m && (m[0] * m[4]) - (m[1] * m[3]) < 0)
        {
            return null; // mirrored
        }

        if (d.Rotation != Math.Truncate(d.Rotation) || (int)d.Rotation % 90 != 0)
        {
            return null;
        }

        var r = (360 - ((int)d.Rotation % 360)) % 360;
        return r == 0 ? null : r;
    }

    // ffprobe's display matrix (three lines of three numbers after an
    // offset); null when it isn't of that form.
    private static double[]? Matrix(string s)
    {
        var out_ = new List<double>();
        foreach (var line in s.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            foreach (var f in line[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    return null;
                }

                out_.Add(n);
            }
        }

        return out_.Count == 9 ? out_.ToArray() : null;
    }

    private static readonly Dictionary<string, string> StereoTypes = new(StringComparer.Ordinal) { ["side by side"] = "sbs", ["top and bottom"] = "tab" };

    private static readonly Dictionary<string, string> StereoModes = new(StringComparer.Ordinal)
    {
        ["left_right"] = "sbs", ["right_left"] = "sbs-rl", ["top_bottom"] = "tab", ["bottom_top"] = "tab-rl",
    };

    // A picture's 3D layout from its stereo 3D side data, else from
    // Matroska's stereo_mode tag; null for 2D or a layout MSFP doesn't name.
    private static string? ThreeD(Stream s)
    {
        foreach (var d in s.SideData)
        {
            if (d.Type != "Stereo 3D")
            {
                continue;
            }

            var t = StereoTypes.GetValueOrDefault(d.StereoType, "");
            if (t.Length > 0 && d.Inverted == 1)
            {
                t += "-rl";
            }

            return Absent(t);
        }

        return StereoModes.GetValueOrDefault(s.Tags.Get("stereo_mode"));
    }

    /// <summary>
    /// A stream's frame rate, reduced, each term in bounds: the declared rate
    /// (r_frame_rate) when the average agrees with it within 0.1%, else the
    /// average (a variable rate), else whichever is valid; "" for none.
    /// </summary>
    public static string Fps(Stream s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var nominal = Reduced(s.RFrameRate, '/', MaxFpsTerm);
        var avg = Reduced(s.AvgFrameRate, '/', MaxFpsTerm);
        static double Value(string r)
        {
            var p = r.Split('/');
            return double.Parse(p[0], CultureInfo.InvariantCulture) / double.Parse(p[1], CultureInfo.InvariantCulture);
        }

        if (nominal.Length > 0 && avg.Length > 0 && Math.Abs(Value(nominal) - Value(avg)) <= Value(nominal) / 1000)
        {
            return nominal;
        }

        return avg.Length > 0 ? avg : nominal;
    }

    // "<a><sep><b>" in lowest terms, each term 1 to top; "" for none ("0/0",
    // "0:1", "N/A") or out of bounds.
    private static string Reduced(string s, char sep, int top)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        if (i < 0 || !int.TryParse(s[..i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var a) ||
            !int.TryParse(s[(i + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var b) || a <= 0 || b <= 0)
        {
            return "";
        }

        int g = a, m = b;
        while (m != 0)
        {
            (g, m) = (m, g % m);
        }

        a /= g;
        b /= g;
        return a > top || b > top ? "" : a.ToString(CultureInfo.InvariantCulture) + sep + b.ToString(CultureInfo.InvariantCulture);
    }

    // A picture's colour as H.273 code points; null when ffprobe says nothing of it.
    private static VideoColour? ColourOf(Stream s)
    {
        static int? Pick(IReadOnlyDictionary<string, int> m, string name) => m.TryGetValue(name, out var n) ? n : null;
        bool? full = s.ColorRange switch { "pc" => true, "tv" => false, _ => null };
        var c = new VideoColour(Pick(H273.Primaries, s.ColorPrimaries), Pick(H273.Transfer, s.ColorTransfer), Pick(H273.Matrix, s.ColorSpace), full);
        return c == new VideoColour(null, null, null) ? null : c;
    }

    /// <summary>A pixel format's chroma subsampling: "420", "422", "444" or "400" (monochrome).</summary>
    public static string Chroma(string pixFmt)
    {
        ArgumentNullException.ThrowIfNull(pixFmt);
        if (pixFmt.StartsWith("gray", StringComparison.Ordinal))
        {
            return "400";
        }

        if (pixFmt.Contains("444", StringComparison.Ordinal) || pixFmt.StartsWith("gbr", StringComparison.Ordinal))
        {
            return "444";
        }

        return pixFmt.Contains("422", StringComparison.Ordinal) ? "422" : "420";
    }

    private static CopyAudio? AudioOf(Stream s)
    {
        var codec = CodecToken(s.CodecName);
        if (codec.Length == 0)
        {
            return null;
        }

        var d = s.Disposition;
        string? role;
        var forced = d.Forced == 1;
        if (d.Comment == 1)
        {
            role = "commentary";
        }
        else if (d.Descriptions == 1 || d.VisualImpaired == 1)
        {
            role = "description";
        }
        else if (d.Dub == 1)
        {
            role = "dub";
        }
        else
        {
            var t = TitleFlags(s.Tags.Get("title"));
            role = Absent(t.AudioRole);
            forced = forced || t.Forced;
        }

        int? depth = null;
        if (int.TryParse(s.BitsPerRawSample, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n is >= 8 and <= 32)
        {
            depth = n;
        }
        else if (codec.StartsWith("pcm_", StringComparison.Ordinal) && s.BitsPerSample is >= 8 and <= 32)
        {
            depth = s.BitsPerSample;
        }

        var p = s.Profile;
        string? objects = null;
        if (codec is "eac3" or "truehd" && (p.Contains("Atmos", StringComparison.Ordinal) || p.Contains("JOC", StringComparison.Ordinal)))
        {
            objects = "dolby-atmos";
        }
        else if (codec == "dts" && p.Contains("DTS:X", StringComparison.Ordinal))
        {
            objects = "dts-x";
        }

        var codecs = CodecsStrings.Audio(s);
        if (codecs.Length > 0 && (!Copies.CodecsForm(codecs) || Rfc6381.Read(codec, codecs, null, null, out _) is null))
        {
            codecs = "";
        }

        return new CopyAudio("a" + s.Index.ToString(CultureInfo.InvariantCulture), codec)
        {
            Language = Tag(s.Tags.Get("language")),
            Channels = s.Channels is >= 1 and <= 64 ? s.Channels : null,
            SampleRate = int.TryParse(s.SampleRate, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rate) && rate is >= 8_000 and <= 768_000 ? rate : null,
            BitDepth = depth,
            Default = d.Default == 1,
            Forced = forced,
            Original = d.Original == 1,
            Role = role,
            Objects = objects,
            Codecs = Absent(codecs),
        };
    }

    private static CopySubtitle SubtitleOf(Stream s)
    {
        var d = s.Disposition;
        string? role = d.Comment == 1 ? "commentary" : d.Descriptions == 1 ? "description" : null;

        // The title, where a flag is missing.
        var f = TitleFlags(s.Tags.Get("title"));
        role ??= Absent(f.SubtitleRole);
        return new CopySubtitle("s" + s.Index.ToString(CultureInfo.InvariantCulture), CodecToken(s.CodecName))
        {
            Language = Tag(s.Tags.Get("language")),
            Default = d.Default == 1,
            Forced = d.Forced == 1 || f.Forced,
            HearingImpaired = d.HearingImpaired == 1 || d.Captions == 1 || f.HearingImpaired,
            Role = role,
        };
    }

    /// <summary>What a track's title says, read only where a flag is missing (the implementers' notes, "Track titles to roles and flags").</summary>
    internal readonly record struct FromTitle(string AudioRole, string SubtitleRole, bool Forced, bool HearingImpaired);

    [GeneratedRegex(@"\bcommentary\b|\bcommentaire\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleCommentary();

    [GeneratedRegex(@"\baudio[ -]description\b|\bdescriptive\b|\bdescribed video\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleDescription();

    [GeneratedRegex(@"\bAD\b")]
    private static partial Regex TitleAd();

    [GeneratedRegex(@"\bSDH\b|\bhearing[ -]impaired\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleSdh();

    [GeneratedRegex(@"\bCC\b")]
    private static partial Regex TitleCc();

    [GeneratedRegex(@"\bforced\b|(^|[^a-z])forc(é|ée|és|ées)([^a-z]|$)|\bforeign parts only\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleForced();

    [GeneratedRegex(@"\beasy[ -]?reader\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleEasyReader();

    internal static FromTitle TitleFlags(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return new FromTitle("", "", false, false);
        }

        string audio = "", subtitle = "";
        if (TitleCommentary().IsMatch(title))
        {
            (audio, subtitle) = ("commentary", "commentary");
        }
        else if (TitleDescription().IsMatch(title) || TitleAd().IsMatch(title))
        {
            audio = "description";
        }

        if (TitleEasyReader().IsMatch(title) && subtitle.Length == 0)
        {
            subtitle = "easyreader";
        }

        return new FromTitle(audio, subtitle, TitleForced().IsMatch(title), TitleSdh().IsMatch(title) || TitleCc().IsMatch(title));
    }

    // A copy's chapters: in order, each after the one before and within the
    // copy, the first MaxChapters; a name only when it is plain text that
    // doesn't look like a file name, an address or a site.
    private static List<CopyChapter> ChaptersOf(IReadOnlyList<Chapter> list, long duration)
    {
        var out_ = new List<CopyChapter>();
        foreach (var ch in list)
        {
            if (out_.Count == Copies.MaxChapters)
            {
                break;
            }

            if (!Ffprobe.Seconds(ch.StartTime, out var start) || (duration > 0 && start > duration) || (out_.Count > 0 && start <= out_[^1].StartMs))
            {
                continue;
            }

            var name = Music.CleanText(ch.Tags.Get("title"), Copies.MaxChapterName);
            out_.Add(new CopyChapter(start, name.Length > 0 && !LooksLikeLocation(name) ? name : null));
        }

        return out_;
    }

    [GeneratedRegex(@"[a-z][a-z0-9+.-]*://|\bwww\.|@[a-z0-9-]+\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationScheme();

    [GeneratedRegex(@"\b[a-z0-9-]+\.(com|net|org|info|io|tv|me|cc|co|to|ws|biz|xyz|ru|fr|de|uk|nl|se|es|it|pl|eu|in|cn|jp|kr)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationSite();

    [GeneratedRegex(@"\.(mkv|mp4|m4v|avi|ts|m2ts|mov|wmv|webm|mpg|mpeg|vob|iso|srt|ass|ssa|sub|idx|sup|vtt|nfo|txt|jpg|png|mp3|flac|aac|ac3|dts)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationFile();

    /// <summary>Whether a chapter name looks like a file name, a path, an address or a site rather than a chapter's name.</summary>
    public static bool LooksLikeLocation(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Contains('/', StringComparison.Ordinal) || s.Contains('\\', StringComparison.Ordinal) ||
            LocationScheme().IsMatch(s) || LocationSite().IsMatch(s) || LocationFile().IsMatch(s);
    }
}
