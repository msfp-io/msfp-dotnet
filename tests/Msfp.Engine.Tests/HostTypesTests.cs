using System.Text.Json;

namespace Msfp.Tests;

// Config defaults, errors, events, and the records' JSON.
public sealed class HostTypesTests
{
    // The defaults are Go's withDefaults, number for number.
    [Fact]
    public void ConfigDefaults()
    {
        using var store = new Stores.MemoryStore();
        var c = new MsfpConfig { Address = "https://a.example", Store = store }.WithDefaults();
        Assert.Same(TimeProvider.System, c.TimeProvider);
        Assert.NotNull(c.Random);
        Assert.NotNull(c.Logger);
        Assert.Same(PlainSealer.Instance, c.Sealer);
        Assert.Equal(new Limits { Streams = 2, Transcodes = 1 }, c.NewPairing);
        Assert.Equal(TimeSpan.FromHours(24), c.LongestPlayback);
        Assert.Equal(TimeSpan.FromMinutes(30), c.SyncEvery);
        Assert.Equal(new CallLimits
        {
            AddressRate = 50, AddressBurst = 400, PeerRate = 20, PeerBurst = 200, AllRate = 200, AllBurst = 2000,
            PlayRate = 0.5, PlayBurst = 20, PeerAtOnce = 8, AllAtOnce = 64, QueueWait = TimeSpan.FromSeconds(5),
        }, c.Limits);

        var set = new MsfpConfig { Address = "", Store = store, Limits = new CallLimits { PeerRate = 3 }, SyncEvery = TimeSpan.FromMinutes(1) }.WithDefaults();
        Assert.Equal(3, set.Limits.PeerRate);
        Assert.Equal(50, set.Limits.AddressRate);
        Assert.Equal(TimeSpan.FromMinutes(1), set.SyncEvery);
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal("msfp: PlainHTTP", new MsfpException(MsfpError.PlainHttp).Message);
        Assert.Equal("msfp: Sealed: why", new MsfpException(MsfpError.Sealed, "why").Message);
        var inner = new IOException("disk");
        var e = new MsfpException(MsfpError.Internal, null, inner);
        Assert.Equal("msfp: Internal: disk", e.Message);
        Assert.Same(inner, e.InnerException);
        Assert.Throws<MsfpException>(() => new Limits { Streams = 1, Transcodes = 2 }.Check());
        new Limits { Streams = 50, Transcodes = 50, BitrateKbps = 1_000_000 }.Check();
    }

    [Theory]
    [InlineData(404, "not-supported", MsfpError.NotSupported)]
    [InlineData(404, "not-found", MsfpError.NotFound)]
    [InlineData(404, "", MsfpError.NotFound)]
    [InlineData(400, "invalid-request", MsfpError.InvalidRequest)]
    [InlineData(409, "rotation-refused", MsfpError.RotationRefused)]
    [InlineData(409, "not-playable", MsfpError.NotPlayable)]
    [InlineData(409, "stream-limit-reached", MsfpError.StreamLimitReached)]
    [InlineData(409, "other", MsfpError.StreamLimitReached)]
    [InlineData(410, "playback-stopped", MsfpError.PlaybackStopped)]
    [InlineData(429, "rate-limited", MsfpError.RateLimited)]
    [InlineData(503, "server-busy", MsfpError.ServerBusy)]
    [InlineData(500, "internal-error", MsfpError.PeerFailed)]
    [InlineData(418, "", MsfpError.Unreachable)]
    public void Refusals(int status, string type, MsfpError code) => Assert.Equal(code, Problems.RefusalError(status, type).Code);

    [Fact]
    public void ProblemStatuses()
    {
        Assert.Equal(10, Problems.ByType.Count);
        Assert.Equal((409, "Rotation refused"), Problems.ByType[ProblemType.Rotation]);
        Assert.Equal(503, Problems.ByType[ProblemType.ServerBusy].Status);
    }

    [Fact]
    public void Events()
    {
        Assert.Equal("p1", new PeerWaiting("p1", "One", "FP").PeerId);
        Assert.Null(new RotationEnded(2).PeerId);
        Assert.Null(new AddressAnnounced("https://a").PeerId);
        Assert.Null(new MoveEnded(0).PeerId);
        MsfpEvent e = new InboundChanged("p2", "Two", Inbound.No);
        Assert.Equal("p2", e.PeerId);
    }

    // Records write as Go's: empty members left out, times in UTC with "Z"
    // and the fraction trimmed, states as their strings.
    [Fact]
    public void RecordJson()
    {
        var p = new PeerRecord
        {
            Id = "p1",
            Key = [1, 2],
            Created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)).AddTicks(1200000),
            State = PeerState.Ours,
            Inbound = Inbound.Confirming,
            Pins = [],
        };
        var json = JsonSerializer.Serialize(p, MsfpJson.Options);
        Assert.Equal(
            """{"id":"p1","key":"AQI=","name":"","created":"2026-01-02T08:04:05.12Z","state":"ours","address":"","inbound":"confirming","limits":{"streams":0,"transcodes":0},"catalogRead":{}}""",
            json);
        var back = JsonSerializer.Deserialize<PeerRecord>(json, MsfpJson.Options)!;
        Assert.Equal(p.Created, back.Created);
        Assert.Equal(PeerState.Ours, back.State);
        Assert.Null(back.Pins);

        Assert.Equal("""{"streams":1,"transcodes":0,"audio":true}""", JsonSerializer.Serialize(new Limits { Streams = 1, Audio = true }, MsfpJson.Options));
        Assert.Equal(EndReason.TooLong, JsonSerializer.Deserialize<EndReason>("\"tooLong\""));
    }

    [Fact]
    public void Titles()
    {
        var video = new VideoTitle { Key = "movie/tmdb/1", Kind = "movie", Libraries = ["L"], Ids = new() { ["imdb"] = "tt1" } };
        var json = TitleJson.Marshal(video);
        Assert.Equal("""{"video":{"key":"movie/tmdb/1","kind":"movie","libraries":["L"],"ids":{"imdb":"tt1"}}}""", System.Text.Encoding.UTF8.GetString(json));
        var back = Assert.IsType<VideoTitle>(TitleJson.Unmarshal(json));
        Assert.Equal("tt1", back.Ids!["imdb"]);
        Assert.Throws<JsonException>(() => TitleJson.Unmarshal("{}"u8));
    }

    // Titles with their copies, as the Go engine writes them (msfp-go's
    // MarshalTitle at the revision of version 0): read as they are, written
    // back the same, member for member.
    [Theory]
    [InlineData("""{"video":{"key":"movie/tmdb/1","kind":"movie","libraries":["L1"],"copies":[{"id":"ver_x","name":"4K","durationMs":1000,"startMs":5,"container":"mkv","size":9,"etag":"e1","video":{"codec":"hevc","width":3840,"height":2160,"hdr":["dv"]},"audioTracks":[{"id":"a0","language":"fr","channels":6,"default":true,"codec":"eac3"}],"subtitleTracks":[{"id":"s0","language":"en","forced":true,"format":"subrip"}],"chapters":[{"startMs":0,"name":"One"}]}]}}""")]
    [InlineData("""{"music":{"key":"track/mb/x","kind":"track","libraries":null,"name":"Song","copies":[{"id":"ver_y","durationMs":1000,"container":"flac","codec":"flac","size":9,"etag":"e2","sampleRate":44100}]}}""")]
    public void TitlesWithCopies(string go)
    {
        var t = TitleJson.Unmarshal(System.Text.Encoding.UTF8.GetBytes(go));
        if (t is VideoTitle v)
        {
            var c = Assert.Single(v.Copies!);
            Assert.Equal(("ver_x", "4K", 5L, "e1", 2160), (c.Id, c.Name, c.StartMs, c.ETag, c.Video!.Height));
            Assert.Equal(6, c.AudioTracks[0].Channels);
            Assert.True(c.SubtitleTracks[0].Forced);
            Assert.Equal("One", c.Chapters[0].Name);
            Assert.Equal(["fr"], v.AudioLanguages());
        }
        else
        {
            var c = Assert.Single(Assert.IsType<MusicTitle>(t).Copies!);
            Assert.Equal(("ver_y", "e2", 44100), (c.Id, c.Etag, c.SampleRate));
        }

        var back = System.Text.Json.Nodes.JsonNode.Parse(TitleJson.Marshal(t));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(go), back), back!.ToJsonString());
    }

    // A host's store implements the two plain calls; the forms that give a
    // value back come with the interface.
    private sealed class HostStore(IStore inner) : IStore
    {
        public ValueTask ReadAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct) => inner.ReadAsync(fn, ct);

        public ValueTask UpdateAsync(Func<IStoreTx, ValueTask> fn, CancellationToken ct) => inner.UpdateAsync(fn, ct);
    }

    [Fact]
    public async Task StoreValueForms()
    {
        var ct = TestContext.Current.CancellationToken;
        using var memory = new Stores.MemoryStore();
        IStore st = new HostStore(memory);
        var id = await st.UpdateAsync(async tx =>
        {
            await tx.PutLibraryIdAsync("h", "p");
            return "p";
        }, ct);
        Assert.Equal("p", id);
        Assert.Equal("p", await st.ReadAsync(async tx => (await tx.LibraryIdsAsync())["h"], ct));
    }
}
