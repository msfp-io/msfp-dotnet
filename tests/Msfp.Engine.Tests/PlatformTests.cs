using Msfp.Protocol;

namespace Msfp.Tests;

// What MSFP says for media servers other than Jellyfin (the spec's
// api/platforms.md; msfp-go's platforms_test.go): a copy of several parts,
// media servers' language codes.
public sealed class PlatformTests
{
    private const string Film = "movie/tmdb/949";

    // A copy of several files is never served as file (media section 1):
    // the host is told its parts, and a file start for it is refused.
    [Fact]
    public async Task SeveralPartsNeverFile()
    {
        var h = HostKit.FilmHost();
        h.EditCopy(Film, 0, c => c with { Parts = [new CopyPart(10, 2_700_000), new CopyPart(10, 2_700_000)] });
        h.Protocol = "file";
        using var s = await Sharing.StartAsync(h);
        // Asked as file: refused before the host is asked.
        Assert.Equal(MsfpError.DeliveryRefused, (await Assert.ThrowsAsync<MsfpException>(() =>
            s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, AudioLanguage = "en", Delivery = Playback.DeliveryFile }))).Code);
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
        string[] codes = ["eng", "fre"];
        for (var i = 0; i < codes.Length; i++)
        {
            var c = v.Copies![i];
            v.Copies[i] = c with
            {
                AudioTracks = [c.AudioTracks[0] with { Language = codes[i] }, new CopyAudio("a1", "aac") { Language = new[] { "fra", "und" }[i] }],
                SubtitleTracks = [new CopySubtitle("s0", "subrip") { Language = "ger" }, new CopySubtitle("s1", "subrip") { Language = "fre-ca" }],
            };
        }

        using var s = await Sharing.StartAsync(h);
        var kept = (VideoTitle)(await s.PeerTitlesAsync())[Film];
        Assert.Equal(["en", "fr"], kept.AudioLanguages());
        Assert.Equal(["de", "fr-CA"], kept.SubtitleLanguages());
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, AudioLanguage = "fr", SubtitleLanguage = "de" });
        // The first copy whose audio has the language: the English one's
        // second track, fra (playback section 2.6).
        Assert.Equal(Film + "#0", h.Starts()[0].Copy);
        Assert.Equal(("a1", "fr"), (pb.AudioTrack, pb.AudioTracks[1].Language));
        Assert.Equal("s0", pb.SubtitleTrack);
        Assert.Equal(("de", "fr-CA"), (pb.SubtitleTracks[0].Language, pb.SubtitleTracks[1].Language));
        var body = s.Exchanges("play")[0].ResponseBody;
        Assert.DoesNotContain("\"fre\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"und\"", body, StringComparison.Ordinal);
    }
}
