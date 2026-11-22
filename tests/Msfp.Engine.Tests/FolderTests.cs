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
        Assert.Equal((4L, "matroska", "Heat (1995) [tmdbid-949].mkv", true), (heat.Size, heat.Container, heat.Id, heat.Previews));
        Assert.NotEmpty(heat.ETag);
        Assert.Equal("fr", Assert.Single(heat.SubtitleTracks).Language);
        Assert.Equal(Path.Combine("Alien (1979) [tmdbid-348]", "Alien.mp4"), byKey["movie/tmdb/348"].Copies![0].Id);
        Assert.Equal(new Place { Season = 1, Episode = 1 }, byKey["series/tmdb/95396/e1949537"].Place);
        var extra = byKey.Single(kv => kv.Key.StartsWith("movie/tmdb/348/x/", StringComparison.Ordinal)).Value;
        Assert.Equal(("extra", "trailer", "Teaser"), (extra.Kind, extra.ExtraType, extra.Name));
        var local = byKey.Single(kv => kv.Key.StartsWith("movie/local/", StringComparison.Ordinal)).Value;
        Assert.Equal(("Holidays", 2019, true), (local.Name, local.Year, local.Image));
        Assert.Equal("picture", Encoding.UTF8.GetString(await f.ImageAsync(local.Key, Ct)));
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(async () => await f.ImageAsync("movie/tmdb/949", Ct))).Code);
        var info = await f.DescribeAsync(heat.Id, Ct);
        Assert.Equal(12, info.Trickplay?.TileCount);

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
}
