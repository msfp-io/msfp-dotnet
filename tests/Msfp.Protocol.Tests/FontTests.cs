namespace Msfp.Protocol.Tests;

/// <summary>wire/media_test.go: the fonts cases of video.json (MSFP Media).</summary>
public class FontTests
{
    [Fact]
    public void FontVectors()
    {
        var cases = TestVectors.Cases("media/v0/video.json", "fonts");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var ok = Font.TryCheck(c.Hex(), out var type, out var reason);
            if (ok != c.Valid() || type != c.Str("type") || !MediaTests.SameStep(ok ? null : reason, c.Str("reason")))
            {
                failures.Add($"{c.Str("name")}: \"{type}\" {reason}, want valid {c.Valid()} \"{c.Str("type")}\" ({c.Str("reason")})");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "video.json fonts");
    }

    // A font is accepted only with its signature and tables within the
    // file (FuzzCheckFont's seeds).
    [Fact]
    public void CheckFont()
    {
        var font = new List<byte>("\x00\x01\x00\x00\x00\x09\x00\x00\x00\x00\x00\x00"u8.ToArray());
        foreach (var tag in new[] { "cmap", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "name", "post" })
        {
            font.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            font.AddRange([0, 0, 0, 0, 0, 0, 0, 0x9c, 0, 0, 0, 4]);
        }

        font.AddRange([0, 0, 0, 0]);
        Assert.True(Font.TryCheck(font.ToArray(), out var type, out _));
        Assert.Equal(Font.Ttf, type);
        Assert.False(Font.TryCheck("OTTO\x00\x01"u8, out type, out var reason));
        Assert.Equal("", type);
        Assert.StartsWith("step 3:", reason, StringComparison.Ordinal);
        Assert.False(Font.TryCheck("ttcf"u8, out _, out reason));
        Assert.StartsWith("step 2:", reason, StringComparison.Ordinal);
        Assert.False(Font.TryCheck(new byte[Font.MaxFont + 1], out _, out reason));
        Assert.StartsWith("step 1:", reason, StringComparison.Ordinal);
    }
}
