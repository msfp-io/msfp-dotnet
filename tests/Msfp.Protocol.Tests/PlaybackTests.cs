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
        Assert.True(Playback.PlayId("play_abcdefghijklmnopqrstuvwxyz"[..5] + new string('a', 26)));
        Assert.False(Playback.PlayId("play_" + new string('a', 25)));
        Assert.False(Playback.PlayId("play_" + new string('a', 26) + "\n"));
        Assert.False(Playback.PlayId("play_" + new string('1', 26)));
        Assert.True(Playback.Id("A-z_0"));
        Assert.False(Playback.Id(new string('a', 65)));
        Assert.False(Playback.Id("a\n"));
    }

    // A play answer's stream URL is its stream root's, under one ticket,
    // with the name its protocol gives (media section 1).
    [Fact]
    public void StreamBase()
    {
        var root = Playback.StreamRoot("https://b.example.org");
        Assert.Equal("https://b.example.org/api/v1/stream/", root);
        Assert.Equal(root, Playback.StreamRoot("https://b.example.org/"));
        Assert.True(Playback.TryStreamBase(root + "T1/master.m3u8", root, "hls", out var b));
        Assert.Equal(root + "T1/", b);
        Assert.True(Playback.TryStreamBase(root + "T1/file", root, "file", out _));
        (string Url, string Protocol)[] bad =
        [
            (root + "T1/file", "hls"), (root + "T1/master.m3u8", "file"), (root + "T1/v0.m3u8", "hls"),
            (root + "T1/master.m3u8", "dash"), (root + "T1/master.m3u8?x", "hls"), (root + "T1/master.m3u8#x", "hls"),
            (root + "T%31/file", "file"), (root + "/file", "file"), (root + "T1//file", "file"), (root + "../T1/file", "file"),
            (root + "T1/x/file", "file"), ("https://c.example.org/api/v1/stream/T1/file", "file"),
            ("https://b.example.org/api/v1/stream//T1/file", "file"), (root + "T1", "file"),
        ];
        foreach (var (url, protocol) in bad)
        {
            Assert.False(Playback.TryStreamBase(url, root, protocol, out _), $"{url} ({protocol}) accepted");
        }

        Assert.Equal(b + "subtitles/s1", Playback.SubtitleUrl(b, "s1", false));
        Assert.Equal(b + "pictures/s2", Playback.SubtitleUrl(b, "s2", true));
        Assert.Equal(b + "trickplay/7", Playback.SheetUrl(b, 7));
    }

    [Fact]
    public void TrickplayBounds()
    {
        var ok = new Trickplay(320, 180, 10, 10, 10_000, 250, 3);
        Assert.True(ok.InBounds);
        Assert.Equal(3, ok.SheetCount);
        Trickplay[] bad =
        [
            ok with { Sheets = 2 }, ok with { TileWidth = 0 }, ok with { TileWidth = 410 }, ok with { Rows = 101 },
            ok with { IntervalMs = 99 }, ok with { IntervalMs = 3_600_001 }, ok with { TileCount = 0, Sheets = 0 },
            ok with { Columns = 1, Rows = 1, TileCount = 1001, Sheets = 1001 },
        ];
        foreach (var t in bad)
        {
            Assert.False(t.InBounds, $"{t} accepted");
        }
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
