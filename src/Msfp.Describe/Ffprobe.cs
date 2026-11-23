using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msfp.Describe;

/// <summary>A stream's, a chapter's or the format's tags. ffprobe prints Matroska's in upper case and MP4's in lower case: <see cref="Get"/> reads both.</summary>
public sealed class Tags : Dictionary<string, string>
{
    /// <summary>Empty tags.</summary>
    public Tags()
        : base(StringComparer.Ordinal)
    {
    }

    /// <summary>The tag named key, in any case; "" when absent.</summary>
    public string Get(string key)
    {
        if (TryGetValue(key, out var v))
        {
            return v;
        }

        foreach (var (k, value) in this)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return "";
    }
}

/// <summary>ffprobe's format section.</summary>
public sealed class Format
{
    /// <summary>format_name.</summary>
    public string Name { get; set; } = "";

    /// <summary>nb_streams.</summary>
    public int Streams { get; set; }

    /// <summary>duration, in seconds as ffprobe prints it.</summary>
    public string Duration { get; set; } = "";

    /// <summary>size.</summary>
    public string Size { get; set; } = "";

    /// <summary>tags.</summary>
    public Tags Tags { get; set; } = new();
}

/// <summary>A stream's flags, by ffprobe's names (1 set, 0 not).</summary>
public sealed record Disposition
{
    /// <summary>default.</summary>
    public int Default { get; init; }

    /// <summary>dub.</summary>
    public int Dub { get; init; }

    /// <summary>original.</summary>
    public int Original { get; init; }

    /// <summary>comment.</summary>
    public int Comment { get; init; }

    /// <summary>forced.</summary>
    public int Forced { get; init; }

    /// <summary>hearing_impaired.</summary>
    public int HearingImpaired { get; init; }

    /// <summary>visual_impaired.</summary>
    public int VisualImpaired { get; init; }

    /// <summary>attached_pic.</summary>
    public int AttachedPic { get; init; }

    /// <summary>captions.</summary>
    public int Captions { get; init; }

    /// <summary>descriptions.</summary>
    public int Descriptions { get; init; }

    /// <summary>still_image.</summary>
    public int StillImage { get; init; }
}

/// <summary>A stream's or a frame's side data: a display matrix, a Dolby Vision configuration record, a stereo 3D layout, HDR10+.</summary>
public sealed record SideData
{
    /// <summary>side_data_type.</summary>
    public string Type { get; init; } = "";

    /// <summary>rotation (counterclockwise, as ffprobe gives it).</summary>
    public double Rotation { get; init; }

    /// <summary>displaymatrix.</summary>
    public string DisplayMatrix { get; init; } = "";

    /// <summary>dv_profile.</summary>
    public int DvProfile { get; init; }

    /// <summary>dv_level.</summary>
    public int DvLevel { get; init; }

    /// <summary>el_present_flag.</summary>
    public int ElPresent { get; init; }

    /// <summary>dv_bl_signal_compatibility_id.</summary>
    public int Compatibility { get; init; }

    /// <summary>type (stereo 3D).</summary>
    public string StereoType { get; init; } = "";

    /// <summary>inverted (stereo 3D).</summary>
    public int Inverted { get; init; }
}

/// <summary>ffprobe's stream section: the fields a copy uses.</summary>
public sealed record Stream
{
    /// <summary>index: the stream's index in the file.</summary>
    public int Index { get; init; }

    /// <summary>codec_type.</summary>
    public string CodecType { get; init; } = "";

    /// <summary>codec_name.</summary>
    public string CodecName { get; init; } = "";

    /// <summary>codec_tag_string.</summary>
    public string CodecTag { get; init; } = "";

    /// <summary>profile.</summary>
    public string Profile { get; init; } = "";

    /// <summary>level.</summary>
    public int Level { get; init; }

    /// <summary>width.</summary>
    public int Width { get; init; }

    /// <summary>height.</summary>
    public int Height { get; init; }

    /// <summary>pix_fmt.</summary>
    public string PixFmt { get; init; } = "";

    /// <summary>bits_per_raw_sample.</summary>
    public string BitsPerRawSample { get; init; } = "";

    /// <summary>bits_per_sample.</summary>
    public int BitsPerSample { get; init; }

    /// <summary>color_range.</summary>
    public string ColorRange { get; init; } = "";

    /// <summary>color_space.</summary>
    public string ColorSpace { get; init; } = "";

    /// <summary>color_transfer.</summary>
    public string ColorTransfer { get; init; } = "";

    /// <summary>color_primaries.</summary>
    public string ColorPrimaries { get; init; } = "";

    /// <summary>field_order.</summary>
    public string FieldOrder { get; init; } = "";

    /// <summary>refs.</summary>
    public int Refs { get; init; }

    /// <summary>sample_aspect_ratio.</summary>
    public string SampleAspectRatio { get; init; } = "";

    /// <summary>avg_frame_rate.</summary>
    public string AvgFrameRate { get; init; } = "";

    /// <summary>r_frame_rate.</summary>
    public string RFrameRate { get; init; } = "";

    /// <summary>channels.</summary>
    public int Channels { get; init; }

    /// <summary>sample_rate.</summary>
    public string SampleRate { get; init; } = "";

    /// <summary>disposition.</summary>
    public Disposition Disposition { get; init; } = new();

    /// <summary>tags.</summary>
    public Tags Tags { get; init; } = new();

    /// <summary>side_data_list.</summary>
    public IReadOnlyList<SideData> SideData { get; init; } = [];

    /// <summary>The stream's extradata, from the run with -show_data; null when that run didn't cover the stream or it has none.</summary>
    public byte[]? Record { get; init; }
}

/// <summary>ffprobe's chapter section.</summary>
public sealed record Chapter
{
    /// <summary>start_time.</summary>
    public string StartTime { get; init; } = "";

    /// <summary>end_time.</summary>
    public string EndTime { get; init; } = "";

    /// <summary>tags.</summary>
    public Tags Tags { get; init; } = new();
}

/// <summary>A frame of the frame read: its colour transfer (the alternative transfer characteristics SEI's when the stream has one) and its side data (HDR10+).</summary>
public sealed record Frame
{
    /// <summary>color_transfer.</summary>
    public string ColorTransfer { get; init; } = "";

    /// <summary>side_data_list.</summary>
    public IReadOnlyList<SideData> SideData { get; init; } = [];
}

/// <summary>Reading ffprobe's JSON, as far as a copy uses it.</summary>
internal static class Ffprobe
{
    /// <summary>One JSON value from b, nothing after it; a <see cref="DescribeException"/> of kind Probe otherwise.</summary>
    public static JsonNode Strict(ReadOnlySpan<byte> b)
    {
        try
        {
            return JsonNode.Parse(b, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) ?? throw new DescribeException(DescribeError.Probe, "null JSON value");
        }
        catch (JsonException e)
        {
            throw new DescribeException(DescribeError.Probe, e.Message);
        }
    }

    public static string Str(JsonNode? o, string key) =>
        o is JsonObject j && j[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static int Int(JsonNode? o, string key) =>
        o is JsonObject j && j[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out var n) && n is >= int.MinValue and <= int.MaxValue ? (int)n : 0;

    public static double Num(JsonNode? o, string key) =>
        o is JsonObject j && j[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var n) ? n : 0;

    public static Tags TagsOf(JsonNode? o)
    {
        var t = new Tags();
        if (o is JsonObject j && j["tags"] is JsonObject tags)
        {
            foreach (var (k, v) in tags)
            {
                if (v is JsonValue s && s.TryGetValue<string>(out var str))
                {
                    t[k] = str;
                }
            }
        }

        return t;
    }

    public static IReadOnlyList<SideData> SideDataOf(JsonNode? o)
    {
        var list = new List<SideData>();
        if (o is JsonObject j && j["side_data_list"] is JsonArray a)
        {
            foreach (var d in a)
            {
                list.Add(new SideData
                {
                    Type = Str(d, "side_data_type"), Rotation = Num(d, "rotation"), DisplayMatrix = Str(d, "displaymatrix"),
                    DvProfile = Int(d, "dv_profile"), DvLevel = Int(d, "dv_level"), ElPresent = Int(d, "el_present_flag"),
                    Compatibility = Int(d, "dv_bl_signal_compatibility_id"), StereoType = Str(d, "type"), Inverted = Int(d, "inverted"),
                });
            }
        }

        return list;
    }

    public static Stream StreamOf(JsonNode s)
    {
        var d = s["disposition"];
        return new Stream
        {
            Index = Int(s, "index"), CodecType = Str(s, "codec_type"), CodecName = Str(s, "codec_name"), CodecTag = Str(s, "codec_tag_string"),
            Profile = Str(s, "profile"), Level = Int(s, "level"), Width = Int(s, "width"), Height = Int(s, "height"), PixFmt = Str(s, "pix_fmt"),
            BitsPerRawSample = Str(s, "bits_per_raw_sample"), BitsPerSample = Int(s, "bits_per_sample"), ColorRange = Str(s, "color_range"),
            ColorSpace = Str(s, "color_space"), ColorTransfer = Str(s, "color_transfer"), ColorPrimaries = Str(s, "color_primaries"),
            FieldOrder = Str(s, "field_order"), Refs = Int(s, "refs"), SampleAspectRatio = Str(s, "sample_aspect_ratio"),
            AvgFrameRate = Str(s, "avg_frame_rate"), RFrameRate = Str(s, "r_frame_rate"), Channels = Int(s, "channels"), SampleRate = Str(s, "sample_rate"),
            Disposition = new Disposition
            {
                Default = Int(d, "default"), Dub = Int(d, "dub"), Original = Int(d, "original"), Comment = Int(d, "comment"), Forced = Int(d, "forced"),
                HearingImpaired = Int(d, "hearing_impaired"), VisualImpaired = Int(d, "visual_impaired"), AttachedPic = Int(d, "attached_pic"),
                Captions = Int(d, "captions"), Descriptions = Int(d, "descriptions"), StillImage = Int(d, "still_image"),
            },
            Tags = TagsOf(s),
            SideData = SideDataOf(s),
        };
    }

    public static IReadOnlyList<Frame> FramesOf(JsonNode o)
    {
        var list = new List<Frame>();
        if (o["frames"] is JsonArray a)
        {
            foreach (var f in a)
            {
                list.Add(new Frame { ColorTransfer = Str(f, "color_transfer"), SideData = SideDataOf(f) });
            }
        }

        return list;
    }

    /// <summary>ffprobe's dump of a stream's data ("00000000: 0164 000c ...  .d.."): each line's 16 bytes in the 40 columns after its offset; null when the dump isn't of that form or its length isn't size.</summary>
    public static byte[]? HexDump(string s, int size)
    {
        var out_ = new List<byte>(Math.Min(s.Length / 3, Math.Max(size, 0)));
        foreach (var line in s.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length < 11 || line[8..10] != ": ")
            {
                return null;
            }

            var cols = line[10..Math.Min(line.Length, 50)].TrimEnd(' ').Replace(" ", "", StringComparison.Ordinal);
            if (cols.Length % 2 != 0 || cols.Length > 32)
            {
                return null;
            }

            try
            {
                out_.AddRange(Convert.FromHexString(cols));
            }
            catch (FormatException)
            {
                return null;
            }

            if (out_.Count > size)
            {
                return null;
            }
        }

        return out_.Count == size ? out_.ToArray() : null;
    }

    /// <summary>ffprobe's time in seconds ("12.345000") in milliseconds, rounded; false when it isn't one.</summary>
    public static bool Seconds(string s, out long ms)
    {
        ms = 0;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || f < 0 || f > 1e10 || double.IsNaN(f))
        {
            return false;
        }

        ms = (long)((f * 1000) + 0.5);
        return true;
    }
}
