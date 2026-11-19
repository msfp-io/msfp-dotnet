using System.Globalization;
using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

// The catalog both ways (msfp-go's catalog_test.go): libraries offered by
// public IDs, titles paged, read whole then as changes, gone keys of titles
// sent only, keys checked, and what a reader keeps.
public sealed class CatalogTests
{
    private static List<string> Keys(Dictionary<string, Title> titles) => [.. titles.Keys.Order(StringComparer.Ordinal)];

    private static JsonObject Json(string body) => JsonNode.Parse(body)!.AsObject();

    [Fact]
    public async Task LibrariesAndPaging()
    {
        var h = new FakeHost { Libs = [new Library { Id = "films", Name = " Films\u200b ", Kind = "movies" }, new Library { Id = "other", Name = "Other", Kind = "weird" }] };
        for (var i = 1; i <= 12_001; i++)
        {
            h.Film("films", "movie/tmdb/" + i.ToString(CultureInfo.InvariantCulture), "en");
        }

        using var s = await Sharing.StartAsync(h);
        var libs = await s.B.Engine.WantsAsync(s.OnB);
        Assert.Equal(2, libs.Count);
        foreach (var l in libs)
        {
            Assert.False(l.Id is "films" or "other", "the host's IDs never leave");
            Assert.True(l.Wanted);
            Assert.False(l.Name == "Films" && l.Kind != "movies");
            Assert.False(l.Name == "Other" && l.Kind != "");
        }

        Assert.Equal(12_001, (await s.PeerTitlesAsync()).Count);
        var pages = s.Exchanges("titles");
        Assert.Equal(3, pages.Count);
        var first = Json(pages[0].ResponseBody);
        Assert.Equal(5000, first["titles"]!.AsArray().Count);
        Assert.False(string.IsNullOrEmpty((string?)first["next"]));
        Assert.True((long)first["seq"]! >= 1);
        Assert.Contains("\"titles\":[", pages[2].ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"next\"", pages[2].ResponseBody, StringComparison.Ordinal);
        // The limits, with playback on.
        var v = await s.B.Engine.PeerAsync(s.OnB);
        Assert.NotNull(v.TheirLimits);
        Assert.Equal(2, v.TheirLimits.Streams);
        Assert.Equal(1, v.TheirLimits.Transcodes);
    }

    // A whole reading that fails halfway keeps the previous complete list:
    // nothing of the pages it read (catalog section 3.2).
    [Fact]
    public async Task WholeReadingFailsHalfway()
    {
        var h = new FakeHost { Libs = [new Library { Id = "films", Name = "Films", Kind = "movies" }, new Library { Id = "more", Name = "More", Kind = "movies" }] };
        for (var i = 1; i <= 6_000; i++)
        {
            h.Film("films", "movie/tmdb/" + i.ToString(CultureInfo.InvariantCulture), "en");
        }

        h.Film("more", "movie/tmdb/9999999", "en");
        using var s = await Sharing.StartAsync(h);
        var before = Keys(await s.PeerTitlesAsync());
        Assert.Equal(6_001, before.Count);
        // A's next list, whole since its offers changed: one title less, one
        // more, on its first page; its second page is broken.
        h.Remove("movie/tmdb/1");
        h.Film("films", "movie/tmdb/10000", "en");
        s.A.Engine.CatalogChanged();
        await s.A.Engine.SetOffersAsync(s.OnA, ["films"]);
        var pages = 0;
        s.Hostile((path, _, _) => path.EndsWith("/titles", StringComparison.Ordinal) && ++pages == 2 ? "{\"titles\":"u8.ToArray() : null);
        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
        Assert.Equal(2, pages);
        Assert.Equal(before, Keys(await s.PeerTitlesAsync()));
    }

    [Fact]
    public async Task CatalogChanges()
    {
        var h = new FakeHost { Libs = [new Library { Id = "films", Name = "Films", Kind = "movies" }, new Library { Id = "kept", Name = "Not offered", Kind = "movies" }] };
        foreach (var k in new[] { "movie/tmdb/1", "movie/tmdb/2", "movie/tmdb/3" })
        {
            h.Film("films", k, "en");
        }

        using var s = await Sharing.StartAsync(h);
        // Offer one library only: the other's titles are never sent.
        await s.A.Engine.SetOffersAsync(s.OnA, ["films"]);
        h.Film("kept", "movie/tmdb/9", "en");
        s.A.Engine.CatalogChanged();
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.Equal(["movie/tmdb/1", "movie/tmdb/2", "movie/tmdb/3"], Keys(await s.PeerTitlesAsync()));
        // One added, one removed, one whose ids changed (a change too), and
        // the unoffered one removed: never named in gone.
        h.Film("films", "movie/tmdb/4", "en");
        h.Remove("movie/tmdb/1");
        h.Remove("movie/tmdb/9");
        lock (h.Lock)
        {
            ((VideoTitle)h.Titles["movie/tmdb/2"]).Ids = new() { ["imdb"] = "tt0133093" };
        }

        s.A.Engine.CatalogChanged();
        await s.B.Engine.ReadNowAsync(s.OnB);
        var x = s.Exchanges("titles")[^1];
        Assert.Contains("\"since\":", x.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"changes\":true", x.ResponseBody, StringComparison.Ordinal);
        var ans = Json(x.ResponseBody);
        Assert.Equal(["movie/tmdb/2", "movie/tmdb/4"], ans["titles"]!.AsArray().Select(t => (string)t!["key"]!).ToList());
        Assert.Equal(["movie/tmdb/1"], ans["gone"]!.AsArray().Select(g => (string)g!).ToList());
        var got = await s.PeerTitlesAsync();
        Assert.Equal(["movie/tmdb/2", "movie/tmdb/3", "movie/tmdb/4"], Keys(got));
        Assert.Equal("tt0133093", ((VideoTitle)got["movie/tmdb/2"]).Ids!["imdb"]);
        // Offers changed: the next reading is a whole one.
        await s.A.Engine.SetOffersAsync(s.OnA, ["films", "kept"]);
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.DoesNotContain("\"changes\":true", s.Exchanges("titles")[^1].ResponseBody, StringComparison.Ordinal);
    }

    // A new place is a change of the title (video section 4.1): sent again
    // under the same key; one that loses its place is gone.
    [Fact]
    public async Task PlaceChanges()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en");
        foreach (var k in new[] { "series/tmdb/1399/e1", "series/tmdb/1399/e2" })
        {
            h.Titles[k] = new VideoTitle { Key = k, Kind = "episode", Libraries = ["films"], Place = new Place { Season = 1, Episode = 1 } };
        }

        using var s = await Sharing.StartAsync(h);
        Assert.Equal(["movie/tmdb/1", "series/tmdb/1399/e1", "series/tmdb/1399/e2"], Keys(await s.PeerTitlesAsync()));
        lock (h.Lock)
        {
            ((VideoTitle)h.Titles["series/tmdb/1399/e1"]).Place = new Place { Season = 2, Episode = 5 };
            ((VideoTitle)h.Titles["series/tmdb/1399/e2"]).Place = null;
        }

        s.A.Engine.CatalogChanged();
        await s.B.Engine.ReadNowAsync(s.OnB);
        var ans = Json(s.Exchanges("titles")[^1].ResponseBody);
        Assert.True((bool)ans["changes"]!);
        Assert.Equal(["series/tmdb/1399/e1"], ans["titles"]!.AsArray().Select(t => (string)t!["key"]!).ToList());
        Assert.Equal(["series/tmdb/1399/e2"], ans["gone"]!.AsArray().Select(g => (string)g!).ToList());
        var got = await s.PeerTitlesAsync();
        Assert.Equal(["movie/tmdb/1", "series/tmdb/1399/e1"], Keys(got));
        Assert.Equal(new Place { Season = 2, Episode = 5 }, ((VideoTitle)got["series/tmdb/1399/e1"]).Place);
    }

    // A host's ChangeLog answers changes; one too old, the whole list.
    private class LoggingHost(FakeHost h) : ICatalog, IChangeLog
    {
        public long Seq { get; set; }

        public List<long> Since { get; } = [];

        public bool Old { get; set; }

        public ValueTask<IReadOnlyList<Library>> LibrariesAsync(CancellationToken ct) => h.LibrariesAsync(ct);

        public ValueTask<TitlePage> TitlesAsync(IReadOnlyList<string> libraries, string after, int limit, CancellationToken ct) =>
            h.TitlesAsync(libraries, after, limit, ct);

        public ValueTask<IReadOnlyList<Copy>> CopiesAsync(string key, IReadOnlyList<string> libraries, CancellationToken ct) =>
            h.CopiesAsync(key, libraries, ct);

        public virtual ValueTask<long> SeqAsync(CancellationToken ct) => ValueTask.FromResult(Seq);

        public async ValueTask<ChangePage> ChangesAsync(IReadOnlyList<string> libraries, long since, string after, int limit, CancellationToken ct)
        {
            lock (Since)
            {
                Since.Add(since);
            }

            if (Old)
            {
                throw new MsfpException(MsfpError.TooOld);
            }

            var page = await h.TitlesAsync(libraries, after, limit, ct);
            return new ChangePage(page.Titles, ["movie/tmdb/77"], "", false);
        }
    }

    private static async Task<(TestServer A, TestServer B, string OnA, string OnB)> LogPairAsync(Network net, FakeHost h, ICatalog catalog)
    {
        h.Libs = [new Library { Id = "films", Name = "Films", Kind = "movies" }];
        var a = await TestServer.StartAsync(net, "https://a.example.org", with: c => c with { Catalog = catalog, Playback = h, Media = h });
        var b = await TestServer.StartAsync(net, "https://b.example.org");
        var (onA, onB) = await Kit.LinkAsync(a, b);
        await a.Engine.CheckAsync(onA);
        await b.Engine.CheckAsync(onB);
        await Task.WhenAll(a.WaitAsync(), b.WaitAsync());
        return (a, b, onA, onB);
    }

    [Fact]
    public async Task HostChangeLog()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/5", "en");
        var l = new LoggingHost(h) { Seq = 6 };
        var net = new Network();
        var (a, b, onA, onB) = await LogPairAsync(net, h, l);
        using var _a = a;
        using var _b = b;
        await a.Engine.SetOffersAsync(onA, ["films"]);
        var libs = await b.Engine.TheirLibrariesAsync(onB);
        await b.Engine.SetWantsAsync(onB, [libs[0].Id]);
        // The offers were set at 6: a reading at 7 is after them.
        l.Seq = 7;
        await b.Engine.ReadNowAsync(onB);
        l.Seq = 9;
        await b.Engine.ReadNowAsync(onB);
        Assert.Equal([7L], l.Since);
        // movie/tmdb/77 was never sent: not in gone.
        var last = net.SeenBy(a.Host).Where(x => x.Target.EndsWith("/titles", StringComparison.Ordinal)).Last();
        Assert.Contains("\"changes\":true", last.ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("77", last.ResponseBody, StringComparison.Ordinal);
        l.Old = true;
        await b.Engine.ReadNowAsync(onB);
        last = net.SeenBy(a.Host).Where(x => x.Target.EndsWith("/titles", StringComparison.Ordinal)).Last();
        Assert.DoesNotContain("\"changes\"", last.ResponseBody, StringComparison.Ordinal);
        Assert.Contains("movie/tmdb/5", last.ResponseBody, StringComparison.Ordinal);
    }

    // A ChangeLog that takes change numbers itself (ISeqBumper).
    private sealed class BumpingHost(FakeHost h) : LoggingHost(h), ISeqBumper
    {
        public long Now()
        {
            lock (this)
            {
                return Seq;
            }
        }

        public override ValueTask<long> SeqAsync(CancellationToken ct) => ValueTask.FromResult(Now());

        public ValueTask<long> BumpSeqAsync(CancellationToken ct)
        {
            lock (this)
            {
                Seq++;
                return ValueTask.FromResult(Seq);
            }
        }
    }

    // A host whose log takes a number after a change of offers: the reading
    // after the next whole one is of changes, with no title changed between.
    [Fact]
    public async Task HostChangeLogBumped()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/5", "en");
        var l = new BumpingHost(h) { Seq = 6 };
        var (a, b, onA, onB) = await LogPairAsync(new Network(), h, l);
        using var _a = a;
        using var _b = b;
        var before = l.Now();
        await a.Engine.SetOffersAsync(onA, ["films"]);
        var after = l.Now();
        Assert.Equal(before + 1, after);
        var libs = await b.Engine.TheirLibrariesAsync(onB);
        await b.Engine.SetWantsAsync(onB, [libs[0].Id]);
        for (var i = 0; i < 2; i++)
        {
            await b.Engine.ReadNowAsync(onB);
        }

        Assert.Equal([after], l.Since);
    }

    // Keys MSFP doesn't take never leave: leading zeros, another kind, an
    // unknown scheme; members are cleaned before they go.
    [Fact]
    public async Task OwnTitlesChecked()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en", "fr");
        foreach (var k in new[] { "movie/tmdb/0949", "movie/imdb/949", "series/tmdb/1399/s01" })
        {
            h.Titles[k] = new VideoTitle { Key = k, Kind = "movie", Libraries = ["films"] };
        }

        h.Titles["series/tmdb/1399"] = new VideoTitle { Key = "series/tmdb/1399", Kind = "movie", Libraries = ["films"] };
        h.Titles["series/tmdb/1399/e1"] = new VideoTitle
        {
            Key = "series/tmdb/1399/e1", Kind = "episode", Libraries = ["films"], Audio = ["en", "EN", "x"],
            With = ["series/tmdb/1399/e3", "series/tmdb/2/e1", "series/tmdb/1399/e2"],
            Ids = new() { ["imdb"] = "tt01", ["tvdb"] = "81189", ["other"] = "1" },
            Place = new Place { Season = 1, Episode = 0 },
        };
        // An episode keyed by its TMDB ID without a place, or with one out of
        // range, isn't sent; place on other titles isn't sent.
        h.Titles["series/tmdb/1399/e7"] = new VideoTitle { Key = "series/tmdb/1399/e7", Kind = "episode", Libraries = ["films"] };
        h.Titles["series/tmdb/1399/e8"] = new VideoTitle
        {
            Key = "series/tmdb/1399/e8", Kind = "episode", Libraries = ["films"], Place = new Place { Season = 10_000, Episode = 1 },
        };
        h.Titles["series/tmdb/1399/s2/e1"] = new VideoTitle
        {
            Key = "series/tmdb/1399/s2/e1", Kind = "episode", Libraries = ["films"], Place = new Place { Season = 2, Episode = 1 },
        };
        ((VideoTitle)h.Titles["movie/tmdb/1"]).Place = new Place { Season = 1, Episode = 1 };
        using var s = await Sharing.StartAsync(h);
        var got = await s.PeerTitlesAsync();
        Assert.Equal(["movie/tmdb/1", "series/tmdb/1399/e1", "series/tmdb/1399/s2/e1"], Keys(got));
        var body = s.Exchanges("titles")[^1].ResponseBody;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "\"place\""));
        Assert.Contains("\"key\":\"series/tmdb/1399/e1\",\"kind\":\"episode\",\"library\":", body, StringComparison.Ordinal);
        Assert.Contains("\"place\":{\"season\":1,\"episode\":0},\"with\":", body, StringComparison.Ordinal);
        Assert.Null(((VideoTitle)got["movie/tmdb/1"]).Place);
        Assert.Null(((VideoTitle)got["series/tmdb/1399/s2/e1"]).Place);
        var ep = (VideoTitle)got["series/tmdb/1399/e1"];
        Assert.Equal(new Place { Season = 1, Episode = 0 }, ep.Place);
        Assert.Equal(["en"], ep.Audio!);
        Assert.Equal(["series/tmdb/1399/e2", "series/tmdb/1399/e3"], ep.With!);
        Assert.Single(ep.Ids!);
        Assert.Equal("81189", ep.Ids!["tvdb"]);
    }

    // A hostile peer's titles: what a reader keeps is MSFP's, title by
    // title; members of the wrong type at the top fail the reading, which
    // keeps what was kept before.
    [Fact]
    public async Task HostileTitles()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en");
        using var s = await Sharing.StartAsync(h);
        var lib = s.LibraryPub;
        var page = "{\"titles\":[" +
            "{\"key\":\"movie/tmdb/2\",\"kind\":\"movie\",\"library\":\"" + lib + "\",\"audio\":[\"fr\",\"fr\",\"toolongtoolongtag\"],\"ids\":{\"imdb\":\"tt0133093\",\"x\":1}}," +
            "{\"key\":\"movie/tmdb/03\",\"kind\":\"movie\",\"library\":\"" + lib + "\"}," +
            "{\"key\":\"movie/tmdb/4\",\"kind\":\"series\",\"library\":\"" + lib + "\"}," +
            "{\"key\":\"movie/tmdb/5\",\"kind\":\"movie\",\"library\":\"elsewhere\"}," +
            "{\"key\":\"track/mb/0c000000-0000-4000-8000-000000000003\",\"kind\":\"track\",\"library\":\"" + lib + "\",\"name\":\"<b>x</b>\"}," +
            "7,{\"key\":6}],\"seq\":3}";
        s.Hostile((path, _, _) => path.EndsWith("/titles", StringComparison.Ordinal) ? HostKit.Bytes(page) : null);
        await s.B.Engine.ReadNowAsync(s.OnB);
        var got = await s.PeerTitlesAsync();
        Assert.Equal(["movie/tmdb/2", "track/mb/0c000000-0000-4000-8000-000000000003"], Keys(got));
        var v = (VideoTitle)got["movie/tmdb/2"];
        Assert.Equal(["fr"], v.Audio!);
        Assert.Single(v.Ids!);
        Assert.Equal("<b>x</b>", ((MusicTitle)got["track/mb/0c000000-0000-4000-8000-000000000003"]).Name);
        // Wrong types at the top: the reading fails, what was kept stays.
        foreach (var bad in new[] { "{\"titles\":{}}", "{\"titles\":[],\"next\":7}", "{\"titles\":[],\"seq\":\"1\"}", "{\"titles\":[],\"changes\":1}", "{\"titles\":[],\"gone\":\"x\"}" })
        {
            page = bad;
            await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
            Assert.Equal(2, (await s.PeerTitlesAsync()).Count);
        }

        // A page of more than 5,000 keys: a peer filling the database.
        page = "{\"titles\":[" + string.Join(",", Enumerable.Range(0, 5001).Select(i =>
            $"{{\"key\":\"movie/tmdb/{i + 10}\",\"kind\":\"movie\",\"library\":\"{lib}\"}}")) + "]}";
        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
        // Changes mixed with the whole list on later pages: a failure.
        var calls = 0;
        s.Hostile((path, _, _) =>
        {
            if (!path.EndsWith("/titles", StringComparison.Ordinal))
            {
                return null;
            }

            return ++calls == 1
                ? "{\"titles\":[],\"next\":\"movie/tmdb/1\",\"changes\":true,\"gone\":[\"movie/tmdb/2\"],\"seq\":5}"u8.ToArray()
                : "{\"titles\":[]}"u8.ToArray();
        });
        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB));
    }

    // Music titles go with their descriptions, checked before they leave.
    [Fact]
    public async Task MusicTitles()
    {
        var h = new FakeHost { Libs = [new Library { Id = "music", Name = "Music", Kind = "music" }] };
        const string Track = "track/mb/0c000000-0000-4000-8000-000000000003";
        h.Titles[Track] = new MusicTitle
        {
            Key = Track, Kind = "track", Libraries = ["music"], Name = "The\tBoxer",
            Artists = ["artist/mb/0a000000-0000-4000-8000-000000000001", "nope"], ArtistNames = "Simon & Garfunkel",
            Album = "album/mb/0b000000-0000-4000-8000-000000000002", Disc = 1, Number = 2, Year = 1970, DurationMs = 308000,
            Ids = new() { ["isrc"] = "USSM17000123", ["imdb"] = "tt0133093" },
        };
        const string Nameless = "track/mb/0c000000-0000-4000-8000-000000000004";
        h.Titles[Nameless] = new MusicTitle { Key = Nameless, Kind = "track", Libraries = ["music"] };
        using var s = await Sharing.StartAsync(h);
        var got = await s.PeerTitlesAsync();
        Assert.Single(got);
        var m = Assert.IsType<MusicTitle>(got[Track]);
        Assert.Equal("The Boxer", m.Name);
        Assert.Equal(["artist/mb/0a000000-0000-4000-8000-000000000001"], m.Artists!);
        Assert.Equal("Simon & Garfunkel", m.ArtistNames);
        Assert.Equal((1, 2, 1970, 308000L), (m.Disc, m.Number, m.Year, m.DurationMs));
        Assert.Single(m.Ids!);
        Assert.Equal(s.LibraryPub, m.Libraries![0]);
        // A server without music: none of it is sent.
        var h2 = new FakeHost { Libs = h.Libs };
        h2.Titles[Track] = h.Titles[Track];
        using var s2 = await Sharing.StartAsync(h2, cfgB: c => c with { Parts = ["catalog", "playback", "media", "video"] });
        Assert.Empty(await s2.PeerTitlesAsync());
    }

    // A film's picture (video section 3.2): the host gives each copy's, the
    // best goes, stepped; a reader keeps it with the title, and another
    // picture is a change of the title.
    [Fact]
    public async Task TitlePictures()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en");
        h.Film("films", "movie/tmdb/2", "en");
        h.Film("films", "movie/tmdb/3", "en");
        ((VideoTitle)h.Titles["movie/tmdb/1"]).Pictures =
            [new Picture { Height = 1080 }, new Picture { Height = 2200, Hdr = ["dv", "HDR10", "hdr10"] }, new Picture { Height = 2160 }];
        ((VideoTitle)h.Titles["movie/tmdb/2"]).Pictures = [new Picture { Height = 360 }, new Picture { Height = 0, Hdr = ["hdr10"] }];
        using var s = await Sharing.StartAsync(h);
        var got = await s.PeerTitlesAsync();
        var v1 = ((VideoTitle)got["movie/tmdb/1"]).Video;
        Assert.NotNull(v1);
        Assert.Equal(2160, v1.Height);
        Assert.Equal(["hdr10", "dv"], v1.Hdr!);
        var v2 = ((VideoTitle)got["movie/tmdb/2"]).Video;
        Assert.NotNull(v2);
        Assert.Equal(480, v2.Height);
        Assert.Null(v2.Hdr);
        Assert.Null(((VideoTitle)got["movie/tmdb/3"]).Video);
        var body = s.Exchanges("titles")[^1].ResponseBody;
        Assert.Contains("\"video\":{\"height\":2160,\"hdr\":[\"hdr10\",\"dv\"]}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("pictures", body, StringComparison.Ordinal);
        // Another picture: the title is sent again in a changes reading.
        lock (h.Lock)
        {
            ((VideoTitle)h.Titles["movie/tmdb/3"]).Pictures = [new Picture { Height = 720 }];
        }

        s.A.Engine.CatalogChanged();
        await s.B.Engine.ReadNowAsync(s.OnB);
        var x = s.Exchanges("titles")[^1].ResponseBody;
        Assert.Contains("\"changes\":true", x, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"movie/tmdb/3\"", x, StringComparison.Ordinal);
        Assert.DoesNotContain("\"key\":\"movie/tmdb/1\"", x, StringComparison.Ordinal);
        Assert.Equal(720, ((VideoTitle)(await s.PeerTitlesAsync())["movie/tmdb/3"]).Video?.Height);
    }

    public static TheoryData<int[], string[][], int, string[]?> BestPictures => new()
    {
        { [], [], 0, null },
        { [0], [[]], 0, null },
        { [1600], [[]], 1440, null },
        { [9000], [[]], 4320, null },
        { [2160, 2160], [[], ["hlg"]], 2160, ["hlg"] },
        { [2160, 2160], [["hlg"], ["dv", "hdr10"]], 2160, ["hdr10", "dv"] },
        { [1080, 2160], [["hdr10"], []], 2160, null },
        { [1080], [["hdr10+", "sdr"]], 1080, null },
    };

    [Theory]
    [MemberData(nameof(BestPictures))]
    public void BestPicture(int[] heights, string[][] hdr, int height, string[]? wantHdr)
    {
        var pictures = heights.Select((x, i) => new Picture { Height = x, Hdr = hdr[i].Length > 0 ? [.. hdr[i]] : null });
        var best = Picture.Best(pictures);
        if (height == 0)
        {
            Assert.Null(best);
            return;
        }

        Assert.NotNull(best);
        Assert.Equal(height, best.Height);
        Assert.Equal(wantHdr, best.Hdr?.ToArray());
    }
}
