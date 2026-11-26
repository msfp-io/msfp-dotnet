using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

// The relay against a hostile peer (msfp-go's relay_test.go): everything a
// peer sends is checked before it reaches the viewer, its URLs, its
// playlists, subtitles, pictures and sheets, its ranges and headers; and
// the media vectors.
public sealed class RelayTests
{
    private const string Film = "movie/tmdb/949";

    // A sharing whose owning side, A, rewrites its answers: the media of path
    // name (after the ticket), or of a call.
    internal sealed class Hostile
    {
        private readonly Dictionary<string, Func<HeaderList, byte[], byte[]>> _rewrites = new(StringComparer.Ordinal);

        public Sharing S { get; }

        public Hostile(Sharing s)
        {
            S = s;
            s.Hostile((path, h, body) =>
            {
                lock (_rewrites)
                {
                    foreach (var (name, f) in _rewrites)
                    {
                        if (path.EndsWith("/" + name, StringComparison.Ordinal))
                        {
                            return f(h, body);
                        }
                    }
                }

                return null;
            });
        }

        public void Set(string name, Func<HeaderList, byte[], byte[]>? f)
        {
            lock (_rewrites)
            {
                if (f is null)
                {
                    _rewrites.Remove(name);
                }
                else
                {
                    _rewrites[name] = f;
                }
            }
        }

        public void Body(string name, byte[] body) => Set(name, (_, _) => body);

        public void Body(string name, string body) => Body(name, HostKit.Bytes(body));

        public async Task<RemotePlayback> PlayAsync()
        {
            var pb = await S.B.Engine.PlayAsync(S.OnB, new PlayRequest { Title = Film });
            // The playlists first: the owning server serves only the names
            // they give (media section 1).
            foreach (var p in new[] { new MediaPath { Kind = MediaKind.Master }, new MediaPath { Kind = MediaKind.Playlist, Name = "v0.m3u8" } })
            {
                await S.B.Engine.FetchAsync(pb.Id, p, "", new MemorySink());
            }

            return pb;
        }
    }

    private static async Task<(MemorySink? Out, MsfpError? Error)> FetchAsync(Sharing s, string id, MediaPath p, string range = "")
    {
        var sink = new MemorySink();
        try
        {
            await s.B.Engine.FetchAsync(id, p, range, sink);
            return (sink, null);
        }
        catch (MsfpException e)
        {
            Assert.False(sink.Started, "a failed fetch wrote something");
            return (null, e.Code);
        }
    }

    [Fact]
    public async Task RelayHostilePeer()
    {
        var h = HostKit.FilmHost();
        var sheet = Sheets.Jpeg20x10;
        h.EditCopy(Film, 0, c => c with { PreviewLayout = new Msfp.Protocol.PreviewLayout(10, 10, 2, 1, 10_000, 3) });
        h.Files["pictures/s1/0"] = [];
        h.Files["trickplay/0"] = sheet;
        h.Files["trickplay/1"] = sheet;
        using var s = await Sharing.StartAsync(h);
        var x = new Hostile(s);
        var pb = await x.PlayAsync();
        Assert.Equal(2, pb.Sheets);
        var master = new MediaPath { Kind = MediaKind.Master };
        var seg = new MediaPath { Kind = MediaKind.Segment, Name = "seg0.m4s" };
        var cases = new (string Name, string Body, MediaPath Path)[]
        {
            ("master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nhttps://evil.example/v.m3u8\n", master),
            ("master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n../v0.m3u8\n", master),
            ("master.m3u8", "#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"k\"\n", master),
            ("master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,URI=\"https://evil.example/a\"\n", master),
            ("master.m3u8", "#EXTM3U\n" + string.Concat(Enumerable.Repeat("#x\n", 1 << 20)), master),
            ("master.m3u8", "not a playlist", master),
            ("seg0.m4s", "#EXTM3U\n#EXTINF:1,\nhttps://evil.example/x\n", seg),
            ("subtitles/s0/0", "<script>WEBVTT", new MediaPath { Kind = MediaKind.Subtitles, Track = "s0" }),
            ("subtitles/s0/0", "WEBVTT\n" + new string('x', 5 << 20), new MediaPath { Kind = MediaKind.Subtitles, Track = "s0" }),
            ("pictures/s1/0", "PG\0", new MediaPath { Kind = MediaKind.Pictures, Track = "s1" }),
            ("trickplay/0", "not a picture", new MediaPath { Kind = MediaKind.Sheet }),
        };
        foreach (var c in cases)
        {
            x.Body(c.Name, c.Body);
            Assert.True((await FetchAsync(s, pb.Id, c.Path)).Error == MsfpError.NotFound, c.Name + " " + c.Body[..Math.Min(40, c.Body.Length)]);
            x.Set(c.Name, null);
        }

        // Tags outside the subset dropped, URIs kept as they are.
        x.Body("master.m3u8", "#EXTM3U\n# a comment\n#EXT-X-SESSION-DATA:DATA-ID=\"x\",VALUE=\"y\"\n#EXT-X-STREAM-INF:BANDWIDTH=1\r\nv0.m3u8\n");
        Assert.Equal("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv0.m3u8\n", (await FetchAsync(s, pb.Id, master)).Out?.Text);
        x.Set("master.m3u8", null);
        // WebVTT rebuilt (a tag made of references never comes back); an
        // empty PGS part relayed empty.
        x.Body("subtitles/s0/0", "WEBVTT\nSTYLE\n::cue{}\n\n1\n00:00:01.000 --> 00:00:02.000 align:start\n<c.x>&lt;b&gt;hi</c>\n");
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000 align:start\nhi\n", (await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Subtitles, Track = "s0" })).Out?.Text);
        var pgs = (await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Pictures, Track = "s1" })).Out;
        Assert.NotNull(pgs);
        Assert.Equal((200, 0, "application/octet-stream"), (pgs.Status, pgs.Body.Length, pgs.ContentType));
        // Sheets: re-encoded; of another format, too large, or past the
        // count: refused.
        x.Set("trickplay/0", (_, b) => [.. b, .. "trailing bytes"u8]);
        var o = (await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Sheet })).Out;
        Assert.NotNull(o);
        Assert.Equal("image/jpeg", o.ContentType);
        Assert.DoesNotContain("trailing", o.Text, StringComparison.Ordinal);
        Assert.NotNull(Jpeg.Size(o.Body));
        foreach (var bad in new[] { Sheets.Png20x10, Sheets.Jpeg21x10, Sheets.Jpeg20x11 })
        {
            x.Body("trickplay/0", bad);
            Assert.Equal(MsfpError.NotFound, (await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Sheet })).Error);
        }

        Assert.Equal(MsfpError.NotFound, (await FetchAsync(s, pb.Id, new MediaPath { Kind = MediaKind.Sheet, Sheet = 2 })).Error);
        // Ranges and headers.
        x.Set("seg0.m4s", (hl, b) =>
        {
            hl.Set("Content-Range", "bytes 0-3/20");
            return b;
        });
        Assert.Equal(MsfpError.NotFound, (await FetchAsync(s, pb.Id, seg, "bytes=2-5")).Error);
        // For a segment, unlike the file, an unknown total is fine; and a 200
        // to a range is the whole segment, accepted (media section 2.1).
        x.Set("seg0.m4s", (hl, b) =>
        {
            hl.Set("Content-Range", "bytes 2-5/*");
            return b;
        });
        var star = await FetchAsync(s, pb.Id, seg, "bytes=2-5");
        Assert.Equal(((MsfpError?)null, 206, "2345"), (star.Error, star.Out?.Status, star.Out?.Text));
        x.Set("seg0.m4s", (hl, _) =>
        {
            hl.Remove("Content-Range");
            hl.Set("X-Test-Status", "200");
            return HostKit.Bytes("0123456789abcdefghij");
        });
        var whole = await FetchAsync(s, pb.Id, seg, "bytes=2-5");
        Assert.Equal(((MsfpError?)null, 200, "0123456789abcdefghij"), (whole.Error, whole.Out?.Status, whole.Out?.Text));
        x.Set("seg0.m4s", (hl, b) =>
        {
            hl.Set("Content-Length", "99");
            return b;
        });
        Assert.Equal(MsfpError.NotFound, (await FetchAsync(s, pb.Id, seg, "bytes=2-5")).Error);
        x.Set("seg0.m4s", (hl, b) =>
        {
            hl.Set("Content-Encoding", "gzip");
            return b;
        });
        Assert.Equal(MsfpError.NotFound, (await FetchAsync(s, pb.Id, seg)).Error);
        x.Set("seg0.m4s", (hl, b) =>
        {
            hl.Set("Content-Type", "text/html");
            return b;
        });
        o = (await FetchAsync(s, pb.Id, seg)).Out;
        Assert.NotNull(o);
        Assert.Equal(("application/octet-stream", "0123456789abcdefghij"), (o.ContentType, o.Text));
        x.Set("seg0.m4s", null);
        // The peer's failures pass nothing on.
        lock (h.Lock)
        {
            h.Serve = (_, _, _, _) => throw new MsfpException(MsfpError.ServerBusy);
        }

        Assert.Equal(MsfpError.ServerBusy, (await FetchAsync(s, pb.Id, seg)).Error);
        lock (h.Lock)
        {
            h.Serve = (_, _, _, _) => throw new InvalidOperationException("broken");
        }

        Assert.Equal(MsfpError.NotFound, (await FetchAsync(s, pb.Id, seg)).Error);
    }

    // A play answer that breaks a "must" is stopped and given up; what may be
    // cleaned is.
    [Fact]
    public async Task HostilePlayAnswers()
    {
        var h = HostKit.FilmHost();
        using var s = await Sharing.StartAsync(h);
        var x = new Hostile(s);
        void Edit(Action<JsonObject> f) => x.Set("play", (_, b) =>
        {
            var m = JsonNode.Parse(b)!.AsObject();
            f(m);
            return HostKit.Bytes(m.ToJsonString());
        });
        var breaks = new Dictionary<string, Action<JsonObject>>
        {
            ["a URL for a ticket"] = m => m["ticket"] = "https://evil.example/api/v1/stream/x/",
            ["a ticket of another form"] = m => m["ticket"] = "%41",
            ["no ticket"] = m => m.Remove("ticket"),
            ["a file protocol for the baseline"] = m => m["protocol"] = "file",
            ["a method MSFP lacks"] = m => m["method"] = "stream",
            ["a member of the wrong type"] = m => m["positionMs"] = "0",
            ["no versionId"] = m => m.Remove("versionId"),
        };
        foreach (var (name, f) in breaks)
        {
            var before = s.Exchanges("stop").Count;
            Edit(f);
            var e = await Assert.ThrowsAsync<MsfpException>(x.PlayAsync);
            Assert.True(e.Code == MsfpError.NotPlayable, name + ": " + e.Code);
            await s.WaitAsync();
            Assert.True(s.Exchanges("stop").Count == before + 1, name + ": no stop sent");
        }

        Edit(m => m["id"] = "play.x");
        Assert.Equal(MsfpError.NotPlayable, (await Assert.ThrowsAsync<MsfpException>(x.PlayAsync)).Code);
        // Cleaned, not refused.
        foreach (var v in s.A.Engine.Playbacks())
        {
            s.A.Engine.StopPlayback(v.Id, EndReason.Operator);
        }

        Edit(m =>
        {
            m["subtitles"] = JsonNode.Parse("[{\"id\":\"s0\",\"form\":\"picture\"},{\"id\":\"s1\",\"form\":\"picture\"}," +
                "{\"id\":\"s9\",\"form\":\"text\"},{\"id\":\"s1\",\"form\":\"text\"}]");
            m["versions"] = JsonNode.Parse("[{\"id\":\"../x\",\"name\":\"x\"},{\"id\":\"v1\",\"name\":\"<b>HD</b>\"}]");
            m["durationMs"] = 9_000_000_000;
            m["fontCount"] = 65;
            m["stream"] = new JsonObject { ["url"] = "https://evil.example/x" };
            m["trickplay"] = new JsonObject { ["tileWidth"] = 5000 };
        });
        var pb = await s.B.Engine.PlayAsync(s.OnB, new PlayRequest { Title = Film });
        Assert.Equal("s1", Assert.Single(pb.Subtitles).Id);
        Assert.Equal((0, 5_400_000L, 0), (pb.Fonts, pb.DurationMs, pb.Sheets));
    }

    // A case's input: its hex when it has one, else its text.
    private static byte[] Input(JsonObject c) =>
        (string?)c["hex"] is { Length: > 0 } hex ? Convert.FromHexString(hex) : HostKit.Bytes((string?)c["text"] ?? "");

    // The cases of vectors/v0/media.json, through a relay: what a peer's
    // playlists, WebVTT and PGS parts become on the way to the viewer.
    [Fact]
    public async Task MediaVectorsThroughRelay()
    {
        var v = Vectors.Load("media.json");
        var h = HostKit.FilmHost();
        h.Files["pictures/s1/0"] = [];
        // The playback of the hls vectors: audio tracks a1 to a32 (32 at most
        // in a copy).
        h.EditCopy(Film, 0, c => c with
        {
            AudioTracks = Enumerable.Range(1, 32).Select(n => new CopyAudio("a" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), "aac")).ToList(),
        });
        using var s = await Sharing.StartAsync(h);
        var x = new Hostile(s);
        var pb = await x.PlayAsync();
        async Task<(MemorySink? Out, MsfpError? Error)> Fetch(string name, byte[] body, MediaPath p)
        {
            x.Body(name, body);
            return await FetchAsync(s, pb.Id, p);
        }

        foreach (var c in v["hls"]!.AsArray().Select(n => n!.AsObject()))
        {
            // Each as a media playlist (a master's tags are a subset of a
            // media playlist's checks).
            var input = Input(c);
            var (_, error) = await Fetch("v0.m3u8", input, new MediaPath { Kind = MediaKind.Playlist, Name = "v0.m3u8" });
            Assert.True((error is null) == (bool)c["valid"]!, "hls " + (string)c["name"]! + ": " + error);
        }

        foreach (var c in v["webvtt"]!.AsArray().Select(n => n!.AsObject()))
        {
            var input = Input(c);
            var (output, error) = await Fetch("subtitles/s0/0", input, new MediaPath { Kind = MediaKind.Subtitles, Track = "s0" });
            var valid = (bool)c["valid"]!;
            Assert.True((error is null) == valid, "webvtt " + (string)c["name"]! + ": " + error);
            if (valid)
            {
                Assert.Equal((string)c["output"]!, output!.Text);
            }
        }

        foreach (var c in v["pgs"]!.AsArray().Select(n => n!.AsObject()))
        {
            var input = Input(c);
            var (output, error) = await Fetch("pictures/s1/0", input, new MediaPath { Kind = MediaKind.Pictures, Track = "s1" });
            var valid = (bool)c["valid"]!;
            Assert.True((error is null) == valid, "pgs " + (string)c["name"]! + ": " + error);
            if (valid)
            {
                Assert.Equal(input, output!.Body);
            }
        }
    }
}
