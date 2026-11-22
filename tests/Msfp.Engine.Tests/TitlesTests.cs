using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

// The revision of version 0's titles (msfp-go's titles_test.go): pages
// bounded by size, local titles shared only when allowed, extras under
// their titles, and the image of a local title.
public sealed class TitlesTests
{
    private static List<string> Keys(Dictionary<string, Title> titles) => [.. titles.Keys.Order(StringComparer.Ordinal)];

    // A page ends early at 3 MiB of answer, next set: the reader asks on,
    // and keeps every title (catalog section 3.1).
    [Fact]
    public async Task PagesBoundedBySize()
    {
        var h = new FakeHost();
        var chapters = Enumerable.Range(0, Copies.MaxChapters)
            .Select(i => new CopyChapter(i * 1000L, new string('c', 60) + i.ToString(CultureInfo.InvariantCulture))).ToList();
        for (var i = 1; i <= 300; i++)
        {
            var key = "movie/tmdb/" + i.ToString(CultureInfo.InvariantCulture);
            h.Film("films", key, "en");
            var v = (VideoTitle)h.Titles[key];
            v.Copies![0] = v.Copies[0] with { Chapters = chapters };
        }

        using var s = await Sharing.StartAsync(h);
        Assert.Equal(300, (await s.PeerTitlesAsync()).Count);
        var pages = s.Exchanges("titles");
        Assert.True(pages.Count >= 2, $"{pages.Count} pages");
        var after = "";
        for (var i = 0; i < pages.Count; i++)
        {
            var body = pages[i].ResponseBody;
            var ans = JsonNode.Parse(body)!.AsObject();
            var titles = ans["titles"]!.AsArray();
            var next = (string?)ans["next"];
            var p = new CatalogPage
            {
                Titles = titles.Count, Bytes = Encoding.UTF8.GetByteCount(body), Remain = i < pages.Count - 1, After = after,
                LastKey = (string)titles[^1]!["key"]!, Next = next,
            };
            after = next ?? after;
            Assert.True(CatalogRules.CheckPage(p) is null, $"page {i} ({p.Bytes} bytes, {p.Titles} titles): {CatalogRules.CheckPage(p)}");
        }
    }

    // A host with a film shared under TMDB and its extras, and a local film
    // with its extra and its image (msfp-go's localHost).
    internal static (FakeHost H, string Local, byte[] Image) LocalHost()
    {
        var h = new FakeHost();
        h.Film("films", "movie/tmdb/1", "en");
        const string Local = "movie/local/q3Zp0Lw8";
        void Extra(string key, string type, string name) =>
            h.Titles[key] = new VideoTitle { Key = key, Kind = "extra", Libraries = ["films"], ExtraType = type, Name = name, Copies = FakeHost.OneCopy(key) };
        Extra("movie/tmdb/1/x/Tr7aQ1", "trailer", " Teaser\n");
        Extra("movie/tmdb/1/x/Th3me", "themesong", "");
        Extra("movie/tmdb/77/x/Orphan", "trailer", "");
        Extra(Local + "/x/Ex1", "featurette", "Making of");
        h.Titles[Local] = new VideoTitle
        {
            Key = Local, Kind = "movie", Libraries = ["films"], Name = "Summer\t2019", Year = 2019, Image = true,
            Ids = new() { ["imdb"] = "tt0133093" }, Copies = FakeHost.OneCopy(Local),
        };
        h.Titles["movie/local/NoName"] = new VideoTitle { Key = "movie/local/NoName", Kind = "movie", Libraries = ["films"], Copies = FakeHost.OneCopy("noname") };
        // Bytes after the image's end, which re-encoding drops.
        var img = Png(4, 3).Concat("trailing"u8.ToArray()).ToArray();
        h.Images[Local] = img;
        return (h, Local, img);
    }

    // Local titles are shared only once the owner allows it, per pairing
    // (catalog section 5): the change forces a whole reading. Extras go with
    // their title only, never theme songs.
    [Fact]
    public async Task LocalTitlesAndExtras()
    {
        var (h, local, _) = LocalHost();
        using var s = await Sharing.StartAsync(h);
        Assert.Equal(["movie/tmdb/1", "movie/tmdb/1/x/Tr7aQ1"], Keys(await s.PeerTitlesAsync()));
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.TitleImageAsync(s.OnB, local))).Code);
        var x = (VideoTitle)(await s.PeerTitlesAsync())["movie/tmdb/1/x/Tr7aQ1"];
        Assert.Equal(("trailer", "Teaser", 1), (x.ExtraType, x.Name, x.Copies!.Count));

        await s.A.Engine.SetLocalAsync(s.OnA, true);
        Assert.True((await s.A.Engine.PeerAsync(s.OnA)).Local);
        await s.B.Engine.ReadNowAsync(s.OnB);
        var body = s.Exchanges("titles")[^1].ResponseBody;
        Assert.DoesNotContain("\"changes\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tt0133093", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NoName", body, StringComparison.Ordinal);
        var got = await s.PeerTitlesAsync();
        Assert.Equal([local, local + "/x/Ex1", "movie/tmdb/1", "movie/tmdb/1/x/Tr7aQ1"], Keys(got));
        var v = (VideoTitle)got[local];
        Assert.Equal(("Summer 2019", 2019, true), (v.Name, v.Year, v.Image));
        Assert.Null(v.Ids);

        // Off again: a whole reading drops them.
        await s.A.Engine.SetLocalAsync(s.OnA, false);
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.Equal(["movie/tmdb/1", "movie/tmdb/1/x/Tr7aQ1"], Keys(await s.PeerTitlesAsync()));

        // The film gone: its extra goes with it.
        h.Remove("movie/tmdb/1");
        s.A.Engine.CatalogChanged();
        await s.B.Engine.ReadNowAsync(s.OnB);
        Assert.Empty(await s.PeerTitlesAsync());
    }

    // The image of a local title (catalog section 4.4): asked for, served
    // under its ticket only, checked and re-encoded.
    [Fact]
    public async Task LocalTitleImage()
    {
        var (h, local, raw) = LocalHost();
        using var s = await Sharing.StartAsync(h);
        await s.A.Engine.SetLocalAsync(s.OnA, true);
        await s.B.Engine.ReadNowAsync(s.OnB);
        var (img, format) = await s.B.Engine.TitleImageAsync(s.OnB, local);
        Assert.Equal(CatalogRules.ImagePng, format);
        Assert.NotEqual(raw, img);
        Assert.False(Encoding.Latin1.GetString(img).Contains("trailing", StringComparison.Ordinal), "the image wasn't re-encoded");
        Assert.Equal((4, 3), (BinaryPrimitives.ReadInt32BigEndian(img.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(img.AsSpan(20))));
        Assert.Equal(Pixels(Png(4, 3)), Pixels(img));
        var calls = s.Exchanges("federation/image");
        Assert.Equal(200, Assert.Single(calls).Status);
        var url = (string)JsonNode.Parse(calls[0].ResponseBody)!["url"]!;
        Assert.EndsWith("/image", url, StringComparison.Ordinal);
        var target = url[url.IndexOf("/api/v1/stream/", StringComparison.Ordinal)..];
        Assert.Equal(200, await HostKit.GetAsync(s.A, target));

        // The ticket opens that image only: another name, a HEAD, a browser.
        var b = target[..^"image".Length];
        Assert.Equal(404, await HostKit.GetAsync(s.A, b + "file"));
        Assert.Equal(404, await HostKit.GetAsync(s.A, b + "master.m3u8"));
        Assert.Equal(404, await HostKit.GetAsync(s.A, target, "HEAD"));
        Assert.Equal(404, await HostKit.GetAsync(s.A, target, "GET", "Origin", "https://x.example"));

        // A title that isn't local, or isn't kept with an image: not asked.
        foreach (var key in new[] { "movie/tmdb/1", local + "/x/Ex1", "movie/local/Other" })
        {
            Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.TitleImageAsync(s.OnB, key))).Code);
        }

        Assert.Single(s.Exchanges("federation/image"));

        // An image a receiver refuses is never sent; one it would take, but
        // off its stream root, is dropped by the viewer.
        lock (h.Lock)
        {
            h.Images[local] = "GIF89a"u8.ToArray();
        }

        await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.TitleImageAsync(s.OnB, local));
        lock (h.Lock)
        {
            h.Images[local] = raw;
        }

        s.Hostile((path, _, _) => path.EndsWith("/federation/image", StringComparison.Ordinal)
            ? "{\"url\":\"https://elsewhere.example/api/v1/stream/abc/image\"}"u8.ToArray()
            : null);
        Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.TitleImageAsync(s.OnB, local))).Code);
        s.Hostile(null);

        // Local titles no longer shared: the owner refuses.
        await s.A.Engine.SetLocalAsync(s.OnA, false);
        Assert.Equal(MsfpError.NotFound, (await Assert.ThrowsAsync<MsfpException>(() => s.B.Engine.TitleImageAsync(s.OnB, local))).Code);
        Assert.Equal(404, await HostKit.GetAsync(s.A, target));
    }

    // A copy's names and track titles as sent (video section 3.6): labels
    // only, never a file's or a release's name.
    [Theory]
    [InlineData("Director's Cut", "Director's Cut")]
    [InlineData("4K HDR", "4K HDR")]
    [InlineData("Film.2160p.WEB-DL-GRP.mkv", "")]
    [InlineData("movie.mkv", "")]
    [InlineData("[GRP] Film", "")]
    [InlineData("Film BluRay", "")]
    [InlineData("Film x264", "")]
    [InlineData("Extended-GRP", "")]
    [InlineData(" Copy\ten ", "Copy en")]
    public void CopyLabels(string name, string want) => Assert.Equal(want, MsfpEngine.CopyLabel(name));

    [Theory]
    [InlineData("Commentary", "Commentary")]
    [InlineData("Signs & Songs", "Signs & Songs")]
    [InlineData("SDH", "SDH")]
    [InlineData("Commentaire du réalisateur", null)]
    [InlineData("Forcés", "Forcés")]
    [InlineData("Stereo 5.1", "Stereo 5.1")]
    [InlineData("German DTS-HD by GRP", null)]
    [InlineData("", null)]
    public void TrackTitles(string title, string? want) => Assert.Equal(want, MsfpEngine.TrackTitle(title));

    // A PNG of PngImage's re-encoding: each filter undone, an interlaced one
    // kept so, what isn't a PNG refused.
    [Fact]
    public void PngReencoded()
    {
        foreach (var filter in new byte[] { 0, 1, 2, 3, 4 })
        {
            var png = Png(5, 4, filter);
            var again = PngImage.Reencode(png);
            Assert.NotNull(again);
            Assert.Equal(Pixels(Png(5, 4)), Pixels(again));
        }

        Assert.Null(PngImage.Reencode("GIF89a"u8.ToArray()));
        var bad = Png(4, 3);
        bad[^20] ^= 1; // the data's CRC no longer matches
        Assert.Null(PngImage.Reencode(bad));
        Assert.Null(PngImage.Reencode(Png(4097, 1)));
    }

    // A width × height RGBA PNG, its pixels a pattern, each line with that
    // filter.
    internal static byte[] Png(int width, int height, byte filter = 0)
    {
        var stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        var previous = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var line = new byte[stride];
            for (var i = 0; i < stride; i++)
            {
                line[i] = (byte)((y * 31) + (i * 7));
            }

            var o = y * (stride + 1);
            raw[o] = filter;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= 4 ? line[i - 4] : 0, b = previous[i], c = i >= 4 ? previous[i - 4] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                var predictor = filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c,
                    _ => 0,
                };
                raw[o + 1 + i] = (byte)(line[i] - predictor);
            }

            previous = line;
        }

        using var z = new MemoryStream();
        using (var d = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            d.Write(raw);
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "tEXt", "Comment\0hello"u8.ToArray());
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    // A non-interlaced RGBA PNG's pixels, every line unfiltered (lines of
    // filter 0 only, as PngImage writes them, and Png(…, 0)).
    private static byte[] Pixels(byte[] png)
    {
        using var idat = new MemoryStream();
        for (var at = 8; at < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (Encoding.ASCII.GetString(png, at + 4, 4) == "IDAT")
            {
                idat.Write(png, at + 8, length);
            }

            at += 12 + length;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var word = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        s.Write(word);
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc(body));
        s.Write(word);
    }

    private static uint Crc(byte[] data)
    {
        var c = 0xffffffffu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xffffffffu;
    }
}
