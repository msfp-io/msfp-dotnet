using Msfp.Describe;
using Msfp.Protocol;
using S = Msfp.Describe.Stream;

namespace Msfp.Describe.Tests;

// msfp-go's describe/codecs_test.go and sniff_test.go, the same vectors.
public class CodecsTests
{
    private static byte[] H(string s) => Convert.FromHexString(s);

    public static TheoryData<string, S, string> VideoCases() => new()
    {
        { "h264 fields", new S { CodecName = "h264", Profile = "High", Level = 41 }, "avc1.640029" },
        { "h264 constrained baseline", new S { CodecName = "h264", Profile = "Constrained Baseline", Level = 30 }, "avc1.42401E" },
        { "h264 record wins", new S { CodecName = "h264", Profile = "High", Level = 41, Record = H("0142c01effe10010" + "6742c01e" + "da0280bfe5c044000003000400000300c83c58b9a0") }, "avc1.42C01E" },
        { "h264 avc3", new S { CodecName = "h264", CodecTag = "avc3", Profile = "Main", Level = 31 }, "avc3.4D001F" },
        { "h264 dolby vision dva1", new S { CodecName = "h264", CodecTag = "dva1", Profile = "High", Level = 40 }, "avc1.640028" },
        { "h264 dvav", new S { CodecName = "h264", CodecTag = "dvav", Profile = "High", Level = 40 }, "avc3.640028" },
        { "h264 unknown profile", new S { CodecName = "h264", Profile = "Foo", Level = 40 }, "" },
        { "h264 no level", new S { CodecName = "h264", Profile = "High", Level = -99 }, "" },
        { "hevc fields", new S { CodecName = "hevc", Profile = "Main 10", Level = 153 }, "hvc1.2.4.L153" },
        { "hevc main", new S { CodecName = "hevc", Profile = "Main", Level = 120 }, "hvc1.1.2.L120" },
        { "hevc dvh1", new S { CodecName = "hevc", CodecTag = "dvh1", Profile = "Main 10", Level = 150 }, "hvc1.2.4.L150" },
        { "hevc dvhe", new S { CodecName = "hevc", CodecTag = "dvhe", Profile = "Main 10", Level = 150 }, "hev1.2.4.L150" },
        { "hevc rext without record", new S { CodecName = "hevc", Profile = "Rext", Level = 120 }, "" },
        { "hevc rext record", new S { CodecName = "hevc", Profile = "Rext", Level = 60, Record = H("0104080000009d08000000003cf000fcfefafa00000f04") }, "hvc1.4.10.L60.9D.8" },
        { "av1 fields", new S { CodecName = "av1", Profile = "Main", Level = 8, PixFmt = "yuv420p10le" }, "av01.0.08M.10" },
        { "av1 4:2:2 fields", new S { CodecName = "av1", Profile = "Professional", Level = 12, PixFmt = "yuv422p10le", ColorPrimaries = "bt2020", ColorTransfer = "smpte2084", ColorSpace = "bt2020nc" }, "av01.2.12M.10.0.100.09.16.09.0" },
        { "av1 mono", new S { CodecName = "av1", Profile = "Main", Level = 4, PixFmt = "gray", ColorRange = "pc" }, "av01.0.04M.08.1.110.01.01.01.1" },
        { "av1 record", new S { CodecName = "av1", Profile = "Main", Level = 99, Record = H("81094c00") }, "av01.0.09M.10" },
        { "av1 no depth", new S { CodecName = "av1", Profile = "Main", Level = 8 }, "" },
        { "vp9 fields", new S { CodecName = "vp9", Profile = "Profile 0", Level = -99, PixFmt = "yuv420p", Width = 1920, Height = 1080, AvgFrameRate = "30/1" }, "vp09.00.40.08" },
        { "vp9 profile 2", new S { CodecName = "vp9", Profile = "Profile 2", PixFmt = "yuv420p10le", Width = 3840, Height = 2160, AvgFrameRate = "60/1" }, "vp09.02.51.10" },
        { "vp9 4:4:4", new S { CodecName = "vp9", Profile = "Profile 1", PixFmt = "yuv444p", Width = 640, Height = 360 }, "vp09.01.21.08.03.01.01.01.00" },
        { "vp9 matroska record", new S { CodecName = "vp9", Profile = "Profile 0", PixFmt = "yuv420p", Width = 640, Height = 360, Record = H("010100" + "02011f" + "030108") }, "vp09.00.31.08" },
        { "vp9 unknown profile", new S { CodecName = "vp9", PixFmt = "yuv420p" }, "" },
        { "mpeg2 none", new S { CodecName = "mpeg2video", Profile = "Main" }, "" },
    };

    [Theory]
    [MemberData(nameof(VideoCases))]
    public void Video(string name, S s, string want)
    {
        var got = CodecsStrings.Video(s, Shaper.VideoBitDepth(s));
        Assert.True(want == got, name + ": " + got);
        if (got.Length > 0)
        {
            var depth = Shaper.VideoBitDepth(s);
            Assert.NotNull(Rfc6381.Read(s.CodecName, got, depth == 0 ? null : depth, null, out _));
        }
    }

    [Theory]
    [InlineData("aac", "LC", "", "mp4a.40.2")]
    [InlineData("aac", "HE-AAC", "", "mp4a.40.5")]
    [InlineData("aac", "HE-AACv2", "", "mp4a.40.29")]
    [InlineData("aac", "LC", "2b92", "mp4a.40.5")]
    [InlineData("aac", "HE-AAC", "1188", "mp4a.40.5")]
    [InlineData("aac", "HE-AAC", "eb09", "mp4a.40.29")]
    [InlineData("aac", "xHE-AAC", "", "mp4a.40.42")]
    [InlineData("aac", "", "", "")]
    [InlineData("dts", "DTS", "", "dtsc")]
    [InlineData("dts", "DTS-ES", "", "dtsc")]
    [InlineData("dts", "DTS 96/24", "", "dtsc")]
    [InlineData("dts", "DTS-HD HRA", "", "dtsh")]
    [InlineData("dts", "DTS-HD MA", "", "dtsh")]
    [InlineData("dts", "DTS-HD MA + DTS:X IMAX", "", "dtsh")]
    [InlineData("dts", "DTS Express", "", "dtse")]
    [InlineData("dts", "", "", "")]
    [InlineData("ac3", "", "", "ac-3")]
    [InlineData("eac3", "Dolby Digital Plus + Dolby Atmos", "", "ec-3")]
    [InlineData("truehd", "", "", "mlpa")]
    [InlineData("flac", "", "", "fLaC")]
    [InlineData("opus", "", "", "Opus")]
    [InlineData("mp3", "", "", "")]
    public void Audio(string codec, string profile, string record, string want)
    {
        var got = CodecsStrings.Audio(new S { CodecName = codec, Profile = profile, Record = record.Length > 0 ? H(record) : null });
        Assert.Equal(want, got);
        if (got.Length > 0)
        {
            Assert.NotNull(Rfc6381.Read(codec, got, null, null, out _));
        }
    }

    private static byte[] Ftyp(string major, params string[] compat)
    {
        var b = new List<byte> { 0, 0, 0, (byte)(16 + (4 * compat.Length)) };
        b.AddRange(System.Text.Encoding.Latin1.GetBytes("ftyp" + major + "\0\0\0\0"));
        foreach (var c in compat)
        {
            b.AddRange(System.Text.Encoding.Latin1.GetBytes(c));
        }

        return b.ToArray();
    }

    private static byte[] Ts(int n, int off, int count)
    {
        var b = new byte[count * n];
        for (var k = 0; k < count; k++)
        {
            b[off + (k * n)] = 0x47;
        }

        return b;
    }

    private static byte[] L(string s) => System.Text.Encoding.Latin1.GetBytes(s);

    private static byte[] Mp3()
    {
        var b = new byte[(417 * 2) + 4];
        foreach (var i in new[] { 0, 417 })
        {
            new byte[] { 0xff, 0xfb, 0x90, 0x00 }.CopyTo(b, i);
        }

        return b;
    }

    private static byte[] Adts()
    {
        var b = new byte[40];
        byte[] hdr = [0xff, 0xf1, 0x50, 0x80, 0x02, 0x1f, 0xfc];
        hdr.CopyTo(b, 0);
        hdr.CopyTo(b, 16);
        return b;
    }

    public static TheoryData<string, byte[], string> SniffCases() => new()
    {
        { "isom", Ftyp("isom"), "video/mp4" }, { "qt", Ftyp("qt  "), "video/quicktime" }, { "M4A", Ftyp("M4A "), "audio/mp4" },
        { "3gp", Ftyp("3gp5"), "video/3gpp" }, { "3g2", Ftyp("3g2a"), "video/3gpp2" }, { "dash", Ftyp("dash"), "video/mp4" },
        { "unknown, compat", Ftyp("zzzz", "mj2s"), "video/mj2" }, { "unknown", Ftyp("zzzz"), "video/mp4" },
        { "matroska", [0x1a, 0x45, 0xdf, 0xa3, 0x42, 0x82, 0x88, .. L("matroska")], "video/matroska" },
        { "webm", [0x1a, 0x45, 0xdf, 0xa3, 0x42, 0x82, 0x84, .. L("webm")], "video/webm" },
        { "ebml, no type", [0x1a, 0x45, 0xdf, 0xa3], "video/matroska" },
        { "ts", Ts(188, 0, 5), "video/mp2t" }, { "m2ts", Ts(192, 4, 5), "video/vnd.dlna.mpeg-tts" },
        { "ts, too short", Ts(188, 0, 2), "" }, { "ts, broken", [.. Ts(188, 0, 3), 0x00], "" },
        { "avi", L("RIFF\x24\0\0\0AVI fmt "), "video/avi" }, { "wav", L("RIFF\x24\0\0\0WAVEfmt "), "audio/wave" },
        { "rf64", L("RF64\x24\0\0\0WAVEfmt "), "audio/wave" }, { "aiff", L("FORM\0\0\0\u0010AIFF"), "audio/aiff" },
        { "ogg", L("OggS\0\x02"), "video/ogg" }, { "flac", L("fLaC\0\0\0\x22"), "audio/flac" },
        { "mpeg-ps", [0, 0, 1, 0xba, 0x44], "video/mpeg" }, { "flv", L("FLV\x01\x05"), "video/x-flv" },
        { "mp3", Mp3(), "audio/mpeg" }, { "mp3, id3", [.. L("ID3\x04\0\0\0\0\0\0"), .. Mp3()], "audio/mpeg" },
        { "aac, id3", [.. L("ID3\x04\0\0\0\0\0\0"), .. Adts()], "audio/aac" }, { "adts", Adts(), "audio/aac" },
        { "one mp3 header", [0xff, 0xfb, 0x90, 0x00, 1, 2, 3], "" }, { "hls", L("#EXTM3U\n#EXT-X-ENDLIST\n"), "" },
        { "ffconcat", L("ffconcat version 1.0\nfile a.mkv\n"), "" }, { "sdp", L("v=0\no=- 0 0 IN IP4 127.0.0.1\n"), "" },
        { "asf (open)", [0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11], "" }, { "nothing", [], "" },
    };

    [Theory]
    [MemberData(nameof(SniffCases))]
    public void Sniff(string name, byte[] head, string want)
    {
        var got = MediaTypes.Sniff(head);
        Assert.True(want == got, name + ": " + got);
        Assert.True(got.Length == 0 || MediaTypes.Demuxer(got).Length > 0, name + ": no demuxer");
    }
}
