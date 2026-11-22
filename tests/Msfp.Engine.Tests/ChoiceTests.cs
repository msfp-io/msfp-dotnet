using Msfp.Protocol;

namespace Msfp.Tests;

// The table of MSFP video section 5.4, row by row (playback section 2.6's,
// of the copy and the audio, are Msfp.Protocol's: ChooseCopy, ChooseAudio);
// what a device is said to play; ranges (msfp-go's choose_test.go).
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

    [Fact]
    public void Coarsen()
    {
        var c = MsfpEngine.Coarsen(new PlayCapabilities { Containers = ["MP4", "mkv", "mp4", "avi"], VideoCodecs = ["h264", "H265"], MaxAudioChannels = 7, MaxHeight = 1079, MaxBitrateKbps = 8999 }, true)!;
        Assert.Equal(["avi", "mkv", "mp4"], c.Containers);
        Assert.Equal((1, 6, 720, 8000), (c.VideoCodecs.Count, c.MaxAudioChannels, c.MaxHeight, c.MaxBitrateKbps));
        var low = MsfpEngine.Coarsen(new PlayCapabilities { MaxAudioChannels = 1, MaxHeight = 100, MaxBitrateKbps = 999 }, true)!;
        Assert.Equal((2, 480, 0), (low.MaxAudioChannels, low.MaxHeight, low.MaxBitrateKbps));
        Assert.Null(MsfpEngine.Coarsen(new PlayCapabilities(), true));
        // A track's, with video's members only: nothing said.
        Assert.Null(MsfpEngine.Coarsen(new PlayCapabilities { VideoCodecs = ["h264"], MaxHeight = 1080 }, false));
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
