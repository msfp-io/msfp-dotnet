namespace Msfp.Protocol.Tests;

/// <summary>wire/playback_test.go.</summary>
public class PlaybackTests
{
    [Fact]
    public void TrackId()
    {
        foreach (var (id, want) in new[] { ("a0", 0), ("a1", 1), ("a10", 10), ("a999", 999) })
        {
            Assert.True(Playback.TryTrackId(id, 'a', out var n), id);
            Assert.Equal(want, n);
        }

        foreach (var id in new[] { "", "a", "a1000", "a+1", "s1", "A1", "a-1", "a1 ", "a01", "a00", "a007" })
        {
            Assert.False(Playback.TryTrackId(id, 'a', out _), $"{id} accepted");
        }
    }

    [Fact]
    public void PlayIdAndId()
    {
        Assert.True(Playback.PlayId("play_" + new string('a', 26)));
        Assert.True(Playback.PlayId("Pb7-x_" + new string('Z', 58)));
        Assert.False(Playback.PlayId(new string('a', 65)));
        Assert.False(Playback.PlayId("play_" + new string('a', 26) + "\n"));
        Assert.False(Playback.PlayId(""));
        Assert.True(Playback.Id("A-z_0"));
        Assert.False(Playback.Id(new string('a', 65)));
        Assert.False(Playback.Id("a\n"));
    }

    // Every media URL is built from the stream root, a ticket and a fixed
    // name (MSFP Media fetching 1).
    [Fact]
    public void Urls()
    {
        var root = Playback.StreamRoot("https://b.example.org");
        Assert.Equal("https://b.example.org/api/v1/stream/", root);
        Assert.Equal(root, Playback.StreamRoot("https://b.example.org/"));
        Assert.Equal(root + "T1/master.m3u8", MediaUrls.Stream(root, "T1", "hls"));
        Assert.Equal(root + "T1/file", MediaUrls.Stream(root, "T1", "file"));
        Assert.Equal(root + "T1/subtitles/s1", MediaUrls.Subtitle(root, "T1", new ServedSubtitle("s1", "text", true)));
        Assert.Equal(root + "T1/ass/s1", MediaUrls.Subtitle(root, "T1", new ServedSubtitle("s1", "text", true), ass: true));
        Assert.Equal(root + "T1/pictures/s2", MediaUrls.Subtitle(root, "T1", new ServedSubtitle("s2", "picture")));
        Assert.Equal(root + "T1/fonts/3", MediaUrls.Font(root, "T1", 3));
        Assert.Equal(root + "T1/trickplay/7", MediaUrls.Sheet(root, "T1", 7));
        Assert.Equal(root + "T1/queue/2/file", MediaUrls.Queue(root, "T1", 2, "file"));
        Assert.Equal(root + "T1/queue/0/master.m3u8", MediaUrls.Queue(root, "T1", 0, "hls"));
        Assert.Equal(root + "T1/image", MediaUrls.Image(root, "T1"));
    }

    // Video section 5.4's table: the forms known before play.
    [Fact]
    public void ServedForms()
    {
        Assert.Equal(new ServedSubtitle("s1", "text"), Video.FormOf("s1", "subrip"));
        Assert.Equal(new ServedSubtitle("s1", "text", true), Video.FormOf("s1", "ssa"));
        Assert.Equal(new ServedSubtitle("s1", "picture"), Video.FormOf("s1", "dvb_subtitle"));
        Assert.Null(Video.FormOf("s1", "dvb_teletext"));
        Assert.Null(Video.FormOf("s1", ""));
    }

    [Theory]
    [InlineData("video/mp4", "video/mp4")]
    [InlineData("Video/MP2T; charset=x", "video/mp2t")]
    [InlineData("audio/mp4+x.y-z", "audio/mp4+x.y-z")]
    [InlineData("audio/mpegurl", "application/octet-stream")]
    [InlineData("application/vnd.apple.mpegurl", "application/octet-stream")]
    [InlineData("text/html", "application/octet-stream")]
    [InlineData("video/", "application/octet-stream")]
    [InlineData("video/a_b", "application/octet-stream")]
    [InlineData("video/a b", "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    [InlineData("video/mp4/x", "application/octet-stream")]
    public void RelayedMediaType(string input, string want) => Assert.Equal(want, Playback.RelayedMediaType(input));
}
