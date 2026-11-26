using System.Diagnostics;
using System.Security.Cryptography;
using Msfp.Describe;
using Msfp.Protocol;

namespace Msfp.Examples.ShareFolder;

/// <summary>
/// Describes video files as MSFP video section 3.2 lists a copy, with the
/// Msfp.Describe package: its media type from its first bytes, its length,
/// its picture, its tracks and chapters from ffprobe when it is installed
/// (two runs, the codec records read, attachments never), shaped into MSFP's
/// vocabularies; its etag from its content. Without ffprobe, or for a file it
/// can't read, a copy says only what every file says (its size, its media
/// type, its etag) and its length is 0. What a file gave is kept by path,
/// size and time, so a file is read once until it changes.
/// </summary>
internal sealed class Probes
{
    private readonly Prober _prober = new();
    private readonly Dictionary<string, (long Size, DateTime Time, Protocol.Copy Copy, int Streams)> _done = new(StringComparer.Ordinal);

    /// <summary>
    /// What the file at path says of itself: a copy with its media type,
    /// etag, length, picture, audio and subtitle tracks and chapters; and how
    /// many streams it has. The rest is the caller's.
    /// </summary>
    public async Task<(Protocol.Copy Copy, int Streams)> DescribeAsync(string path, FileInfo info, CancellationToken ct)
    {
        lock (_done)
        {
            if (_done.TryGetValue(path, out var d) && d.Size == info.Length && d.Time == info.LastWriteTimeUtc)
            {
                return (d.Copy, d.Streams);
            }
        }

        Protocol.Copy c;
        var streams = 0;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            var pr = await _prober.ProbeAsync(path, cts.Token);
            c = Shaper.Shape(pr, null);
            streams = pr.Format.Streams;
            foreach (var s in pr.Streams)
            {
                streams = Math.Max(streams, s.Index + 1);
            }
        }
        catch (Exception e) when (e is DescribeException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            c = new Protocol.Copy("", 0, MediaTypeOf(path), info.Length, "", []); // no ffprobe, or a file it can't read
        }

        c = c with { ETag = ContentTag(path) };
        lock (_done)
        {
            _done[path] = (info.Length, info.LastWriteTimeUtc, c, streams);
        }

        return (c, streams);
    }

    /// <summary>How many files' descriptions are kept.</summary>
    public int Count
    {
        get
        {
            lock (_done)
            {
                return _done.Count;
            }
        }
    }

    /// <summary>Forgets what the files outside here gave.</summary>
    public void Keep(IReadOnlySet<string> here)
    {
        lock (_done)
        {
            foreach (var path in _done.Keys.Where(p => !here.Contains(p)).ToList())
            {
                _done.Remove(path);
            }
        }
    }

    // The media type of a file ffprobe didn't read: from its first bytes
    // where they tell it, else from its extension, else video/mp4.
    private static string MediaTypeOf(string path)
    {
        var head = new byte[MediaTypes.SniffSize];
        var n = 0;
        try
        {
            using var f = File.OpenRead(path);
            n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        var t = MediaTypes.Sniff(head.AsSpan(0, n));
        if (t.Length == 0)
        {
            t = MediaTypes.ContainerType(Path.GetExtension(path).TrimStart('.'));
        }

        return t.Length > 0 ? t : "video/mp4";
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
