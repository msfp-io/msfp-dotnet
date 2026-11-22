namespace Msfp.Protocol.Tests;

/// <summary>The copies tests of wire/video_test.go.</summary>
public class CopiesTests
{
    private static Copy Bare(string id, VideoStream? video = null) => new(id, 1, "video/matroska", 1, "t", []) { Video = video };

    private static VideoStream V(int w, int h, params string[] hdr) => new("hevc", w, h) { Hdr = hdr };

    private static readonly VideoColour Pq = new(9, VideoColour.TransferPq, 9, false);

    // The order of copies the notes advise: high dynamic range from
    // colour's transfer (16 or 18), a dv object, or a non-empty hdr.
    [Fact]
    public void Order()
    {
        Copy[] copies =
        [
            Bare("none"),
            Bare("hd", V(1920, 1080)),
            Bare("wide", V(3840, 1600)), // counts as 2160
            Bare("uhd-dv", V(3840, 2160, "hdr10plus") with { Colour = Pq, Dv = new VideoDv(8, 6, 1) }),
            Bare("uhd", V(3840, 2160)),
            Bare("uhd-hdr", V(3840, 2160) with { Colour = Pq }),
            Bare("uhd-hlg", V(3840, 2160) with { Colour = new VideoColour(null, VideoColour.TransferHlg, null) }),
            Bare("uhd-p5", V(3840, 2160) with { Dv = new VideoDv(5, 6, 0) }),
            Bare("hd2", V(1920, 1080)),
        ];
        var sorted = Copies.Sorted(copies);
        Assert.Equal(["uhd-dv", "uhd-hdr", "uhd-hlg", "uhd-p5", "wide", "uhd", "hd", "hd2", "none"], sorted.Select(c => c.Id));
        Assert.True(Copies.Better(sorted[0], sorted[1]));
        Assert.False(Copies.Better(sorted[4], sorted[5]));
    }

    // A copy written is read back unchanged, and audioTracks stays even
    // when empty.
    [Fact]
    public void RoundTrip()
    {
        var c = new Copy("v1", 1000, "video/matroska", 3, "t",
            [new CopyAudio("a0", "eac3") { Default = true, Language = "fr-CA", Codecs = "ec-3", Objects = "dolby-atmos", Role = "main" }])
        {
            StartMs = 0,
            EndMs = 500,
            Parts = 2,
            Video = new VideoStream("hevc", 1920, 1080)
            {
                Codecs = "hvc1.2.4.L153.B0", SampleAspect = "32:27", Rotation = 90, BitDepth = 10,
                Fps = "24000/1001", Hdr = ["hdr10plus"], Dv = new VideoDv(8, 6, 1) { El = false },
                Colour = new VideoColour(9, 16, null, false),
            },
            SubtitleTracks = [new CopySubtitle("s1", "hdmv_pgs_subtitle") { HearingImpaired = true, Role = "subtitle" }, new CopySubtitle("s2", "")],
            Chapters = [new CopyChapter(0, "One"), new CopyChapter(10)],
            Segments = [new CopySegment("intro", 0, 10)],
            PreviewLayout = new PreviewLayout(320, 180, 10, 10, 10_000, 150),
            ThreeD = "sbs-rl",
        };
        var b = Copies.Write(c);
        var got = Copies.Read(b);
        Assert.NotNull(got);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(b), System.Text.Encoding.UTF8.GetString(Copies.Write(got)));
        Assert.Equal(2, got.PreviewLayout!.Sheets);
        Assert.False(got.Video!.Colour!.FullRange);

        var bare = Copies.Write(new Copy("v1", 0, "video/matroska", 1, "t", []));
        Assert.Equal("""{"id":"v1","durationMs":0,"mediaType":"video/matroska","size":1,"etag":"t","audioTracks":[]}""",
            System.Text.Encoding.UTF8.GetString(bare));
        Assert.NotNull(Copies.Read(bare));
    }

    // Bounds the vectors don't reach on their own.
    [Fact]
    public void Bounds()
    {
        static Copy? R(string extra) => Copies.Read(System.Text.Encoding.UTF8.GetBytes(
            """{"id":"v1","durationMs":1000,"mediaType":"video/matroska","size":10,"etag":"t","audioTracks":[]""" + extra + "}"));

        Assert.Equal("24000/1001", R(""","video":{"codec":"x","width":1,"height":1,"fps":"24000/1001"}""")?.Video?.Fps);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"fps":"48000/2002"}""")?.Video?.Fps);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"fps":23.976}""")?.Video?.Fps);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"sampleAspect":"65536:1"}""")?.Video?.SampleAspect);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"rotation":45}""")?.Video?.Rotation);
        Assert.Equal("vvc", R(""","video":{"codec":"vvc","width":1,"height":1}""")?.Video?.Codec);
        Assert.Null(R(""","video":{"codec":"VVC","width":1,"height":1}""")?.Video);
        Assert.Null(Copies.Read("""{"id":"v1","durationMs":1000,"container":"matroska","size":10,"etag":"t","audioTracks":[]}"""u8));
        Assert.Equal(new VideoDv(8, 1, 1), R(""","video":{"codec":"h264","width":1,"height":1,"dv":{"profile":8,"level":1,"compatibility":1,"bl":true}}""")?.Video?.Dv);
        Assert.Equal(new VideoDv(127, 63, 15), R(""","video":{"codec":"h264","width":1,"height":1,"dv":{"profile":127,"level":63,"compatibility":15,"el":1}}""")?.Video?.Dv);
        Assert.Null(R(""","parts":[{"size":5,"durationMs":1},{"size":5,"durationMs":1}]""")!.Parts);
        Assert.Equal(16, R(""","parts":16""")!.Parts);
        Assert.Null(R(""","startMs":1001""")?.StartMs);
        Assert.Null(R(""","startMs":10,"endMs":10""")?.EndMs);
        Assert.Equal(4, R(""","video":{"codec":"h264","width":1,"height":1,"refFrames":4}""")?.Video?.RefFrames);
        Assert.Null(R(""","video":{"codec":"hevc","width":1,"height":1,"refFrames":4}""")?.Video?.RefFrames);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"colour":{"primaries":2}}""")?.Video?.Colour);
        Assert.Null(Copies.Read("""{"id":"v1","durationMs":1000,"mediaType":"video/matroska","size":10,"etag":"t","audioTracks":null}"""u8));
    }

    // What a viewer's server reads from codecs, beyond the vectors.
    [Fact]
    public void CodecsFacts()
    {
        Assert.True(Rfc6381.Required("aac"));
        Assert.False(Rfc6381.Required("eac3"));
        Assert.Equal("dvh1", Rfc6381.DolbyVisionEntry("hvc1"));
        Assert.Null(Rfc6381.DolbyVisionEntry("vp09"));
        var f = Rfc6381.Read("hevc", "hvc1.2.4.L153.B0", 10, new VideoDv(8, 6, 1), out _);
        Assert.Equal("dvh1.08.06", f?.DolbyVision);
        Assert.Null(Rfc6381.Read("h264", null, null, null, out var why));
        Assert.Contains("required for h264", why, StringComparison.Ordinal);
        Assert.Equal(new CodecsFacts("eac3", null), Rfc6381.Read("eac3", null, null, null, out _));
    }
}
