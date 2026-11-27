using System.Text;
using System.Text.Json.Nodes;
using Msfp.Protocol;
using Place = Msfp.Place;

namespace Msfp.Tests;

// MSFP Media's additions, between two engines (msfp-go's
// media_language_test.go): replaces, paused, the titles filters, msfpMedia
// in the discovery document.
public sealed class MediaLanguageTests
{
    private const string Film = "movie/tmdb/949";

    private static async Task<(int Status, string Body)> CallAsync(Sharing s, string call, string body)
    {
        var (status, answer) = await s.B.Engine.CallForTestAsync(s.OnB, call, HostKit.Bytes(body));
        return (status, Encoding.UTF8.GetString(answer));
    }

    // A play that replaces a live playback of the same peer counts as it:
    // with one stream, the new one starts; the old one ends at once, on both
    // sides; a replaces that names no live playback of the caller is
    // ignored, and a malformed one refused (MSFP Media playing 2.2 and 2.3).
    [Fact]
    public async Task Replaces()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h, cfgA: c => c with { NewPairing = new Limits { Streams = 1, Transcodes = 1 } });
        var old = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Delivery = Playback.DeliveryFile });
        Assert.Equal(MsfpError.StreamLimitReached,
            (await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film }))).Code);
        var theirOld = s.A.Engine.Playbacks()[0].Id;
        var stops = s.Exchanges("stop").Count;
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Replaces = old.Id });
        await s.WaitAsync();
        Assert.Contains("\"replaces\":\"" + theirOld + "\"", s.Exchanges("play")[^1].RequestBody, StringComparison.Ordinal);
        Assert.NotEqual("", h.Starts()[^1].Replaces);
        Assert.Contains(EndReason.Replaced, h.Stops().Values);
        Assert.Equal(stops, s.Exchanges("stop").Count); // replacing needs no stop
        var live = Assert.Single(s.A.Engine.Playbacks());
        Assert.NotEqual(theirOld, live.Id);
        var ended = s.A.Events.Of<PlaybackEnded>().Last();
        Assert.Equal((theirOld, EndReason.Replaced), (ended.Playback, ended.Reason));
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(() =>
            s.B.Engine.FetchAsync(old.Id, new MediaPath { Kind = MediaKind.File }, "", new MemorySink()))).Code);
        await s.B.Engine.StopAsync(pb.Id, -1);
        await s.A.WaitAsync();

        // By hand: one live, a replaces of no live playback of B's is
        // ignored, and the limit refuses; a malformed one is refused.
        var again = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        Assert.Equal(409, (await CallAsync(s, "play", "{\"title\":\"movie/tmdb/949\",\"replaces\":\"play_none\"}")).Status);
        foreach (var r in new[] { "\"x y\"", "\"\"", "7" })
        {
            Assert.True((await CallAsync(s, "play", "{\"title\":\"movie/tmdb/949\",\"replaces\":" + r + "}")).Status == 400, r);
        }

        await s.B.Engine.StopAsync(again.Id, -1);
    }

    // progress's paused is a boolean (MSFP Media playing 3): the host hears
    // it; another type is refused.
    [Fact]
    public async Task ProgressPaused()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        var id = s.A.Engine.Playbacks()[0].Id;
        Assert.Equal(200, (await CallAsync(s, "progress", "{\"id\":\"" + id + "\",\"positionMs\":1000,\"paused\":true}")).Status);
        var last = h.Reports()[^1];
        Assert.True(last.Paused);
        Assert.Equal(1000, last.PositionMs);
        foreach (var v in new[] { "\"yes\"", "1" })
        {
            Assert.True((await CallAsync(s, "progress", "{\"id\":\"" + id + "\",\"paused\":" + v + "}")).Status == 400, v);
        }
    }

    // titles: libraries absent lists every library offered; kind and under
    // list only the titles that pass; under always answers the whole list
    // (MSFP Media listing 3 and 3.6).
    [Fact]
    public async Task TitlesFilters()
    {
        var h = new FakeHost();
        void Add(string key, string kind, Place? place, bool copies)
        {
            lock (h.Lock)
            {
                h.Titles[key] = new VideoTitle
                {
                    Key = key, Kind = kind, Libraries = ["films"], Place = place, Copies = copies ? FakeHost.OneCopy(key) : null,
                    ExtraType = kind == "extra" ? "trailer" : null,
                };
            }
        }

        h.Film("films", "movie/tmdb/1", "en");
        Add("movie/tmdb/1/x/trailer1", "extra", null, true);
        Add("series/tmdb/1399", "series", null, false);
        Add("series/tmdb/1399/s1", "season", null, false);
        Add("series/tmdb/1399/s1/e2", "episode", null, true);
        Add("series/tmdb/1399/e63056", "episode", new Place { Season = 1, Episode = 1 }, true);
        Add("series/tmdb/1399/e63099", "episode", new Place { Season = 2, Episode = 1 }, true);
        Add("series/tmdb/1399/s2", "season", null, false);
        Add("series/tmdb/1399/x/making", "extra", null, true);
        Add("series/tmdb/2", "series", null, false);
        Add("series/tmdb/2/s1", "season", null, false);
        using var s = await Sharing.StartAsync(h);

        async Task<(List<string> Keys, bool Changes, long Seq)> Keys(string body)
        {
            var (status, answer) = await CallAsync(s, "titles", body);
            Assert.True(status == 200, body);
            var a = JsonNode.Parse(answer)!.AsObject();
            var keys = a["titles"]!.AsArray().Select(t => (string)t!["key"]!).ToList();
            return (keys, (bool?)a["changes"] ?? false, (long?)a["seq"] ?? 0);
        }

        Assert.Equal(11, (await Keys("{\"after\":\"\"}")).Keys.Count);
        foreach (var (body, want) in new[]
        {
            ("{\"kind\":\"season\"}", "series/tmdb/1399/s1 series/tmdb/1399/s2 series/tmdb/2/s1"),
            ("{\"kind\":\"extra\"}", "movie/tmdb/1/x/trailer1 series/tmdb/1399/x/making"),
            ("{\"kind\":\"toaster\"}", ""),
            ("{\"under\":\"movie/tmdb/1\"}", "movie/tmdb/1/x/trailer1"),
            ("{\"under\":\"series/tmdb/1399\"}", "series/tmdb/1399/s1 series/tmdb/1399/s2 series/tmdb/1399/x/making"),
            ("{\"under\":\"series/tmdb/1399/s1\"}", "series/tmdb/1399/e63056 series/tmdb/1399/s1/e2"),
            ("{\"under\":\"series/tmdb/1399/s1\",\"kind\":\"extra\"}", ""),
            ("{\"under\":\"series/tmdb/1399/s1/e2\"}", ""),
            ("{\"under\":\"not a key\"}", ""),
            ("{\"under\":\"\"}", ""),
        })
        {
            Assert.Equal(want, string.Join(' ', (await Keys(body)).Keys));
        }

        foreach (var body in new[]
        {
            "{\"kind\":\"Season\"}", "{\"kind\":5}", "{\"under\":7}", "{\"libraries\":\"films\"}", "{\"libraries\":[7]}",
            "{\"under\":\"" + new string('a', 513) + "\"}",
        })
        {
            Assert.True((await CallAsync(s, "titles", body)).Status == 404, body);
        }

        // A seq kept: since with kind gives changes, since with under the
        // whole list.
        var first = await Keys("{\"kind\":\"season\"}");
        Assert.NotEqual(0, first.Seq);
        var since = ",\"since\":" + first.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        Assert.True((await Keys("{\"kind\":\"season\"" + since)).Changes);
        var under = await Keys("{\"under\":\"series/tmdb/2\"" + since);
        Assert.False(under.Changes);
        Assert.Equal(new[] { "series/tmdb/2/s1" }, under.Keys);
    }

    // The discovery document names the MSFP Media versions its parts carry
    // (MSFP core section 11).
    [Fact]
    public async Task DiscoveryMsfpMedia()
    {
        using var a = await TestServer.StartAsync(new Network(), "https://a.example.org", "Server A", discovery: true);
        var res = a.Engine.HandleDiscovery(new MsfpRequest
        {
            Method = "GET", Target = "/.well-known/msfp", Headers = new HeaderList(), Body = Stream.Null,
        });
        Assert.Equal(200, res.Status);
        Assert.Equal(new[] { 0 }, Discovery.Read(res.Body.Span).MsfpMedia);
    }
}
