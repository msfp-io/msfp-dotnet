using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msfp.Protocol.Tests;

/// <summary>wire/video_test.go, and the cases of video.json.</summary>
public class VideoTests
{
    // A copies member, after a comma.
    private const string OneCopy =
        ""","copies":[{"id":"v1","durationMs":1000,"mediaType":"video/matroska","size":1,"etag":"t","audioTracks":[]}]""";

    private static VideoTitle? Read(string s) => Video.ReadTitle(Encoding.UTF8.GetBytes(s));

    // Every list of video.json.
    [Fact]
    public void VideoVectors()
    {
        var file = TestVectors.Load("video.json");
        var libraries = file["libraries"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var root = file["streamRoot"]!.GetValue<string>();
        var failures = new List<string>();
        var total = 0;

        void Check(string list, JsonObject c, byte[]? got, bool ok)
        {
            total++;
            if (ok != c.Valid())
            {
                failures.Add($"{list} {c.Str("name")}: kept {ok}, want {c.Valid()} ({c.Str("reason")})");
                return;
            }

            if (!ok || got is null || c["output"] is null)
            {
                return;
            }

            // False members are left out of both sides: the writer drops a
            // flag false, but keeps dv's el, bl and rpu when false.
            var have = NoFalse(JsonNode.Parse(got));
            if (!JsonNode.DeepEquals(have, NoFalse(JsonNode.Parse(c.Str("output")))))
            {
                failures.Add($"{list} {c.Str("name")}:\n    got  {have!.ToJsonString()}\n    want {c.Str("output")}");
            }
        }

        foreach (var list in new[] { "titles", "extras", "localTitles" })
        {
            foreach (var c in TestVectors.Cases("video.json", list))
            {
                var got = Read(c.Str("text"));
                if (got is not null && !libraries.Contains(got.Library))
                {
                    got = null; // the caller's check: a library it asked for
                }

                Check(list, c, got is null ? null : Write(got), got is not null);
            }
        }

        foreach (var c in TestVectors.Cases("video.json", "copies"))
        {
            // Six cases repeat a member (video's codecs), which I-JSON
            // refuses: they are read with the last value, as the generator
            // reads them.
            var raw = Encoding.UTF8.GetBytes(c.Str("text"));
            var got = Copies.Read(raw) ?? (c.Valid() ? Copies.Read(LastWins(JsonDocument.Parse(raw).RootElement)) : null);
            Check("copies", c, got is null ? null : Copies.Write(got), got is not null);
        }

        foreach (var list in new[] { "localKeys", "tvdbKeys" })
        {
            foreach (var c in TestVectors.Cases("video.json", list))
            {
                var ok = VideoKey.TryParse(c.Str("text"), out var k);
                Check(list, c, null, ok);
                if (ok && (k.Kind != c.Str("kind") || k.ToString() != c.Str("text")))
                {
                    failures.Add($"{list} {c.Str("name")}: kind {k.Kind}, {k}");
                }
            }
        }

        foreach (var c in TestVectors.Cases("video.json", "previews"))
        {
            var body = Encoding.UTF8.GetBytes(c.Str("text"));
            if (c.Str("side") == "request")
            {
                Check("previews", c, null, Throws(() => Video.ReadPreviewsRequest(body)) is null);
                continue;
            }

            PreviewsAnswer? p = null;
            var error = Throws(() => p = Video.ReadPreviewsAnswer(body));
            Check("previews", c, p is null ? null : Video.WritePreviewsAnswer(p), error is null);
        }

        foreach (var c in TestVectors.Cases("video.json", "codecs"))
        {
            var o = JsonNode.Parse(c.Str("text"))!.AsObject();
            CodecsFacts? f = null;
            if (Names.Token(o["codec"]) is { } codec)
            {
                var dv = o["dv"] is JsonObject d && JsonMembers.AsInteger(d["profile"]) is { } pr && JsonMembers.AsInteger(d["level"]) is { } lv
                    ? new VideoDv((int)pr, (int)lv, (int)(JsonMembers.AsInteger(d["compatibility"]) ?? 0))
                    : null;
                var depth = JsonMembers.AsInteger(o["bitDepth"]) is { } bd and >= 1 and <= 32 ? (int?)bd : null;
                f = Rfc6381.Read(codec, JsonMembers.AsString(o["codecs"]), depth, dv, out _);
            }

            Check("codecs", c, f is null ? null : JsonOut.Object(w => w.String("codec", f.Codec).String("codecs", f.Codecs)
                .Integer("profile", f.Profile).String("tier", f.Tier).Integer("level", f.Level).Integer("bitDepth", f.BitDepth)
                .String("chroma", f.Chroma).String("constraints", f.Constraints).String("mp4a", f.Mp4a)
                .String("dolbyVision", f.DolbyVision)), f is not null);
        }

        var answerCopy = Copies.Read(Encoding.UTF8.GetBytes(file["answerCopy"]!.GetValue<string>()))!;
        foreach (var c in TestVectors.Cases("video.json", "answers"))
        {
            var o = JsonNode.Parse(c.Str("text")) as JsonObject;
            var ticket = o is null ? null : JsonMembers.AsString(o["ticket"]);
            byte[]? output = null;
            if (o is not null && ticket is not null && Playback.Id(ticket))
            {
                var served = Video.ReadServed(o["subtitles"], answerCopy.SubtitleTracks);
                var fonts = Video.ReadFontCount(o["fontCount"]);
                var a = new PlayAnswer { Ticket = ticket, Protocol = Playback.ProtocolHls, Subtitles = served, FontCount = fonts };
                var urls = a.Urls(root).Skip(1).ToList(); // the stream's is playback.json's
                for (var n = 0; n < (answerCopy.PreviewLayout?.Sheets ?? 0); n++)
                {
                    urls.Add(MediaUrls.Sheet(root, ticket, n));
                }

                output = JsonOut.Object(w => w
                    .Objects("subtitles", served.Count > 0 ? served : null, (e, s) => e.String("id", s.Id).String("form", s.Form).Bool("ass", s.Ass ? true : null))
                    .Integer("fontCount", fonts).Strings("urls", urls));
            }

            Check("answers", c, output, output is not null);
        }

        foreach (var c in TestVectors.Cases("video.json", "fontSets"))
        {
            var sizes = (JsonNode.Parse(c.Str("text")) as JsonArray)?.Select(e => JsonMembers.AsInteger(e) ?? 0).ToList() ?? [0];
            var ok = Font.CheckSet(sizes, out _);
            var output = JsonOut.Object(o => o.Integer("fonts", sizes.Count).Integer("bytes", sizes.Sum()));
            Check("fontSets", c, ok ? output : null, ok);
        }

        TestVectors.NoFailures(failures, total, "video.json");
    }

    private static ProtocolException? Throws(Action a)
    {
        try
        {
            a();
            return null;
        }
        catch (ProtocolException e)
        {
            return e;
        }
    }

    // A kept title as the vectors write it: absent members left out.
    internal static byte[] Write(VideoTitle t) => JsonOut.Object(o => o
        .String("key", t.Key).String("library", t.Library)
        .String("extraType", t.ExtraType.Length > 0 ? t.ExtraType : null)
        .String("name", t.Description?.Name)
        .Integer("year", t.Description is { Year: > 0 } d ? d.Year : null)
        .Bool("image", t.Description is { Image: true } ? true : null)
        .Object("place", t.Place is { } pl ? o => o.Integer("season", pl.Season).Integer("episode", pl.Episode) : null)
        .Strings("with", t.With.Count > 0 ? t.With : null)
        .Object("ids", t.Ids.Count > 0 ? i => { foreach (var (k, v) in t.Ids) { i.String(k, v); } } : null)
        .Objects("copies", t.Copies.Count > 0 ? t.Copies : null, Copies.Write));

    // A JSON value whose repeated members keep their last value.
    private static JsonNode? LastWins(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject())
                {
                    o[p.Name] = LastWins(p.Value);
                }

                return o;
            case JsonValueKind.Array:
                return new JsonArray(e.EnumerateArray().Select(LastWins).ToArray());
            default:
                return JsonNode.Parse(e.GetRawText());
        }
    }

    // Drops the members that are false: a flag false is kept as absent.
    private static JsonNode? NoFalse(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var name in o.Where(m => m.Value is JsonValue v && v.GetValueKind() == JsonValueKind.False)
                             .Select(m => m.Key).ToList())
                {
                    o.Remove(name);
                }

                foreach (var (_, v) in o)
                {
                    NoFalse(v);
                }

                break;
            case JsonArray a:
                foreach (var e in a)
                {
                    NoFalse(e);
                }

                break;
        }

        return n;
    }

    // Video title keys in their one canonical form (MSFP Media video 4): any
    // other form of the same title is refused.
    [Fact]
    public void ParseVideoKey()
    {
        var valid = new Dictionary<string, VideoKey>
        {
            ["movie/tmdb/949"] = new("movie", "tmdb", 949),
            ["movie/tmdb/9999999999"] = new("movie", "tmdb", 9_999_999_999),
            ["series/tmdb/1399"] = new("series", "tmdb", 1399),
            ["series/tmdb/1399/s0"] = new("season", "tmdb", 1399),
            ["series/tmdb/1399/s9999"] = new("season", "tmdb", 1399, Season: 9999),
            ["series/tmdb/1399/e63056"] = new("episode", "tmdb", 1399, EpisodeId: 63056),
            ["series/tmdb/1399/e1"] = new("episode", "tmdb", 1399, EpisodeId: 1),
            ["series/tmdb/1399/s1/e2"] = new("episode", "tmdb", 1399, Season: 1, Episode: 2),
            ["series/tmdb/1399/s1/e0"] = new("episode", "tmdb", 1399, Season: 1),
            ["series/tmdb/1399/s0/e99999"] = new("episode", "tmdb", 1399, Episode: 99999),
            ["series/tmdb/1/e9999999999"] = new("episode", "tmdb", 1, EpisodeId: 9_999_999_999),
            ["series/tvdb/81189/a27"] = new("episode", "tvdb", 81189, Episode: 27, Absolute: true),
            ["series/tvdb/81189/a0"] = new("episode", "tvdb", 81189, Absolute: true),
            ["series/tvdb/81189/s1/e2"] = new("episode", "tvdb", 81189, Season: 1, Episode: 2),
            ["movie/local/q3Zp0Lw8"] = new("movie", "local", Local: "q3Zp0Lw8"),
            ["series/local/Hk2v9TnA/s1/e3"] = new("episode", "local", Local: "Hk2v9TnA", Season: 1, Episode: 3),
            ["movie/tmdb/348/x/Tr7aQ1"] = new("extra", "tmdb", 348, Extra: "Tr7aQ1", ParentKind: "movie"),
            ["series/tmdb/1399/s0/x/a"] = new("extra", "tmdb", 1399, Extra: "a", ParentKind: "season"),
            ["series/local/x/x/x"] = new("extra", "local", Local: "x", Extra: "x", ParentKind: "series"),
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
            "movie/tmdb/949/s1", "movie/imdb/tt0133093", "movie/tvdb/81189", "Movie/tmdb/949", "movie/TMDB/949",
            "season/tmdb/1399", "episode/tmdb/1399", "series/tmdb/1399/", "series/tmdb/1399/s01", "series/tmdb/1399/s10000",
            "series/tmdb/1399/e0", "series/tmdb/1399/e063056", "series/tmdb/1399/e12345678901", "series/tmdb/1399/a1",
            "series/tmdb/1399/s1/e01", "series/tmdb/1399/s1/e100000", "series/tmdb/1399/s1/e", "series/tmdb/1399/s/e1",
            "series/tmdb/1399/e63056/e1", "series/tmdb/1399/s1/e2/x", "series/tmdb/+1399", "series/tmdb/1399/s+1",
            "series/tmdb/1399/s1/s2", "movie/tmdb/949 ", "item/0123456789abcdef0123456789abcdef",
            "series/tmdb/1399/e63056/x/a", "series/tmdb/1399/s1/e1/x/a", "movie/tmdb/1/x/a/x/b", "movie/tmdb/1/x/",
            "movie/tmdb/1/x/a.b", "movie/tmdb/1/y/a", "series/local/a/e1", "series/tvdb/1/e1", "movie/tmdb/٩٤٩",
        ];
        var accepted = invalid.Where(k => VideoKey.TryParse(k, out _)).ToList();
        Assert.True(accepted.Count == 0, "accepted: " + string.Join(", ", accepted));

        Assert.True(VideoKey.TryParse("series/tvdb/81189/s1/x/Tr1", out var extra));
        Assert.Equal("series/tvdb/81189/s1", extra.Parent.ToString());
        Assert.Equal("series/tvdb/81189", extra.Series);
        Assert.True(extra.Playable);
        Assert.Null(new VideoKey("movie", "tmdb", 1).Parent);
    }

    // What a receiver keeps of a title (MSFP Media listing 3.3, MSFP
    // Media video 3.1): a bad member skipped, or counted as absent, not the whole title.
    [Fact]
    public void ReadVideoTitle()
    {
        var got = Read("""{"key":"series/tmdb/1399/e63056","kind":"episode","library":"L","place":{"season":1,"episode":1,"x":2}""" + OneCopy +
            ""","with":["series/tmdb/1399/e63057","series/tmdb/1399/e63056","series/tmdb/2/e1","series/tmdb/1399/s1/e3",7,"series/tmdb/1399/s1","series/tvdb/1399/s1/e3"]""" +
            ""","ids":{"imdb":"tt1480055","tvdb":"0123","wikidata":"Q1"},"audio":["en"]}""");
        Assert.NotNull(got);
        Assert.Equal(["series/tmdb/1399/e63057", "series/tmdb/1399/s1/e3"], got.With);
        Assert.Equal(new Dictionary<string, string> { ["imdb"] = "tt1480055" }, got.Ids);
        Assert.Equal(new Place(1, 1), got.Place);
        Assert.Single(got.Copies);

        foreach (var place in new[]
                 {
                     "", ""","place":null""", ""","place":[1,1]""", ""","place":{"season":1}""",
                     ""","place":{"season":1.0,"episode":1}""", ""","place":{"season":"1","episode":1}""",
                     ""","place":{"season":10000,"episode":1}""", ""","place":{"season":1,"episode":-1}""",
                 })
        {
            Assert.True(Read("""{"key":"series/tmdb/1399/e63056","kind":"episode","library":"L" """ + OneCopy + place + "}") is null, place);
        }

        // No copies, copies of the wrong type, none left: skipped; series and
        // seasons carry none.
        foreach (var s in new[]
                 {
                     """{"key":"movie/tmdb/949","kind":"movie","library":"L"}""",
                     """{"key":"movie/tmdb/949","kind":"movie","library":"L","copies":{}}""",
                     """{"key":"movie/tmdb/949","kind":"movie","library":"L","copies":null}""",
                     """{"key":"movie/tmdb/949","kind":"movie","library":"L","copies":[1,{"id":"v1"}]}""",
                     """{"key":"movie/tmdb/949","kind":"series","library":"L","copies":[]}""",
                     """{"key":1,"kind":"movie","library":"L"}""", "\"x\"",
                 })
        {
            Assert.True(Read(s) is null, s);
        }

        got = Read("""{"key":"series/tmdb/1399/s1","kind":"season","library":"L" """ + OneCopy + ""","ids":{"imdb":"tt0944947"}}""");
        Assert.True(got is { Copies.Count: 0, Ids.Count: 0 });

        // Repeated copy IDs: the first kept; at most 16.
        var list = Enumerable.Range(0, 20).Select(i =>
            $$"""{"id":"v{{i % 17}}","durationMs":1,"mediaType":"x/y","size":1,"etag":"t","audioTracks":[]}""");
        got = Read("""{"key":"movie/tmdb/1","kind":"movie","library":"L","copies":[""" + string.Join(",", list) + "]}");
        Assert.NotNull(got);
        Assert.Equal(Copies.MaxCopies, got.Copies.Count);
        Assert.Equal("x/y", got.Copies[0].MediaType);

        // The entry read isn't changed.
        var node = JsonNode.Parse("""{"key":"movie/tmdb/1","kind":"movie","library":"L","x":null""" + OneCopy + "}");
        Assert.NotNull(Video.ReadTitle(node));
        Assert.True(node!.AsObject().ContainsKey("x"));
    }

    [Fact]
    public void CleanWithCap()
    {
        var with = Enumerable.Range(0, 20).Select(i => new VideoKey("episode", "tmdb", 1, EpisodeId: i + 2).ToString());
        Assert.Equal(Video.MaxWith, Video.CleanWith(new VideoKey("episode", "tmdb", 1, EpisodeId: 1), with).Count);
    }

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

    // ids only where the receiver can't find them itself: episodes, and
    // titles keyed under tvdb; tmdb only on those keyed under another scheme.
    [Fact]
    public void OtherIdTmdb()
    {
        Assert.True(Video.OtherId(new VideoKey("series", "tvdb", 81189), "tmdb", "1396"));
        Assert.True(Video.OtherId(new VideoKey("episode", "tvdb", 81189, Episode: 3, Absolute: true), "tmdb", "62085"));
        Assert.False(Video.OtherId(new VideoKey("series", "tmdb", 1396), "tmdb", "1396"));
        Assert.False(Video.OtherId(new VideoKey("series", "tvdb", 81189), "tmdb", "01396"));
        Assert.False(Video.OtherId(new VideoKey("movie", "local", Local: "x"), "imdb", "tt0133093"));
        Assert.False(Video.OtherId(new VideoKey("movie", "tmdb", 603), "imdb", "tt0133093"));
        Assert.False(Video.OtherId(new VideoKey("series", "tmdb", 1399), "tvdb", "121361"));
        Assert.True(Video.OtherId(new VideoKey("series", "tvdb", 121361), "imdb", "tt0944947"));
        Assert.False(Video.OtherId(new VideoKey("series", "tvdb", 121361), "tvdb", "121361"));
        Assert.True(Video.OtherId(new VideoKey("episode", "tmdb", 1399, EpisodeId: 63056), "tvdb", "3254641"));
    }

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
    public void CleanHdr() =>
        Assert.Equal(["hdr10plus", "x"], Video.CleanHdr(["x", "hdr10plus", "HDR10", "hdr10plus", ""]));
}
