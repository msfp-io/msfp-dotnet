using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>wire/music_test.go: the cases of music.json, and the music part's own checks.</summary>
public class MusicTests
{
    [Fact]
    public void MusicKeyVectors()
    {
        var cases = TestVectors.Cases("music.json", "keys");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var ok = MusicKey.TryParse(c.Str("text"), out var k);
            if (ok != c.Valid() || (ok && (k.Kind != c.Str("kind") || k.ToString() != c.Str("text"))))
            {
                failures.Add($"{c.Str("name")}: {k} {ok}, want {c.Valid()} {c.Str("kind")}");
            }

            if (TitleKey.TryParse(c.Str("text"), out var tk) && c.Valid() && (tk.Part != "music" || tk.Playable != (c.Str("kind") == "track")))
            {
                failures.Add($"{c.Str("name")}: TitleKey {tk}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "music.json keys");
    }

    [Theory]
    [InlineData("titles")]
    [InlineData("localTitles")]
    public void MusicTitleVectors(string section)
    {
        var libraries = TestVectors.Load("music.json")["libraries"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var cases = TestVectors.Cases("music.json", section);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var got = Music.ReadTitle(Encoding.UTF8.GetBytes(c.Str("text")));
            if (got is not null && !libraries.Contains(got.Library))
            {
                got = null; // the caller's check: a library it asked for
            }

            if ((got is not null) != c.Valid())
            {
                failures.Add($"{c.Str("name")}: kept {got is not null}, want {c.Valid()}");
                continue;
            }

            if (got is not null && Differs(JsonNode.Parse(got.ToJson()), c.Str("output")) is { } diff)
            {
                failures.Add($"{c.Str("name")}:{diff}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "music.json " + section);
    }

    [Fact]
    public void MusicQueueVectors()
    {
        var cases = TestVectors.Cases("music.json", "queue");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var answer = c.Str("kind") == "answer";
            var request = answer ? c.Str("request") : c.Str("text");
            IReadOnlyList<QueueAsk> asked = [];
            var why = "";
            try
            {
                var req = JsonNode.Parse(request)!.AsObject();
                asked = Music.CheckQueue(req);
            }
            catch (PlayRefusal e)
            {
                why = e.Status + " " + e.Problem;
            }

            var ok = why.Length == 0;
            if (!answer)
            {
                var wantWhy = c.Str("reason");
                if (ok != c.Valid() || (!ok && !wantWhy.StartsWith(why.StartsWith("400", StringComparison.Ordinal) ? "400" : why,
                        StringComparison.Ordinal)))
                {
                    failures.Add($"{c.Str("name")}: accepted {ok} ({why}), want {c.Valid()} ({wantWhy})");
                }

                continue;
            }

            if (!ok)
            {
                failures.Add($"{c.Str("name")}: the request refused ({why})");
                continue;
            }

            var a = StrictJson.ReadAnswer(Encoding.UTF8.GetBytes(c.Str("text")), keepNestedNulls: true);
            var kept = Music.ReadQueue(a["queue"], asked);
            var have = JsonNode.Parse(JsonOut.Object(o => o.Objects("q", kept, (w, e) => e.Write(w))))!["q"];
            if (Differs(have, c.Str("output")) is { } diff)
            {
                failures.Add($"{c.Str("name")}:{diff}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "music.json queue");
    }

    [Fact]
    public void MusicLyricsVectors()
    {
        var cases = TestVectors.Cases("music.json", "lyrics");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var body = Encoding.UTF8.GetBytes(c.Str("text"));
            if (c.Str("kind") == "request")
            {
                var ok = Music.TryReadLyricsRequest(body, out var r);
                if (ok != c.Valid() || (ok && (r!.Title.Length == 0 || r.VersionId.Length == 0)))
                {
                    failures.Add($"{c.Str("name")}: read {ok} {r}, want {c.Valid()}");
                }

                continue;
            }

            var sets = Music.ReadLyrics(body);
            if ((sets is not null) != c.Valid())
            {
                failures.Add($"{c.Str("name")}: kept {sets is not null}, want {c.Valid()}");
            }
            else if (sets is not null && Differs(JsonNode.Parse(Music.WriteLyrics(sets)), c.Str("output")) is { } diff)
            {
                failures.Add($"{c.Str("name")}:{diff}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "music.json lyrics");
    }

    // Null when have is the same JSON value as want; else both, to show.
    private static string? Differs(JsonNode? have, string want) =>
        JsonNode.DeepEquals(have, JsonNode.Parse(want)) ? null : $"\n    got  {have?.ToJsonString()}\n    want {want}";

    [Fact]
    public void MusicDates()
    {
        var cases = new Dictionary<string, int>
        {
            ["1970"] = 1970, ["1970-01"] = 1970, ["2024-02-29"] = 2024, ["0999"] = 0,
            ["2023-02-29"] = 0, ["1970-13"] = 0, ["1970-1-1"] = 0, ["1970-00"] = 0, ["19700"] = 0, [""] = 0,
        };
        foreach (var (s, want) in cases)
        {
            var ok = Music.TryMusicDate(s, out var y);
            Assert.True(y == want && ok == (want > 0), $"TryMusicDate({s}) = {y} {ok}, want {want}");
        }

        Assert.Equal(0, Music.R128Gain(-1280));
    }

    // An owning server's own title, written with ToJson, reads back as it was.
    [Fact]
    public void MusicTitleRoundTrip()
    {
        var sent = new MusicTitle
        {
            Key = "track/local/T1", Kind = "track", Library = "m1", Name = "x",
            Artists = ["artist/local/A1"], Album = "album/mb/0b000000-0000-4000-8000-000000000002", Disc = 1, Number = 2,
            Copies =
            [
                new MusicCopy
                {
                    Id = "c1", DurationMs = 1000, MediaType = "audio/flac", Codec = "flac", Size = 10, Etag = "e1",
                    ReplayGain = new ReplayGain(-4.12, null, null, null), Lyrics = "plain",
                },
            ],
        };
        var got = Music.ReadTitle(sent.ToJson());
        Assert.NotNull(got);
        Assert.Equal(Encoding.UTF8.GetString(sent.ToJson()), Encoding.UTF8.GetString(got.ToJson()));

        var album = new MusicTitle { Key = "album/local/A2", Kind = "album", Library = "m1", Name = "y", Compilation = true, Image = true };
        var back = Music.ReadTitle(album.ToJson());
        Assert.NotNull(back);
        Assert.Equal(Encoding.UTF8.GetString(album.ToJson()), Encoding.UTF8.GetString(back.ToJson()));
    }

    [Fact]
    public void QueueChecks()
    {
        Assert.Empty(Music.CheckQueue(JsonNode.Parse("""{"title":"track/local/T1"}""")!.AsObject()));
        Assert.Equal(400, Assert.Throws<PlayRefusal>(() => Music.CheckQueue(JsonNode.Parse("""{"title":"album/local/A1"}""")!.AsObject())).Status);
        var q = Music.CheckQueue(JsonNode.Parse(
            """{"title":"track/local/T1","delivery":"file","queue":[{"title":"track/local/T2"},{"title":"track/local/T3","delivery":"transcode"}]}""")!.AsObject());
        Assert.Equal([new QueueAsk("track/local/T2", "", "file", false), new QueueAsk("track/local/T3", "", "transcode", true)], q);
        Assert.Equal(("direct", "file"), (q[0].Method, q[0].Protocol));
        Assert.Equal(("transcode", "hls"), (q[1].Method, q[1].Protocol));
    }

    [Fact]
    public void CleanText()
    {
        Assert.Equal("a b c", Music.CleanText("  a \t\n b\u2028c\u200b  ", 100));
        Assert.Equal("ab", Music.CleanText("ab c", 3));
        Assert.Equal("\U0001F600\U0001F600", Music.CleanText("\U0001F600\U0001F600\U0001F600", 2));
    }
}
