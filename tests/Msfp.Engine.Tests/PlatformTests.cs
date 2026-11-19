namespace Msfp.Tests;

// What MSFP says for media servers other than Jellyfin (the spec's
// api/platforms.md; msfp-go's platforms_test.go): a copy of several parts,
// media servers' language codes, a marker whose end isn't known.
public sealed class PlatformTests
{
    private const string Film = "movie/tmdb/949";

    // A copy of several files is never served as file (media section 1):
    // the host is told its parts, and a file start for it is refused.
    [Fact]
    public async Task SeveralPartsNeverFile()
    {
        var h = HostKit.FilmHost();
        h.Info["movie/tmdb/949#0"].Parts = 2;
        h.Protocol = "file";
        using var s = await Sharing.StartAsync(h);
        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, AudioLanguage = "en" }));
        Assert.Equal(2, Assert.Single(h.Starts()).Parts);
        await s.A.WaitAsync();
        Assert.Equal(EndReason.Stopped, h.Stops()["h1"]);
        Assert.Empty(s.A.Engine.Playbacks());
        // As hls, it plays; a copy of one file still plays as file.
        h.Protocol = "hls";
        Assert.Equal("hls", (await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, AudioLanguage = "en" })).Protocol);
        h.Protocol = "file";
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, AudioLanguage = "fr" });
        Assert.Equal("file", pb.Protocol);
        Assert.Equal(1, h.Starts()[^1].Parts);
    }

    // A media server's ISO 639-2 codes go out as ISO 639-1 tags (catalog
    // section 3), in titles, tracks and the choice of the copy.
    [Fact]
    public async Task MediaServerLanguageCodes()
    {
        var h = HostKit.FilmHost();
        var v = (VideoTitle)h.Titles[Film];
        v.Audio = ["eng", "fre", "fra", "und"];
        v.Subtitles = ["ger", "fre-ca"];
        string[] codes = ["eng", "fre"];
        for (var i = 0; i < codes.Length; i++)
        {
            var copy = h.Copies[Film][i];
            copy.Audio = [codes[i]];
            var info = h.Info[copy.Handle];
            info.AudioTracks = [new Track { Id = "a0", Label = "Audio", Language = codes[i], Default = true }];
            info.SubtitleTracks = [new Track { Id = "s0", Label = "Deutsch", Language = "GER" }, new Track { Id = "s1", Label = "?", Language = "und" }];
        }

        using var s = await Sharing.StartAsync(h);
        var kept = (VideoTitle)(await s.PeerTitlesAsync())[Film];
        Assert.Equal(["en", "fr"], kept.Audio!);
        Assert.Equal(["de", "fr-CA"], kept.Subtitles!);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, AudioLanguage = "fr", SubtitleLanguage = "de" });
        Assert.Equal(Film + "#1", h.Starts()[0].Copy);
        Assert.Equal("fr", pb.AudioTracks[0].Language);
        Assert.Equal("s0", pb.SubtitleTrack);
        Assert.Equal("de", pb.SubtitleTracks[0].Language);
        Assert.True(string.IsNullOrEmpty(pb.SubtitleTracks[1].Language));
        var body = s.Exchanges("play")[0].ResponseBody;
        Assert.DoesNotContain("\"fre\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"und\"", body, StringComparison.Ordinal);
    }

    // A marker whose end the host doesn't know ends at the copy's length
    // (video section 5.3).
    [Fact]
    public async Task MarkerToTheEnd()
    {
        var h = HostKit.FilmHost();
        h.Segments = [new Segment("intro", 0, 60_000), new Segment("outro", 5_300_000, 0)];
        using var s = await Sharing.StartAsync(h);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        Assert.Equal([new Segment("intro", 0, 60_000), new Segment("outro", 5_300_000, 5_400_000)], pb.Segments);
    }
}
