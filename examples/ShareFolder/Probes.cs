using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msfp.Protocol;

namespace Msfp.Examples.ShareFolder;

/// <summary>What ffprobe said of a file: what a copy carries of it (MSFP video section 3.2).</summary>
/// <param name="DurationMs">Its length, to the second below; 0 when unknown.</param>
/// <param name="BitrateKbps">Its bitrate, to the 500 below; null when unknown.</param>
/// <param name="Video">Its picture; null when unknown.</param>
/// <param name="Audio">Its audio tracks, a0, a1… by position.</param>
internal sealed record Probed(long DurationMs, long? BitrateKbps, VideoStream? Video, IReadOnlyList<CopyAudio> Audio);

/// <summary>
/// Describes video files with ffprobe, when it is installed: their length,
/// bitrate, picture and audio tracks, as MSFP video section 3.2 lists a copy.
/// Without ffprobe, or for a file it can't read, a copy says only what every
/// file says (its size, its container) and its length is 0. What ffprobe said
/// is kept by file, size and time, so a file is probed once until it changes.
/// </summary>
internal sealed class Probes
{
    private readonly Dictionary<string, (long Size, DateTime Time, Probed Probed)> _done = new(StringComparer.Ordinal);

    private static readonly Probed Nothing = new(0, null, null, []);

    /// <summary>What ffprobe says of the file at path.</summary>
    public Probed Describe(string path, FileInfo info)
    {
        lock (_done)
        {
            if (_done.TryGetValue(path, out var d) && d.Size == info.Length && d.Time == info.LastWriteTimeUtc)
            {
                return d.Probed;
            }
        }

        var p = Ffprobe(path);
        lock (_done)
        {
            _done[path] = (info.Length, info.LastWriteTimeUtc, p);
        }

        return p;
    }

    private static Probed Ffprobe(string path)
    {
        Output? o;
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
                return Nothing;
            }

            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill();
                return Nothing;
            }

            o = JsonSerializer.Deserialize<Output>(read.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            return Nothing; // no ffprobe, or a file it can't read
        }

        if (o is null)
        {
            return Nothing;
        }

        var duration = double.TryParse(o.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
            ? (long)s * 1000 // rounded down to a whole second
            : 0;
        long? bitrate = long.TryParse(o.Format?.BitRate, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b >= 500_000
            ? b / 1000 / 500 * 500 // rounded down to a whole 500
            : null;
        VideoStream? video = null;
        var audio = new List<CopyAudio>();
        foreach (var st in o.Streams ?? [])
        {
            switch (st.CodecType)
            {
                case "video" when video is null && st.Width > 0 && st.Height > 0:
                    video = new VideoStream(Names.NameOrOther(Names.VideoCodecs, st.CodecName ?? ""), st.Width, st.Height)
                    {
                        Fps = FrameRate(st.AvgFrameRate),
                    };
                    break;
                case "audio":
                    audio.Add(new CopyAudio("a" + audio.Count.ToString(CultureInfo.InvariantCulture), Names.NameOrOther(Names.AudioCodecs, st.CodecName ?? ""))
                    {
                        Language = Languages.TrySentTag(st.Tags?.Language ?? "", out var tag) ? tag : null,
                        Channels = st.Channels > 0 ? st.Channels : null,
                        SampleRate = int.TryParse(st.SampleRate, NumberStyles.None, CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : null,
                        Default = st.Disposition?.Default == 1,
                        Forced = st.Disposition?.Forced == 1,
                    });
                    break;
            }

            // Subtitles inside the file are left out: this example serves
            // only the .vtt files beside it.
        }

        return new Probed(duration, bitrate, video, audio);
    }

    // ffprobe's "24000/1001", to two decimals; null when unknown.
    private static double? FrameRate(string? s)
    {
        var parts = (s ?? "").Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) || den == 0 || num <= 0)
        {
            return null;
        }

        return Math.Floor(num / den * 100) / 100;
    }

    // What ffprobe's JSON says that a copy uses.
    private sealed record Output(
        [property: JsonPropertyName("format")] OutputFormat? Format,
        [property: JsonPropertyName("streams")] List<OutputStream>? Streams);

    private sealed record OutputFormat(
        [property: JsonPropertyName("duration")] string? Duration,
        [property: JsonPropertyName("bit_rate")] string? BitRate);

    private sealed record OutputStream(
        [property: JsonPropertyName("codec_type")] string? CodecType,
        [property: JsonPropertyName("codec_name")] string? CodecName,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("avg_frame_rate")] string? AvgFrameRate,
        [property: JsonPropertyName("channels")] int Channels,
        [property: JsonPropertyName("sample_rate")] string? SampleRate,
        [property: JsonPropertyName("disposition")] OutputDisposition? Disposition,
        [property: JsonPropertyName("tags")] OutputTags? Tags);

    private sealed record OutputDisposition(
        [property: JsonPropertyName("default")] int Default,
        [property: JsonPropertyName("forced")] int Forced);

    private sealed record OutputTags([property: JsonPropertyName("language")] string? Language);
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
