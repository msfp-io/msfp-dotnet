using System.Diagnostics;
using System.Globalization;
using Msfp.Protocol;

namespace Msfp.Examples.ShareFolder;

internal sealed partial class Folder
{
    // The baseline HLS copies ffmpeg made, by playback handle: their
    // temporary folders, removed when the playback stops.
    private readonly Dictionary<string, string> _made = new(StringComparer.Ordinal);
    private int _n;

    /// <summary>
    /// Makes the baseline of a copy outside it (MSFP playback section 2.7), as
    /// the request's Served says: the picture to H.264 High 4.1, 8-bit 4:2:0,
    /// tagged BT.709, unless it is copied; the audio track played to AAC-LC
    /// stereo at 48 kHz, unless it is copied; as complete HLS with MPEG-TS
    /// segments, in a temporary folder the playback's stop removes. ffmpeg
    /// makes the whole copy before the answer: simple, and fine for short
    /// files; a real host transcodes as the viewer plays.
    /// </summary>
    private async ValueTask<Started> BaselineAsync(StartRequest request, string path, CancellationToken ct)
    {
        request.Admit?.Invoke(Playback.MethodTranscode);
        var dir = Directory.CreateTempSubdirectory("share-folder-baseline-").FullName;
        var args = new List<string> { "-nostdin", "-v", "error", "-i", path, "-map_metadata", "-1", "-sn", "-dn" };
        if (request.AudioOnly || request.Served.Video.Length == 0)
        {
            args.Add("-vn");
        }
        else if (request.Served.Video == Names.Copy)
        {
            args.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
        }
        else
        {
            args.AddRange(["-map", "0:v:0", "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level:v", "4.1",
                "-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        }

        if (request.AudioTrack.StartsWith('a'))
        {
            args.AddRange(["-map", "0:" + request.AudioTrack[1..]]);
            args.AddRange(request.Served.Audio == Names.Copy
                ? ["-c:a", "copy"]
                : ["-c:a", "aac", "-profile:a", "aac_low", "-ac", "2", "-ar", "48000"]);
        }

        args.AddRange(["-f", "hls", "-hls_time", "6", "-hls_playlist_type", "vod", "-hls_segment_filename", Path.Combine(dir, "seg%d.ts"),
            "-master_pl_name", "master.m3u8", Path.Combine(dir, "v0.m3u8")]);
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        args.ForEach(start.ArgumentList.Add);
        try
        {
            using var p = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg didn't start");
            // A start given up (its request cut, the server stopping) ends ffmpeg too.
            using var end = ct.Register(() =>
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already exited
                }
            });
            var errors = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException("ffmpeg: " + errors.Trim());
            }
        }
        catch
        {
            Directory.Delete(dir, true);
            throw; // the engine releases what Admit counted
        }

        lock (_made)
        {
            var handle = "made" + (++_n).ToString(CultureInfo.InvariantCulture) + " " + request.Copy;
            _made[handle] = dir;
            return new Started { Handle = handle, Method = Playback.MethodTranscode, Protocol = Playback.ProtocolHls, PositionMs = request.StartMs };
        }
    }

    /// <summary>The folder of the baseline made for a playback; null for one served from the folder.</summary>
    private string? Made(string handle)
    {
        lock (_made)
        {
            return _made.GetValueOrDefault(handle);
        }
    }

    public ValueTask StopAsync(string handle, EndReason reason, CancellationToken ct)
    {
        string? dir;
        lock (_made)
        {
            _made.Remove(handle, out dir);
        }

        if (dir is not null)
        {
            Directory.Delete(dir, true);
        }

        return ValueTask.CompletedTask;
    }
}
