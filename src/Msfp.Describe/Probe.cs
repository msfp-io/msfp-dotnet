using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Msfp.Protocol;

namespace Msfp.Describe;

/// <summary>What failed in <see cref="Prober"/> or a parser.</summary>
public enum DescribeError
{
    /// <summary>A path that isn't a regular file once its links are resolved: a directory, a FIFO, a device, a socket.</summary>
    NotRegular,

    /// <summary>A file whose first bytes say no media type <see cref="MediaTypes.Sniff"/> knows: it isn't probed.</summary>
    UnknownType,

    /// <summary>A run of ffprobe that failed: its exit, its time limit, its output cap, or JSON that isn't ffprobe's.</summary>
    Probe,

    /// <summary>A file ffprobe found no stream in.</summary>
    NoStreams,

    /// <summary>A codec record that can't be read.</summary>
    Record,

    /// <summary>The file couldn't be opened or read.</summary>
    Io,
}

/// <summary>A failure of the describe package; its message never names the file's path.</summary>
public sealed class DescribeException : Exception
{
    /// <summary>A failure of the given kind.</summary>
    public DescribeException(DescribeError kind, string message)
        : base("describe: " + message) => Kind = kind;

    /// <summary>What failed.</summary>
    public DescribeError Kind { get; }
}

/// <summary>
/// ffprobe's JSON outputs for one file: Streams the first run's
/// (-show_format -show_streams -show_chapters), Data the second run's
/// (-show_streams -show_data on some streams), Frames the frame read's
/// (-show_frames), null when it wasn't made. Kept as they came (a host may
/// store them and <see cref="Probe.Parse"/> them again); they hold no path.
/// </summary>
public sealed record Outputs(byte[] Streams, IReadOnlyList<byte[]> Data, byte[]? Frames = null);

/// <summary>What ffprobe said of one file: its media type (sniffed), size, format, streams (each with its record when the second run read one), chapters and, when the frame read was made, its frames.</summary>
public sealed class Probe
{
    private Probe(string mediaType, long size, Outputs outputs)
    {
        MediaType = mediaType;
        Size = size;
        Outputs = outputs;
    }

    /// <summary>The sniffed media type.</summary>
    public string MediaType { get; }

    /// <summary>The file's size.</summary>
    public long Size { get; }

    /// <summary>ffprobe's format section.</summary>
    public Format Format { get; private set; } = new();

    /// <summary>The streams.</summary>
    public IReadOnlyList<Stream> Streams { get; private set; } = [];

    /// <summary>The chapters.</summary>
    public IReadOnlyList<Chapter> Chapters { get; private set; } = [];

    /// <summary>The frame read's frames; null when it wasn't made.</summary>
    public IReadOnlyList<Frame>? Frames { get; internal set; }

    /// <summary>ffprobe's outputs as they came.</summary>
    public Outputs Outputs { get; internal set; }

    /// <summary>
    /// Reads ffprobe's outputs for a file of the given media type and size,
    /// as <see cref="Prober.ProbeAsync"/> made them (or as a host kept them):
    /// strict JSON, the records of the second run set on their streams.
    /// </summary>
    public static Probe Parse(string mediaType, long size, Outputs outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var first = Ffprobe.Strict(outputs.Streams);
        var streams = (first["streams"] as JsonArray)?.Where(s => s is JsonObject).Select(s => Ffprobe.StreamOf(s!)).ToList() ?? [];
        if (streams.Count == 0)
        {
            throw new DescribeException(DescribeError.NoStreams, "no streams");
        }

        var pr = new Probe(mediaType, size, outputs)
        {
            Chapters = (first["chapters"] as JsonArray)?.Where(c => c is JsonObject).Select(c => new Chapter
            {
                StartTime = Ffprobe.Str(c, "start_time"), EndTime = Ffprobe.Str(c, "end_time"), Tags = Ffprobe.TagsOf(c),
            }).ToList() ?? [],
        };
        if (first["format"] is JsonObject f)
        {
            pr.Format = new Format
            {
                Name = Ffprobe.Str(f, "format_name"), Streams = Ffprobe.Int(f, "nb_streams"), Duration = Ffprobe.Str(f, "duration"),
                Size = Ffprobe.Str(f, "size"), Tags = Ffprobe.TagsOf(f),
            };
        }

        foreach (var d in outputs.Data)
        {
            foreach (var ds in Ffprobe.Strict(d)["streams"] as JsonArray ?? [])
            {
                var size_ = Ffprobe.Int(ds, "extradata_size");
                if (size_ <= 0 || Ffprobe.HexDump(Ffprobe.Str(ds, "extradata"), size_) is not { } rec)
                {
                    continue;
                }

                var index = Ffprobe.Int(ds, "index");
                for (var i = 0; i < streams.Count; i++)
                {
                    if (streams[i].Index == index && streams[i].CodecType != "attachment")
                    {
                        streams[i] = streams[i] with { Record = rec };
                    }
                }
            }
        }

        pr.Streams = streams;
        if (outputs.Frames is { } frames)
        {
            pr.Frames = Ffprobe.FramesOf(Ffprobe.Strict(frames));
        }

        return pr;
    }

    /// <summary>The probe's picture: its first video stream that isn't an attached picture (cover art) or a still image and has a size; null for none.</summary>
    public Stream? Picture() =>
        Streams.FirstOrDefault(s => s.CodecType == "video" && s.Disposition.AttachedPic == 0 && s.Disposition.StillImage == 0 && s.Width > 0 && s.Height > 0);

    /// <summary>
    /// Whether the probe's picture is worth the frame read: a PQ or HLG
    /// transfer, or a 10-bit picture whose transfer is BT.2020's or BT.709's
    /// (broadcast HLG signals those in the VUI and 18 in the SEI), and the
    /// read not made yet.
    /// </summary>
    public bool NeedsFrames()
    {
        if (Frames is not null || Picture() is not { } s)
        {
            return false;
        }

        return s.ColorTransfer switch
        {
            "smpte2084" or "arib-std-b67" => true,
            "bt2020-10" or "bt709" => Shaper.VideoBitDepth(s) >= 10,
            _ => false,
        };
    }

    /// <summary>
    /// The probe's output kept for the caller (an identifier reading the title
    /// tag and embedded IDs): the first run's JSON, each stream of the second
    /// run's records merged into its stream, without format.filename, without
    /// attachment streams, and without any string holding dir. Cover art and
    /// attachment data are never in it: no run reads them. Tags are kept.
    /// </summary>
    public byte[] Raw(string dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        var tree = Ffprobe.Strict(Outputs.Streams) as JsonObject ?? throw new DescribeException(DescribeError.Probe, "not an object");
        (tree["format"] as JsonObject)?.Remove("filename");
        var data = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var b in Outputs.Data)
        {
            try
            {
                foreach (var s in JsonNode.Parse(b)?["streams"] as JsonArray ?? [])
                {
                    if (s is JsonObject o)
                    {
                        data[o["index"]?.ToJsonString() ?? ""] = o;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (tree["streams"] is JsonArray list)
        {
            var kept = new JsonArray();
            foreach (var e in list.ToList())
            {
                if (e is not JsonObject s || (s["codec_type"] is JsonValue t && t.TryGetValue<string>(out var ct) && ct == "attachment"))
                {
                    continue;
                }

                if (data.TryGetValue(s["index"]?.ToJsonString() ?? "", out var ds))
                {
                    foreach (var k in new[] { "extradata", "extradata_size" })
                    {
                        if (ds[k] is { } v)
                        {
                            s[k] = v.DeepClone();
                        }
                    }
                }

                list.Remove(s);
                kept.Add(s);
            }

            tree["streams"] = kept;
        }

        dir = dir.TrimEnd(Path.DirectorySeparatorChar);
        if (dir.Length > 0 && dir != ".")
        {
            Scrub(tree, dir);
        }

        return Encoding.UTF8.GetBytes(tree.ToJsonString());
    }

    // Every string member or entry of a JSON tree that holds dir removed.
    private static void Scrub(JsonNode? v, string dir)
    {
        switch (v)
        {
            case JsonObject o:
                foreach (var (k, e) in o.ToList())
                {
                    if (e is JsonValue s && s.TryGetValue<string>(out var str) && str.Contains(dir, StringComparison.Ordinal))
                    {
                        o.Remove(k);
                    }
                    else
                    {
                        Scrub(e, dir);
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue s && s.TryGetValue<string>(out var str) && str.Contains(dir, StringComparison.Ordinal))
                    {
                        a[i] = "";
                    }
                    else
                    {
                        Scrub(a[i], dir);
                    }
                }

                break;
        }
    }
}

/// <summary>
/// Runs ffprobe on local files, as MSFP's facts need it and no more: the
/// file's real path resolved, a regular file opened read-only and checked,
/// its media type sniffed from its first bytes (a type
/// <see cref="MediaTypes.Sniff"/> doesn't give is refused, unprobed), then
/// ffprobe in a child process with the sniffed demuxer (-f), the file
/// protocol only, bounded probe size and analysis, a time limit and an
/// output cap, its JSON parsed strictly: (1) -show_format -show_streams
/// -show_chapters, without data; (2) -show_streams -show_data on the video
/// streams (V: attached pictures left out), and on the audio streams when
/// one is AAC: the codec records. Attachments (fonts) and cover art are
/// never read. With <see cref="Frames"/>, a third run reads the first
/// frames of an HDR picture (<see cref="Probe.NeedsFrames"/>). On Linux
/// ffprobe reads the very file that was checked, by the descriptor this
/// process holds open (/proc/&lt;pid&gt;/fd/&lt;n&gt;), even if the path changed
/// since; elsewhere, by its real path, checked just before.
/// </summary>
public sealed class Prober
{
    /// <summary>Each run's time limit by default.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Each run's output cap by default, in bytes.</summary>
    public const int DefaultMaxOutput = 8 << 20;

    /// <summary>ffprobe's -probesize by default, in bytes.</summary>
    public const long DefaultProbeSize = 32 << 20;

    /// <summary>ffprobe's -analyzeduration by default.</summary>
    public static readonly TimeSpan DefaultAnalyzeDuration = TimeSpan.FromSeconds(30);

    /// <summary>How many frames the frame read decodes.</summary>
    public const int FrameCount = 5;

    /// <summary>The ffprobe program; "" is "ffprobe", looked up in PATH.</summary>
    public string FFprobe { get; init; } = "";

    /// <summary>Bounds each run; zero is <see cref="DefaultTimeout"/>.</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>Bounds each run's output, in bytes; 0 is <see cref="DefaultMaxOutput"/>. A run past it is stopped and fails.</summary>
    public int MaxOutput { get; init; }

    /// <summary>ffprobe's -probesize, in bytes; 0 is <see cref="DefaultProbeSize"/>.</summary>
    public long ProbeSize { get; init; }

    /// <summary>ffprobe's -analyzeduration; zero is <see cref="DefaultAnalyzeDuration"/>.</summary>
    public TimeSpan AnalyzeDuration { get; init; }

    /// <summary>Asks for the frame read on an HDR picture.</summary>
    public bool Frames { get; init; }

    /// <summary>From 1 to 19, lowers each ffprobe child's priority to that niceness as soon as it starts (Unix); 0 leaves it.</summary>
    public int Nice { get; init; }

    /// <summary>Probes the file at path (<see cref="ProbeAsync"/>) and shapes the copy (<see cref="Shaper.Shape"/>). The copy's ID and etag are the host's to set.</summary>
    public async Task<(Copy Copy, Probe Probe)> DescribeAsync(string path, IReadOnlyList<External>? external, CancellationToken ct)
    {
        var pr = await ProbeAsync(path, ct).ConfigureAwait(false);
        return (Shaper.Shape(pr, external), pr);
    }

    /// <summary>Reads the file at path: links resolved, a regular file opened read-only, its first bytes sniffed, then ffprobe's runs.</summary>
    public async Task<Probe> ProbeAsync(string path, CancellationToken ct)
    {
        using var f = OpenRegular(path);
        var head = new byte[MediaTypes.SniffSize];
        int n;
        try
        {
            n = RandomAccess.Read(f.Handle, head, 0);
        }
        catch (IOException e)
        {
            throw new DescribeException(DescribeError.Io, "reading the file: " + e.GetType().Name);
        }

        var mediaType = MediaTypes.Sniff(head.AsSpan(0, n));
        var demuxer = MediaTypes.Demuxer(mediaType);
        if (demuxer.Length == 0)
        {
            throw new DescribeException(DescribeError.UnknownType, "unknown media type");
        }

        var streams = await RunAsync(f, demuxer, ct, "-show_format", "-show_streams", "-show_chapters").ConfigureAwait(false);
        var first = Ffprobe.Strict(streams);
        bool video = false, aac = false;
        foreach (var s in first["streams"] as JsonArray ?? [])
        {
            var type = Ffprobe.Str(s, "codec_type");
            video = video || (type == "video" && Ffprobe.Int(s?["disposition"], "attached_pic") == 0);
            aac = aac || (type == "audio" && Ffprobe.Str(s, "codec_name") == "aac");
        }

        var data = new List<byte[]>();
        foreach (var (spec, want) in new[] { ("V", video), ("a", aac) })
        {
            if (want)
            {
                data.Add(await RunAsync(f, demuxer, ct, "-select_streams", spec, "-show_streams", "-show_data",
                    "-show_entries", "stream=index,extradata_size,extradata").ConfigureAwait(false));
            }
        }

        var pr = Probe.Parse(mediaType, f.Size, new Outputs(streams, data));
        if (Frames && pr.NeedsFrames())
        {
            await ReadFramesAsync(f, demuxer, pr, ct).ConfigureAwait(false);
        }

        return pr;
    }

    /// <summary>The frame read for a probe already made (without <see cref="Frames"/>, or by <see cref="Probe.Parse"/> from earlier outputs), when <see cref="Probe.NeedsFrames"/> says it is worth it.</summary>
    public async Task ReadFramesAsync(string path, Probe pr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pr);
        if (!pr.NeedsFrames())
        {
            return;
        }

        using var f = OpenRegular(path);
        var demuxer = MediaTypes.Demuxer(pr.MediaType);
        if (demuxer.Length == 0)
        {
            demuxer = MediaTypes.Demuxer(MediaTypes.AudioOrVideo(pr.MediaType, false)); // audio/mp4 and the like
        }

        if (demuxer.Length == 0)
        {
            throw new DescribeException(DescribeError.UnknownType, "unknown media type");
        }

        await ReadFramesAsync(f, demuxer, pr, ct).ConfigureAwait(false);
    }

    private async Task ReadFramesAsync(Opened f, string demuxer, Probe pr, CancellationToken ct)
    {
        var b = await RunAsync(f, demuxer, ct, "-select_streams", "V:0", "-read_intervals", "%+#" + FrameCount.ToString(CultureInfo.InvariantCulture),
            "-show_frames", "-show_entries", "frame=color_transfer:frame_side_data=side_data_type").ConfigureAwait(false);
        pr.Frames = Ffprobe.FramesOf(Ffprobe.Strict(b));
        pr.Outputs = pr.Outputs with { Frames = b };
    }

    // One run of ffprobe with the common arguments and args: its output.
    private async Task<byte[]> RunAsync(Opened f, string demuxer, CancellationToken ct, params string[] args)
    {
        var timeout = Timeout > TimeSpan.Zero ? Timeout : DefaultTimeout;
        var limit = MaxOutput > 0 ? MaxOutput : DefaultMaxOutput;
        var psi = new ProcessStartInfo(FFprobe.Length > 0 ? FFprobe : "ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.Environment.Clear();
        foreach (var a in new[] { "-v", "error", "-hide_banner", "-of", "json",
                     "-probesize", (ProbeSize > 0 ? ProbeSize : DefaultProbeSize).ToString(CultureInfo.InvariantCulture),
                     "-analyzeduration", ((long)(AnalyzeDuration > TimeSpan.Zero ? AnalyzeDuration : DefaultAnalyzeDuration).TotalMicroseconds).ToString(CultureInfo.InvariantCulture),
                     "-protocol_whitelist", "file", "-f", demuxer })
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("file:" + f.ChildPath);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new DescribeException(DescribeError.Probe, "ffprobe didn't start");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new DescribeException(DescribeError.Probe, "ffprobe didn't start: " + e.NativeErrorCode.ToString(CultureInfo.InvariantCulture));
        }

        using (p)
        {
            p.StandardInput.Close();
            if (Nice > 0 && OperatingSystem.IsLinux())
            {
                _ = SetPriority(0, p.Id, Math.Min(Nice, 19));
            }

            var over = false;
            var stdout = new MemoryStream();
            var read = Task.Run(
                async () =>
                {
                    var buf = new byte[65536];
                    int k;
                    while ((k = await p.StandardOutput.BaseStream.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        if (stdout.Length + k > limit)
                        {
                            over = true;
                            await cts.CancelAsync().ConfigureAwait(false);
                            return;
                        }

                        stdout.Write(buf, 0, k);
                    }
                },
                CancellationToken.None);
            var err = Task.Run(
                async () =>
                {
                    var buf = new char[2048];
                    var got = await p.StandardError.ReadBlockAsync(buf, cts.Token).ConfigureAwait(false);
                    var s = new string(buf, 0, got);
                    var rest = new char[4096];
                    while (await p.StandardError.ReadAsync(rest, cts.Token).ConfigureAwait(false) > 0)
                    {
                        // dropped past the first 2,048 characters
                    }

                    return s;
                },
                CancellationToken.None);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                await read.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }

                if (over)
                {
                    throw new DescribeException(DescribeError.Probe, "ffprobe failed: output past " + limit.ToString(CultureInfo.InvariantCulture) + " bytes");
                }

                ct.ThrowIfCancellationRequested();
                throw new DescribeException(DescribeError.Probe, "ffprobe failed: past " + timeout.ToString());
            }

            if (over)
            {
                throw new DescribeException(DescribeError.Probe, "ffprobe failed: output past " + limit.ToString(CultureInfo.InvariantCulture) + " bytes");
            }

            if (p.ExitCode != 0)
            {
                string msg;
                try
                {
                    msg = (await err.ConfigureAwait(false)).Trim().Replace(f.ChildPath, "file", StringComparison.Ordinal).Replace(f.Real, "file", StringComparison.Ordinal);
                }
                catch (OperationCanceledException)
                {
                    msg = "";
                }

                throw new DescribeException(DescribeError.Probe, "ffprobe failed: exit " + p.ExitCode.ToString(CultureInfo.InvariantCulture) + (msg.Length > 0 ? ": " + msg : ""));
            }

            return stdout.ToArray();
        }
    }

    // A file opened read-only after its checks: the real path a regular
    // file, the handle open on that very file.
    private sealed class Opened(SafeFileHandle handle, string real, long size) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;

        public string Real { get; } = real;

        public long Size { get; } = size;

        public string ChildPath => OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fd")
            ? "/proc/" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "/fd/" + Handle.DangerousGetHandle().ToString()
            : Real;

        public void Dispose() => Handle.Dispose();
    }

    // Resolves path's links and opens the real file read-only: it must be a
    // regular file, and the file opened the one the real path names. Errors
    // never carry the path.
    private static Opened OpenRegular(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string real;
        try
        {
            real = RealPath(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DescribeException(DescribeError.Io, "resolving the path: " + e.GetType().Name);
        }

        var info = new FileInfo(real);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
            (!OperatingSystem.IsWindows() && !IsRegular(real)))
        {
            throw new DescribeException(DescribeError.NotRegular, "not a regular file");
        }

        SafeFileHandle h;
        try
        {
            h = File.OpenHandle(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DescribeException(DescribeError.Io, "opening the file: " + e.GetType().Name);
        }

        try
        {
            if (!OperatingSystem.IsWindows() && !SameRegularFile(h, real))
            {
                throw new DescribeException(DescribeError.NotRegular, "not a regular file");
            }

            return new Opened(h, real, RandomAccess.GetLength(h));
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    // The path with every link resolved, component by component.
    private static string RealPath(string full)
    {
        var root = Path.GetPathRoot(full) ?? "/";
        var current = root;
        var parts = new Queue<string>(full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        var hops = 0;
        while (parts.Count > 0)
        {
            var next = Path.Combine(current, parts.Dequeue());
            var fi = new FileInfo(next);
            if (fi.LinkTarget is { } target)
            {
                if (++hops > 40)
                {
                    throw new IOException("too many links");
                }

                var resolved = Path.GetFullPath(target, current);
                var rest = parts.ToList();
                parts = new Queue<string>(resolved[(Path.GetPathRoot(resolved) ?? "/").Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Concat(rest));
                current = Path.GetPathRoot(resolved) ?? "/";
                continue;
            }

            current = next;
        }

        return current;
    }

    // Unix: lstat's file type of path is a regular file (S_IFREG).
    private static bool IsRegular(string path) => Stat(path, follow: false) is { } st && (st.Mode & 0xF000) == 0x8000;

    // Unix: the handle is open on a regular file, the one path names (same device and inode).
    private static bool SameRegularFile(SafeFileHandle h, string path)
    {
        var a = FStat(h);
        var b = Stat(path, follow: false);
        return a is { } x && b is { } y && (x.Mode & 0xF000) == 0x8000 && x.Dev == y.Dev && x.Ino == y.Ino;
    }

    private readonly record struct StatInfo(ulong Dev, ulong Ino, uint Mode);

    // struct stat's layout: st_dev, st_ino first everywhere on Linux; st_mode
    // after st_nlink (8 bytes) on x86-64, right after st_ino on the generic
    // layout (arm64, riscv64…). Other systems: no check beyond FileInfo's.
    private static int ModeOffset => RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16;

    private static StatInfo? Stat(string path, bool follow)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new StatInfo(0, 0, 0x8000);
        }

        var buf = new byte[256];
        var r = follow ? StatNative(path, buf) : LStatNative(path, buf);
        return r == 0 ? Read(buf) : null;
    }

    private static StatInfo? FStat(SafeFileHandle h)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new StatInfo(0, 0, 0x8000);
        }

        var buf = new byte[256];
        var ok = false;
        h.DangerousAddRef(ref ok);
        try
        {
            return FStatNative((int)h.DangerousGetHandle(), buf) == 0 ? Read(buf) : null;
        }
        finally
        {
            if (ok)
            {
                h.DangerousRelease();
            }
        }
    }

    private static StatInfo Read(byte[] b) =>
        new(BitConverter.ToUInt64(b, 0), BitConverter.ToUInt64(b, 8), BitConverter.ToUInt32(b, ModeOffset));

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buf);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStatNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buf);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatNative(int fd, byte[] buf);

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriority(int which, int who, int prio);
}
