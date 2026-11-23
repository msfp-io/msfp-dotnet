using Msfp.Describe;

namespace Msfp.Describe.Tests;

// ffprobe for real: run where ffprobe is (DESCRIBE_FFPROBE, or ffprobe in
// PATH) and the media of make-media.sh is (DESCRIBE_MEDIA); skipped
// otherwise (the SDK image has no ffprobe).
public class ProbeTests
{
    private static (Prober P, string Dir) Bed()
    {
        var dir = Environment.GetEnvironmentVariable("DESCRIBE_MEDIA");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Assert.Skip("DESCRIBE_MEDIA not set");
        }

        return (new Prober { FFprobe = Environment.GetEnvironmentVariable("DESCRIBE_FFPROBE") ?? "", Frames = true, Nice = 19 }, dir!);
    }

    [Fact]
    public async Task ProbesThroughALink()
    {
        var (p, dir) = Bed();
        var link = Path.Combine(Path.GetTempPath(), "describe-link-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.CreateSymbolicLink(link, Path.Combine(dir, "with-font.mkv"));
        try
        {
            var (c, pr) = await p.DescribeAsync(link, [new External("fr", "subrip")], TestContext.Current.CancellationToken);
            Assert.Equal("video/matroska", c.MediaType);
            Assert.Equal(new FileInfo(Path.Combine(dir, "with-font.mkv")).Length, c.Size);
            Assert.True(c.DurationMs > 900);
            Assert.StartsWith("avc1.", c.Video!.Codecs, StringComparison.Ordinal);
            Assert.NotNull(c.Video.RefFrames);

            // The font is stream 2: the external track is s3.
            Assert.True(c.SubtitleTracks is [{ Id: "s1", Format: "ass" }, { Id: "s3", External: true, Language: "fr" }]);
            var raw = System.Text.Encoding.UTF8.GetString(pr.Raw(dir));
            Assert.DoesNotContain(dir, raw, StringComparison.Ordinal);
            Assert.DoesNotContain("attachment", raw, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Theory]
    [InlineData("h264-aac.mp4", "avc1.64000C")]
    [InlineData("hevc-hdr10.mkv", "hvc1.2.4.L60.90")]
    [InlineData("hevc-hlg-atc.ts", "hvc1.2.4.L60.90")]
    [InlineData("hevc-rext.mp4", "hev1.4.10.L60.9D.8")]
    [InlineData("av1-444.mkv", "av01.1.00M.08.0.000.01.01.01.0")]
    [InlineData("vp9-444.mp4", "vp09.01.11.08.03.01.01.01.00")]
    public async Task CodecsFromRecords(string file, string codecs)
    {
        var (p, dir) = Bed();
        var (c, _) = await p.DescribeAsync(Path.Combine(dir, file), null, TestContext.Current.CancellationToken);
        Assert.Equal(codecs, c.Video!.Codecs);
        if (file.StartsWith("hevc-hlg", StringComparison.Ordinal))
        {
            Assert.Equal(18, c.Video.Colour!.Transfer); // the frame read's SEI
        }
    }

    [Fact]
    public async Task RefusesWhatIsNotMedia()
    {
        var (p, dir) = Bed();
        var e = await Assert.ThrowsAsync<DescribeException>(() => p.ProbeAsync(Path.Combine(dir, "playlist.mkv"), TestContext.Current.CancellationToken));
        Assert.Equal(DescribeError.UnknownType, e.Kind);
        e = await Assert.ThrowsAsync<DescribeException>(() => p.ProbeAsync(dir, TestContext.Current.CancellationToken));
        Assert.Equal(DescribeError.NotRegular, e.Kind);
        Assert.DoesNotContain(dir, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAFifo()
    {
        var (p, _) = Bed();
        var fifo = Path.Combine(Path.GetTempPath(), "describe-fifo-" + Guid.NewGuid().ToString("N") + ".mkv");
        using (var mk = System.Diagnostics.Process.Start("mkfifo", fifo))
        {
            await mk.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            var e = await Assert.ThrowsAsync<DescribeException>(() => p.ProbeAsync(fifo, TestContext.Current.CancellationToken));
            Assert.Equal(DescribeError.NotRegular, e.Kind);
        }
        finally
        {
            File.Delete(fifo);
        }
    }
}
