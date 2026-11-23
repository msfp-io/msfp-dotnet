using System.Text;
using System.Text.Json.Nodes;
using Msfp.Describe;
using Msfp.Protocol;

namespace Msfp.Describe.Tests;

public class ShapeTests
{
    internal static Probe Fixture(string name)
    {
        var f = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", name + ".json")))!;
        var o = f["outputs"]!;
        byte[] Raw(JsonNode? n) => Encoding.UTF8.GetBytes(n!.ToJsonString());
        var data = (o["data"] as JsonArray)?.Select(Raw).ToList() ?? [];
        return Probe.Parse(f["mediaType"]!.GetValue<string>(), f["size"]!.GetValue<long>(), new Outputs(Raw(o["streams"]), data, o["frames"] is { } fr ? Raw(fr) : null));
    }

    // A JSON tree with its members sorted, to compare two writers' copies.
    private static string Canonical(JsonNode? n) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonValue.Create(p.Key).ToJsonString() + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        null => "null",
        _ => n.ToJsonString(),
    };

    public static TheoryData<string> Names()
    {
        var d = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "testdata"), "*.json").Order(StringComparer.Ordinal))
        {
            if (!f.EndsWith(".copy.json", StringComparison.Ordinal))
            {
                d.Add(Path.GetFileNameWithoutExtension(f));
            }
        }

        return d;
    }

    // Each fixture's copy against msfp-go's golden copy (the same
    // testdata): the two engines shape the same facts; and read back by the
    // protocol's reader unchanged.
    [Theory]
    [MemberData(nameof(Names))]
    public void FixtureAgreesWithGo(string name)
    {
        var c = Shaper.Shape(Fixture(name), [new External("fre", "subrip") { Forced = true }]) with { Id = "v1", ETag = "e1" };
        var got = Copies.Write(c);
        var want = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", name + ".copy.json"));
        Assert.Equal(Canonical(JsonNode.Parse(want)), Canonical(JsonNode.Parse(got)));
        var back = Copies.Read(got);
        Assert.NotNull(back);
        Assert.Equal(Canonical(JsonNode.Parse(got)), Canonical(JsonNode.Parse(Copies.Write(back))));
    }

    [Fact]
    public void AtLeastFifteenFixtures() => Assert.True(Names().Count >= 15);

    private static Copy Shape(string name) => Shaper.Shape(Fixture(name), null);

    [Fact]
    public void Facts()
    {
        var c = Shape("h264-aac-mp4");
        Assert.Equal(("video/mp4", 2002L, "avc1.64000C", 4, 270, "24000/1001"), (c.MediaType, c.DurationMs, c.Video!.Codecs, c.Video.RefFrames, c.Video.Rotation, c.Video.Fps));
        Assert.Equal(("a1", "mp4a.40.2"), (c.AudioTracks.Single().Id, c.AudioTracks[0].Codecs));
        Assert.Equal([new CopyChapter(0, "Opening"), new CopyChapter(1000)], c.Chapters);

        c = Shape("h264-high10-mp4");
        Assert.Equal(("avc3.6E000C", 10), (c.Video!.Codecs, c.Video.BitDepth));
        c = Shape("h264-int-ts");
        Assert.True(c.MediaType == "video/mp2t" && c.Video!.Interlaced && c.Video.Codecs == "avc1.640015" && c.Video.RefFrames == 4 && c.Video.Fps == "25/1");

        c = Shape("hevc-hdr10-mkv");
        var v = c.Video!;
        Assert.Equal("hvc1.2.4.L60.90", v.Codecs);
        Assert.Equal(new VideoColour(9, 16, 9, false), v.Colour);
        Assert.True(Copies.HighRange(v));
        Assert.Empty(v.Hdr);
        Assert.True(c.AudioTracks is [{ Language: "fr", Default: true, Codecs: "ec-3", Channels: 6 }, { Language: "en", Role: "commentary" }]);
        Assert.True(c.SubtitleTracks is [{ Id: "s3", Forced: true, Format: "subrip" }, { Id: "s4", HearingImpaired: true, Format: "ass" }]);

        c = Shape("hevc-hlg-atc-ts");
        Assert.Equal((VideoColour.TransferHlg, "hvc1.2.4.L60.90"), (c.Video!.Colour!.Transfer, c.Video.Codecs));
        Assert.Equal("hev1.4.10.L60.9D.8", Shape("hevc-rext-mp4").Video!.Codecs);
        Assert.Equal(("hev1.2.4.L60.90", "24/1"), (Shape("hevc-pq-mp4").Video!.Codecs, Shape("hevc-pq-mp4").Video!.Fps));
        Assert.Equal("av01.0.00M.10", Shape("av1-mp4").Video!.Codecs);
        Assert.Equal("av01.1.00M.08.0.000.01.01.01.0", Shape("av1-444-mkv").Video!.Codecs);
        Assert.Equal("vp09.01.11.08.03.01.01.01.00", Shape("vp9-444-mp4").Video!.Codecs);
        c = Shape("vp9-opus-webm");
        Assert.Equal(("video/webm", "vp09.00.11.08", "opus"), (c.MediaType, c.Video!.Codecs, c.AudioTracks[0].Codec));

        c = Shape("dts-truehd-3d-mkv");
        Assert.Equal(("sbs", "dtsc", "mlpa", 24), (c.ThreeD, c.AudioTracks[0].Codecs, c.AudioTracks[1].Codecs, c.AudioTracks[1].BitDepth));
        c = Shape("mpeg2-m2ts");
        Assert.Equal(("video/vnd.dlna.mpeg-tts", "mpeg2video", null), (c.MediaType, c.Video!.Codec, c.Video.Codecs));
        c = Shape("mpeg4-avi");
        Assert.Equal(("video/avi", "pcm_s16le", 16), (c.MediaType, c.AudioTracks[0].Codec, c.AudioTracks[0].BitDepth));
        c = Shape("tone-flac");
        Assert.Equal(("audio/flac", null, "fLaC"), (c.MediaType, c.Video, c.AudioTracks[0].Codecs));
        c = Shape("tone-wav");
        Assert.Equal(("audio/wave", 24), (c.MediaType, c.AudioTracks[0].BitDepth));
        Assert.Equal(("audio/aac", "a0"), (Shape("adts-aac").MediaType, Shape("adts-aac").AudioTracks[0].Id));

        // Attachments take a stream index: external subtitles count above it.
        c = Shaper.Shape(Fixture("with-font-mkv"), [new External("eng", "webvtt")]);
        Assert.True(c.SubtitleTracks is [_, { Id: "s3", External: true, Language: "en" }]);

        c = Shape("handmade-dv-atmos");
        v = c.Video!;
        Assert.Equal(("hvc1.2.4.L153", 8, 1, false), (v.Codecs, v.Dv!.Profile, v.Dv.Compatibility, v.Dv.El!.Value));
        Assert.Equal(["hdr10plus"], v.Hdr);
        Assert.Null(c.ThreeD);
        Assert.True(c.AudioTracks is [{ Objects: "dolby-atmos", Original: true }, { Objects: "dolby-atmos" }, { Objects: "dts-x", Codecs: "dtsh", Role: "dub" }, { Codecs: "mp4a.40.29", Role: "description" }]);
        Assert.True(c.SubtitleTracks is [{ Id: "s5", Forced: true, Format: "hdmv_pgs_subtitle" }, { HearingImpaired: true }]);
        Assert.Equal([new CopyChapter(0, "Chapter 1"), new CopyChapter(60000), new CopyChapter(120000), new CopyChapter(180000), new CopyChapter(240000, "The Desert")], c.Chapters);
    }

    [Theory]
    [InlineData(90, 0, -65536, 65536, 0, 270)]
    [InlineData(-90, 0, 65536, -65536, 0, 90)]
    [InlineData(180, -65536, 0, 0, -65536, 180)]
    [InlineData(0, 65536, 0, 0, 65536, -1)]
    [InlineData(0, -65536, 0, 0, 65536, -1)] // mirrored
    public void Rotation(double angle, int a, int b, int c, int d, int want)
    {
        var m = $"\n00000000: {a} {b} 0\n00000001: {c} {d} 0\n00000002: 0 0 1073741824\n";
        var got = Shaper.Rotation(new SideData { Rotation = angle, DisplayMatrix = m });
        Assert.Equal(want < 0 ? null : want, got);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(90.5)]
    public void OddRotations(double angle) => Assert.Null(Shaper.Rotation(new SideData { Rotation = angle }));

    [Theory]
    [InlineData("Director's Commentary", "commentary", "commentary", false, false)]
    [InlineData("English AD", "description", "", false, false)]
    [InlineData("Audio Description", "description", "", false, false)]
    [InlineData("English (SDH)", "", "", false, true)]
    [InlineData("CC", "", "", false, true)]
    [InlineData("Forced", "", "", true, false)]
    [InlineData("Français (forcés)", "", "", true, false)]
    [InlineData("Foreign parts only", "", "", true, false)]
    [InlineData("Easy Reader", "", "easyreader", false, false)]
    [InlineData("English", "", "", false, false)]
    [InlineData("RELEASEGROUP", "", "", false, false)]
    [InlineData("Ad Astra", "", "", false, false)]
    [InlineData("Special Forces", "", "", false, false)]
    public void TitleFlags(string title, string audio, string subtitle, bool forced, bool hi) =>
        Assert.Equal(new Shaper.FromTitle(audio, subtitle, forced, hi), Shaper.TitleFlags(title));

    [Theory]
    [InlineData("Chapter 3", false)]
    [InlineData("The Desert", false)]
    [InlineData("00:12:00.000", false)]
    [InlineData("Opening", false)]
    [InlineData("movie.mkv", true)]
    [InlineData("www.example.com", true)]
    [InlineData("visit example.org", true)]
    [InlineData("http://a", true)]
    [InlineData("C:\\x", true)]
    [InlineData("a/b", true)]
    [InlineData("me@mail.example", true)]
    [InlineData("Dr. Strangelove", false)]
    public void LooksLikeLocation(string s, bool want) => Assert.Equal(want, Shaper.LooksLikeLocation(s));

    [Theory]
    [InlineData("24000/1001", "24000/1001", "24000/1001")]
    [InlineData("24000/1001", "10000000/417083", "24000/1001")]
    [InlineData("24000/1001", "2997/125", "24000/1001")]
    [InlineData("50/1", "25/1", "25/1")]
    [InlineData("0/0", "30/1", "30/1")]
    [InlineData("90000/1", "0/0", "90000/1")]
    [InlineData("2000000/1", "0/0", "")]
    public void Fps(string r, string avg, string want) => Assert.Equal(want, Shaper.Fps(new Msfp.Describe.Stream { RFrameRate = r, AvgFrameRate = avg }));

    [Theory]
    [InlineData("video/matroska", false, "audio/matroska")]
    [InlineData("video/mp4", false, "audio/mp4")]
    [InlineData("audio/mp4", true, "video/mp4")]
    [InlineData("video/quicktime", false, "video/quicktime")]
    [InlineData("video/avi", false, "video/avi")]
    [InlineData("audio/mpeg", true, "audio/mpeg")]
    [InlineData("video/mp2t", false, "audio/mp2t")]
    [InlineData("video/vnd.dlna.mpeg-tts", false, "video/vnd.dlna.mpeg-tts")]
    public void AudioOrVideo(string t, bool picture, string want) => Assert.Equal(want, MediaTypes.AudioOrVideo(t, picture));

    [Theory]
    [InlineData("video/x-msvideo", "video/avi")]
    [InlineData("Audio/X-WAV", "audio/wave")]
    [InlineData("video/x-matroska; codecs=x", "video/matroska")]
    [InlineData("video/mp4", "video/mp4")]
    public void CanonicalType(string t, string want) => Assert.Equal(want, MediaTypes.CanonicalType(t));

    [Theory]
    [InlineData("matroska,webm", "video/matroska")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "video/mp4")]
    [InlineData("mpegts", "video/mp2t")]
    [InlineData("mkv", "video/matroska")]
    [InlineData("m2ts", "video/vnd.dlna.mpeg-tts")]
    [InlineData("avi", "video/avi")]
    [InlineData("webm", "video/webm")]
    [InlineData("hls", "")]
    [InlineData("mkv,webm", "video/matroska")]
    [InlineData("concat", "")]
    public void ContainerType(string name, string want) => Assert.Equal(want, MediaTypes.ContainerType(name));

    // Raw keeps no attachment, no format.filename, no string holding the folder.
    [Fact]
    public void RawWithoutPaths()
    {
        var raw = Encoding.UTF8.GetString(Fixture("with-font-mkv").Raw("/media/films"));
        Assert.DoesNotContain("attachment", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"filename\"", raw, StringComparison.Ordinal);
        Assert.Contains("extradata", raw, StringComparison.Ordinal);
    }
}
