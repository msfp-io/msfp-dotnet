using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msfp.Protocol;

namespace Msfp.Examples.ShareFolder;

/// <summary>What a file says of itself: what a copy carries of it (MSFP video section 3.2).</summary>
/// <param name="MediaType">Its media type, from its first bytes (Sniff.cs).</param>
/// <param name="Etag">Its etag, from its content; "" when it can't be read.</param>
/// <param name="DurationMs">Its length, in milliseconds; 0 when unknown.</param>
/// <param name="Video">Its picture; null when unknown, or none.</param>
/// <param name="Audio">Its audio tracks, a&lt;n&gt; by stream index.</param>
/// <param name="Streams">How many streams the file has, numbered from 0.</param>
internal sealed record Probed(string MediaType, string Etag, long DurationMs, VideoStream? Video, IReadOnlyList<CopyAudio> Audio, int Streams);

/// <summary>
/// Describes video files as MSFP video section 3.2 lists a copy: its media
/// type from its first bytes (Sniff.cs), its etag from its content, and, with
/// ffprobe when it is installed, its exact length, its picture and its audio
/// tracks, in the vocabularies MSFP takes (video sections 3.5 and 3.6): the
/// registry's codec tokens (ffprobe's codec_name), RFC 6381 codecs strings,
/// H.273 colour code points. Without ffprobe, or for a file it can't read, a
/// copy says only what every file says (its size, its media type, its etag)
/// and its length is 0. What a file gave is kept by path, size and time, so a
/// file is read once until it changes.
/// </summary>
internal sealed class Probes
{
    private readonly Dictionary<string, (long Size, DateTime Time, Probed Probed)> _done = new(StringComparer.Ordinal);

    /// <summary>What the file at path says of itself.</summary>
    public Probed Describe(string path, FileInfo info)
    {
        lock (_done)
        {
            if (_done.TryGetValue(path, out var d) && d.Size == info.Length && d.Time == info.LastWriteTimeUtc)
            {
                return d.Probed;
            }
        }

        var (duration, video, audio, streams, ok) = Ffprobe(path);
        var picture = ok && streams.Any(s => s.CodecType == "video" && s.Disposition?.AttachedPic != 1);
        var p = new Probed(Sniff.MediaTypeOf(path, picture || !ok), ContentTag(path), duration, video, audio, streams.Count);
        lock (_done)
        {
            _done[path] = (info.Length, info.LastWriteTimeUtc, p);
        }

        return p;
    }

    // The file's etag: from its content, so that it changes whenever its
    // bytes change, and only then (MSFP video section 3.2): the first 16
    // bytes of its SHA-256, base64url. "" when it can't be read.
    internal static string ContentTag(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return Base64UrlText.Encode(SHA256.HashData(f).AsSpan(0, 16));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static (long DurationMs, VideoStream? Video, List<CopyAudio> Audio, List<FfprobeStream> Streams, bool Ok) Ffprobe(string path)
    {
        var nothing = (0L, (VideoStream?)null, new List<CopyAudio>(), new List<FfprobeStream>(), false);
        FfprobeOut? o;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffprobe")
            {
                ArgumentList = { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return nothing;
            }

            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill();
                return nothing;
            }

            o = JsonSerializer.Deserialize<FfprobeOut>(read.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            return nothing; // no ffprobe, or a file it can't read
        }

        if (o is null)
        {
            return nothing;
        }

        var duration = double.TryParse(o.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
            ? (long)(s * 1000) // exact, in milliseconds
            : 0;
        VideoStream? video = null;
        var audio = new List<CopyAudio>();
        var streams = o.Streams ?? [];
        foreach (var st in streams)
        {
            switch (st.CodecType)
            {
                case "video" when video is null && st.Width > 0 && st.Height > 0 && st.Disposition?.AttachedPic != 1:
                    video = VideoStreamOf(st);
                    break;
                case "audio":
                    audio.Add(AudioTrack(st));
                    break;
            }

            // Subtitles inside the file are left out: this example serves
            // only the .vtt files beside it.
        }

        return (duration, video, audio, streams, true);
    }

    // A copy's picture from ffprobe's stream.
    internal static VideoStream VideoStreamOf(FfprobeStream s)
    {
        var depth = BitDepth(s);
        var sar = Reduced(s.SampleAspectRatio, ':');
        int? rotation = null;
        VideoDv? dv = null;
        IReadOnlyList<string> hdr = [];
        foreach (var d in s.SideData ?? [])
        {
            if (d.Type?.StartsWith("DOVI configuration", StringComparison.Ordinal) == true)
            {
                dv = new VideoDv(d.DvProfile, d.DvLevel, d.Compatibility) { El = d.ElPresent == 1 };
            }
            else if (d.Type == "Display Matrix")
            {
                // ffprobe gives the angle counterclockwise; MSFP clockwise.
                var r = ((360 - ((int)d.Rotation % 360)) % 360);
                if ((int)d.Rotation == d.Rotation && r % 90 == 0)
                {
                    rotation = r;
                }
            }
            else if (d.Type?.Contains("HDR10+", StringComparison.Ordinal) == true || d.Type?.Contains("SMPTE2094-40", StringComparison.Ordinal) == true)
            {
                hdr = ["hdr10plus"];
            }
        }

        var v = new VideoStream(s.CodecName ?? "", s.Width, s.Height)
        {
            BitDepth = depth == 0 ? null : depth,
            Fps = Present(Reduced(s.AvgFrameRate, '/')) ?? Present(Reduced(s.RFrameRate, '/')),
            SampleAspect = sar.Length > 0 && sar != "1:1" ? sar : null,
            RefFrames = s.CodecName == "h264" && s.Refs is >= 1 and <= 16 ? s.Refs : null,
            Interlaced = s.FieldOrder is { Length: > 0 } and not ("progressive" or "unknown"),
            Colour = Colour(s),
            Rotation = rotation,
            Dv = dv,
            Hdr = hdr,
        };
        return v with { Codecs = Present(VideoCodecs(s, depth)) };
    }

    // A copy's audio track from ffprobe's stream: its ID the stream's index
    // in the file (MSFP video section 3.2).
    internal static CopyAudio AudioTrack(FfprobeStream s)
    {
        var d = s.Disposition;
        var p = s.Profile ?? "";
        var codec = s.CodecName ?? "";
        string? objects = null;
        if (codec is "eac3" or "truehd" && (p.Contains("Atmos", StringComparison.Ordinal) || p.Contains("JOC", StringComparison.Ordinal)))
        {
            objects = "dolby-atmos";
        }
        else if (codec == "dts" && p.Contains("DTS:X", StringComparison.Ordinal))
        {
            objects = "dts-x";
        }

        return new CopyAudio("a" + s.Index.ToString(CultureInfo.InvariantCulture), codec)
        {
            Language = Languages.TrySentTag(s.Tags?.Language ?? "", out var tag) ? tag : null,
            Channels = s.Channels > 0 ? s.Channels : null,
            SampleRate = int.TryParse(s.SampleRate, NumberStyles.None, CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : null,
            BitDepth = int.TryParse(s.BitsPerRawSample, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b is >= 8 and <= 32 ? b : null,
            Default = d?.Default == 1,
            Forced = d?.Forced == 1,
            Original = d?.Original == 1,
            Role = d?.Comment == 1 ? "commentary" : d?.Descriptions == 1 || d?.VisualImpaired == 1 ? "description" : d?.Dub == 1 ? "dub" : null,
            Objects = objects,
            Codecs = Present(AudioCodecs(codec, p)),
        };
    }

    // "<a><sep><b>" in lowest terms, "" for none ("0/0", "0:1").
    internal static string Reduced(string? s, char sep)
    {
        var parts = (s ?? "").Split(sep);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b) || a <= 0 || b <= 0)
        {
            return "";
        }

        var g = a;
        for (var m = b; m != 0; (g, m) = (m, g % m))
        {
        }

        return (a / g).ToString(CultureInfo.InvariantCulture) + sep + (b / g).ToString(CultureInfo.InvariantCulture);
    }

    // A picture's bit depth: ffprobe's bits_per_raw_sample, else what its
    // pixel format says.
    private static int BitDepth(FfprobeStream s)
    {
        if (int.TryParse(s.BitsPerRawSample, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 16)
        {
            return n;
        }

        foreach (var d in new[] { "16", "14", "12", "10", "9" })
        {
            if (s.PixFmt?.Contains("p" + d, StringComparison.Ordinal) == true)
            {
                return int.Parse(d, CultureInfo.InvariantCulture);
            }
        }

        return string.IsNullOrEmpty(s.PixFmt) ? 0 : 8;
    }

    // ffmpeg's colour names as ITU-T H.273 code points (the implementers'
    // notes' table).
    private static readonly Dictionary<string, int> H273Primaries = new(StringComparer.Ordinal)
    {
        ["bt709"] = 1, ["bt470m"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7, ["film"] = 8, ["bt2020"] = 9,
        ["smpte428"] = 10, ["smpte431"] = 11, ["smpte432"] = 12, ["jedec-p22"] = 22, ["ebu3213"] = 22,
    };

    private static readonly Dictionary<string, int> H273Transfer = new(StringComparer.Ordinal)
    {
        ["bt709"] = 1, ["gamma22"] = 4, ["gamma28"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7, ["linear"] = 8, ["log100"] = 9,
        ["log316"] = 10, ["iec61966-2-4"] = 11, ["bt1361e"] = 12, ["iec61966-2-1"] = 13, ["bt2020-10"] = 14, ["bt2020-12"] = 15,
        ["smpte2084"] = 16, ["smpte428"] = 17, ["arib-std-b67"] = 18,
    };

    private static readonly Dictionary<string, int> H273Matrix = new(StringComparer.Ordinal)
    {
        ["gbr"] = 0, ["bt709"] = 1, ["fcc"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7, ["ycgco"] = 8,
        ["bt2020nc"] = 9, ["bt2020c"] = 10, ["smpte2085"] = 11, ["chroma-derived-nc"] = 12, ["chroma-derived-c"] = 13, ["ictcp"] = 14,
    };

    // A picture's colour as H.273 code points; null when ffprobe says
    // nothing of it.
    private static VideoColour? Colour(FfprobeStream s)
    {
        static int? Pick(Dictionary<string, int> m, string? name) => name is not null && m.TryGetValue(name, out var n) ? n : null;
        var c = new VideoColour(Pick(H273Primaries, s.ColorPrimaries), Pick(H273Transfer, s.ColorTransfer), Pick(H273Matrix, s.ColorSpace),
            s.ColorRange switch { "pc" => true, "tv" => false, _ => null });
        return c == new VideoColour(null, null, null) ? null : c;
    }

    // A pixel format's chroma subsampling: "420", "422", "444" or "400"
    // (monochrome).
    private static string Chroma(string? pixFmt)
    {
        var p = pixFmt ?? "";
        if (p.StartsWith("gray", StringComparison.Ordinal))
        {
            return "400";
        }

        if (p.Contains("444", StringComparison.Ordinal) || p.StartsWith("gbr", StringComparison.Ordinal))
        {
            return "444";
        }

        return p.Contains("422", StringComparison.Ordinal) ? "422" : "420";
    }

    private static string Hex2(int n) => n.ToString("X2", CultureInfo.InvariantCulture);

    private static string D2(int n) => n.ToString("D2", CultureInfo.InvariantCulture);

    // A picture's RFC 6381 codecs string (MSFP video section 3.5), from what
    // ffprobe says; "" when it can't be made whole (an HEVC of a profile from
    // 4, whose constraint bytes ffprobe doesn't give): the copy is then one a
    // viewer's server doesn't play as it is. On Dolby Vision, the base
    // codec's string.
    private static string VideoCodecs(FfprobeStream s, int depth)
    {
        var level = s.Level;
        switch (s.CodecName)
        {
            case "h264":
                var avc = s.Profile switch
                {
                    "Baseline" or "Constrained Baseline" => 66, "Main" => 77, "Extended" => 88, "High" => 100, "High 10" => 110,
                    "High 4:2:2" => 122, "High 4:4:4 Predictive" => 244, _ => 0,
                };
                if (avc == 0 || level is <= 0 or > 255)
                {
                    return "";
                }

                return (s.CodecTag == "avc3" ? "avc3" : "avc1") + "." + Hex2(avc) + Hex2(s.Profile == "Constrained Baseline" ? 0x40 : 0) + Hex2(level);
            case "hevc":
                var hevc = s.Profile switch { "Main" => 1, "Main 10" => 2, "Main Still Picture" => 3, "Rext" => 4, _ => 0 };
                if (hevc is 0 or >= 4 || level is <= 0 or > 255)
                {
                    return "";
                }

                // Tier unknown: L; compatibility flags: the profile's own bit.
                return (s.CodecTag == "hev1" ? "hev1" : "hvc1") + "." + hevc.ToString(CultureInfo.InvariantCulture) + "." +
                    (1 << hevc).ToString("X", CultureInfo.InvariantCulture) + ".L" + level.ToString(CultureInfo.InvariantCulture);
            case "av1":
                var av1 = s.Profile switch { "High" => 1, "Professional" => 2, _ => 0 };
                if (level is < 0 or > 31 || depth == 0)
                {
                    return "";
                }

                var o = "av01." + av1.ToString(CultureInfo.InvariantCulture) + "." + D2(level) + "M." + D2(depth);
                if (Chroma(s.PixFmt) is var ch && ch != "420")
                {
                    o += "." + ch switch { "444" => "0.000", "422" => "0.100", _ => "1.110" } + ".01.01.01.0";
                }

                return o;
            case "vp9":
                var vp9 = s.Profile switch { "Profile 1" => 1, "Profile 2" => 2, "Profile 3" => 3, _ => 0 };
                if (depth == 0)
                {
                    return "";
                }

                var vl = level is < 10 or > 99 ? 10 : level; // a guess under 10: a consumer ignores VP9's level
                var vo = "vp09." + D2(vp9) + "." + D2(vl) + "." + D2(depth);
                if (vp9 is 1 or 3)
                {
                    vo += Chroma(s.PixFmt) switch { "444" => ".03", "422" => ".02", _ => ".01" };
                }

                return vo;
        }

        return "";
    }

    // A track's RFC 6381 codecs string, from its codec and ffprobe's
    // profile: AAC's object type, DTS's flavour, the registry's fixed codes;
    // "" when not known.
    internal static string AudioCodecs(string codec, string profile)
    {
        switch (codec)
        {
            case "aac":
                var ot = profile switch
                {
                    "Main" => 1, "LC" => 2, "SSR" => 3, "LTP" => 4, "HE-AAC" => 5, "HE-AACv2" => 29, "LD" => 23, "ELD" => 39, _ => 0,
                };
                return ot == 0 ? "" : "mp4a.40." + ot.ToString(CultureInfo.InvariantCulture);
            case "dts":
                return profile.StartsWith("DTS-HD", StringComparison.Ordinal) ? "dtsh"
                    : profile == "DTS Express" ? "dtse"
                    : profile.StartsWith("DTS", StringComparison.Ordinal) ? "dtsc" : "";
        }

        return codec switch
        {
            "ac3" => "ac-3", "eac3" => "ec-3", "flac" => "fLaC", "opus" => "Opus", "truehd" => "mlpa", "alac" => "alac", _ => "",
        };
    }

    private static string? Present(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // What ffprobe's JSON says that a copy uses.
    private sealed record FfprobeOut(
        [property: JsonPropertyName("format")] FfprobeFormat? Format,
        [property: JsonPropertyName("streams")] List<FfprobeStream>? Streams);

    private sealed record FfprobeFormat([property: JsonPropertyName("duration")] string? Duration);
}

/// <summary>What ffprobe's JSON says of a stream that a copy uses.</summary>
internal sealed record FfprobeStream
{
    [JsonPropertyName("index")] public int Index { get; init; }

    [JsonPropertyName("codec_type")] public string? CodecType { get; init; }

    [JsonPropertyName("codec_name")] public string? CodecName { get; init; }

    [JsonPropertyName("codec_tag_string")] public string? CodecTag { get; init; }

    [JsonPropertyName("profile")] public string? Profile { get; init; }

    [JsonPropertyName("level")] public int Level { get; init; }

    [JsonPropertyName("width")] public int Width { get; init; }

    [JsonPropertyName("height")] public int Height { get; init; }

    [JsonPropertyName("pix_fmt")] public string? PixFmt { get; init; }

    [JsonPropertyName("bits_per_raw_sample")] public string? BitsPerRawSample { get; init; }

    [JsonPropertyName("color_range")] public string? ColorRange { get; init; }

    [JsonPropertyName("color_space")] public string? ColorSpace { get; init; }

    [JsonPropertyName("color_transfer")] public string? ColorTransfer { get; init; }

    [JsonPropertyName("color_primaries")] public string? ColorPrimaries { get; init; }

    [JsonPropertyName("field_order")] public string? FieldOrder { get; init; }

    [JsonPropertyName("refs")] public int Refs { get; init; }

    [JsonPropertyName("sample_aspect_ratio")] public string? SampleAspectRatio { get; init; }

    [JsonPropertyName("avg_frame_rate")] public string? AvgFrameRate { get; init; }

    [JsonPropertyName("r_frame_rate")] public string? RFrameRate { get; init; }

    [JsonPropertyName("channels")] public int Channels { get; init; }

    [JsonPropertyName("sample_rate")] public string? SampleRate { get; init; }

    [JsonPropertyName("disposition")] public FfprobeDisposition? Disposition { get; init; }

    [JsonPropertyName("tags")] public FfprobeTags? Tags { get; init; }

    [JsonPropertyName("side_data_list")] public List<FfprobeSide>? SideData { get; init; }
}

/// <summary>A stream's flags, as ffprobe gives them (0 or 1).</summary>
internal sealed record FfprobeDisposition
{
    [JsonPropertyName("default")] public int Default { get; init; }

    [JsonPropertyName("forced")] public int Forced { get; init; }

    [JsonPropertyName("comment")] public int Comment { get; init; }

    [JsonPropertyName("dub")] public int Dub { get; init; }

    [JsonPropertyName("original")] public int Original { get; init; }

    [JsonPropertyName("visual_impaired")] public int VisualImpaired { get; init; }

    [JsonPropertyName("hearing_impaired")] public int HearingImpaired { get; init; }

    [JsonPropertyName("attached_pic")] public int AttachedPic { get; init; }

    [JsonPropertyName("descriptions")] public int Descriptions { get; init; }
}

/// <summary>A stream's tags.</summary>
internal sealed record FfprobeTags([property: JsonPropertyName("language")] string? Language);

/// <summary>A stream's side data: its display matrix, its Dolby Vision configuration record.</summary>
internal sealed record FfprobeSide
{
    [JsonPropertyName("side_data_type")] public string? Type { get; init; }

    [JsonPropertyName("rotation")] public double Rotation { get; init; }

    [JsonPropertyName("dv_profile")] public int DvProfile { get; init; }

    [JsonPropertyName("dv_level")] public int DvLevel { get; init; }

    [JsonPropertyName("el_present_flag")] public int ElPresent { get; init; }

    [JsonPropertyName("dv_bl_signal_compatibility_id")] public int Compatibility { get; init; }
}

/// <summary>
/// The engine's <see cref="ISheetEncoder"/>, by ffmpeg, when it is installed:
/// .NET has no JPEG codec, and the engine re-encodes every preview sheet it
/// serves and relays, and every JPEG image of a peer's local title, so that
/// only pixels pass. ffmpeg decodes the JPEG and encodes its pixels again,
/// nothing else kept. Without ffmpeg, the example has no SheetEncoder: it
/// offers no previews, relays none, and keeps only peers' PNG images.
/// </summary>
internal sealed class FfmpegSheets : ISheetEncoder
{
    /// <summary>The encoder when ffmpeg runs here; null otherwise.</summary>
    public static FfmpegSheets? IfInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg") { ArgumentList = { "-version" }, RedirectStandardOutput = true, UseShellExecute = false });
            if (p is null)
            {
                return null;
            }

            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? new FfmpegSheets() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async ValueTask<byte[]> ReencodeAsync(ReadOnlyMemory<byte> jpeg, int maxWidth, int maxHeight, CancellationToken ct)
    {
        // The engine checked the size from the JPEG's header; ffmpeg keeps it,
        // and no more than its first frame.
        using var p = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            ArgumentList = { "-v", "quiet", "-f", "jpeg_pipe", "-i", "pipe:0", "-frames:v", "1", "-map_metadata", "-1", "-q:v", "3", "-f", "mjpeg", "pipe:1" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new MsfpException(MsfpError.NotAnImage);
        using var output = new MemoryStream();
        var read = p.StandardOutput.BaseStream.CopyToAsync(output, ct);
        try
        {
            await p.StandardInput.BaseStream.WriteAsync(jpeg, ct);
        }
        catch (IOException)
        {
            // ffmpeg stopped reading: what it wrote, if anything, says.
        }

        p.StandardInput.Close();
        await read;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || output.Length == 0)
        {
            throw new MsfpException(MsfpError.NotAnImage);
        }

        return output.ToArray();
    }
}
