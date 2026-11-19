using System.Buffers.Binary;

namespace Msfp.Protocol;

/// <summary>
/// Subtitles that are pictures (PGS, from Blu-rays; MSFP video, section
/// 6.4): a track served as it is, only when every part of it is
/// well-formed and within its limits. The player decodes and draws it;
/// nothing in it is run, and a browser never renders it as a page.
/// </summary>
public static class Pgs
{
    /// <summary>The most a whole PGS track may be, in bytes.</summary>
    public const int MaxPgs = 64 << 20;

    private const int MaxSide = 4096;      // a picture's or the screen's width or height
    private const int MaxObjects = 20_000; // pictures in a track

    private const byte Palette = 0x14;
    private const byte Object = 0x15;
    private const byte Presentation = 0x16;
    private const byte Window = 0x17;
    private const byte End = 0x80;

    /// <summary>
    /// Reads a PGS track whole: every segment's header and length, the
    /// presentations', windows' and palettes' sizes, and every picture's
    /// run-length data decoded (counted, not drawn) within the size it says
    /// it has. An empty part, of no segments, is valid: it is the answer for
    /// a stretch with no subtitles (video section 6.4).
    /// </summary>
    /// <exception cref="ProtocolException">The track isn't well-formed (<see cref="ProtocolError.Pgs"/>).</exception>
    public static void Check(ReadOnlySpan<byte> raw)
    {
        if (!Valid(raw))
        {
            throw new ProtocolException(ProtocolError.Pgs);
        }
    }

    /// <summary>Whether a PGS track is well-formed, as <see cref="Check"/> reads it.</summary>
    public static bool Valid(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > MaxPgs)
        {
            return false;
        }

        var objects = new Dictionary<ushort, Picture>();
        var count = 0;
        for (var pos = 0; pos < raw.Length;)
        {
            if (pos + 13 > raw.Length || raw[pos] != 'P' || raw[pos + 1] != 'G')
            {
                return false;
            }

            var type = raw[pos + 10];
            var size = BinaryPrimitives.ReadUInt16BigEndian(raw[(pos + 11)..]);
            if (pos + 13 + size > raw.Length)
            {
                return false;
            }

            var seg = raw.Slice(pos + 13, size);
            pos += 13 + size;
            switch (type)
            {
                case Presentation:
                {
                    // Width, height, rate, number, state, palette update,
                    // palette, then each object shown: 8 bytes, 16 when
                    // cropped.
                    if (seg.Length < 11)
                    {
                        return false;
                    }

                    // The screen the player makes a canvas of.
                    int w = BinaryPrimitives.ReadUInt16BigEndian(seg), h = BinaryPrimitives.ReadUInt16BigEndian(seg[2..]);
                    if (w == 0 || h == 0 || w > MaxSide || h > MaxSide)
                    {
                        return false;
                    }

                    var o = 11;
                    for (var k = 0; k < seg[10]; k++)
                    {
                        if (o + 8 > seg.Length)
                        {
                            return false;
                        }

                        var cropped = (seg[o + 3] & 0x80) != 0;
                        o += cropped ? 16 : 8;
                    }

                    if (o > seg.Length)
                    {
                        return false;
                    }

                    break;
                }

                case Window:
                    if (seg.Length < 1 || seg.Length != 1 + (9 * seg[0]))
                    {
                        return false;
                    }

                    break;
                case Palette:
                    if (seg.Length < 2 || (seg.Length - 2) % 5 != 0)
                    {
                        return false;
                    }

                    break;
                case Object:
                {
                    if (seg.Length < 4)
                    {
                        return false;
                    }

                    var id = BinaryPrimitives.ReadUInt16BigEndian(seg);
                    var flags = seg[3];
                    if ((flags & 0x80) != 0)
                    {
                        // Its first fragment: its size.
                        if (seg.Length < 11)
                        {
                            return false;
                        }

                        int w = BinaryPrimitives.ReadUInt16BigEndian(seg[7..]), h = BinaryPrimitives.ReadUInt16BigEndian(seg[9..]);
                        if (w == 0 || h == 0 || w > MaxSide || h > MaxSide)
                        {
                            return false;
                        }

                        if (++count > MaxObjects)
                        {
                            return false;
                        }

                        var picture = new Picture(w, h);
                        picture.Data.AddRange(seg[11..]);
                        objects[id] = picture;
                    }
                    else
                    {
                        if (!objects.TryGetValue(id, out var picture))
                        {
                            return false;
                        }

                        picture.Data.AddRange(seg[4..]);
                    }

                    if ((flags & 0x40) != 0)
                    {
                        // Its last fragment: checked whole.
                        if (!objects.TryGetValue(id, out var picture) || !RunsFit(picture.Data, picture.Width, picture.Height))
                        {
                            return false;
                        }

                        objects.Remove(id);
                    }

                    break;
                }

                case End:
                    if (seg.Length != 0)
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private sealed class Picture(int width, int height)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public List<byte> Data { get; } = [];
    }

    // Whether a picture's run-length data stays within w by h: no line
    // longer than w, no more than h lines, no run cut short.
    private static bool RunsFit(List<byte> d, int w, int h)
    {
        int x = 0, y = 0;
        for (var i = 0; i < d.Count;)
        {
            var b = d[i];
            i++;
            if (b != 0)
            {
                x++; // one pixel
            }
            else
            {
                if (i >= d.Count)
                {
                    return false;
                }

                var f = d[i];
                i++;
                if (f == 0)
                {
                    // The end of a line.
                    x = 0;
                    y++;
                    if (y > h)
                    {
                        return false;
                    }

                    continue;
                }

                var n = f & 0x3f;
                if ((f & 0x40) != 0)
                {
                    if (i >= d.Count)
                    {
                        return false;
                    }

                    n = (n << 8) | d[i];
                    i++;
                }

                if ((f & 0x80) != 0)
                {
                    // A color follows.
                    if (i >= d.Count)
                    {
                        return false;
                    }

                    i++;
                }

                x += n;
            }

            if (x > w)
            {
                return false;
            }
        }

        return true;
    }
}
