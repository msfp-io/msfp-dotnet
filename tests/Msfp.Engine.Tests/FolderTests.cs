using System.Security.Cryptography;
using System.Text;
using Msfp.Examples.ShareFolder;
using Msfp.Protocol;

namespace Msfp.Tests;

// The example's folder (examples/ShareFolder/Folder.cs, msfp-go's
// examples/share-folder/folder_test.go).
public sealed class FolderTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("msfp-folder-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private void Write(string rel, string body)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, body);
    }

    // The folder's titles: films by TMDB ID, loose or in a folder with its
    // extras; a series with its seasons, an episode by its TMDB episode ID
    // (placed) and one by its numbers; a local film with its image; copies
    // with their exact sizes and etags, subtitles and previews.
    [Fact]
    public async Task Titles()
    {
        Write("Heat (1995) [tmdbid-949].mkv", "heat");
        Write("Heat (1995) [tmdbid-949].fr.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nBonjour\n");
        Write("Heat (1995) [tmdbid-949].trickplay/layout.json",
            """{"tileWidth":160,"tileHeight":90,"columns":10,"rows":10,"intervalMs":10000,"tileCount":12}""");
        Write("Alien (1979) [tmdbid-348]/Alien.mp4", "alien film");
        Write("Alien (1979) [tmdbid-348]/sample.mp4", "short");
        Write("Alien (1979) [tmdbid-348]/Trailers/Teaser.mp4", "teaser");
        Write("Severance (2022) [tmdbid-95396]/Season 1/S01E01 [tmdbid-1949537].mkv", "e1");
        Write("Severance (2022) [tmdbid-95396]/Season 1/S01E02.mkv", "e2");
        Write("Holidays (2019).mp4", "home");
        Write("Holidays (2019).jpg", "picture");
        Write("Lain [tvdbid-78914].mkv", "a film by TheTVDB: none");

        var f = new Folder(dir) { Opaque = s => "o" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20] };
        var page = await f.TitlesAsync([], "", 100, Ct);
        var byKey = page.Titles.Cast<VideoTitle>().ToDictionary(t => t.Key, StringComparer.Ordinal);
        var keys = page.Titles.Select(t => t.Key).ToList();
        foreach (var k in new[] { "movie/tmdb/949", "movie/tmdb/348", "series/tmdb/95396", "series/tmdb/95396/s1", "series/tmdb/95396/e1949537", "series/tmdb/95396/s1/e2" })
        {
            Assert.True(byKey.ContainsKey(k), k + " missing from " + string.Join(" ", keys));
        }

        Assert.Equal(8, keys.Count);
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        var heat = byKey["movie/tmdb/949"].Copies![0];
        Assert.Equal((4L, "video/matroska", "Heat (1995) [tmdbid-949].mkv", 12), (heat.Size, heat.MediaType, heat.Id, heat.PreviewLayout?.TileCount));
        Assert.True(Playback.Id(heat.ETag));
        Assert.Equal("fr", Assert.Single(heat.SubtitleTracks).Language);
        Assert.Equal(Path.Combine("Alien (1979) [tmdbid-348]", "Alien.mp4"), byKey["movie/tmdb/348"].Copies![0].Id);
        Assert.Equal(new Place { Season = 1, Episode = 1 }, byKey["series/tmdb/95396/e1949537"].Place);
        var extra = byKey.Single(kv => kv.Key.StartsWith("movie/tmdb/348/x/", StringComparison.Ordinal)).Value;
        Assert.Equal(("extra", "trailer", null), (extra.Kind, extra.ExtraType, extra.Name)); // an extra carries no name
        var local = byKey.Single(kv => kv.Key.StartsWith("movie/local/", StringComparison.Ordinal)).Value;
        Assert.Equal(("Holidays", 2019, true), (local.Name, local.Year, local.Image));
        Assert.Equal("picture", Encoding.UTF8.GetString(await f.ImageAsync(local.Key, Ct)));
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(async () => await f.ImageAsync("movie/tmdb/949", Ct))).Code);
        // The etag follows the bytes: the same content, the same etag.
        var again = await f.TitlesAsync([], "", 100, Ct);
        Assert.Equal(heat.ETag, again.Titles.Cast<VideoTitle>().Single(t => t.Key == "movie/tmdb/949").Copies![0].ETag);

        // Its subtitles by stretch, and nothing outside the folder.
        var sink = new MemorySink();
        await f.ServeAsync("file " + heat.Id, new MediaPath { Kind = MediaKind.Subtitles, Track = "s0", Part = 0 }, null, sink, Ct);
        Assert.Contains("Bonjour", sink.Text, StringComparison.Ordinal);
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(async () =>
            await f.ServeAsync("file ../x.mkv", new MediaPath { Kind = MediaKind.File }, null, new MemorySink(), Ct))).Code);
    }

    // Shared through the engine: what a peer reads of it (local titles once
    // allowed), and a download of a film's file.
    [Fact]
    public async Task Shared()
    {
        Write("Heat (1995) [tmdbid-949].mkv", "heat file");
        Write("Heat (1995) [tmdbid-949].fr.vtt", "WEBVTT\n");
        Write("Severance (2022) [tmdbid-95396]/Season 1/S01E02.mkv", "e2");
        Write("Holidays (2019).mp4", "home");
        var f = new Folder(dir);
        var net = new Network();
        using var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A",
            with: c => c with { Catalog = f, Playback = f, Media = f, NewPairing = new Limits { Streams = 1, Transcodes = 1, Downloads = 1 } });
        f.Opaque = a.Engine.OpaqueKeyPart;
        using var b = await TestServer.StartAsync(net, "https://b.example.org", "Server B");
        var (onA, onB) = await Kit.LinkAsync(a, b);
        await a.Engine.SetOffersAsync(onA, ["folder"]);
        var libs = await b.Engine.TheirLibrariesAsync(onB);
        await b.Engine.SetWantsAsync(onB, libs.Select(l => l.Id));
        Assert.Equal(4, await b.Engine.ReadNowAsync(onB));
        await a.Engine.SetLocalAsync(onA, true);
        await b.Engine.ReadNowAsync(onB);
        IReadOnlyList<Title> titles = [];
        await b.Store.ReadAsync(async tx => titles = await tx.PeerTitlesAsync(onB, "", 100), CancellationToken.None);
        Assert.Contains(titles, t => t.Key.StartsWith("movie/local/", StringComparison.Ordinal));
        var pb = await b.Engine.PlayAsync(onB, new PlayRequest { Title = "movie/tmdb/949", Purpose = Playback.PurposeDownload, Delivery = Playback.DeliveryFile });
        var sink = new MemorySink();
        await b.Engine.FetchAsync(pb.Id, new MediaPath { Kind = MediaKind.File }, "", sink);
        Assert.Equal("heat file", sink.Text);
    }

    // A file's media type from its first bytes (MSFP video section 3.5, the
    // registry's brand table).
    [Theory]
    [InlineData("isom", "", "video/mp4")]
    [InlineData("qt  ", "", "video/quicktime")]
    [InlineData("M4A ", "", "audio/mp4")]
    [InlineData("3gp5", "", "video/3gpp")]
    [InlineData("zzzz", "mp42", "video/mp4")] // unknown, a compatible brand
    [InlineData("zzzz", "", "video/mp4")] // unknown
    public void SniffBrands(string major, string compatible, string want)
    {
        var b = new List<byte> { 0, 0, 0, (byte)(16 + compatible.Length) };
        b.AddRange(Encoding.Latin1.GetBytes("ftyp" + major + "\0\0\0\0" + compatible));
        Assert.Equal(want, Sniff.Of(b.ToArray()));
    }

    [Fact]
    public void SniffOthers()
    {
        static byte[] Ts(int n, int off)
        {
            var b = new byte[(3 * n) + off];
            for (var k = 0; k < 3; k++)
            {
                b[off + (k * n)] = 0x47;
            }

            return b;
        }

        Assert.Equal("video/matroska", Sniff.Of([0x1a, 0x45, 0xdf, 0xa3, 0x42, 0x82, 0x88, .. "matroska"u8]));
        Assert.Equal("video/webm", Sniff.Of([0x1a, 0x45, 0xdf, 0xa3, 0x42, 0x82, 0x84, .. "webm"u8]));
        Assert.Equal("video/mp2t", Sniff.Of(Ts(188, 0)));
        Assert.Equal("video/vnd.dlna.mpeg-tts", Sniff.Of(Ts(192, 4)));
        Assert.Equal("", Sniff.Of("plain"u8));
        Assert.Equal(("audio/matroska", "video/quicktime", "video/mp4"),
            (Sniff.AudioOrVideo("video/matroska", false), Sniff.AudioOrVideo("video/quicktime", false), Sniff.AudioOrVideo("video/mp4", true)));
    }

    // ffprobe's facts in MSFP's vocabularies: H.273 numbers, codecs strings.
    [Fact]
    public void ProbeMapping()
    {
        var v = Probes.VideoStreamOf(new FfprobeStream
        {
            CodecName = "hevc", CodecTag = "hvc1", Profile = "Main 10", Level = 153, Width = 3840, Height = 2160, PixFmt = "yuv420p10le",
            ColorRange = "tv", ColorSpace = "bt2020nc", ColorTransfer = "smpte2084", ColorPrimaries = "bt2020", AvgFrameRate = "48000/2002",
            SampleAspectRatio = "1:1", FieldOrder = "progressive", SideData = [new FfprobeSide { Type = "Display Matrix", Rotation = -90 }],
        });
        Assert.Equal(("hvc1.2.4.L153", 10, "24000/1001", null, false, 90), (v.Codecs, v.BitDepth, v.Fps, v.SampleAspect, v.Interlaced, v.Rotation));
        Assert.Equal(new VideoColour(9, 16, 9, false), v.Colour);
        Assert.NotNull(Rfc6381.Read(v.Codec, v.Codecs, v.BitDepth, v.Dv, out var why));
        Assert.Empty(why);
        var h = Probes.VideoStreamOf(new FfprobeStream { CodecName = "h264", Profile = "High", Level = 41, Width = 1920, Height = 1080, PixFmt = "yuv420p", Refs = 4 });
        Assert.Equal(("avc1.640029", 4, 8), (h.Codecs, h.RefFrames, h.BitDepth));
        Assert.Null(Probes.VideoStreamOf(new FfprobeStream { CodecName = "hevc", Profile = "Rext", Level = 120, Width = 1, Height = 1 }).Codecs);
        var a = Probes.AudioTrack(new FfprobeStream { Index = 1, CodecName = "eac3", Profile = "Dolby Digital Plus + Dolby Atmos", Channels = 6 });
        Assert.Equal(("a1", "ec-3", "dolby-atmos"), (a.Id, a.Codecs, a.Objects));
        foreach (var (codec, profile, want) in new[]
        {
            ("aac", "LC", "mp4a.40.2"), ("aac", "HE-AACv2", "mp4a.40.29"), ("dts", "DTS-HD MA", "dtsh"), ("dts", "DTS", "dtsc"),
            ("dts", "DTS Express", "dtse"), ("flac", "", "fLaC"),
        })
        {
            Assert.Equal(want, Probes.AudioCodecs(codec, profile));
        }
    }
}
