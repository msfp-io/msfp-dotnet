namespace Msfp.Protocol.Tests;

/// <summary>The copies tests of wire/video_test.go.</summary>
public class CopiesTests
{
    private static Copy Bare(string id, VideoStream? video = null) => new(id, 1, "mkv", 1, "t", []) { Video = video };

    private static VideoStream V(int w, int h, params string[] hdr) => new("hevc", w, h) { Hdr = hdr };

    // The order of copies (video section 3.7).
    [Fact]
    public void Order()
    {
        Copy[] copies =
        [
            Bare("none"),
            Bare("hd", V(1920, 1080)),
            Bare("wide", V(3840, 1600)), // counts as 2160
            Bare("uhd-dv", V(3840, 2160, "hdr10", "dv")),
            Bare("uhd", V(3840, 2160)),
            Bare("uhd-hdr", V(3840, 2160, "hdr10")),
            Bare("hd2", V(1920, 1080)),
        ];
        var sorted = Copies.Sorted(copies);
        Assert.Equal(["uhd-dv", "uhd-hdr", "wide", "uhd", "hd", "hd2", "none"], sorted.Select(c => c.Id));
        Assert.True(Copies.Better(sorted[0], sorted[1]));
        Assert.False(Copies.Better(sorted[4], sorted[5]));
    }

    // A copy written is read back unchanged, and audioTracks stays even
    // when empty.
    [Fact]
    public void RoundTrip()
    {
        var c = new Copy("v1", 1000, "mkv", 3, "t", [new CopyAudio("a0", "aac") { FileIndex = 0, Default = true, Language = "fr-CA" }])
        {
            StartMs = 0,
            Parts = [new CopyPart(1, 500), new CopyPart(2, 500)],
            Video = new VideoStream("h264", 1920, 1080)
            {
                Level = 4.1, Fps = 23.98, Hdr = ["hdr10", "dv"], Dv = new VideoDv(8, 6, 1),
                Colour = new VideoColour("bt2020", null, null, "tv"),
            },
            SubtitleTracks = [new CopySubtitle("s1", "pgs") { HearingImpaired = true }],
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

        var bare = Copies.Write(new Copy("v1", 0, "mkv", 1, "t", []));
        Assert.Equal("""{"id":"v1","durationMs":0,"container":"mkv","size":1,"etag":"t","audioTracks":[]}""",
            System.Text.Encoding.UTF8.GetString(bare));
        Assert.NotNull(Copies.Read(bare));
    }

    // Bounds the vectors don't reach on their own.
    [Fact]
    public void Bounds()
    {
        static Copy? R(string extra) => Copies.Read(System.Text.Encoding.UTF8.GetBytes(
            """{"id":"v1","durationMs":1000,"container":"mkv","size":10,"etag":"t","audioTracks":[]""" + extra + "}"));

        Assert.Null(R(",\"added\":\"2023-02-29\"")?.Added);
        Assert.Equal("2024-02-29", R(",\"added\":\"2024-02-29\"")?.Added);
        Assert.Equal(1500, R(""","bitrateKbps":1999""")?.BitrateKbps);
        Assert.Equal(23.98, R(""","video":{"codec":"x","width":1,"height":1,"fps":23.976}""")?.Video?.Fps);
        Assert.Equal(0.13, R(""","video":{"codec":"x","width":1,"height":1,"fps":0.125}""")?.Video?.Fps);
        Assert.Null(R(""","video":{"codec":"x","width":1,"height":1,"fps":0.004}""")?.Video?.Fps);
        Assert.Equal(Names.Other, R(""","video":{"codec":"x","width":1,"height":1}""")?.Video?.Codec);
        Assert.Null(R(""","video":{"codec":"h264","width":1,"height":1,"hdr":["hdr10"],"dv":{"profile":8,"level":1,"compatibility":1}}""")?.Video?.Dv);
        Assert.Empty(R(""","parts":[{"size":5,"durationMs":1},{"size":4,"durationMs":1}]""")!.Parts);
        Assert.Null(R(""","startMs":1001""")?.StartMs);
        Assert.Null(Copies.Read("""{"id":"v1","durationMs":1000,"container":"mkv","size":10,"etag":"t","audioTracks":null}"""u8));
    }
}
