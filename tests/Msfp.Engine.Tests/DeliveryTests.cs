using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

// The revision of version 0 (proposal 16; msfp-go's delivery_test.go):
// deliveries and their refusals, the file by byte ranges, restarts on the
// same etag, downloads and scans within their limits, a music queue,
// previews and lyrics before any playback, ASS and fonts.
public sealed class DeliveryTests
{
    private const string Film = "movie/tmdb/949";

    private static async Task<MsfpError> ErrorOf(Func<Task> f) => (await Assert.ThrowsAsync<MsfpException>(f)).Code;

    private static async Task<int> CallAsync(Sharing s, string call, string body) =>
        (await s.B.Engine.CallForTestAsync(s.OnB, call, HostKit.Bytes(body))).Status;

    // The stream URL of play answer n of A (the last by default), as a target on its stream route.
    private static string StreamOf(Sharing s, int n = -1)
    {
        var plays = s.Exchanges("play");
        var url = (string)JsonNode.Parse(plays[n < 0 ? plays.Count - 1 : n].ResponseBody)!["stream"]!["url"]!;
        return url[s.A.Address.Length..];
    }

    private static async Task<MemorySink> FetchAsync(Sharing s, string id, MediaPath p, string range = "")
    {
        var sink = new MemorySink();
        await s.B.Engine.FetchAsync(id, p, range, sink);
        return sink;
    }

    private static Task<MsfpError> FetchErrorAsync(Sharing s, string id, MediaPath p, string range = "") =>
        ErrorOf(() => s.B.Engine.FetchAsync(id, p, range, new MemorySink()));

    [Fact]
    public async Task Deliveries()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        async Task<RemotePlayback> Play(PlayRequest r)
        {
            var pb = await s.B.Engine.PlayAsync(s.OnB, r);
            await s.B.Engine.StopAsync(pb.Id, -1);
            return pb;
        }

        // File: the copy as it is, its etag and exact size.
        var pb = await Play(new PlayRequest { Title = Film, Delivery = Playback.DeliveryFile });
        Assert.Equal(("file", "direct", "e0", 20L, false), (pb.Protocol, pb.Method, pb.Etag, pb.Size, pb.CopyChanged));
        Assert.Equal((Playback.DeliveryFile, Playback.PurposePlay), (h.Starts()[^1].Delivery, h.Starts()[^1].Purpose));
        // Remux needs a container; the host is told it.
        Assert.Equal(MsfpError.InvalidRequest, await ErrorOf(() => Play(new PlayRequest { Title = Film, Delivery = Playback.DeliveryRemux })));
        pb = await Play(new PlayRequest { Title = Film, Delivery = Playback.DeliveryRemux, Container = "mp4" });
        Assert.Equal(("remux", "mp4"), (pb.Method, h.Starts()[^1].Container));
        // A subtitle burned in, of the copy; another refused by MSFP itself.
        pb = await Play(new PlayRequest
        {
            Title = Film, Delivery = Playback.DeliveryTranscode, Transcode = new TranscodeTargets { BurnSubtitle = "s0", MaxHeight = 1000 },
        });
        Assert.Equal(("s0", "s0", 720), (pb.BurnedTrack, pb.SubtitleTrack, h.Starts()[^1].Transcode!.MaxHeight));
        var before = h.Starts().Count;
        Assert.Equal(MsfpError.DeliveryRefused, await ErrorOf(() => Play(new PlayRequest
        {
            Title = Film, Delivery = Playback.DeliveryTranscode, Transcode = new TranscodeTargets { BurnSubtitle = "s9" },
        })));
        // The host's own refusal.
        lock (h.Lock)
        {
            h.StartError = new MsfpException(MsfpError.DeliveryRefused);
        }

        Assert.Equal(MsfpError.DeliveryRefused, await ErrorOf(() => Play(new PlayRequest { Title = Film, Delivery = Playback.DeliveryTranscode })));
        lock (h.Lock)
        {
            h.StartError = null;
        }

        // A file above the pairing's bitrate cap.
        h.EditCopy(Film, 0, c => c with { BitrateKbps = 50_000 });
        await s.A.Engine.SetLimitsAsync(s.OnA, new Limits { Streams = 2, Transcodes = 1, BitrateKbps = 10_000 });
        Assert.Equal(MsfpError.DeliveryRefused, await ErrorOf(() => Play(new PlayRequest { Title = Film, Delivery = Playback.DeliveryFile })));
        Assert.Equal(before, h.Starts().Count);
        // Combinations of purpose and delivery MSFP refuses, from the peer.
        foreach (var body in new[]
        {
            "{\"title\":\"movie/tmdb/949\",\"purpose\":\"scan\",\"delivery\":\"remux\",\"container\":\"mp4\"}",
            "{\"title\":\"movie/tmdb/949\",\"purpose\":\"download\",\"delivery\":\"transcode\"}", "{\"title\":\"movie/tmdb/949\",\"delivery\":\"hls\"}",
            "{\"title\":\"movie/tmdb/949\",\"transcode\":{\"maxHeight\":720}}",
            "{\"title\":\"movie/tmdb/949\",\"delivery\":\"transcode\",\"transcode\":{\"maxHeight\":719}}",
            "{\"title\":\"movie/tmdb/949\",\"delivery\":\"transcode\",\"transcode\":{\"burnSubtitle\":\"off\"}}",
        })
        {
            Assert.True(await CallAsync(s, "play", body) == 400, body);
        }
    }

    // The file by byte ranges (media sections 2.1, 2.2 and 3.2), on the
    // owning side and through the relay.
    [Fact]
    public async Task FileRanges()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        var x = new RelayTests.Hostile(s);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Delivery = Playback.DeliveryFile });
        var target = StreamOf(s);
        foreach (var (hdr, status, body, cr, len) in new (string[], int, string, string?, string)[]
        {
            ([], 200, "0123456789abcdefghij", null, "20"),
            (["Range", "bytes=2-5"], 206, "2345", "bytes 2-5/20", "4"),
            (["Range", "bytes=-3"], 206, "hij", "bytes 17-19/20", "3"),
            (["Range", "bytes=15-99"], 206, "fghij", "bytes 15-19/20", "5"),
            (["Range", "bytes=20-"], 416, "", "bytes */20", ""),
            (["Range", "bytes=0-1,3-4"], 416, "", "bytes */20", ""),
            (["Range", "bytes=0-1", "Range", "bytes=3-4"], 416, "", "bytes */20", ""),
        })
        {
            var res = await HostKit.GetFullAsync(s.A, target, "GET", hdr);
            var what = string.Join(" ", hdr);
            Assert.True(res.Status == status, what + ": " + res.Status);
            Assert.True(System.Text.Encoding.UTF8.GetString(res.Body.Span) == body, what);
            Assert.True(res.Header("Content-Range") == cr, what + ": " + res.Header("Content-Range"));
            Assert.True(len.Length == 0 || res.Header("Content-Length") == len, what + ": " + res.Header("Content-Length"));
            if (status != 416)
            {
                Assert.Equal(("\"e0\"", "bytes"), (res.Header("ETag"), res.Header("Accept-Ranges")));
            }
        }

        foreach (var m in new[] { "HEAD", "POST", "PUT" })
        {
            var res = await HostKit.GetFullAsync(s.A, target, m);
            Assert.Equal((405, "GET"), (res.Status, res.Header("Allow")));
        }

        // Through the relay.
        var file = new MediaPath { Kind = MediaKind.File };
        var o = await FetchAsync(s, pb.Id, file, "bytes=2-5");
        Assert.Equal((206, "2345", "bytes 2-5/20"), (o.Status, o.Text, o.ContentRange));
        Assert.Equal("e0", o.ETag);
        o = await FetchAsync(s, pb.Id, file);
        Assert.Equal((200, "0123456789abcdefghij"), (o.Status, o.Text));
        Assert.Equal(MsfpError.RangeNotSatisfiable, await FetchErrorAsync(s, pb.Id, file, "bytes=30-"));
        // Another etag, another size, a 200 to a range: the copy changed.
        foreach (var (name, f) in new (string, Func<HeaderList, byte[], byte[]>)[]
        {
            ("another etag", (hl, b) => { hl.Set("ETag", "\"e9\""); return b; }),
            ("a weak etag", (hl, b) => { hl.Set("ETag", "W/\"e0\""); return b; }),
            ("another size", (hl, b) => { hl.Set("Content-Range", "bytes 2-5/21"); return b; }),
            ("a 200", (hl, b) => { hl.Remove("Content-Range"); return HostKit.Bytes("0123456789abcdefghij"); }),
        })
        {
            x.Set("file", f);
            Assert.True(await FetchErrorAsync(s, pb.Id, file, "bytes=2-5") == MsfpError.NotFound, name);
        }
    }

    // A file being read whose playback ended starts again on the same etag,
    // and only on it (playback section 2.7).
    [Fact]
    public async Task Restart()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        var old = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Delivery = Playback.DeliveryFile });
        s.A.Engine.StopPlayback(s.A.Engine.Playbacks()[0].Id, EndReason.Expired);
        await s.A.WaitAsync();
        var again = await s.B.Engine.RestartAsync(old);
        Assert.NotEqual(old.Id, again.Id);
        Assert.Equal((old.Etag, old.VersionId, 20L), (again.Etag, again.VersionId, again.Size));
        Assert.Equal((Playback.DeliveryFile, Film + "#0"), (h.Starts()[^1].Delivery, h.Starts()[^1].Copy));
        Assert.Equal("abcdefghij", (await FetchAsync(s, again.Id, new MediaPath { Kind = MediaKind.File }, "bytes=10-")).Text);
        // The copy changed meanwhile: no join, the new playback stopped.
        h.EditCopy(Film, 0, c => c with { ETag = "e9" });
        var stops = s.Exchanges("stop").Count;
        Assert.Equal(MsfpError.CopyChanged, await ErrorOf(() => s.B.Engine.RestartAsync(again)));
        await s.WaitAsync();
        Assert.Equal(2, s.Exchanges("stop").Count - stops);
        Assert.Empty(s.A.Engine.Playbacks());
        Assert.Equal(MsfpError.InvalidRequest, await ErrorOf(() => s.B.Engine.RestartAsync(new RemotePlayback { Peer = s.OnB, Title = Film, Etag = "e0" })));
    }

    // Downloads: only from a peer that allows them, within its limits at
    // once and a day, their ticket without the idle rule, their late
    // reports after their end (playback section 7).
    [Fact]
    public async Task Downloads()
    {
        var clock = new FakeTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var h = HostKit.FilmHost();
        var limits = new Limits { Streams = 2, Transcodes = 1, Downloads = 1, DownloadsPerDay = 2, DownloadKbps = 100_000 };
        using var s = await Sharing.StartAsync(h, c => c with { NewPairing = limits }, clock: clock);
        PeerRecord? onB = null;
        await s.B.Store.ReadAsync(async tx => onB = await tx.PeerAsync(s.OnB), CancellationToken.None);
        Assert.Equal((1, 2), (onB!.TheirLimits?.Downloads, onB.TheirLimits?.DownloadsPerDay));
        Assert.Contains("\"downloads\":1,\"downloadsPerDay\":2,\"downloadKbps\":100000", s.Exchanges("libraries")[0].ResponseBody, StringComparison.Ordinal);
        Task<RemotePlayback> Download() => s.B.Engine.PlayAsync(s.OnB, new PlayRequest
        {
            Title = Film, Purpose = Playback.PurposeDownload, Delivery = Playback.DeliveryFile, StartMs = 60_000,
        });
        Assert.Equal(MsfpError.InvalidRequest, await ErrorOf(() => s.B.Engine.PlayAsync(s.OnB, new PlayRequest
        {
            Title = Film, Purpose = Playback.PurposeDownload, Delivery = Playback.DeliveryRemux, Container = "mp4",
        })));
        var first = await Download();
        Assert.Equal((Playback.PurposeDownload, 0L), (h.Starts()[^1].Purpose, h.Starts()[^1].StartMs));
        Assert.Equal(Playback.PurposeDownload, Assert.Single(s.A.Engine.Playbacks()).Purpose);
        Assert.Equal(MsfpError.StreamLimitReached, await ErrorOf(Download));
        // A download counts against no stream.
        await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        // No idle rule: three minutes without a read, it lives on.
        clock.Advance(TimeSpan.FromMinutes(3));
        await s.A.Engine.RunOnceAsync();
        Assert.Equal(Playback.PurposeDownload, Assert.Single(s.A.Engine.Playbacks()).Purpose);
        Assert.Equal("0123", (await FetchAsync(s, first.Id, new MediaPath { Kind = MediaKind.File }, "bytes=0-3")).Text);
        await s.B.Engine.StopAsync(first.Id, 0);
        var second = await Download();
        await s.B.Engine.StopAsync(second.Id, -1);
        Assert.Equal(MsfpError.StreamLimitReached, await ErrorOf(Download));
        // Late reports, once ended: kept for 30 days, the playback ended;
        // one older than what was kept (the stop's), ignored.
        await s.A.WaitAsync();
        var reports = h.Reports().Count;
        await s.B.Engine.ReportOfflineAsync(first.Id, new OfflineReport(1, clock.GetUtcNow().AddHours(-1)));
        Assert.Equal(reports, h.Reports().Count);
        clock.Advance(TimeSpan.FromMinutes(10));
        var at = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().AddMinutes(-2).ToUnixTimeSeconds());
        await s.B.Engine.ReportOfflineAsync(first.Id, new OfflineReport(1_000_000, at));
        await s.B.Engine.ReportOfflineAsync(first.Id, new OfflineReport(2_000_000, at.AddMinutes(1), Stop: true));
        var last = h.Reports()[^1];
        Assert.Equal((true, true, 2_000_000L, at.AddMinutes(1), "h1"), (last.Late, last.Stop, last.PositionMs, last.At, last.Handle));
        Assert.Contains("\"at\":", s.Exchanges("progress")[0].RequestBody, StringComparison.Ordinal);
        Assert.Equal(MsfpError.InvalidRequest, await ErrorOf(() => s.B.Engine.ReportOfflineAsync(first.Id, new OfflineReport(0, clock.GetUtcNow().AddHours(1)))));
        // A play's progress after its end is still refused.
        var theirs = (string)JsonNode.Parse(s.Exchanges("play")[2].ResponseBody)!["id"]!;
        try
        {
            s.A.Engine.StopPlayback(theirs, EndReason.Operator);
        }
        catch (MsfpException)
        {
        }

        Assert.Equal(410, await CallAsync(s, "progress", "{\"id\":\"" + theirs + "\",\"at\":" + clock.GetUtcNow().ToUnixTimeSeconds() + "}"));
        // The 24 hours of a download's ticket.
        clock.Advance(TimeSpan.FromHours(25));
        await Download();
        clock.Advance(TimeSpan.FromHours(24));
        await s.A.Engine.RunOnceAsync();
        Assert.Empty(s.A.Engine.Playbacks());
        // A peer without downloads: none asked.
        var plays = s.Exchanges("play").Count;
        await s.A.Engine.SetLimitsAsync(s.OnA, new Limits { Streams = 2, Transcodes = 1 });
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.Equal(MsfpError.StreamLimitReached, await ErrorOf(Download));
        Assert.Equal(plays, s.Exchanges("play").Count);
    }

    // A download's reads are paced at its speed cap, on the engine's clock
    // (its timers move it on: what was waited is counted, not waited); a
    // play's are not.
    [Fact]
    public async Task DownloadPace()
    {
        var clock = new TimerTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var h = HostKit.FilmHost();
        var big = HostKit.Bytes(string.Concat(Enumerable.Repeat("0123456789", 30_000))); // 300,000 bytes
        h.Files["file"] = big;
        h.EditCopy(Film, 0, c => c with { Size = big.Length });
        var limits = new Limits { Streams = 1, Transcodes = 1, Downloads = 1, DownloadKbps = 800 }; // 100,000 bytes a second
        using var s = await Sharing.StartAsync(h, c => c with { NewPairing = limits }, clock: clock);
        async Task<TimeSpan> Read(string purpose)
        {
            var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Purpose = purpose, Delivery = Playback.DeliveryFile });
            try
            {
                var before = clock.Waited;
                var o = await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.File });
                Assert.Equal(big, o.Body);
                return clock.Waited - before;
            }
            finally
            {
                await s.B.Engine.StopAsync(pb.Id, -1);
            }
        }

        // A second's worth at once, then nine tenths of the cap: 200,000
        // bytes at 90,000 a second.
        var waited = await Read(Playback.PurposeDownload);
        Assert.True(waited >= TimeSpan.FromMilliseconds(2200) && waited <= TimeSpan.FromMilliseconds(2300), $"a download of 300,000 bytes at 100,000 a second waited {waited}");
        Assert.Equal(TimeSpan.Zero, await Read(Playback.PurposePlay));
    }

    // A download's late reports count after a restart of either side: both
    // keep it in their stores, 30 days after its play (playback section 7).
    [Fact]
    public async Task DownloadsAfterRestart()
    {
        var clock = new FakeTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var h = HostKit.FilmHost();
        var limits = new Limits { Streams = 1, Transcodes = 1, Downloads = 1 };
        using var s = await Sharing.StartAsync(h, c => c with { NewPairing = limits }, clock: clock);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Purpose = Playback.PurposeDownload, Delivery = Playback.DeliveryFile });
        static async Task<IReadOnlyList<DownloadRecord>> Kept(TestServer sv)
        {
            IReadOnlyList<DownloadRecord> got = [];
            await sv.Store.ReadAsync(async tx => got = await ((IDownloadsTx)tx).DownloadsAsync(), CancellationToken.None);
            return got;
        }

        var a = Assert.Single(await Kept(s.A));
        var b = Assert.Single(await Kept(s.B));
        Assert.True(!a.Viewer && a.Handle.Length > 0 && b.Viewer && b.Id == pb.Id, "the downloads kept");
        Assert.Equal(TimeSpan.Zero, a.PlayedAt.Offset);
        // Both restart, the download still live on A.
        async Task RestartA()
        {
            s.A.Dispose();
            s.A = await TestServer.StartAsync(s.Net, s.A.Address, "Server A", store: s.A.Store, clock: clock,
                with: c => c with { Catalog = h, Playback = h, Media = h, SheetEncoder = new TestSheets(), NewPairing = limits });
        }

        await RestartA();
        s.B.Dispose();
        s.B = await TestServer.StartAsync(s.Net, s.B.Address, "Server B", store: s.B.Store, clock: clock);
        Assert.Empty(s.A.Engine.Playbacks());
        clock.Advance(TimeSpan.FromHours(48));
        var at = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().AddHours(-1).ToUnixTimeSeconds());
        await s.B.Engine.ReportOfflineAsync(pb.Id, new OfflineReport(5_000, at));
        await s.A.WaitAsync();
        var last = h.Reports()[^1];
        Assert.Equal((true, 5_000L, at), (last.Late, last.PositionMs, last.At));
        Assert.Equal(at, Assert.Single(await Kept(s.A)).LastAt);
        // Once more after a restart: what was kept (the at) still counts.
        await RestartA();
        var reports = h.Reports().Count;
        await s.B.Engine.ReportOfflineAsync(pb.Id, new OfflineReport(1, at.AddMinutes(-1)));
        await s.A.WaitAsync();
        Assert.Equal(reports, h.Reports().Count);
        // After 30 days, gone on both sides.
        clock.Advance(TimeSpan.FromDays(30));
        await s.A.Engine.RunOnceAsync();
        Assert.Empty(await Kept(s.A));
        Assert.Equal(MsfpError.NotFound, await ErrorOf(() => s.B.Engine.ReportOfflineAsync(pb.Id, new OfflineReport(0, clock.GetUtcNow()))));
        s.B.Dispose();
        s.B = await TestServer.StartAsync(s.Net, s.B.Address, "Server B", store: s.B.Store, clock: clock);
        Assert.Empty(await Kept(s.B));
    }

    // A scan's cap counts the bytes sent: a read cut short counts only what
    // went (media section 4.2).
    [Fact]
    public async Task ScanCapCountsSent()
    {
        var h = HostKit.FilmHost();
        var big = new byte[Media.ScanCap + 1000];
        h.Files["file"] = big;
        h.EditCopy(Film, 0, c => c with { Size = big.Length });
        using var s = await Sharing.StartAsync(h, c => c with { NewPairing = new Limits { Streams = 1, Transcodes = 1, ScansPerDay = 1 } });
        var scan = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Purpose = Playback.PurposeScan });
        // The host fails after 1,000 bytes of a read of the whole cap.
        h.Serve = async (_, range, output, ct) =>
        {
            output.Start(206, "video/mp4", range!.Last - range.First + 1, "");
            await output.WriteAsync(big.AsMemory(0, 1000), ct);
            throw new IOException("the disk failed");
        };
        var file = StreamOf(s);
        var cut = await HostKit.GetFullAsync(s.A, file, "GET", "Range", "bytes=0-");
        Assert.Equal((206, 1000), (cut.Status, cut.Body.Length));
        // Relayed, a file cut short fails on the viewer's side.
        await Assert.ThrowsAsync<IOException>(() => s.B.Engine.FetchAsync(scan.Id, new MediaPath { Kind = MediaKind.File }, "bytes=0-1999", new MemorySink()));
        h.Serve = null;
        var o = await HostKit.GetFullAsync(s.A, file, "GET", "Range", "bytes=0-");
        Assert.Equal((206, Media.ScanCap - 2000), (o.Status, o.Body.Length)); // 1,000 bytes sent twice
        Assert.Equal(416, await HostKit.GetAsync(s.A, file, "GET", "Range", "bytes=0-0"));
    }

    // Scans: a range of the file only, within 32 MiB per ticket, cut at the
    // cap, then 416; a day's count; the idle rule (playback section 8, media
    // section 4.2).
    [Fact]
    public async Task Scans()
    {
        var clock = new FakeTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var h = HostKit.FilmHost();
        var big = new byte[Media.ScanCap + 1000];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)i;
        }

        h.Files["file"] = big;
        h.EditCopy(Film, 0, c => c with { Size = big.Length, Previews = true });
        h.Info["movie/tmdb/949#0"] = new CopyInfo { Trickplay = new Trickplay { TileWidth = 10, TileHeight = 10, Columns = 2, Rows = 1, IntervalMs = 10_000, TileCount = 3 } };
        using var s = await Sharing.StartAsync(h, c => c with { NewPairing = new Limits { Streams = 1, Transcodes = 1, ScansPerDay = 2 } }, clock: clock);
        var scan = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Purpose = Playback.PurposeScan });
        Assert.Equal((Playback.DeliveryFile, "file", 0, true), (scan.Delivery, scan.Protocol, scan.SubtitleTracks.Count, scan.Trickplay is null));
        Assert.Contains("\"purpose\":\"scan\",\"delivery\":\"file\"", s.Exchanges("play")[0].RequestBody, StringComparison.Ordinal);
        // A scan counts against no stream.
        await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        var file = new MediaPath { Kind = MediaKind.File };
        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s, scan.Id, file));
        Assert.Equal(big[..10], (await FetchAsync(s, scan.Id, file, "bytes=0-9")).Body);
        var o = await FetchAsync(s, scan.Id, file, "bytes=0-");
        Assert.Equal((206, Media.ScanCap - 10, "bytes 0-33554421/33555432"), (o.Status, (long)o.Body.Length, o.ContentRange));
        Assert.Equal(MsfpError.RangeNotSatisfiable, await FetchErrorAsync(s, scan.Id, file, "bytes=0-0"));
        // Only the file opens under a scan's ticket; its progress changes
        // nothing.
        var streamBase = StreamOf(s, 0)[..^"file".Length];
        foreach (var name in new[] { "subtitles/s0/0", "trickplay/0", "master.m3u8" })
        {
            Assert.True(await HostKit.GetAsync(s.A, streamBase + name) == 404, name);
        }

        var theirs = (string)JsonNode.Parse(s.Exchanges("play")[0].ResponseBody)!["id"]!;
        Assert.Equal(200, await CallAsync(s, "progress", "{\"id\":\"" + theirs + "\",\"positionMs\":5}"));
        Assert.Empty(h.Progresses());
        // The idle rule holds.
        clock.Advance(TimeSpan.FromMinutes(3));
        await s.A.Engine.RunOnceAsync();
        Assert.Empty(s.A.Engine.Playbacks());
        await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Purpose = Playback.PurposeScan });
        Assert.Equal(MsfpError.StreamLimitReached, await ErrorOf(() => s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film, Purpose = Playback.PurposeScan })));
    }

    // A music play's queue: its entries prepared under one playback, each at
    // its own URL by its place, a place left empty for a track not offered;
    // the track playing in progress (music section 5.2).
    [Fact]
    public async Task MusicQueue()
    {
        var h = new FakeHost { Libs = [new Library { Id = "music", Name = "Music", Kind = "music" }], Protocol = "file", Queue = true };
        static string Key(string n) => "track/mb/0c000000-0000-4000-8000-00000000000" + n;
        foreach (var n in new[] { "3", "5", "6" })
        {
            h.Titles[Key(n)] = HostKit.MusicTrack(Key(n), "c" + n);
        }

        h.Files["file"] = HostKit.Bytes("0123456789abcdefghij");
        h.Files["queue/0/file"] = HostKit.Bytes("queue entry zero....");
        h.Files["queue/2/file"] = HostKit.Bytes("queue entry two....!");
        using var s = await Sharing.StartAsync(h, c => c with { NewPairing = new Limits { Streams = 1, Transcodes = 1 } });
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Key("3"), Delivery = Playback.DeliveryFile, Queue = [Key("5"), Key("7"), Key("6")] });
        Assert.Equal(2, pb.Queue.Count);
        Assert.Equal((0, Key("5"), 2, 200_000L), (pb.Queue[0].Place, pb.Queue[0].Title, pb.Queue[1].Place, pb.Queue[1].DurationMs));
        var got = h.Starts()[0].Queue;
        Assert.Equal((2, "c5", 2), (got.Count, got[0].Copy, got[1].Place));
        var body = s.Exchanges("play")[0].ResponseBody;
        Assert.Contains("/queue/0/file", body, StringComparison.Ordinal);
        Assert.Contains("/queue/2/file", body, StringComparison.Ordinal);
        foreach (var (entry, want) in new[] { (0, "0123456789abcdefghij"), (1, "queue entry zero...."), (3, "queue entry two....!") })
        {
            Assert.Equal(want, (await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.File, Entry = entry })).Text);
        }

        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s, pb.Id, new MediaPath { Kind = MediaKind.File, Entry = 2 }));
        // One stream for the whole queue.
        Assert.Single(s.A.Engine.Playbacks());
        // The track playing, in progress.
        Assert.Equal(MsfpError.InvalidRequest, Assert.Throws<MsfpException>(() => s.B.Engine.PlayingTrack(pb.Id, Key("7"))).Code);
        s.B.Engine.PlayingTrack(pb.Id, Key("6"));
        s.B.Engine.Progress(pb.Id, 5000, false);
        await s.B.WaitAsync();
        await s.A.WaitAsync();
        var last = h.Reports()[^1];
        Assert.Equal((Key("6"), 5000L), (last.Title, last.PositionMs));
        // A queue MSFP refuses.
        string[] tooLong = [Key("5"), Key("6"), Key("7"), Key("8"), Key("9"), "track/mb/0c000000-0000-4000-8000-000000000010"];
        Assert.Equal(MsfpError.InvalidRequest, await ErrorOf(() => s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Key("3"), Queue = tooLong, Replaces = pb.Id })));
        Assert.Equal(400, await CallAsync(s, "play", "{\"title\":\"" + Key("3") + "\",\"queue\":[\"" + Key("3") + "\"]}"));
    }

    // previews before a first play, under a ticket of their own that is no
    // playback; lyrics with none (video section 5.6, music section 6).
    [Fact]
    public async Task PreviewsAndLyrics()
    {
        var h = HostKit.FilmHost();
        h.Libs = [new Library { Id = "films", Name = "Films", Kind = "movies" }, new Library { Id = "music", Name = "Music", Kind = "music" }];
        h.EditCopy(Film, 0, c => c with { Previews = true });
        h.Info["movie/tmdb/949#0"] = new CopyInfo { Trickplay = new Trickplay { TileWidth = 10, TileHeight = 10, Columns = 2, Rows = 1, IntervalMs = 10_000, TileCount = 3 } };
        h.Files["trickplay/0"] = Sheets.Jpeg20x10;
        h.Files["trickplay/1"] = Sheets.Jpeg20x10;
        const string Track = "track/mb/0c000000-0000-4000-8000-000000000003";
        h.Titles[Track] = HostKit.MusicTrack(Track, "t", "synced");
        h.Lyrics["t"] = [new LyricsSet("en", "main", true, 0, "", "", [new LyricsLine(0, "Hello\u0007"), new LyricsLine(1500, "")])];
        using var s = await Sharing.StartAsync(h);
        var titles = await s.PeerTitlesAsync();
        var copies = ((VideoTitle)titles[Film]).Copies!;
        Assert.True(copies[0].Previews && !copies[1].Previews);
        var pv = await s.B.Engine.PreviewsAsync(s.OnB, Film, copies[0].Id);
        Assert.Equal((2, 2, 3), (pv.Trickplay.Sheets, pv.Sheets.Count, pv.Trickplay.TileCount));
        Assert.Empty(s.A.Engine.Playbacks());
        Assert.Empty(h.Starts());
        Assert.Equal(MsfpError.NotFound, await ErrorOf(() => s.B.Engine.PreviewsAsync(s.OnB, Film, copies[1].Id)));
        // The ticket opens the sheets only, of that copy.
        var sheets = JsonNode.Parse(s.Exchanges("previews")[0].ResponseBody)!["trickplay"]!["sheets"]!.AsArray();
        Assert.Equal(2, sheets.Count);
        var url = (string)sheets[0]!;
        var streamBase = url[s.A.Address.Length..^"trickplay/0".Length];
        foreach (var (name, want) in new[] { ("trickplay/1", 200), ("trickplay/2", 404), ("file", 404), ("master.m3u8", 404), ("image", 404) })
        {
            Assert.True(await HostKit.GetAsync(s.A, streamBase + name) == want, name);
        }

        Assert.Equal(405, await HostKit.GetAsync(s.A, streamBase + "trickplay/0", "HEAD"));
        // The owning side re-encodes the host's sheets: only their pixels
        // leave; one outside the layout isn't sent.
        h.Files["trickplay/0"] = [.. Sheets.Jpeg20x10, .. HostKit.Bytes("trailing bytes")];
        h.Files["trickplay/1"] = Sheets.Jpeg20x11;
        var reencoded = await HostKit.GetFullAsync(s.A, streamBase + "trickplay/0");
        Assert.Equal((200, "image/jpeg"), (reencoded.Status, reencoded.Header("Content-Type")));
        Assert.Equal(Sheets.Jpeg20x10, reencoded.Body.ToArray());
        Assert.Equal(500, await HostKit.GetAsync(s.A, streamBase + "trickplay/1"));
        h.Files["trickplay/0"] = h.Files["trickplay/1"] = Sheets.Jpeg20x10;
        foreach (var (body, want) in new[]
        {
            ("{\"title\":\"movie/tmdb/949\"}", 400), ("{\"title\":\"series/tmdb/1\",\"versionId\":\"x\"}", 400),
            ("{\"title\":\"movie/tmdb/949\",\"versionId\":\"nope\"}", 404), ("{\"title\":\"movie/tmdb/949\",\"versionId\":\"" + copies[1].Id + "\"}", 404),
        })
        {
            Assert.True(await CallAsync(s, "previews", body) == want, body);
        }

        // Lyrics, cleaned.
        var mcopy = ((MusicTitle)titles[Track]).Copies![0];
        var sets = await s.B.Engine.TrackLyricsAsync(s.OnB, Track, mcopy.Id);
        var set = Assert.Single(sets);
        Assert.Equal((2, "Hello", 1500L), (set.Line.Count, set.Line[0].Value, set.Line[1].Start));
        Assert.Equal(MsfpError.NotFound, await ErrorOf(() => s.B.Engine.TrackLyricsAsync(s.OnB, Film, copies[0].Id)));
        foreach (var (body, want) in new[] { ("{\"title\":\"" + Track + "\"}", 400), ("{\"title\":\"" + Track + "\",\"versionId\":\"nope\"}", 404) })
        {
            Assert.True(await CallAsync(s, "lyrics", body) == want, body);
        }
    }

    // ASS tracks and their fonts, whole text tracks: served from the host,
    // rebuilt or checked on both sides (video sections 6.3 to 6.6).
    [Fact]
    public async Task AssAndFonts()
    {
        var v = Vectors.Load("media.json");
        var ass = v["ass"]!.AsArray().Select(n => n!.AsObject()).First(c => (bool)c["valid"]!);
        var font = v["fonts"]!.AsArray().Select(n => n!.AsObject()).First(c => (bool)c["valid"]!);
        var fontBytes = Convert.FromHexString((string)font["hex"]!);
        var h = HostKit.FilmHost();
        h.EditCopy(Film, 0, c => c with
        {
            SubtitleTracks = [.. c.SubtitleTracks, new CopySubtitle("s2", "ass") { Language = "ja" }, new CopySubtitle("s3", "other")],
        });
        h.Info["movie/tmdb/949#0"] = new CopyInfo { Fonts = 1, NotApart = ["s1"] };
        h.Files["ass/s2/0"] = HostKit.Bytes((string)ass["text"]!);
        h.Files["ass/s2/whole"] = HostKit.Bytes((string)ass["text"]!);
        h.Files["fonts/0"] = fontBytes;
        h.Files["subtitles/s2/whole"] = HostKit.Bytes("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<b>whole</b>\n");
        using var s = await Sharing.StartAsync(h);
        var x = new RelayTests.Hostile(s);
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        var byId = pb.SubtitleTracks.ToDictionary(t => t.Id);
        Assert.Equal(4, byId.Count);
        Assert.True(byId["s2"].Ass && byId["s2"].Text && byId["s2"].Format == "ass");
        Assert.Equal(1, pb.Fonts);
        Assert.False(byId["s1"].Pictures);
        Assert.False(byId["s3"].Text);
        var o = await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Ass, Track = "s2" });
        Assert.Equal(((string)ass["output"]!, "text/x-ass; charset=utf-8"), (o.Text, o.ContentType));
        await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Ass, Track = "s2", Whole = true });
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<b>whole</b>\n",
            (await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Subtitles, Track = "s2", Whole = true })).Text);
        o = await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Font });
        Assert.Equal((string)font["type"]!, o.ContentType);
        Assert.Equal(fontBytes, o.Body);
        foreach (var p in new[]
        {
            new MediaPath { Kind = MediaKind.Font, Font = 1 }, new MediaPath { Kind = MediaKind.Ass, Track = "s0" },
            new MediaPath { Kind = MediaKind.Pictures, Track = "s1" }, new MediaPath { Kind = MediaKind.Pictures, Track = "s2", Whole = true },
        })
        {
            Assert.True(await FetchErrorAsync(s, pb.Id, p) == MsfpError.NotFound, p.ToString());
        }

        // A hostile peer's: refused.
        x.Body("fonts/0", "ttcf collection");
        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Font }));
        x.Body("ass/s2/0", "not a script");
        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Ass, Track = "s2" }));
        // The host's own outside MSFP's rules: not sent.
        x.Set("ass/s2/0", null);
        lock (h.Lock)
        {
            h.Files["ass/s2/0"] = HostKit.Bytes("not a script");
        }

        Assert.Equal(MsfpError.NotFound, await FetchErrorAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Ass, Track = "s2" }));
    }
}
