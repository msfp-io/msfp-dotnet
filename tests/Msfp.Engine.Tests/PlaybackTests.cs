using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

// Playback both ways (msfp-go's playback_test.go): play, the choice of the
// copy and the tracks, the answer as the viewer's side keeps it, the media
// relayed, progress and stop, the limits counted in one step, and every way
// a playback ends.
public sealed class PlaybackTests
{
    private const string Film = "movie/tmdb/949";

    private static T Last<T>(TestServer s)
        where T : MsfpEvent => s.Events.Of<T>().Last();

    private static async Task<MemorySink> FetchAsync(TestServer s, string id, MediaPath path, string range = "")
    {
        var sink = new MemorySink();
        await s.Engine.FetchAsync(id, path, range, sink);
        return sink;
    }

    private static async Task<MsfpError> FetchErrorAsync(TestServer s, string id, MediaPath path, string range = "") =>
        (await Assert.ThrowsAsync<MsfpException>(() => s.Engine.FetchAsync(id, path, range, new MemorySink()))).Code;

    private static async Task<MsfpError> PlayErrorAsync(Sharing s, PlayRequest? r = null) =>
        (await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.PlayAsync(s.OnB, r ?? new PlayRequest { Title = Film }))).Code;

    private static async Task<int> CallAsync(Sharing s, string call, string body) =>
        (await s.B.Engine.CallForTestAsync(s.OnB, call, HostKit.Bytes(body))).Status;

    [Fact]
    public async Task PlayAndRelay()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest
        {
            Title = Film, AudioLanguage = "fr", SubtitleLanguage = "fr",
            Capabilities = new PlayCapabilities { Containers = ["MP4"], MaxHeight = 1000, MaxBitrateKbps = 12345 },
        });
        Assert.Equal(("direct", "hls", "a0", "s0"), (pb.Method, pb.Protocol, pb.AudioTrack, pb.SubtitleTrack));
        Assert.Equal(2, pb.Versions.Count);
        Assert.Equal(pb.Versions[1].Id, pb.VersionId);
        Assert.Equal(2, pb.SubtitleTracks.Count);
        Assert.Equal(120, pb.SubtitleStretchSeconds);
        Assert.False(pb.Id.StartsWith("play_", StringComparison.Ordinal));
        // A heard the French copy, the coarsened capabilities, the cap.
        var started = Assert.Single(h.Starts());
        Assert.Equal("movie/tmdb/949#1", started.Copy);
        Assert.Equal(12000, started.MaxBitrateKbps);
        Assert.Single(started.Capabilities.Containers);
        Assert.Equal(720, started.Capabilities.MaxHeight);
        var plays = s.Exchanges("play");
        Assert.Contains("\"containers\":[\"mp4\"]", plays[0].RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"maxBitrateKbps\":12000", plays[0].RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("movie/tmdb/949#", plays[0].ResponseBody, StringComparison.Ordinal);
        // The media, through B.
        var o = await FetchAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Master });
        Assert.Equal(HostKit.Master, o.Text);
        Assert.Equal("application/vnd.apple.mpegurl", o.ContentType);
        o = await FetchAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Segment, Name = "v0.m3u8" });
        Assert.Equal(HostKit.MediaList, o.Text); // by a name the master named
        o = await FetchAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Segment, Name = "seg0.m4s" }, "bytes=2-5");
        Assert.Equal((206, "2345", "bytes 2-5/20", "video/mp4"), (o.Status, o.Text, o.ContentRange, o.ContentType));
        Assert.Equal(MsfpError.RangeNotSatisfiable, await FetchErrorAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Segment, Name = "seg0.m4s" }, "bytes=0-1,4-5"));
        o = await FetchAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Subtitles, Track = "s0" });
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<i>Bonjour</i>\n", o.Text);
        Assert.Equal("text/vtt; charset=utf-8", o.ContentType);
        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Subtitles, Track = "s1" }));
        // Progress and stop.
        s.B.Engine.Progress(pb.Id, 60_000, true);
        await s.B.WaitAsync();
        var progress = Assert.Single(s.Exchanges("progress"));
        Assert.Equal(200, progress.Status);
        Assert.Contains("\"paused\":true", progress.RequestBody, StringComparison.Ordinal);
        var view = Assert.Single(s.A.Engine.Playbacks());
        Assert.Equal(("active", Film), (view.State, view.Title));
        Assert.NotEmpty(view.PeerName);
        await s.B.Engine.StopAsync(pb.Id, 61_000);
        await s.A.WaitAsync();
        Assert.Equal(EndReason.Stopped, h.Stops()["h1"]);
        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Master }));
        Assert.NotEmpty(s.A.Events.Of<PlaybackEnded>());
    }

    // play's malformed requests are refused with 400, a key not offered with
    // 404, nothing at all for a pairing with no streams, and a ticket works
    // only as MSFP says.
    [Fact]
    public async Task PlayRefusalsAndTickets()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        foreach (var body in new[]
        {
            "{}", "{\"title\":7}", "{\"title\":\"series/tmdb/1399\"}", "{\"title\":\"movie/tmdb/0949\"}",
            "{\"title\":\"movie/tmdb/949\",\"startPositionMs\":-1}", "{\"title\":\"movie/tmdb/949\",\"audioTrack\":\"a01\"}",
            "{\"title\":\"movie/tmdb/949\",\"subtitleTrack\":\"s007\"}", "{\"title\":\"movie/tmdb/949\",\"capabilities\":{\"containers\":[\"mp4\",1]}}",
            "{\"title\":\"movie/tmdb/949\",\"capabilities\":{\"containers\":\"mp4\"}}", "{\"title\":\"movie/tmdb/949\",\"capabilities\":\"mp4\"}",
            "{\"title\":\"movie/tmdb/949\",\"audioLanguage\":\"fr_CA\"}", "{\"title\":\"movie/tmdb/949\",\"maxBitrateKbps\":10000001}",
        })
        {
            Assert.True(await CallAsync(s, "play", body) == 400, body);
        }

        Assert.Equal(404, await CallAsync(s, "play", "{\"title\":\"movie/tmdb/9999999999\"}"));
        // Unknown values and members ignored; a track the copy hasn't, as absent.
        Assert.Equal(200, await CallAsync(s, "play", "{\"title\":\"movie/tmdb/949\",\"capabilities\":{\"containers\":[\"MP4\",\"x\"]},\"audioTrack\":\"a7\",\"other\":1}"));
        var got = h.Starts()[0];
        Assert.Empty(got.Capabilities.Containers);
        Assert.Equal("a0", got.AudioTrack);
        var url = (string)JsonNode.Parse(s.Exchanges("play")[^1].ResponseBody)!["stream"]!["url"]!;
        Assert.StartsWith(s.A.Address + "/api/v1/stream/", url, StringComparison.Ordinal);
        var target = url[s.A.Address.Length..];
        var streamBase = target[..^"master.m3u8".Length];
        Assert.Equal(200, await HostKit.GetAsync(s.A, target));
        var cases = new (string Target, string Method, string[] Headers, int Want)[]
        {
            (target + "?x=1", "GET", [], 404), (target, "GET", ["Origin", "https://example.org"], 404),
            (target, "GET", ["Sec-Fetch-Mode", "cors"], 404), (target, "GET", ["Sec-Fetch-Site", "same-origin"], 404),
            (target, "POST", [], 405), (target, "HEAD", [], 405), (streamBase + "file", "GET", [], 404),
            (streamBase + "subtitles/s0/whole", "HEAD", [], 405), (streamBase + "fonts/0", "GET", [], 404), (streamBase + "ass/s0/0", "GET", [], 404),
            (streamBase + "queue/0/master.m3u8", "GET", [], 404),
            (streamBase + "%2e%2e/x", "GET", [], 404), (streamBase + "seg0.m4s", "GET", ["Range", "bytes=-"], 416),
            (streamBase + "seg0.m4s", "GET", ["Range", "bytes=0-1,3-4"], 416), (streamBase + "seg0.m4s", "GET", ["Range", "bytes=0-3"], 206),
            (streamBase + "seg0.m4s", "GET", ["Range", "bytes=40-"], 416), (streamBase + "v0.m3u8", "GET", ["Range", "bytes=0-3"], 200),
            (streamBase + "trickplay/0", "GET", [], 404), (streamBase + "subtitles/s0/01", "GET", [], 404),
            (streamBase + "subtitles/s0/0", "GET", [], 200), (streamBase + "pictures/s0/0", "GET", [], 404),
            ("/api/v1/stream/" + new string('A', 43) + "/master.m3u8", "GET", [], 404),
        };
        foreach (var c in cases)
        {
            var status = await HostKit.GetAsync(s.A, c.Target, c.Method, c.Headers);
            Assert.True(status == c.Want, $"{c.Target} {c.Method} {string.Join(" ", c.Headers)}: {status}, want {c.Want}");
        }

        // No stream at all for this pairing: refused before anything else.
        await s.A.Engine.SetLimitsAsync(s.OnA, new Limits());
        Assert.Equal(409, await CallAsync(s, "play", "{\"title\":\"movie/tmdb/949\"}"));
    }

    // Two plays that arrive together never both take the last stream; a
    // transcode past the pairing's transcodes is 503.
    [Fact]
    public async Task LimitsAtOnce()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h, c => c with { NewPairing = new Limits { Streams = 1, Transcodes = 0 } });
        var hold = new TaskCompletionSource();
        h.Hold = hold.Task;
        var tries = Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
                return MsfpError.Internal;
            }
            catch (MsfpException e)
            {
                return e.Code;
            }
        }).ToList();
        await Task.Delay(200);
        hold.SetResult();
        var results = await Task.WhenAll(tries);
        Assert.Single(results, r => r == MsfpError.Internal);
        Assert.Single(results, r => r == MsfpError.StreamLimitReached);
        foreach (var v in s.A.Engine.Playbacks())
        {
            s.A.Engine.StopPlayback(v.Id, EndReason.Operator);
        }

        lock (h.Lock)
        {
            h.Hold = null;
            h.Method = "transcode";
        }

        Assert.Equal(MsfpError.ServerBusy, await PlayErrorAsync(s));
        // A host that doesn't call Admit: counted after, refused the same.
        lock (h.Lock)
        {
            h.Method = "direct";
            h.NoAdmit = true;
        }

        await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        Assert.Equal(MsfpError.StreamLimitReached, await PlayErrorAsync(s));
        await s.A.WaitAsync();
        Assert.NotEmpty(h.Stops());
    }

    // Every ending of playback section 6.3, each telling both sides.
    [Fact]
    public async Task PlaybackEndings()
    {
        var clock = new FakeTime(1800000000);
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h, clock: clock);
        async Task<RemotePlayback> Play() => await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        async Task<EndReason> Reason()
        {
            await s.A.WaitAsync();
            return Last<PlaybackEnded>(s.A).Reason;
        }

        // Expiry: two minutes without use.
        var pb = await Play();
        clock.Advance(TimeSpan.FromSeconds(119));
        await s.A.Engine.RunOnceAsync();
        Assert.Single(s.A.Engine.Playbacks());
        clock.Advance(TimeSpan.FromSeconds(2));
        await s.A.Engine.RunOnceAsync();
        Assert.Equal(EndReason.Expired, await Reason());
        Assert.Empty(s.A.Engine.Playbacks());
        // The viewer's side learns at its next progress: 410.
        s.B.Engine.Progress(pb.Id, 1000, true);
        await s.B.WaitAsync();
        Assert.Equal(pb.Id, Last<RemotePlaybackEnded>(s.B).Playback);
        // Too long.
        await Play();
        for (var i = 0; i < 25; i++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            foreach (var v in s.A.Engine.Playbacks())
            {
                s.A.Engine.TouchForTest(v.Id);
            }

            await s.A.Engine.RunOnceAsync();
        }

        Assert.Equal(EndReason.TooLong, await Reason());
        // The library no longer offered.
        pb = await Play();
        await s.A.Engine.SetOffersAsync(s.OnA, []);
        Assert.Equal(EndReason.NotOffered, await Reason());
        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Master }));
        // The operator.
        await s.A.Engine.SetOffersAsync(s.OnA, ["films"]);
        await Play();
        s.A.Engine.StopPlayback(s.A.Engine.Playbacks()[0].Id, EndReason.Operator);
        Assert.Equal(EndReason.Operator, await Reason());
        // The pairing ended, by the viewer's side: both end.
        pb = await Play();
        await s.B.Engine.EndAsync(s.OnB);
        await s.B.WaitAsync();
        Assert.Equal(EndReason.PairingEnded, await Reason());
        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Master }));
        Assert.Equal(5, h.Stops().Count);
    }

    // After a restart, the playbacks left on peers are stopped.
    [Fact]
    public async Task LeftoversStopped()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        s.B.Dispose();
        s.B = await TestServer.StartAsync(s.Net, s.B.Address, "Server B", store: s.B.Store, clock: s.B.Clock);
        await s.B.Engine.RunOnceAsync();
        await s.A.WaitAsync();
        Assert.Empty(s.A.Engine.Playbacks());
        Assert.Single(s.Exchanges("stop"));
        IReadOnlyList<Leftover> left = [];
        await s.B.Store.ReadAsync(async tx => left = await tx.LeftoversAsync(), CancellationToken.None);
        Assert.Empty(left);
    }

    // A peer that says it can't be called is never fetched from: its
    // playbacks end, its titles go, and nothing connects to it.
    [Fact]
    public async Task NoMediaFromUncallable()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        // A can't be called now; its scheduled hello tells B.
        await s.A.Engine.SetUncallableAsync(true);
        await s.A.Engine.RunOnceAsync();
        await s.WaitAsync();
        Assert.Equal(Inbound.No, (await s.B.Engine.PeerAsync(s.OnB)).Inbound);
        await s.B.WaitAsync();
        var before = s.Net.SeenBy(s.A.Host).Count;
        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s.B, pb.Id, new MediaPath { Kind = MediaKind.Master }));
        Assert.Equal(MsfpError.Uncallable, await PlayErrorAsync(s));
        Assert.Equal(MsfpError.Uncallable, (await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.ReadNowAsync(s.OnB))).Code);
        Assert.False(await s.B.Engine.PlayableAsync(s.OnB, "movie"));
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.PlayableAsync("nobody", "movie"))).Code);
        Assert.Equal(before, s.Net.SeenBy(s.A.Host).Count);
        IReadOnlyList<Title> titles = [];
        await s.B.Store.ReadAsync(async tx => titles = await tx.PeerTitlesAsync(s.OnB, "", 10), CancellationToken.None);
        Assert.Empty(titles);
    }

    // A music track plays audio only, counted apart when the limits say so.
    [Fact]
    public async Task MusicPlay()
    {
        var h = new FakeHost { Libs = [new Library { Id = "music", Name = "Music", Kind = "music" }], Protocol = "file" };
        const string Track = "track/mb/0c000000-0000-4000-8000-000000000003";
        h.Titles[Track] = HostKit.MusicTrack(Track, "t");
        h.Info["t"] = new CopyInfo
        {
            Fonts = 2,
            Trickplay = new Trickplay { TileWidth = 10, TileHeight = 10, Columns = 1, Rows = 1, IntervalMs = 1000, TileCount = 1 },
        };
        using var s = await Sharing.StartAsync(h, c => c with { NewPairing = new Limits { Streams = 0, Audio = true, AudioStreams = 1 } });
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Track, SubtitleTrack = "s0" });
        Assert.True(h.Starts()[0].AudioOnly);
        Assert.Empty(pb.SubtitleTracks);
        Assert.Null(pb.Trickplay);
        Assert.Equal("file", pb.Protocol);
        Assert.DoesNotContain("subtitle", s.Exchanges("play")[0].ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("subtitle", s.Exchanges("play")[0].RequestBody, StringComparison.Ordinal);
        // One audio stream: a second track waits for the first's stop.
        Assert.Equal(MsfpError.StreamLimitReached, await PlayErrorAsync(s, new PlayRequest { Title = Track }));
        await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Track, Replaces = pb.Id });
        // A film counts in streams: 0 here.
        h.Film("music", "movie/tmdb/1", "en");
        Assert.Equal(409, await CallAsync(s, "play", "{\"title\":\"movie/tmdb/1\"}"));
        Assert.Contains("\"audioStreams\":1", s.Exchanges("libraries")[0].ResponseBody, StringComparison.Ordinal);
    }

    // The host's playlists are checked before they leave: one outside the
    // subset is refused, with a warning, not sent.
    [Fact]
    public async Task OwnPlaylistChecked()
    {
        var h = HostKit.FilmHost();
        h.Files["master.m3u8"] = HostKit.Bytes("#EXTM3U\n#EXT-X-KEY:METHOD=NONE\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n");
        h.Files["v0.m3u8"] = HostKit.Bytes("#EXTM3U\n#EXT-X-PROGRAM-DATE-TIME:2020\n#EXTINF:6,\nseg0.m4s\n");
        using var s = await Sharing.StartAsync(h);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        foreach (var p in new[] { new MediaPath { Kind = MediaKind.Master }, new MediaPath { Kind = MediaKind.Playlist, Name = "v0.m3u8" } })
        {
            Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s.B, pb.Id, p));
        }
    }

    // progress and stop of a playback not live, or another's; and an open
    // response cut when its playback ends, on both sides.
    [Fact]
    public async Task StatesAndCuts()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        const string Unknown = "{\"id\":\"play_aaaaaaaaaaaaaaaaaaaaaaaaaa\"}";
        foreach (var (call, body, want) in new[]
        {
            ("progress", Unknown, 410), ("stop", Unknown, 200), ("progress", "{\"id\":\"play_x\"}", 400),
            ("progress", "{}", 400), ("stop", "{\"id\":\"play_aaaaaaaaaaaaaaaaaaaaaaaaaa\",\"positionMs\":-1}", 400),
            ("progress", "{\"id\":\"play_aaaaaaaaaaaaaaaaaaaaaaaaaa\",\"positionMs\":8640000001}", 400),
        })
        {
            Assert.True(await CallAsync(s, call, body) == want, $"{call} {body}");
        }

        // A stopped playback: progress 410, stop 200 again.
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        var theirs = (string)JsonNode.Parse(s.Exchanges("play")[0].ResponseBody)!["id"]!;
        Assert.StartsWith("play_", theirs, StringComparison.Ordinal);
        Assert.Equal(31, theirs.Length);
        Assert.Equal(200, await CallAsync(s, "stop", "{\"id\":\"" + theirs + "\"}"));
        Assert.Equal(200, await CallAsync(s, "stop", "{\"id\":\"" + theirs + "\"}"));
        Assert.Equal(410, await CallAsync(s, "progress", "{\"id\":\"" + theirs + "\"}"));
        await s.B.Engine.StopAsync(pb.Id, -1);
        // A response held open: cut when the playback ends (the copy long
        // enough that its bytes don't end first).
        var cut = new TaskCompletionSource();
        h.EditCopy(Film, 0, c => c with { Size = 1L << 40 });
        h.EditCopy(Film, 1, c => c with { Size = 1L << 40 });
        lock (h.Lock)
        {
            h.Protocol = "file";
            h.Serve = async (_, _, output, ct) =>
            {
                output.Start(200, "video/mp4", -1, "");
                while (true)
                {
                    try
                    {
                        await output.WriteAsync("x"u8.ToArray(), ct);
                        await Task.Delay(5, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        cut.TrySetResult();
                        return;
                    }
                }
            };
        }

        pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Capabilities = new PlayCapabilities { Containers = ["mp4"] } });
        var done = s.B.Engine.FetchAsync(pb.Id, new MediaPath { Kind = MediaKind.File }, "", new MemorySink());
        await Task.Delay(100);
        if (s.A.Engine.Playbacks() is [var v])
        {
            s.A.Engine.StopPlayback(v.Id, EndReason.Operator);
        }

        await cut.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // The relay is cut too; what it says of the cut answer doesn't matter.
            await done.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (MsfpException)
        {
        }
    }

    // The viewer's side tells the owning server where its viewer is at the
    // first report, at once on a pause or a resume, and every 45 seconds
    // while the host reports, never more often (playback section 3);
    // nothing once the host stops reporting.
    [Fact]
    public async Task ProgressPace()
    {
        var clock = new FakeTime(1800000000);
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h, clock: clock);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        async Task<List<long>> Sent()
        {
            await s.WaitAsync();
            await s.WaitAsync();
            return h.Progresses();
        }

        async Task Report(TimeSpan after, long position, bool paused)
        {
            clock.Advance(after);
            s.B.Engine.Progress(pb.Id, position, paused);
            await s.B.Engine.RunOnceAsync();
        }

        var ten = TimeSpan.FromSeconds(10);
        await Report(ten, 10_000, false);
        Assert.Equal([10_000L], await Sent());
        await Report(ten, 20_000, false);
        await Report(ten, 30_000, false);
        await Report(ten, 40_000, false);
        await Report(ten, 50_000, false);
        Assert.Equal([10_000L], await Sent());
        // 45 seconds after the first, with no report at that time: sent by
        // the work of each second, with the latest position.
        clock.Advance(TimeSpan.FromSeconds(5));
        await s.B.Engine.RunOnceAsync();
        Assert.Equal([10_000L, 50_000], await Sent());
        // A pause and a resume, each at once.
        await Report(TimeSpan.FromSeconds(1), 51_000, true);
        await Report(TimeSpan.FromSeconds(1), 51_000, false);
        Assert.Equal([10_000L, 50_000, 51_000, 51_000], await Sent());
        // The host reports no more (the viewer's browser gone): nothing is
        // sent for it.
        clock.Advance(TimeSpan.FromMinutes(2));
        await s.B.Engine.RunOnceAsync();
        Assert.Equal(4, (await Sent()).Count);
    }

    // A host ending a peer's playback by its own rules says why: the peer's
    // ending is that one; a reason that isn't the host's is refused.
    [Fact]
    public async Task StopPlaybackReason()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        var id = s.A.Engine.Playbacks()[0].Id;
        Assert.Equal(MsfpError.InvalidReason, Assert.Throws<MsfpException>(() => s.A.Engine.StopPlayback(id, EndReason.Stopped)).Code);
        s.A.Engine.StopPlayback(id, EndReason.Expired);
        await s.A.WaitAsync();
        Assert.Equal(EndReason.Expired, Last<PlaybackEnded>(s.A).Reason);
        Assert.Equal(EndReason.Expired, h.Stops()["h1"]);
        Assert.Equal(MsfpError.NotFound, Assert.Throws<MsfpException>(() => s.A.Engine.StopPlayback(id, EndReason.Expired)).Code);
    }

    // Play's errors: what lasts says itself (here, B's key lost toward A,
    // which never moved to it); only no answer, or none signed, is busy now
    // (playback section 2.5).
    [Fact]
    public async Task PlayErrorsToldApart()
    {
        var clock = new FakeTime(1800000000);
        using var s = await Sharing.StartAsync(HostKit.FilmHost(), clock: clock);
        s.Net.Down(s.A.Host, true);
        Assert.Equal(MsfpError.ServerBusy, await PlayErrorAsync(s));
        // B rotates twice while A is down: A never knew B's new keys.
        for (var i = 0; i < 2; i++)
        {
            await s.B.Engine.RotateAsync();
            clock.Advance(TimeSpan.FromHours(1));
        }

        s.Net.Down(s.A.Host, false);
        Assert.Equal(MsfpEngine.KeyLost, (await s.B.Engine.PeerAsync(s.OnB)).OurKey);
        Assert.Equal(MsfpError.KeyLost, await PlayErrorAsync(s));
    }

    // The pairing ending while a playback starts ends it as the pairing's
    // end, not as a copy no longer shared (playback section 6.3).
    [Fact]
    public async Task PairingEndsWhilePlayStarts()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        var hold = new TaskCompletionSource();
        var entered = new SemaphoreSlim(0);
        lock (h.Lock)
        {
            h.Hold = hold.Task;
            h.Entered = entered;
        }

        var done = s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        await s.A.Engine.EndAsync(s.OnA);
        hold.SetResult();
        await Assert.ThrowsAsync<MsfpException>(() => done);
        await s.A.WaitAsync();
        Assert.Equal(EndReason.PairingEnded, h.Stops()["h1"]);
    }
}
