using System.Text;

namespace Msfp.Protocol.Tests;

/// <summary>wire/media_test.go: the ass cases of media.json, parts and bounds.</summary>
public class AssTests
{
    [Fact]
    public void AssVectors()
    {
        WebVttTests.RebuiltVectors("ass", Ass.TryRebuild);
        WebVttTests.RebuiltVectors("ass", Ass.TryRebuildWhole);
    }

    // A part's events: those that show during its stretch, from 10 seconds
    // early.
    [Fact]
    public void AssPart()
    {
        var track = Encoding.UTF8.GetBytes("[Script Info]\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,first\n" +
            "Dialogue: 0,0:01:55.00,0:02:05.00,Default,,0,0,0,,across\nDialogue: 0,0:05:00.00,0:05:01.00,Default,,0,0,0,,third\n");
        string[][] wants = [["first", "across"], ["across"], ["third"], []];
        for (var n = 0; n < wants.Length; n++)
        {
            Assert.True(Ass.TryPart(track, n, out var part), $"part {n} refused");
            var output = Encoding.UTF8.GetString(part);
            Assert.True(Ass.TryRebuild(part, out var again) && again.AsSpan().SequenceEqual(part), $"part {n} isn't in its rebuilt form: {output}");
            Assert.Equal(wants[n].Length, output.Split("\nDialogue: ").Length - 1);
            foreach (var w in wants[n])
            {
                Assert.Contains("," + w + "\n", output, StringComparison.Ordinal);
            }
        }

        Assert.False(Ass.TryPart("WEBVTT"u8, 0, out _), "a track that isn't ASS gave a part");
    }

    [Fact]
    public void WholeTrackBounds() =>
        Assert.False(Ass.TryRebuildWhole(Encoding.UTF8.GetBytes("[Script Info]\n" + new string(' ', WebVtt.MaxWholeText)), out _), "a whole track over 16 MiB");

    // Rebuilding settles (FuzzRebuildASS's seeds).
    [Theory]
    [InlineData("[Script Info]\nPlayResX: 1920\n[V4+ Styles]\nStyle: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1\n" +
        "[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\b1\\t(0,500,\\fs30)\\pos(1,2)}Hi{\\p1}m 0 0 l 1 1{\\p0}\n")]
    [InlineData("[Script Info]\r\n[V4 Styles]\r\nStyle: S,Arial,20,65535,255,0,0,0,0,1,2,2,10,10,10,10,0,1\r\n[Events]\r\nDialogue: Marked=0,0:00:01.00,0:00:02.00,S,,0,0,0,,{\\a5\\clip(1,m 0 0 l 4 4)}x\r\n[Fonts]\r\nfontname: a.ttf\r\n")]
    public void RebuildSettles(string input)
    {
        Assert.True(Ass.TryRebuild(Encoding.UTF8.GetBytes(input), out var out_));
        Assert.StartsWith("[Script Info]\nScriptType: v4.00+\n", Encoding.UTF8.GetString(out_), StringComparison.Ordinal);
        WebVttTests.Stable(Encoding.UTF8.GetBytes(input), Ass.TryRebuild);
    }

    [Fact]
    public void RebuildSettlesOnBadBytes() =>
        WebVttTests.Stable([.. "[Script Info]\n[Events]\nDialogue:,0:00:00.00,0:00:00.00,,,,,,,    "u8, 0xe7], Ass.TryRebuild);
}
