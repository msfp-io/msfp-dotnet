using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// What a server's <see cref="IPlayback"/> makes (<see cref="MsfpConfig.Encoders"/>,
/// MSFP Media playing 2.7): the codecs it encodes, registry tokens, the
/// ranges it makes, and the codecs each HLS segment format carries. A peer's
/// transcode to another codec or range, and a remux or a copied stream a
/// segment format can't carry, are refused, 409 target-refused. The
/// baseline's, h264 and aac, and the range sdr, are always among them.
/// </summary>
public sealed record Encoders
{
    /// <summary>The video codecs besides h264 (hevc, av1…).</summary>
    public IReadOnlyList<string> Video { get; init; } = [];

    /// <summary>The audio codecs besides aac (ac3, eac3, opus…).</summary>
    public IReadOnlyList<string> Audio { get; init; } = [];

    /// <summary>The ranges besides sdr: <see cref="Names.RangeKeep"/> for a transcoder that keeps PQ and HLG.</summary>
    public IReadOnlyList<string> Ranges { get; init; } = [];

    /// <summary>
    /// The codecs each segment format carries, by subtype (mp2t, mp4); null,
    /// by default, for <see cref="DefaultSegments"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>>? Segments { get; init; }

    /// <summary>
    /// The codecs each HLS segment format carries when <see cref="Segments"/>
    /// names none: what MPEG-TS and fragmented MP4 segments commonly hold.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> DefaultSegments { get; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["mp2t"] = ["h264", "hevc", "aac", "ac3", "eac3", "mp3"],
            ["mp4"] = ["h264", "hevc", "av1", "vp9", "aac", "ac3", "eac3", "dts", "opus", "flac", "alac", "mp3"],
        };

    // What Msfp.Protocol's step 7 checks it against.
    internal OwnerMakes Makes => new()
    {
        VideoEncoders = [Names.BaselineVideoCodec, .. Video],
        AudioEncoders = [Names.BaselineAudioCodec, .. Audio],
        Ranges = [Names.RangeSdr, .. Ranges],
        SegmentCodecs = Segments ?? DefaultSegments,
    };

    // The refusal of a transcode's codecs not encoded here (the video codec
    // only for a copy with video), as step 7 refuses them; null when they
    // are, or are copy. For a queue entry's own transcode, a codec not made
    // refuses the request (MSFP Media music 5.2).
    internal PlayRefusal? RefuseCodecs(TranscodeTargets t, bool video)
    {
        var m = Makes;
        if (video && t.VideoCodec.Length > 0 && t.VideoCodec != Names.Copy && !m.VideoEncoders!.Contains(t.VideoCodec, StringComparer.Ordinal))
        {
            return new PlayRefusal(409, Playback.ProblemTargetRefused, "a video codec the source doesn't encode");
        }

        if (t.AudioCodec.Length > 0 && t.AudioCodec != Names.Copy && !m.AudioEncoders!.Contains(t.AudioCodec, StringComparer.Ordinal))
        {
            return new PlayRefusal(409, Playback.ProblemTargetRefused, "an audio codec the source doesn't encode");
        }

        return null;
    }
}

/// <summary>
/// What the engine has decided a play serves of the copy (MSFP
/// Media playing 2.7): the method; what it serves of the video ("" for a copy with
/// no video) and of the audio, <see cref="Names.Copy"/> for a stream left
/// unchanged, else the codec it becomes; for a transcode, its targets, the
/// baseline's values for those left out (MaxHeight 0: the copy's own height)
/// and without those a copied stream or a copy with no video doesn't take;
/// for a remux, its segment format (mp2t or mp4); and whether it counts as a
/// transcode (step 8: a transcode whose video and audio are both copied
/// doesn't).
/// </summary>
public sealed record Served
{
    /// <summary>direct, remux or transcode.</summary>
    public string Method { get; init; } = "";

    /// <summary>copy, or the video codec it becomes; "" for a copy with no video.</summary>
    public string Video { get; init; } = "";

    /// <summary>copy, or the audio codec it becomes.</summary>
    public string Audio { get; init; } = "";

    /// <summary>A transcode's targets; null for another delivery.</summary>
    public TranscodeTargets? Transcode { get; init; }

    /// <summary>A remux's segment format: mp2t or mp4.</summary>
    public string Segments { get; init; } = "";

    /// <summary>Whether it counts as a transcode in the pool.</summary>
    public bool Transcodes { get; init; }
}

public sealed partial class MsfpEngine
{
    // Step 7 of MSFP Media playing 2.2 for copy c and its audio track audio:
    // what is served, or the refusals MSFP names (Playback.RefuseDelivery,
    // against what is made here). Without delivery, the baseline: each stream
    // already within it left unchanged, the others converted to its codecs; above
    // the cap, the video (the audio, with no video) converted too.
    // Go's wire.Serve; Msfp.Protocol has its parts (RefuseDelivery, Baseline,
    // WithBaseline, TakesTranscode), put together here.
    internal static (Served? Served, PlayRefusal? Refused) Serve(Protocol.PlayRequest r, Copy c, string audio, int capKbps, Encoders enc)
    {
        if (Playback.RefuseDelivery(r, c, audio, capKbps, enc.Makes) is { } refused)
        {
            return (null, refused);
        }

        var hasVideo = c.Video is not null;
        var video = hasVideo ? Names.Copy : "";
        switch (r.Delivery)
        {
            case Playback.DeliveryFile:
                return (new Served { Method = Playback.MethodDirect, Video = video, Audio = Names.Copy }, null);
            case Playback.DeliveryRemux:
                var slash = r.MediaType.IndexOf('/', StringComparison.Ordinal);
                return (new Served { Method = Playback.MethodRemux, Video = video, Audio = Names.Copy, Segments = r.MediaType[(slash + 1)..] }, null);
            case Playback.DeliveryTranscode:
                var t = r.TargetsServed(hasVideo)!;
                return (new Served
                {
                    Method = Playback.MethodTranscode, Video = t.VideoCodec, Audio = t.AudioCodec, Transcode = t,
                    Transcodes = Playback.TakesTranscode(r, c, audio, capKbps),
                }, null);
        }

        var b = Playback.Baseline(c, audio, r.Purpose is Playback.PurposePlay or "" ? capKbps : 0);
        return (new Served
        {
            Method = b.Method, Video = b.Video ?? "", Audio = b.Audio, Transcodes = b.Method == Playback.MethodTranscode,
        }, null);
    }
}
