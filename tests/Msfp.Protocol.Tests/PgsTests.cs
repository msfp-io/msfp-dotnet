using System.Buffers.Binary;

namespace Msfp.Protocol.Tests;

/// <summary>wire/pgs_test.go, and the pgs cases of media.json.</summary>
public class PgsTests
{
    private const byte Palette = 0x14, Object = 0x15, Presentation = 0x16, Window = 0x17, End = 0x80;

    [Fact]
    public void PgsVectors()
    {
        var cases = TestVectors.Cases("media.json", "pgs");
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (Pgs.Valid(c.Hex()) != c.Valid())
            {
                failures.Add($"{c.Str("name")}: want valid {c.Valid()}");
            }
        }

        TestVectors.NoFailures(failures, cases.Count, "media.json pgs");
    }

    // One PGS segment: its header (magic, times, type, size), then its body.
    private static byte[] Segment(byte type, params byte[] body)
    {
        var b = new byte[13 + body.Length];
        b[0] = (byte)'P';
        b[1] = (byte)'G';
        b[10] = type;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(11), (ushort)body.Length);
        body.CopyTo(b, 13);
        return b;
    }

    // A picture's segment, whole in one: id 1, w by h, its runs.
    private static byte[] Picture(int w, int h, params byte[] runs) =>
        Segment(Object, [0, 1, 0, 0xC0, 0, 0, 0, (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h, .. runs]);

    // A subtitle shown, then hidden.
    private static byte[] Track(byte[] picture) =>
    [
        .. Segment(Presentation, 7, 128, 4, 56, 16, 0, 0, 0x80, 0, 0, 1, 0, 1, 0, 0, 1, 0, 2, 0),
        .. Segment(Window, 1, 0, 1, 0, 2, 0, 0, 3, 0, 1),
        .. Segment(Palette, 0, 0, 1, 235, 128, 128, 255),
        .. picture,
        .. Segment(End),
        .. Segment(Presentation, 7, 128, 4, 56, 16, 0, 1, 0, 0, 0, 0),
        .. Segment(End),
    ];

    [Fact]
    public void CheckPgs()
    {
        var ok = Track(Picture(3, 2, 1, 0, 2, 0, 0, 0, 0x83, 1, 0, 0));
        Pgs.Check(ok);
        var first = Segment(Object, 0, 1, 0, 0x80, 0, 0, 0, 0, 3, 0, 2, 1, 0, 2);
        var rest = Segment(Object, 0, 1, 0, 0x40, 0, 0, 0, 0x83, 1, 0, 0);
        Assert.True(Pgs.Valid(Track([.. first, .. rest])), "in fragments");
        Assert.True(Pgs.Valid([]), "empty");
        var bad = new Dictionary<string, byte[]>
        {
            ["cut short"] = ok[..^3],
            ["not PGS"] = [.. "PX"u8, .. ok[2..]],
            ["unknown segment"] = [.. ok, .. Segment(0x42)],
            ["a huge screen"] = [.. Segment(Presentation, 0x40, 0, 0x40, 0, 16, 0, 0, 0x80, 0, 0, 0), .. Segment(End)],
            ["no screen"] = [.. Segment(Presentation, 0, 0, 0, 0, 16, 0, 0, 0x80, 0, 0, 0), .. Segment(End)],
            ["a line too long"] = Track(Picture(3, 2, 0, 0x84, 1)),
            ["too many lines"] = Track(Picture(3, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0)),
            ["a run cut"] = Track(Picture(3, 2, 0, 0x83)),
            ["a picture huge"] = Track(Picture(5000, 2)),
            ["a picture empty"] = Track(Picture(0, 2)),
            ["a lone fragment"] = Track(rest),
            ["a bent palette"] = [.. ok, .. Segment(Palette, 0, 0, 1)],
            ["a bent window"] = [.. ok, .. Segment(Window, 2, 0)],
            ["a short show"] = [.. ok, .. Segment(Presentation, 1, 2)],
            ["objects past end"] = [.. ok, .. Segment(Presentation, 7, 128, 4, 56, 16, 0, 0, 0, 0, 0, 3, 0, 1)],
            ["end with a body"] = [.. ok, .. Segment(End, 1)],
            ["too large"] = new byte[Pgs.MaxPgs + 1],
        };
        var failures = bad.Where(p => Pgs.Valid(p.Value)).Select(p => p.Key).ToList();
        Assert.True(failures.Count == 0, "accepted: " + string.Join(", ", failures));
        var e = Assert.Throws<ProtocolException>(() => Pgs.Check(ok[..^3]));
        Assert.Equal(ProtocolError.Pgs, e.Error);
    }
}
