using System.Text;

namespace Msfp.Protocol.Tests;

/// <summary>wire/webvtt_test.go, the webvtt cases of video.json, and TestWebVTTPart (music_test.go).</summary>
public class WebVttTests
{
    private static string? Rebuild(string s) =>
        WebVtt.TryRebuild(Encoding.UTF8.GetBytes(s), out var o) ? Encoding.UTF8.GetString(o) : null;

    internal delegate bool Rebuilder(ReadOnlySpan<byte> input, out byte[] output);

    // Rebuilt as the cases say, and stable: a rebuilt part rebuilds to
    // itself (but a script of 1,024 styles without Default, written with
    // 1,025).
    internal static void RebuiltVectors(string section, Rebuilder rebuild)
    {
        var cases = TestVectors.Cases("media/v0/video.json", section);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var input = c.Str("hex").Length > 0 ? c.Hex() : Encoding.UTF8.GetBytes(c.Str("text"));
            var ok = rebuild(input, out var output);
            var got = Encoding.UTF8.GetString(output);
            if (ok != c.Valid() || got != c.Str("output"))
            {
                failures.Add($"{c.Str("name")}: {ok} \"{got}\", want {c.Valid()} \"{c.Str("output")}\"");
                continue;
            }

            if (ok && got.Split("\nStyle: ").Length - 1 <= 1024 && (!rebuild(output, out var again) || !again.AsSpan().SequenceEqual(output)))
            {
                failures.Add($"{c.Str("name")}: rebuilt again as \"{Encoding.UTF8.GetString(again)}\"");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "video.json " + section);
    }

    [Fact]
    public void WebVttVectors()
    {
        RebuiltVectors("webvtt", WebVtt.TryRebuild);

        // A whole track takes the same rules.
        RebuiltVectors("webvtt", WebVtt.TryRebuildWhole);
    }

    [Fact]
    public void RebuildWebVtt()
    {
        var input = "\ufeffWEBVTT\r\n\r\nSTYLE\r\n::cue { background: url(http://198.51.100.1/x) }\r\n\r\nNOTE secret note\r\n\r\n" +
            "1\r\n00:00:01.000 --> 00:00:04.000 position:10% align:start\r\n<i>Hello</i> <script>alert(1)</script>\r\n\r\n" +
            "00:01:00.500 --> 00:01:02.000\r\n<c.yellow>World</c>\r\n";
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:04.000 position:10% align:start\n<i>Hello</i> alert(1)\n\n00:01:00.500 --> 00:01:02.000\n<c.yellow>World</c>\n", Rebuild(input));
        Assert.Null(Rebuild("<html>"));

        // Text that reads as markup in WebVTT is escaped, so it shows as is.
        Assert.Equal("WEBVTT\n\n00:01.000 --> 00:02.000\nx &lt; y &amp; z &gt; w,\n",
            Rebuild("WEBVTT\n\n00:01.000 --> 00:02.000\nx < y & z > w, &lt;i&gt;\n"));
    }

    // Character references as Go's html.UnescapeString reads them.
    [Theory]
    [InlineData("&amp;&lt;&gt;", "&<>")]
    [InlineData("&amp", "&")]
    [InlineData("&ampx", "&x")]
    [InlineData("&notit;", "¬it;")]
    [InlineData("&#65;&#x42;&#X43", "ABC")]
    [InlineData("&#x;", "\uFFFD")]
    [InlineData("&#;", "&#;")]
    [InlineData("&#0;&#xD800;&#x110000;", "\uFFFD\uFFFD\uFFFD")]
    [InlineData("&#x80;&#159;", "€Ÿ")]
    [InlineData("&NotEqualTilde;", "≂̸")]
    [InlineData("&nosuch; & &", "&nosuch; & &")]
    [InlineData("&Afr;", "\U0001D504")]
    public void Unescape(string input, string want) => Assert.Equal(want, WebVtt.UnescapeHtml(input));

    [Fact]
    public void WebVttPart()
    {
        var track = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nfirst\n\n00:01:55.000 --> 00:02:05.000\nacross\n\n" +
            "00:02:30.000 --> 00:02:31.000\n<b>second</b>\n\n00:05:00.000 --> 00:05:01.000\nthird\n";
        (int N, string[] Want)[] cases = [(0, ["first", "across"]), (1, ["across", "<b>second</b>"]), (2, ["third"]), (3, [])];
        foreach (var (n, want) in cases)
        {
            Assert.True(WebVtt.TryPart(Encoding.UTF8.GetBytes(track), n, out var part), $"part {n} refused");
            var output = Encoding.UTF8.GetString(part);
            Assert.True(WebVtt.TryRebuild(part, out var again) && Encoding.UTF8.GetString(again) == output,
                $"part {n} isn't in its rebuilt form: {output}");
            foreach (var w in want)
            {
                Assert.Contains("\n" + w + "\n", output, StringComparison.Ordinal);
            }

            Assert.Equal(want.Length, output.Split("-->").Length - 1);
        }

        Assert.False(WebVtt.TryPart("not vtt"u8, 0, out _));
    }

    // Rebuilding settles: a second rebuild gives what a third does
    // (FuzzRebuildWebVTT's seeds).
    [Theory]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000\n<b>Hi</b> &lt;i&gt;\n")]
    [InlineData("WEBVTT\r\n\r\n1\r\n00:00:01.000 --> 00:00:02.000 align:start\r\na < b &amp; c\r\n")]
    [InlineData("WEBVTT\n00:00.000 --> 00:00.000\n<</b>A")]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000 line:10% line:20% size:50% bogus align:left\n<i><b><c.red.bg_blue>x</i> <u>y</u>\n")]
    public void RebuildSettles(string input) => Stable(Encoding.UTF8.GetBytes(input), WebVtt.TryRebuild);

    internal static void Stable(byte[] input, Rebuilder rebuild)
    {
        if (!rebuild(input, out var out_))
        {
            return;
        }

        Assert.True(rebuild(out_, out var again), "rebuilt, then refused");
        Assert.True(rebuild(again, out var third) && third.AsSpan().SequenceEqual(again), Encoding.UTF8.GetString(again) + " then " + Encoding.UTF8.GetString(third));
    }

    // A whole track has bounds of its own: over a part's, within a whole
    // track's.
    [Fact]
    public void WholeTrackBounds()
    {
        var vtt = new StringBuilder("WEBVTT\n");
        for (var i = 0; i <= 50_000; i++)
        {
            vtt.Append("\n00:00:01.000 --> 00:00:02.000\n").Append(i).Append('\n');
        }

        var cues = Encoding.UTF8.GetBytes(vtt.ToString());
        Assert.False(WebVtt.TryRebuild(cues, out _), "a part of 50,001 cues");
        Assert.True(WebVtt.TryRebuildWhole(cues, out _), "a whole track of 50,001 cues refused");
        var big = Encoding.UTF8.GetBytes("WEBVTT\n" + new string(' ', WebVtt.MaxWebVtt));
        Assert.False(WebVtt.TryRebuild(big, out _), "a part over 5 MiB");
        Assert.True(WebVtt.TryRebuildWhole(big, out _), "a whole track of 5 MiB refused");
    }
}
