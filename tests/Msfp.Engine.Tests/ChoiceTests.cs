using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

// The tables of MSFP playback section 2.6 and video section 5.4, row by
// row; what a device is said to play; ranges (msfp-go's choose_test.go).
public sealed class ChoiceTests
{
    [Theory]
    [InlineData("v-fr", "en", "fr")] // a version of the title: that copy, whatever the language
    [InlineData("v-none", "fr", "fr")] // no copy has it: by language, same tag first
    [InlineData("", "fr-CA", "frca")] // same tag
    [InlineData("", "fr-FR", "frca")] // then same base language: the first
    [InlineData("", "de", "en")] // none matches: the first
    [InlineData("", "", "en")] // nothing asked: the first
    [InlineData("v-none", "", "en")] // a version no copy has, no language
    [InlineData("", "FR-ca", "frca")] // tags compared without case
    [InlineData("v-frca", "", "frca")] // a version alone
    [InlineData("V-fr", "", "en")] // version IDs compared exactly
    public void ChooseCopy(string version, string language, string want)
    {
        Copy[] copies = [new() { Handle = "en", Audio = ["en"] }, new() { Handle = "frca", Audio = ["fr-CA"] }, new() { Handle = "fr", Audio = ["fr"] }];
        Assert.Equal(want, copies[MsfpEngine.ChooseCopy(copies, c => "v-" + c.Handle, version, language)].Handle);
    }

    [Theory]
    [InlineData("a3", "en", "a3")] // a track of the copy: that one, the language ignored
    [InlineData("a9", "en", "a0")] // not a track of the copy: as if absent
    [InlineData("", "fr", "a3")] // same tag, never a commentary
    [InlineData("", "fr-FR", "a2")] // same base language: the first of the title's own sound
    [InlineData("", "de", "a2")] // none matches: the default
    [InlineData("", "", "a2")]
    public void ChooseAudio(string track, string language, string want)
    {
        Track[] tracks = [new() { Id = "a0", Language = "en" }, new() { Id = "a1", Language = "fr", Role = "commentary" },
            new() { Id = "a2", Language = "fr-CA", Default = true }, new() { Id = "a3", Language = "fr" }];
        Assert.Equal(want, MsfpEngine.ChooseAudio(tracks, track, language));
    }

    [Fact]
    public void ChooseAudioWithoutDefault() =>
        Assert.Equal("a0", MsfpEngine.ChooseAudio([new Track { Id = "a0" }, new Track { Id = "a1" }], "", "de"));

    private static readonly Track[] Subtitles = [new() { Id = "s0", Language = "en" }, new() { Id = "s1", Language = "fr", Forced = true },
        new() { Id = "s2", Language = "fr-CA" }, new() { Id = "s3", Language = "de", Forced = true }];

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

    private static JsonObject? Coarsened(Capabilities c) =>
        MsfpEngine.Coarsen(c, true) is { } fill ? JsonNode.Parse(JsonOut.Object(fill))!.AsObject() : null;

    [Fact]
    public void Coarsen()
    {
        var c = Coarsened(new Capabilities { Containers = ["MP4", "mkv", "mp4", "avi"], VideoCodecs = ["h264", "H265"], MaxAudioChannels = 7, MaxHeight = 1079, MaxBitrateKbps = 8999 })!;
        Assert.Equal("{\"containers\":[\"mkv\",\"mp4\"],\"maxAudioChannels\":6,\"maxBitrateKbps\":8000,\"videoCodecs\":[\"h264\"],\"maxHeight\":720}", c.ToJsonString());
        var low = Coarsened(new Capabilities { MaxAudioChannels = 1, MaxHeight = 100, MaxBitrateKbps = 999 })!;
        Assert.Equal("{\"maxAudioChannels\":2,\"maxHeight\":480}", low.ToJsonString());
        Assert.Null(Coarsened(new Capabilities()));
        Assert.Equal((1000, 999, 0), (MsfpEngine.RoundKbps(1999), MsfpEngine.RoundKbps(999), MsfpEngine.RoundKbps(0)));
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
