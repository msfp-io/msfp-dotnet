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
        var h = new FakeHost { Libs = [new Library { Id = "films", Name = " Films\u200b " }, new Library { Id = "other", Name = "Other" }] };
        for (var i = 1; i <= 12_001; i++)
        {
            var key = "movie/tmdb/" + i.ToString(CultureInfo.InvariantCulture);
            h.Film("films", key, "en");
            h.EditCopy(key, 0, c => c with { Video = null }); // small titles: three pages of them, as before
        }

        using var s = await Sharing.StartAsync(h);
        var libs = await s.B.Engine.WantsAsync(s.OnB);
        Assert.Equal(2, libs.Count);
        foreach (var l in libs)
        {
            Assert.False(l.Id is "films" or "other", "the host's IDs never leave");
            Assert.True(l.Wanted);
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
        var h = new FakeHost { Libs = [new Library { Id = "films", Name = "Films" }, new Library { Id = "more", Name = "More" }] };
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
        var h = new FakeHost { Libs = [new Library { Id = "films", Name = "Films" }, new Library { Id = "kept", Name = "Not offered" }] };
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
        Assert.Null(((VideoTitle)got["movie/tmdb/2"]).Ids); // no ids on a film keyed under tmdb (video section 4.3)
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
            h.Titles[k] = new VideoTitle { Key = k, Kind = "episode", Libraries = ["films"], Place = new Place { Season = 1, Episode = 1 }, Copies = FakeHost.OneCopy(k) };
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
        h.Libs = [new Library { Id = "films", Name = "Films" }];
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
        Assert.DoesNotContain("movie/tmdb/77", last.ResponseBody, StringComparison.Ordinal);
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

    // A host's copy with more subtitle tracks than MSFP allows leaves with
    // the first 64 (video section 3.2): the engine sends nothing a receiver
    // would drop.
    [Fact]
    public async Task OwnSubtitleTracksCapped()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en");
        var m = (VideoTitle)h.Titles["movie/tmdb/1"];
        m.Copies![0] = m.Copies[0] with
        {
            SubtitleTracks = [.. Enumerable.Range(0, 70).Select(i => new CopySubtitle("s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "subrip") { Language = "fr" })],
        };
        using var s = await Sharing.StartAsync(h);
        var got = (VideoTitle)(await s.PeerTitlesAsync())["movie/tmdb/1"];
        Assert.Equal(Copies.MaxSubtitleTracks, got.Copies![0].SubtitleTracks.Count);
        var body = s.Exchanges("titles")[^1].ResponseBody;
        Assert.Equal(Copies.MaxSubtitleTracks, body.Split("\"format\":\"subrip\"").Length - 1);
        Assert.DoesNotContain("\"s64\"", body, StringComparison.Ordinal);
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
            Key = "series/tmdb/1399/e1", Kind = "episode", Libraries = ["films"],
            Copies = [new Protocol.Copy("ep1", 0, "video/matroska", 1, "x", [new CopyAudio("a0", "aac") { Language = "eng" }, new CopyAudio("a1", "aac") { Language = "x" }])],
            With = ["series/tmdb/1399/e3", "series/tmdb/2/e1", "series/tmdb/1399/e2"],
            Ids = new() { ["imdb"] = "tt01", ["tvdb"] = "81189", ["other"] = "1" },
            Place = new Place { Season = 1, Episode = 0 },
        };
        // An episode keyed by its TMDB ID without a place, or with one out of
        // range, isn't sent; place on other titles isn't sent.
        h.Titles["series/tmdb/1399/e7"] = new VideoTitle { Key = "series/tmdb/1399/e7", Kind = "episode", Libraries = ["films"], Copies = FakeHost.OneCopy("e7") };
        h.Titles["series/tmdb/1399/e8"] = new VideoTitle
        {
            Key = "series/tmdb/1399/e8", Kind = "episode", Libraries = ["films"], Place = new Place { Season = 10_000, Episode = 1 },
            Copies = FakeHost.OneCopy("e8"),
        };
        h.Titles["series/tmdb/1399/s2/e1"] = new VideoTitle
        {
            Key = "series/tmdb/1399/s2/e1", Kind = "episode", Libraries = ["films"], Place = new Place { Season = 2, Episode = 1 },
            Copies = FakeHost.OneCopy("s2e1"),
        };
        ((VideoTitle)h.Titles["movie/tmdb/1"]).Place = new Place { Season = 1, Episode = 1 };
        using var s = await Sharing.StartAsync(h);
        var got = await s.PeerTitlesAsync();
        Assert.Equal(["movie/tmdb/1", "series/tmdb/1399/e1", "series/tmdb/1399/s2/e1"], Keys(got));
        var body = s.Exchanges("titles")[^1].ResponseBody;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "\"place\""));
        Assert.Contains("\"key\":\"series/tmdb/1399/e1\",\"library\":", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\"", body, StringComparison.Ordinal);
        Assert.Contains("\"place\":{\"season\":1,\"episode\":0},\"with\":", body, StringComparison.Ordinal);
        Assert.Null(((VideoTitle)got["movie/tmdb/1"]).Place);
        Assert.Null(((VideoTitle)got["series/tmdb/1399/s2/e1"]).Place);
        var ep = (VideoTitle)got["series/tmdb/1399/e1"];
        Assert.Equal(new Place { Season = 1, Episode = 0 }, ep.Place);
        Assert.Equal(["en"], ep.AudioLanguages());
        Assert.NotEqual("ep1", ep.Copies![0].Id);
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
            "{\"key\":\"movie/tmdb/2\",\"kind\":\"movie\",\"library\":\"" + lib + "\",\"copies\":[{\"id\":\"v1\",\"durationMs\":1000,\"mediaType\":\"video/matroska\",\"size\":5,\"etag\":\"t1\",\"audioTracks\":[{\"id\":\"a0\",\"codec\":\"aac\",\"language\":\"fre\"},{\"id\":\"a1\",\"codec\":\"aac\",\"language\":\"fr\"}]},{\"id\":\"v1\",\"durationMs\":1,\"mediaType\":\"video/x-matroska\",\"size\":1,\"etag\":\"t\",\"audioTracks\":[]}],\"ids\":{\"imdb\":\"tt0133093\",\"x\":1}}," +
            "{\"key\":\"movie/tmdb/7\",\"kind\":\"movie\",\"library\":\"" + lib + "\"}," +
            "{\"key\":\"movie/tmdb/03\",\"kind\":\"movie\",\"library\":\"" + lib + "\"}," +
            "{\"key\":\"movie/tmdb/4\",\"kind\":\"series\",\"library\":\"" + lib + "\"}," +
            "{\"key\":\"movie/tmdb/5\",\"kind\":\"movie\",\"library\":\"elsewhere\"}," +
            "{\"key\":\"track/mb/0c000000-0000-4000-8000-000000000003\",\"kind\":\"track\",\"library\":\"" + lib + "\",\"name\":\"<b>x</b>\",\"album\":\"album/mb/0b000000-0000-4000-8000-000000000002\",\"copies\":[{\"id\":\"t1\",\"durationMs\":1000,\"mediaType\":\"audio/flac\",\"codec\":\"flac\",\"size\":9,\"etag\":\"e\"}]}," +
            "7,{\"key\":6}],\"seq\":3}";
        s.Hostile((path, _, _) => path.EndsWith("/titles", StringComparison.Ordinal) ? HostKit.Bytes(page) : null);
        await s.B.Engine.ReadNowAsync(s.OnB);
        var got = await s.PeerTitlesAsync();
        Assert.Equal(["movie/tmdb/2", "track/mb/0c000000-0000-4000-8000-000000000003"], Keys(got));
        var v = (VideoTitle)got["movie/tmdb/2"];
        Assert.Equal(["fr"], v.AudioLanguages());
        Assert.Null(v.Ids);
        Assert.Equal("video/matroska", Assert.Single(v.Copies!).MediaType);
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
        var h = new FakeHost { Libs = [new Library { Id = "music", Name = "Music" }] };
        const string Track = "track/mb/0c000000-0000-4000-8000-000000000003";
        h.Titles[Track] = new MusicTitle
        {
            Key = Track, Kind = "track", Libraries = ["music"], Name = "The\tBoxer",
            Artists = ["artist/mb/0a000000-0000-4000-8000-000000000001", "nope"], ArtistNames = "Simon & Garfunkel",
            Album = "album/mb/0b000000-0000-4000-8000-000000000002", Disc = 1, Number = 2, Date = "1970", Genres = ["Folk"],
            Copies = [new MusicCopy { Id = "boxer.flac", DurationMs = 308000, MediaType = "audio/flac", Codec = "flac",
                Size = 36214455, Etag = "7f3a9c21", SampleRate = 44100, BitDepth = 16, Channels = 2 }],
            Ids = new() { ["isrc"] = "USSM17000123", ["imdb"] = "tt0133093", ["mbrecording"] = "0e000000-0000-4000-8000-000000000006" },
        };
        const string Album = "album/mb/0b000000-0000-4000-8000-000000000002";
        h.Titles[Album] = new MusicTitle
        {
            Key = Album, Kind = "album", Libraries = ["music"], Name = "Bridge over Troubled Water", PrimaryType = "album", SecondaryTypes = ["soundtrack"],
            Date = "2011-03-08", OriginalDate = "1970-01-26", Genres = ["Folk rock", "folk ROCK"], Image = true,
            Ids = new() { ["mbreleasegroup"] = "0d000000-0000-4000-8000-000000000005" },
        };
        const string Nameless = "track/mb/0c000000-0000-4000-8000-000000000004";
        h.Titles[Nameless] = new MusicTitle { Key = Nameless, Kind = "track", Libraries = ["music"] };
        // A track's members its album has: left out (music section 3).
        const string Same = "track/mb/0c000000-0000-4000-8000-000000000007";
        h.Titles[Same] = new MusicTitle
        {
            Key = Same, Kind = "track", Libraries = ["music"], Name = "Cecilia", Album = Album, Disc = 1, Number = 3,
            Date = "2011-03-08", OriginalDate = "1970-01-26", Genres = ["Folk rock", "folk ROCK"],
            Copies = [new MusicCopy { Id = "cecilia.flac", DurationMs = 175000, MediaType = "audio/flac", Codec = "flac", Size = 9, Etag = "c1" }],
        };
        using var s = await Sharing.StartAsync(h);
        var got = await s.PeerTitlesAsync();
        Assert.Equal(3, got.Count);
        var same = Assert.IsType<MusicTitle>(got[Same]);
        Assert.Equal((null, null, null), (same.Date, same.OriginalDate, same.Genres));
        var m = Assert.IsType<MusicTitle>(got[Track]);
        Assert.Equal("The Boxer", m.Name);
        Assert.Equal(["artist/mb/0a000000-0000-4000-8000-000000000001"], m.Artists!);
        Assert.Equal("Simon & Garfunkel", m.ArtistNames);
        Assert.Equal((1, 2), (m.Disc, m.Number));
        Assert.Equal("1970", m.Date);
        Assert.Equal(["Folk"], m.Genres!);
        var copy = Assert.Single(m.Copies!);
        Assert.Equal((308000L, "audio/flac"), (copy.DurationMs, copy.MediaType));
        Assert.StartsWith("ver_", copy.Id, StringComparison.Ordinal);
        Assert.Single(m.Ids!);
        Assert.Equal(s.LibraryPub, m.Libraries![0]);
        // An album's description; an image only for a local one.
        var a = Assert.IsType<MusicTitle>(got[Album]);
        Assert.Equal(("album", "1970-01-26"), (a.PrimaryType, a.OriginalDate));
        Assert.Single(a.SecondaryTypes!);
        Assert.Equal(["Folk rock"], a.Genres!);
        Assert.False(a.Image);
        Assert.NotEmpty(a.Ids!["mbreleasegroup"]);
        var sentBody = s.Exchanges("titles")[^1].ResponseBody;
        Assert.DoesNotContain("boxer.flac", sentBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"year\"", sentBody, StringComparison.Ordinal);
        // A server without music: none of it is sent.
        var h2 = new FakeHost { Libs = h.Libs };
        h2.Titles[Track] = h.Titles[Track];
        using var s2 = await Sharing.StartAsync(h2, cfgB: c => c with { Parts = ["catalog", "playback", "media", "video"] });
        Assert.Empty(await s2.PeerTitlesAsync());
    }

    // A film's copies (video section 3.2): sent in the host's order, its
    // preference (video section 3.7), under version IDs, cleaned as a receiver keeps them; the best picture and the
    // languages are worked out from them; a copy's new etag is a change of
    // the title.
    [Fact]
    public async Task TitleCopies()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en", "fr");
        h.Film("films", "movie/tmdb/2", "en");
        var m1 = (VideoTitle)h.Titles["movie/tmdb/1"];
        m1.Copies![0] = m1.Copies[0] with
        {
            Video = new VideoStream("h264", 1920, 1080),
            Chapters = [new CopyChapter(0, "Start"), new CopyChapter(0), new CopyChapter(60_000)],
        };
        m1.Copies[1] = m1.Copies[1] with
        {
            Video = new VideoStream("hevc", 3840, 1600) { Hdr = ["hdr10plus", "dv", "hdr10plus"] },
            AudioTracks = [m1.Copies[1].AudioTracks[0] with { Role = "commentary" }, new CopyAudio("a1", "dts") { Language = "ger", Codecs = "dtsh" }],
        };
        var m2 = (VideoTitle)h.Titles["movie/tmdb/2"];
        m2.Copies![0] = m2.Copies[0] with { Size = 0 }; // a receiver would skip it: so is the title
        using var s = await Sharing.StartAsync(h);
        var got = await s.PeerTitlesAsync();
        Assert.False(got.ContainsKey("movie/tmdb/2"));
        var v = (VideoTitle)got["movie/tmdb/1"];
        var first = v.BestCopy();
        Assert.NotNull(first);
        Assert.Equal(2, v.Copies!.Count);
        Assert.Equal(1080, first.Video?.Height);
        var best = v.Copies[1];
        Assert.Equal(["dv", "hdr10plus"], best.Video!.Hdr); // sorted, each once
        Assert.Equal(("commentary", "dtsh", "de"), (best.AudioTracks[0].Role, best.AudioTracks[1].Codecs, best.AudioTracks[1].Language));
        Assert.Equal(["en", "fr", "de"], v.AudioLanguages());
        Assert.Equal(["fr"], v.SubtitleLanguages());
        var c = v.Copies[0];
        Assert.Equal((2, 20L, "e0"), (c.Chapters.Count, c.Size, c.ETag));
        Assert.All(v.Copies, x => Assert.StartsWith("ver_", x.Id, StringComparison.Ordinal));
        var body = s.Exchanges("titles")[^1].ResponseBody;
        Assert.DoesNotContain("movie/tmdb/1#", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GRP", body, StringComparison.Ordinal);
        // A copy replaced (its etag): the title is sent again in a changes
        // reading.
        lock (h.Lock)
        {
            m1.Copies[0] = m1.Copies[0] with { ETag = "e9" };
        }

        s.A.Engine.CatalogChanged();
        await s.B.Engine.ReadNowAsync(s.OnB);
        var x = s.Exchanges("titles")[^1].ResponseBody;
        Assert.Contains("\"changes\":true", x, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"movie/tmdb/1\"", x, StringComparison.Ordinal);
        Assert.Equal("e9", ((VideoTitle)(await s.PeerTitlesAsync())["movie/tmdb/1"]).Copies![0].ETag);
    }
}
