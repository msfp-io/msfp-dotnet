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

    // A peer offered the folder, and one that isn't.
    private static readonly PeerRef Peer = new("peer", "Peer", ["folder"]);
    private static readonly PeerRef Other = new("other", "Other", []);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private void Write(string rel, string body)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, body);
    }

    // A first titles page forgets the probes of files no longer there.
    [Fact]
    public async Task ForgetsRemovedFiles()
    {
        Write("Heat (1995) [tmdbid-949].mkv", "heat");
        Write("Alien (1979) [tmdbid-348].mp4", "alien");
        var f = new Folder(dir) { Opaque = s => "o" + s.Length };
        await f.TitlesAsync([], "", 100, Ct);
        Assert.Equal(2, f.Probed);
        File.Delete(Path.Combine(dir, "Heat (1995) [tmdbid-949].mkv"));
        var page = await f.TitlesAsync([], "", 100, Ct);
        Assert.Single(page.Titles);
        Assert.Equal(1, f.Probed);
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
        await f.ServeAsync(Peer, "file " + heat.Id, new MediaPath { Kind = MediaKind.Subtitles, Track = "s0", Part = 0 }, null, sink, Ct);
        Assert.Contains("Bonjour", sink.Text, StringComparison.Ordinal);
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(async () =>
            await f.ServeAsync(Peer, "file ../x.mkv", new MediaPath { Kind = MediaKind.File }, null, new MemorySink(), Ct))).Code);
        // A peer the folder isn't offered to finds no copy of it.
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(async () =>
            await f.ServeAsync(Other, "file " + heat.Id, new MediaPath { Kind = MediaKind.Subtitles, Track = "s0", Part = 0 }, null, new MemorySink(), Ct))).Code);
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(async () => await f.DescribeAsync(Other, heat.Id, Ct))).Code);
    }

    // A copy lists at most 64 subtitle tracks (MSFP Media video 3.2): of 70
    // .vtt files beside a film, the first 64 by name.
    [Fact]
    public async Task SubtitlesCapped()
    {
        Write("Heat (1995) [tmdbid-949].mkv", "heat");
        for (var i = 0; i < 70; i++)
        {
            Write("Heat (1995) [tmdbid-949]." + (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".vtt", "WEBVTT\n");
        }

        var f = new Folder(dir) { Opaque = s => "o" + s.Length };
        var page = await f.TitlesAsync([], "", 100, Ct);
        var subs = page.Titles.Cast<VideoTitle>().Single(t => t.Key == "movie/tmdb/949").Copies![0].SubtitleTracks;
        Assert.Equal(Copies.MaxSubtitleTracks, subs.Count);
        Assert.Equal(("s0", "s63", true), (subs[0].Id, subs[63].Id, subs[63].External));
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

    // A file's own subtitle tracks listed with the .vtt beside it (MSFP
    // Media video 3.3: a copy lists all its subtitle tracks), told not served
    // apart; an AVI read. With ffprobe, on the media in EXAMPLE_MEDIA:
    // subs.mkv (h264, aac, SubRip fr, ASS en forced) and film.avi (mpeg4,
    // mp3), made with ffmpeg (testsrc and sine, 2 s); skipped otherwise (the
    // SDK image has no ffprobe).
    [Fact]
    public async Task FileTracks()
    {
        var media = Environment.GetEnvironmentVariable("EXAMPLE_MEDIA");
        if (string.IsNullOrEmpty(media) || !Directory.Exists(media))
        {
            Assert.Skip("EXAMPLE_MEDIA not set");
        }

        File.Copy(Path.Combine(media, "subs.mkv"), Path.Combine(dir, "Heat (1995) [tmdbid-949].mkv"));
        Write("Heat (1995) [tmdbid-949].de.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHallo\n");
        File.Copy(Path.Combine(media, "film.avi"), Path.Combine(dir, "Blade Runner (1982) [tmdbid-78].avi"));
        var f = new Folder(dir);
        var byKey = (await f.TitlesAsync([], "", 100, Ct)).Titles.Cast<VideoTitle>().ToDictionary(t => t.Key, StringComparer.Ordinal);
        var heat = byKey["movie/tmdb/949"].Copies![0];
        Assert.True(heat.SubtitleTracks is [
            { Id: "s2", Format: "subrip", Language: "fr", External: false },
            { Id: "s3", Format: "ass", Language: "en", Forced: true, External: false },
            { Id: "s4", Format: "webvtt", Language: "de", External: true }]);
        Assert.Equal(["s2", "s3"], (await f.DescribeAsync(Peer, heat.Id, Ct)).NotApart);
        var sink = new MemorySink();
        await f.ServeAsync(Peer, "file " + heat.Id, new MediaPath { Kind = MediaKind.Subtitles, Track = "s4", Whole = true }, null, sink, Ct);
        Assert.Contains("Hallo", sink.Text, StringComparison.Ordinal);
        var avi = byKey["movie/tmdb/78"].Copies![0];
        Assert.Equal(("video/avi", "mpeg4", "a1"), (avi.MediaType, avi.Video?.Codec, Assert.Single(avi.AudioTracks).Id));
        Assert.True(avi.DurationMs > 1900);
    }
}
