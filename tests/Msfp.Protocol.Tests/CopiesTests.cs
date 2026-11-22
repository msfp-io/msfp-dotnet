namespace Msfp.Protocol.Tests;

/// <summary>The copies tests of wire/video_test.go.</summary>
public class CopiesTests
{
    private static Copy Bare(string id, VideoStream? video = null) => new(id, 1, "matroska", 1, "t", []) { Video = video };

    private static VideoStream V(int w, int h, params string[] hdr) => new("hevc", w, h) { Hdr = hdr };

    private static readonly VideoColour Pq = new("bt2020", "smpte2084", "bt2020nc", "tv");

    // The recommended order of copies (video section 3.7): high dynamic
    // range from colour's transfer, or a non-empty hdr.
    [Fact]
    public void Order()
    {
        Copy[] copies =
        [
            Bare("none"),
            Bare("hd", V(1920, 1080)),
            Bare("wide", V(3840, 1600)), // counts as 2160
            Bare("uhd-dv", V(3840, 2160, "dv", "hdr10plus") with { Colour = Pq }),
            Bare("uhd", V(3840, 2160)),
            Bare("uhd-hdr", V(3840, 2160) with { Colour = Pq }),
            Bare("uhd-hlg", V(3840, 2160) with { Colour = new VideoColour(null, "arib-std-b67", null, null) }),
            Bare("uhd-p5", V(3840, 2160, "dv")),
            Bare("hd2", V(1920, 1080)),
        ];
        var sorted = Copies.Sorted(copies);
        Assert.Equal(["uhd-dv", "uhd-p5", "uhd-hdr", "uhd-hlg", "wide", "uhd", "hd", "hd2", "none"], sorted.Select(c => c.Id));
        Assert.True(Copies.Better(sorted[0], sorted[1]));
        Assert.False(Copies.Better(sorted[4], sorted[5]));
    }

    // A copy written is read back unchanged, and audioTracks stays even
    // when empty.
    [Fact]
    public void RoundTrip()
    {
        var c = new Copy("v1", 1000, "matroska", 3, "t",
            [new CopyAudio("a0", "aac") { FileIndex = 0, Default = true, Language = "fr-CA", Codecs = "mp4a.40.2", Profile = "LC" }])
        {
            StartMs = 0,
            Parts = [new CopyPart(1, 500), new CopyPart(2, 500)],
            Video = new VideoStream("hevc", 1920, 1080)
            {
                Codecs = "dvh1.08.06", Profile = "Main 10", Level = 4.1, SampleAspect = "32:27", Rotation = 90, BitDepth = 10,
                Fps = "24000/1001", Hdr = ["dv", "hdr10plus"], Dv = new VideoDv(8, 6, 1) { Bl = true, Rpu = true },
                Colour = new VideoColour("bt2020", "smpte2084", null, "tv"),
            },
            SubtitleTracks = [new CopySubtitle("s1", "hdmv_pgs_subtitle") { HearingImpaired = true }, new CopySubtitle("s2", "")],
            Chapters = [new CopyChapter(0, "One"), new CopyChapter(10)],
            Segments = [new CopySegment("intro", 0, 10)],
            Previews = true,
            Added = "2024-02-29",
        };
        var b = Copies.Write(c);
        var got = Copies.Read(b);
        Assert.NotNull(got);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(b), System.Text.Encoding.UTF8.GetString(Copies.Write(got)));
        Assert.Equal(4.1, got.Video!.Level);

        var bare = Copies.Write(new Copy("v1", 0, "matroska", 1, "t", []));
        Assert.Equal("""{"id":"v1","durationMs":0,"container":"matroska","size":1,"etag":"t","audioTracks":[]}""",
            System.Text.Encoding.UTF8.GetString(bare));
        Assert.NotNull(Copies.Read(bare));
    }

    // Bounds the vectors don't reach on their own.
    [Fact]
    public void Bounds()
    {
        static Copy? R(string extra) => Copies.Read(System.Text.Encoding.UTF8.GetBytes(
            """{"id":"v1","durationMs":1000,"container":"matroska","size":10,"etag":"t","audioTracks":[]""" + extra + "}"));

        Assert.Null(R(",\"added\":\"2023-02-29\"")?.Added);
        Assert.Equal("2024-02-29", R(",\"added\":\"2024-02-29\"")?.Added);
        Assert.Equal(1999, R(""","bitrateKbps":1999""")?.BitrateKbps);
        Assert.Equal(1, R(""","bitrateKbps":1""")?.BitrateKbps);
        Assert.Equal("24000/1001", R(""","video":{"codec":"x","width":1,"height":1,"fps":"24000/1001"}""")?.Video?.Fps);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"fps":"48000/2002"}""")?.Video?.Fps);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"fps":23.976}""")?.Video?.Fps);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"sampleAspect":"65536:1"}""")?.Video?.SampleAspect);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"rotation":45}""")?.Video?.Rotation);
        Assert.Equal("vvc", R(""","video":{"codec":"vvc","width":1,"height":1}""")?.Video?.Codec);
        Assert.Null(R(""","video":{"codec":"VVC","width":1,"height":1}""")?.Video);
        Assert.Null(Copies.Read("""{"id":"v1","durationMs":1000,"container":"Matroska","size":10,"etag":"t","audioTracks":[]}"""u8));
        Assert.Null(R(""","video":{"codec":"h264","width":1,"height":1,"hdr":["hdr10plus"],"dv":{"profile":8,"level":1,"compatibility":1}}""")?.Video?.Dv);
        Assert.Equal(new VideoDv(127, 63, 15), R(""","video":{"codec":"h264","width":1,"height":1,"hdr":["dv"],"dv":{"profile":127,"level":63,"compatibility":15,"el":1}}""")?.Video?.Dv);
        Assert.Empty(R(""","parts":[{"size":5,"durationMs":1},{"size":4,"durationMs":1}]""")!.Parts);
        Assert.Null(R(""","startMs":1001""")?.StartMs);
        Assert.Null(Copies.Read("""{"id":"v1","durationMs":1000,"container":"mkv","size":10,"etag":"t","audioTracks":null}"""u8));
    }
}
