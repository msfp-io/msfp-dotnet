using System.Text;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>wire/video_test.go, and the cases of video.json.</summary>
public class VideoTests
{
    private static VideoTitle? Read(string s) => Video.ReadTitle(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void VideoVectors()
    {
        var libraries = TestVectors.Load("video.json")["libraries"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var cases = TestVectors.Cases("video.json", "titles");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var got = Read(c.Str("text"));
            if (got is not null && !libraries.Contains(got.Library))
            {
                got = null; // the caller's check: a library it asked for
            }

            if ((got is not null) != c.Valid())
            {
                failures.Add($"{c.Str("name")}: kept {got is not null}, want {c.Valid()}");
                continue;
            }

            if (got is null)
            {
                continue;
            }

            var have = JsonNode.Parse(Write(got));
            if (!JsonNode.DeepEquals(have, JsonNode.Parse(c.Str("output"))))
            {
                failures.Add($"{c.Str("name")}:\n    got  {have!.ToJsonString()}\n    want {c.Str("output")}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "video.json titles");
    }

    // A kept title as the vectors write it: absent members left out.
    internal static byte[] Write(VideoTitle t) => JsonOut.Object(o => o
        .String("key", t.Key).String("kind", t.Kind).String("library", t.Library)
        .Strings("audio", t.Audio.Count > 0 ? t.Audio : null)
        .Strings("subtitles", t.Subtitles.Count > 0 ? t.Subtitles : null)
        .Object("video", t.Video is { } p ? v => v.Integer("height", p.Height).Strings("hdr", p.Hdr.Count > 0 ? p.Hdr : null) : null)
        .Strings("with", t.With.Count > 0 ? t.With : null)
        .Object("ids", t.Ids.Count > 0 ? i => { foreach (var (k, v) in t.Ids) { i.String(k, v); } } : null));

    // Video title keys in their one canonical form (video section 4): any
    // other form of the same title is refused.
    [Fact]
    public void ParseVideoKey()
    {
        var valid = new Dictionary<string, VideoKey>
        {
            ["movie/tmdb/949"] = new("movie", 949),
            ["movie/tmdb/9999999999"] = new("movie", 9_999_999_999),
            ["series/tmdb/1399"] = new("series", 1399),
            ["series/tmdb/1399/s0"] = new("season", 1399),
            ["series/tmdb/1399/s9999"] = new("season", 1399, 9999),
            ["series/tmdb/1399/e63056"] = new("episode", 1399, EpisodeId: 63056),
            ["series/tmdb/1399/e1"] = new("episode", 1399, EpisodeId: 1),
            ["series/tmdb/1399/s1/e2"] = new("episode", 1399, 1, 2),
            ["series/tmdb/1399/s1/e0"] = new("episode", 1399, 1),
            ["series/tmdb/1399/s0/e99999"] = new("episode", 1399, 0, 99999),
            ["series/tmdb/1/e9999999999"] = new("episode", 1, EpisodeId: 9_999_999_999),
        };
        foreach (var (key, want) in valid)
        {
            Assert.True(VideoKey.TryParse(key, out var got), key);
            Assert.Equal(want, got);
            Assert.Equal(key, got.ToString());
        }

        string[] invalid =
        [
            "", "movie/tmdb/", "movie/tmdb/0", "movie/tmdb/0949", "movie/tmdb/10000000000", "movie/tmdb/-1",
            "movie/tmdb/949/s1", "movie/imdb/tt0133093", "series/tvdb/81189", "Movie/tmdb/949", "movie/TMDB/949",
            "season/tmdb/1399", "episode/tmdb/1399", "series/tmdb/1399/", "series/tmdb/1399/s01", "series/tmdb/1399/s10000",
            "series/tmdb/1399/e0", "series/tmdb/1399/e063056", "series/tmdb/1399/e12345678901",
            "series/tmdb/1399/s1/e01", "series/tmdb/1399/s1/e100000", "series/tmdb/1399/s1/e", "series/tmdb/1399/s/e1",
            "series/tmdb/1399/e63056/e1", "series/tmdb/1399/s1/e2/x", "series/tmdb/+1399", "series/tmdb/1399/s+1",
            "series/tmdb/1399/s1/s2", "movie/tmdb/949 ", "item/0123456789abcdef0123456789abcdef",
            "movie/tmdb/٩٤٩",
        ];
        var accepted = invalid.Where(k => VideoKey.TryParse(k, out _)).ToList();
        Assert.True(accepted.Count == 0, "accepted: " + string.Join(", ", accepted));
    }

    // What a receiver keeps of a title (catalog section 3.3, video section
    // 3.1): a bad member skipped, or counted as absent, not the whole title.
    [Fact]
    public void ReadVideoTitle()
    {
        Assert.Null(Read("""{"key":"movie/tmdb/949","kind":"series","library":"L"}"""));
        Assert.Null(Read("""{"key":"movie/tmdb/0949","kind":"movie","library":"L"}"""));
        foreach (var s in new[] { "\"x\"", """{"key":1,"kind":"movie","library":"L"}""", """{"key":"movie/tmdb/949","kind":"movie","library":3}""" })
        {
            Assert.Null(Read(s));
        }

        var tags = """["fr","FR","fr-CA","x","fr_CA",3,"aaaaaaaaaaaaa","de","en","es","it","nl","pt","ja","ko","zh","ru","sv"]""";
        var got = Read("{\"key\":\"series/tmdb/1399/e63056\",\"kind\":\"episode\",\"library\":\"L\",\"audio\":" + tags +
            ",\"subtitles\":\"fr\",\"with\":[\"series/tmdb/1399/e63057\",\"series/tmdb/1399/e63056\",\"series/tmdb/2/e1\"," +
            "\"series/tmdb/1399/s1/e3\",7,\"series/tmdb/1399/s1\"],\"ids\":{\"imdb\":\"tt1480055\",\"tvdb\":\"0123\",\"wikidata\":\"Q1\"}}");
        Assert.NotNull(got);
        Assert.Equal(["fr", "fr-CA", "de", "en", "es", "it", "nl", "pt", "ja", "ko", "zh", "ru"], got.Audio);
        Assert.Empty(got.Subtitles);
        Assert.Equal(["series/tmdb/1399/e63057", "series/tmdb/1399/s1/e3"], got.With);
        Assert.Equal(new Dictionary<string, string> { ["imdb"] = "tt1480055" }, got.Ids);

        // ids that isn't an object, with that isn't a list: absent.
        got = Read("""{"key":"movie/tmdb/949","kind":"movie","library":"L","ids":["tt0133093"],"with":"x","audio":["en"]}""");
        Assert.NotNull(got);
        Assert.Empty(got.Ids);
        Assert.Empty(got.With);
        Assert.Equal(["en"], got.Audio);

        // Seasons and series have no languages, seasons no ids, films no with.
        got = Read("""{"key":"series/tmdb/1399/s1","kind":"season","library":"L","audio":["en"],"ids":{"imdb":"tt0944947"}}""");
        Assert.True(got is { Audio.Count: 0, Ids.Count: 0 });
        got = Read("""{"key":"movie/tmdb/949","kind":"movie","library":"L","with":["series/tmdb/1399/e1"]}""");
        Assert.True(got is { With.Count: 0 });

        // hdr null is of the wrong type: the picture is absent.
        Assert.Null(Read("""{"key":"movie/tmdb/949","kind":"movie","library":"L","video":{"height":1080,"hdr":null}}""")!.Video);
        Assert.Equal(1080, Read("""{"key":"movie/tmdb/949","kind":"movie","library":"L","video":{"height":1080}}""")!.Video!.Height);

        // At most 16 in with.
        var with = Enumerable.Range(0, 20).Select(i => new VideoKey("episode", 1, EpisodeId: i + 2).ToString());
        Assert.Equal(Video.MaxWith, Video.CleanWith(new VideoKey("episode", 1, EpisodeId: 1), with).Count);
    }

    [Fact]
    public void CleanTags() =>
        Assert.Equal(["fr-CA", "es-419"], Video.CleanTags(["fr-CA", "fr-ca", "", "x", "<script>", "es-419", "toolongtagvalue"]));

    [Theory]
    [InlineData("imdb", "tt0133093", true)]
    [InlineData("imdb", "tt1234567890", true)]
    [InlineData("imdb", "tt012345", false)]
    [InlineData("imdb", "tt12345678901", false)]
    [InlineData("imdb", "nm0000206", false)]
    [InlineData("imdb", "TT0133093", false)]
    [InlineData("tvdb", "81189", true)]
    [InlineData("tvdb", "9999999999", true)]
    [InlineData("tvdb", "081189", false)]
    [InlineData("tvdb", "0", false)]
    [InlineData("tvdb", "12345678901", false)]
    [InlineData("tmdb", "949", false)]
    public void OtherId(string scheme, string value, bool ok) => Assert.Equal(ok, Video.OtherId(scheme, value));

    [Fact]
    public void StepHeight()
    {
        var cases = new Dictionary<int, int> { [0] = 0, [-5] = 0, [1] = 480, [479] = 480, [480] = 480, [719] = 480, [720] = 720, [1600] = 1440, [2160] = 2160, [3000] = 2160, [4320] = 4320, [9000] = 4320 };
        foreach (var (input, want) in cases)
        {
            Assert.Equal(want, Video.StepHeight(input));
        }
    }

    [Fact]
    public void PictureBetter()
    {
        var hd = new Picture(1080, []);
        var uhd = new Picture(2160, []);
        var uhdHdr = new Picture(2160, ["hdr10"]);
        Assert.True(uhd.Better(hd));
        Assert.True(uhdHdr.Better(uhd));
        Assert.False(uhd.Better(uhdHdr));
        Assert.Equal(["hdr10", "dv"], Video.CleanHdr(["dv", "x", "hdr10", "dv"]));
    }
}
