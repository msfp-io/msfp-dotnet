using Msfp.Protocol;
using Msfp.Stores;

namespace Msfp.Tests;

// The table of MSFP video section 5.4, row by row (playback section 2.6's,
// of the copy and the audio, are Msfp.Protocol's: ChooseCopy, ChooseAudio);
// the choice hooks; ranges (msfp-go's choose_test.go).
public sealed class ChoiceTests
{
    private static readonly CopySubtitle[] Subtitles = [new("s0", "subrip") { Language = "en" }, new("s1", "subrip") { Language = "fr", Forced = true },
        new("s2", "subrip") { Language = "fr-CA" }, new("s3", "subrip") { Language = "de", Forced = true }];

    [Theory]
    [InlineData("off", "en", "")] // off: none, the language ignored
    [InlineData("s3", "en", "s3")] // a track of the copy
    [InlineData("s9", "en", "s0")] // not one: as if absent
    [InlineData("", "fr", "s2")] // same base language, a forced one last
    [InlineData("", "fr-CA", "s2")]
    [InlineData("", "de", "s3")] // only a forced one matches
    [InlineData("", "it", "")] // none matches: none shown
    [InlineData("", "", "")]
    public void ChooseSubtitles(string track, string language, string want) =>
        Assert.Equal(want, MsfpEngine.ChooseSubtitles(Subtitles, track, language));

    [Fact]
    public void ForcedFor()
    {
        Assert.Equal("s1", MsfpEngine.ForcedFor(Subtitles, "fr-CA"));
        Assert.Equal("", MsfpEngine.ForcedFor(Subtitles, "en"));
    }

    // The choice hooks (MSFP playback section 2.6): what the request names
    // wins; else the host's hook, the default's when it answers what the copy
    // hasn't.
    [Fact]
    public void ChoiceHooks()
    {
        Copy[] copies =
        [
            new("v0", 0, "", 0, "", [new CopyAudio("a0", "aac") { Language = "en" }]),
            new("v1", 0, "", 0, "", [new CopyAudio("a0", "aac") { Language = "fr" }, new CopyAudio("a1", "aac") { Language = "fr", Role = "karaoke" }])
            {
                SubtitleTracks = [new CopySubtitle("s0", "subrip") { Language = "en" }],
            },
        ];
        using var store = new MemoryStore();
        var cfg = new MsfpConfig { Address = "https://a.example", Store = store };
        // P17b: no language in the request since proposal 17.
        Assert.Equal(1, MsfpEngine.ChosenCopy(cfg, copies, new Protocol.PlayRequest())); // the default by language
        cfg = cfg with { ChooseCopy = (_, _) => 7 };
        Assert.Equal(0, MsfpEngine.ChosenCopy(cfg, copies, new Protocol.PlayRequest())); // a hook out of range
        Assert.Equal(1, MsfpEngine.ChosenCopy(cfg, copies, new Protocol.PlayRequest { VersionId = "v1" })); // versionId named
        cfg = cfg with { ChooseTracks = (_, _) => ("a1", "s9") };
        // The hook's audio, the default's subtitles.
        Assert.Equal(("a1", "s0"), MsfpEngine.ChosenTracks(cfg, copies[1], new Protocol.PlayRequest())); // P17b
        // Named tracks.
        Assert.Equal(("a0", ""), MsfpEngine.ChosenTracks(cfg, copies[1], new Protocol.PlayRequest { AudioTrack = "a0" })); // P17b
        // The default names and titles.
        Assert.Equal("", MsfpDefaults.CopyName("Film.2160p.WEB-DL-GRP.mkv"));
        Assert.Equal("4K HDR", MsfpDefaults.CopyName(" 4K  HDR "));
        Assert.Equal("", MsfpDefaults.TrackTitle("English"));
        Assert.Equal("Commentary 5.1", MsfpDefaults.TrackTitle("Commentary 5.1"));
    }

    [Theory]
    [InlineData("bytes=0-99", 0L, 99L, 0L)]
    [InlineData("bytes=5-", 5L, -1L, 0L)]
    [InlineData("bytes=-10", 0L, 0L, 10L)]
    [InlineData("bytes=000-009", 0L, 9L, 0L)]
    public void ParseRange(string header, long first, long last, long suffix)
    {
        Assert.True(ByteRange.TryParse(header, out var r));
        Assert.Equal(new ByteRange(first, last, suffix), r);
    }

    [Theory]
    [InlineData("bytes=0-1,5-9")]
    [InlineData("bytes=-")]
    [InlineData("bytes=5-1")]
    [InlineData("items=0-9")]
    [InlineData("bytes = 0-9")]
    [InlineData("bytes=0-1234567890123456")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=a-b")]
    [InlineData("bytes=1-2 ")]
    public void ParseRangeRefused(string header) => Assert.False(ByteRange.TryParse(header, out _));
}
