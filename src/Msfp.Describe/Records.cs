namespace Msfp.Describe;

/// <summary>What an H.264 stream's record says: from avcC or an Annex B sequence parameter set.</summary>
/// <param name="Profile">profile_idc.</param>
/// <param name="Constraints">The constraint_set flags byte.</param>
/// <param name="Level">level_idc.</param>
/// <param name="RefFrames">The SPS's max_num_ref_frames; 0 when no SPS was read.</param>
/// <param name="BitDepth">The SPS's luma bit depth; 0 when no SPS was read.</param>
/// <param name="Chroma">chroma_format_idc (0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4); 1 when the SPS doesn't say.</param>
public readonly record struct AvcRecord(int Profile, int Constraints, int Level, int RefFrames, int BitDepth, int Chroma);

/// <summary>
/// What an HEVC stream's record says: the general profile_tier_level of its
/// hvcC or of an Annex B SPS. Compatibility holds
/// general_profile_compatibility_flag[0..31] with flag 0 as its top bit;
/// Constraints the six bytes from general_progressive_source_flag on.
/// </summary>
public readonly record struct HevcRecord(int ProfileSpace, bool HighTier, int Profile, uint Compatibility, byte[] Constraints, int Level);

/// <summary>An av1C's facts: profile, level and tier of operating point 0, bit depth, monochrome, subsampling, chroma sample position.</summary>
public readonly record struct Av1Record(int Profile, int Level, bool HighTier, int BitDepth, bool Monochrome, bool SubX, bool SubY, int ChromaPosition);

/// <summary>What a VP9 stream's record says (vpcC, or Matroska's CodecPrivate features): Level and BitDepth 0, Chroma -1, when it doesn't say.</summary>
public readonly record struct Vp9Record(int Profile, int Level, int BitDepth, int Chroma);

/// <summary>
/// The codec records: the small structures in a file that hold what a
/// codecs string carries and ffprobe doesn't print whole (MSFP
/// implementers' notes, "Building codecs"): avcC, hvcC, av1C, vpcC, the
/// AudioSpecificConfig, Matroska's CodecPrivate, Annex B parameter sets.
/// The parsers read untrusted bytes: they never throw but
/// <see cref="DescribeException"/> of kind Record, and never allocate more
/// than their input.
/// </summary>
public static class Records
{
    private static readonly HashSet<ulong> AvcHighProfiles = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];

    private static DescribeException Bad() => new(DescribeError.Record, "record can't be read");

    /// <summary>Reads an H.264 record: an avcC (its first byte 1) or Annex B parameter sets.</summary>
    public static AvcRecord ParseAvc(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b[0] == 1)
        {
            var r = new AvcRecord(b[1], b[2], b[3], 0, 0, 1);
            if (b.Length >= 8 && (b[5] & 0x1f) >= 1)
            {
                var n = (b[6] << 8) | b[7];
                if (8 + n <= b.Length && n >= 4 && TryAvcSps(b.Slice(8, n), out var sps))
                {
                    r = r with { RefFrames = sps.RefFrames, BitDepth = sps.BitDepth, Chroma = sps.Chroma };
                }
            }

            return r;
        }

        foreach (var nal in AnnexB(b))
        {
            if (nal.Length >= 4 && (nal[0] & 0x1f) == 7)
            {
                return TryAvcSps(nal, out var sps) ? sps : throw Bad();
            }
        }

        throw Bad();
    }

    /// <summary>Whether ParseAvc reads b.</summary>
    public static bool TryParseAvc(ReadOnlySpan<byte> b, out AvcRecord r)
    {
        try
        {
            r = ParseAvc(b);
            return true;
        }
        catch (DescribeException)
        {
            r = default;
            return false;
        }
    }

    // An H.264 SPS NAL unit (its header byte first), up to max_num_ref_frames.
    private static bool TryAvcSps(ReadOnlySpan<byte> nal, out AvcRecord out_)
    {
        out_ = default;
        var r = new BitReader(Unescape(nal[1..]));
        var profile = r.U(8);
        int constraints = (int)r.U(8), level = (int)r.U(8), depth = 8, chromaIdc = 1;
        r.Ue(); // seq_parameter_set_id
        if (AvcHighProfiles.Contains(profile))
        {
            var chroma = r.Ue();
            if (chroma > 3)
            {
                return false;
            }

            chromaIdc = (int)chroma;
            if (chroma == 3)
            {
                r.U(1); // separate_colour_plane_flag
            }

            var d = r.Ue();
            r.Ue(); // bit_depth_chroma_minus8
            if (d > 6)
            {
                return false;
            }

            depth = 8 + (int)d;
            r.U(1); // qpprime_y_zero_transform_bypass_flag
            if (r.U(1) == 1)
            {
                // seq_scaling_matrix_present_flag
                var lists = chroma == 3 ? 12 : 8;
                for (var i = 0; i < lists; i++)
                {
                    if (r.U(1) == 1)
                    {
                        var size = i >= 6 ? 64 : 16;
                        long last = 8, next = 8;
                        for (var j = 0; j < size; j++)
                        {
                            if (next != 0)
                            {
                                next = (last + r.Se() + 256) % 256;
                            }

                            if (next != 0)
                            {
                                last = next;
                            }
                        }
                    }
                }
            }
        }

        r.Ue(); // log2_max_frame_num_minus4
        switch (r.Ue())
        {
            // pic_order_cnt_type
            case 0:
                r.Ue(); // log2_max_pic_order_cnt_lsb_minus4
                break;
            case 1:
                r.U(1);
                r.Se();
                r.Se();
                var n = r.Ue();
                if (n > 255)
                {
                    return false;
                }

                for (ulong i = 0; i < n; i++)
                {
                    r.Se();
                }

                break;
        }

        var refs = r.Ue();
        if (r.Bad || refs > 16)
        {
            return false;
        }

        out_ = new AvcRecord((int)profile, constraints, level, (int)refs, depth, chromaIdc);
        return true;
    }

    /// <summary>Reads an HEVC record: an hvcC (its first byte 1) or Annex B parameter sets.</summary>
    public static HevcRecord ParseHevc(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 13 && b[0] == 1)
        {
            return HevcPtl(b[1..13]);
        }

        foreach (var nal in AnnexB(b))
        {
            if (nal.Length >= 3 && ((nal[0] >> 1) & 0x3f) == 33)
            {
                var sps = Unescape(nal[2..]);

                // sps_video_parameter_set_id, sps_max_sub_layers_minus1 and
                // sps_temporal_id_nesting_flag fill one byte; the general
                // profile_tier_level follows, byte-aligned.
                return sps.Length < 13 ? throw Bad() : HevcPtl(sps.AsSpan(1, 12));
            }
        }

        throw Bad();
    }

    /// <summary>Whether ParseHevc reads b.</summary>
    public static bool TryParseHevc(ReadOnlySpan<byte> b, out HevcRecord r)
    {
        try
        {
            r = ParseHevc(b);
            return true;
        }
        catch (DescribeException)
        {
            r = default;
            return false;
        }
    }

    private static HevcRecord HevcPtl(ReadOnlySpan<byte> p) =>
        new(p[0] >> 6, (p[0] & 0x20) != 0, p[0] & 0x1f, ((uint)p[1] << 24) | ((uint)p[2] << 16) | ((uint)p[3] << 8) | p[4], p[5..11].ToArray(), p[11]);

    /// <summary>Reads an av1C: its marker bit and version 1 first.</summary>
    public static Av1Record ParseAv1(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0x81)
        {
            throw Bad();
        }

        var depth = (b[2] & 0x40) != 0 && (b[2] & 0x20) != 0 ? 12 : (b[2] & 0x40) != 0 ? 10 : 8;
        return new Av1Record(b[1] >> 5, b[1] & 0x1f, (b[2] & 0x80) != 0, depth, (b[2] & 0x10) != 0, (b[2] & 0x08) != 0, (b[2] & 0x04) != 0, b[2] & 0x03);
    }

    /// <summary>Whether ParseAv1 reads b.</summary>
    public static bool TryParseAv1(ReadOnlySpan<byte> b, out Av1Record r)
    {
        try
        {
            r = ParseAv1(b);
            return true;
        }
        catch (DescribeException)
        {
            r = default;
            return false;
        }
    }

    /// <summary>
    /// Reads a VP9 record: Matroska's features (ID, length 1, value; IDs 1
    /// profile, 2 level, 3 bit depth, 4 chroma subsampling) when the bytes are
    /// wholly of that form, else a vpcC box's payload (version 1).
    /// </summary>
    public static Vp9Record ParseVp9(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b.Length % 3 == 0)
        {
            int profile = -1, level = 0, depth = 0, chroma = -1;
            var ok = true;
            for (var i = 0; i < b.Length && ok; i += 3)
            {
                ok = b[i] >= 1 && b[i] <= 4 && b[i + 1] == 1;
                if (ok)
                {
                    int v = b[i + 2];
                    switch (b[i])
                    {
                        case 1: profile = v; break;
                        case 2: level = v; break;
                        case 3: depth = v; break;
                        case 4: chroma = v; break;
                    }
                }
            }

            if (ok && profile is >= 0 and <= 3)
            {
                return new Vp9Record(profile, level, depth, chroma);
            }
        }

        if (b.Length >= 7 && b[0] == 1)
        {
            return new Vp9Record(b[4], b[5], b[6] >> 4, (b[6] >> 1) & 7);
        }

        throw Bad();
    }

    /// <summary>Whether ParseVp9 reads b.</summary>
    public static bool TryParseVp9(ReadOnlySpan<byte> b, out Vp9Record r)
    {
        try
        {
            r = ParseVp9(b);
            return true;
        }
        catch (DescribeException)
        {
            r = default;
            return false;
        }
    }

    /// <summary>Reads an AAC AudioSpecificConfig: its audio object type (2 LC, 5 HE-AAC, 29 HE-AAC v2, 42 xHE-AAC…), the escape (31) read on.</summary>
    public static int ParseAudioConfig(ReadOnlySpan<byte> b)
    {
        var r = new BitReader(b.ToArray());
        var ot = (int)r.U(5);
        if (ot == 31)
        {
            ot = 32 + (int)r.U(6);
        }

        return r.Bad || ot == 0 ? throw Bad() : ot;
    }

    /// <summary>Whether ParseAudioConfig reads b.</summary>
    public static bool TryParseAudioConfig(ReadOnlySpan<byte> b, out int objectType)
    {
        try
        {
            objectType = ParseAudioConfig(b);
            return true;
        }
        catch (DescribeException)
        {
            objectType = 0;
            return false;
        }
    }

    // Annex B bytes split into NAL units, their start codes removed; none
    // when b doesn't start with one.
    internal static List<byte[]> AnnexB(ReadOnlySpan<byte> b)
    {
        var out_ = new List<byte[]>();
        var n = b.StartsWith((ReadOnlySpan<byte>)[0, 0, 1]) ? 3 : b.StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 1]) ? 4 : 0;
        if (n == 0)
        {
            return out_;
        }

        b = b[n..];
        while (b.Length > 0 && out_.Count < 64)
        {
            var i = b.IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (i < 0)
            {
                out_.Add(b.ToArray());
                break;
            }

            var end = i;
            if (end > 0 && b[end - 1] == 0)
            {
                end--;
            }

            out_.Add(b[..end].ToArray());
            b = b[(i + 3)..];
        }

        return out_;
    }

    // A NAL unit's payload without its emulation prevention bytes (00 00 03).
    internal static byte[] Unescape(ReadOnlySpan<byte> b)
    {
        if (b.IndexOf((ReadOnlySpan<byte>)[0, 0, 3]) < 0)
        {
            return b.ToArray();
        }

        var out_ = new List<byte>(b.Length);
        var zeros = 0;
        foreach (var c in b)
        {
            if (zeros >= 2 && c == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = c == 0 ? zeros + 1 : 0;
            out_.Add(c);
        }

        return out_.ToArray();
    }

    // Bits read most significant first; past the end, zeros, and Bad set.
    private sealed class BitReader(byte[] b)
    {
        private int _pos;

        public bool Bad { get; private set; }

        public ulong U(int n)
        {
            ulong v = 0;
            for (var i = 0; i < n; i++)
            {
                if (_pos >= b.Length * 8)
                {
                    Bad = true;
                    return v;
                }

                v = (v << 1) | (uint)((b[_pos / 8] >> (7 - (_pos % 8))) & 1);
                _pos++;
            }

            return v;
        }

        // An unsigned Exp-Golomb code, at most 31 leading zeros.
        public ulong Ue()
        {
            var zeros = 0;
            while (U(1) == 0)
            {
                if (Bad || zeros == 31)
                {
                    Bad = true;
                    return 0;
                }

                zeros++;
            }

            return (1UL << zeros) - 1 + U(zeros);
        }

        public long Se()
        {
            var k = Ue();
            return k % 2 == 1 ? (long)(k + 1) / 2 : -(long)(k / 2);
        }
    }
}
