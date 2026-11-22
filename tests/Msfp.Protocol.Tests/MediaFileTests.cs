using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>wire/media_test.go: the ranges, fileAnswers and scans cases of media.json, and segment answers.</summary>
public class MediaFileTests
{
    private static long Long(JsonObject c, string name) => c[name]?.GetValue<long>() ?? 0;

    private static string? Header(JsonObject c, string name) => c[name]?.GetValue<string>();

    [Fact]
    public void RangeVectors()
    {
        var cases = TestVectors.Cases("media.json", "ranges");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            long start = 0, end = 0;
            var ok = Media.TryParseRange(c.Str("text"), out var r) && r.TrySpan(Long(c, "size"), out start, out end);
            if (!ok)
            {
                start = end = 0;
            }

            if (ok != c.Valid() || (ok && (start != Long(c, "start") || end != Long(c, "end"))))
            {
                failures.Add($"{c.Str("name")}: {ok} {start}-{end}, want {c.Valid()} {Long(c, "start")}-{Long(c, "end")}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "media.json ranges");
    }

    [Fact]
    public void FileAnswerVectors()
    {
        var cases = TestVectors.Cases("media.json", "fileAnswers");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var answer = new MediaAnswer((int)Long(c, "status"), Header(c, "etagHeader"), Header(c, "contentRange"), Header(c, "contentLength"));
            var why = Media.CheckFileAnswer(Header(c, "range"), Long(c, "size"), c.Str("etag"), answer);
            if ((why is null) != c.Valid())
            {
                failures.Add($"{c.Str("name")}: {why}, want valid {c.Valid()} ({c.Str("reason")})");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "media.json fileAnswers");
    }

    [Fact]
    public void ScanVectors()
    {
        var cases = TestVectors.Cases("media.json", "scans");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            long sent = Long(c, "sent"), size = Long(c, "size");
            var cap = Playback.ScanCapOf(Long(c, "scanBytes"));
            var (status, cr) = (416, Media.UnsatisfiedRange(size));
            if (Media.TryScanSpan(sent, Header(c, "range"), size, cap, out var start, out var end))
            {
                (status, cr) = (206, Media.ContentRange(start, end, size));
                if (sent + end - start + 1 > cap)
                {
                    failures.Add($"{c.Str("name")}: past the cap");
                }
            }

            if (status != Long(c, "status") || cr != c.Str("contentRange"))
            {
                failures.Add($"{c.Str("name")}: {status} \"{cr}\", want {Long(c, "status")} \"{c.Str("contentRange")}\"");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "media.json scans");
    }

    // A segment's 206 may give "*" as its total, and a 200 to a Range is
    // the whole segment.
    [Theory]
    [InlineData("", 200, null, null, true)]
    [InlineData("bytes=0-99", 200, null, null, true)]
    [InlineData("bytes=0-99", 206, "bytes 0-99/*", "100", true)]
    [InlineData("bytes=0-99", 206, "bytes 0-99/1000", null, true)]
    [InlineData("bytes=-100", 206, "bytes 900-999/*", null, false)]
    [InlineData("bytes=-100", 206, "bytes 900-999/1000", null, true)]
    [InlineData("bytes=0-99", 206, "bytes 0-100/*", null, false)]
    [InlineData("", 206, "bytes 0-99/*", null, false)]
    [InlineData("bytes=0-99", 416, null, null, false)]
    public void SegmentAnswer(string range, int status, string? contentRange, string? contentLength, bool ok) =>
        Assert.Equal(ok, Media.CheckSegmentAnswer(range, new MediaAnswer(status, null, contentRange, contentLength)) is null);
}
