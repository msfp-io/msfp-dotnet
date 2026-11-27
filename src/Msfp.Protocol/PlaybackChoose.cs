using System.Globalization;

namespace Msfp.Protocol;

// What an owning server plays and serves for a play (MSFP
// Media playing 2.2, 2.6 and 2.7): the copy and the audio the request names, else the
// first copy and the file's default audio; whether it refuses the delivery
// asked of that copy (step 7); what the baseline serves without delivery;
// and whether the playback takes a transcode. The viewer's server chooses:
// the owning server only plays what it is asked.
public static partial class Playback
{
    /// <summary>
    /// Playback section 2.6's copy: the one <paramref name="versionId"/>
    /// names, when it names one of them; else the first, the owner's
    /// preferred. -1 when there are no copies (404).
    /// </summary>
    public static int ChooseCopy(IReadOnlyList<Copy> copies, string versionId)
    {
        ArgumentNullException.ThrowIfNull(copies);
        if (copies.Count == 0)
        {
            return -1;
        }

        for (var i = 0; i < copies.Count; i++)
        {
            if (copies[i].Id == versionId)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// Playback section 2.6's audio: the track <paramref name="track"/>
    /// names, when the copy has it; else the file's default audio: the track
    /// the file marks default, else its first. "" for a copy without audio.
    /// </summary>
    public static string ChooseAudio(Copy copy, string track)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var audio = copy.AudioTracks;
        if (!string.IsNullOrEmpty(track) && audio.Any(a => a.Id == track))
        {
            return track;
        }

        return (audio.FirstOrDefault(a => a.Default) ?? audio.FirstOrDefault())?.Id ?? "";
    }

    /// <summary>
    /// Step 7 of MSFP Media playing 2.2, the refusals the language names.
    /// 409 delivery-refused, the delivery as such: a delivery that isn't
    /// file, remux or transcode (a token read at step 1, MSFP Media base 3);
    /// file of a copy of several parts (whatever the purpose); file or remux
    /// of a play whose copy's overall rate (size × 8 / durationMs) is above
    /// <paramref name="capKbps"/> (<see cref="PlayRequest.BitrateCap"/>, 0:
    /// no cap). 409 target-refused, a target the source doesn't make: a
    /// remux to a media type of another subtype than mp2t or mp4
    /// (<see cref="Names.RemuxSubtypes"/>), or of the wrong top-level type
    /// (video/… for a copy with video, audio/… for one without), or whose
    /// segment format can't carry the copy's streams; a transcode codec the
    /// source doesn't encode (the video codec only for a copy with video); a
    /// copied stream (<see cref="Names.Copy"/>) that neither segment format
    /// carries; burnSubtitle with video copy; a range other than sdr or keep,
    /// or one the source can't make (for a copy with video whose video isn't
    /// copied); and a burnSubtitle the copy doesn't have. What the source
    /// makes is <paramref name="makes"/> (null, or a null member: not
    /// checked here). Null when none applies; a source may still refuse for
    /// reasons of its own (its policy, what it can make of the copy).
    /// </summary>
    /// <param name="request">The request, as <see cref="ReadPlay(ReadOnlySpan{byte}, Func{string, bool}?)"/> read it.</param>
    /// <param name="copy">The copy chosen (<see cref="ChooseCopy"/>).</param>
    /// <param name="audioTrack">The audio track chosen (<see cref="ChooseAudio"/>); "" for none.</param>
    /// <param name="capKbps">The bitrate cap of step 6.</param>
    /// <param name="makes">What the owning server makes.</param>
    public static PlayRefusal? RefuseDelivery(PlayRequest request, Copy copy, string audioTrack, int capKbps, OwnerMakes? makes = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(copy);
        static PlayRefusal Refuse(string why) => new(409, ProblemDeliveryRefused, why);
        static PlayRefusal RefuseTarget(string why) => new(409, ProblemTargetRefused, why);
        var hasVideo = copy.Video is not null;
        var audio = copy.AudioTracks.FirstOrDefault(a => a.Id == audioTrack);
        if (request.Delivery.Length > 0 && request.Delivery is not (DeliveryFile or DeliveryRemux or DeliveryTranscode))
        {
            return Refuse("a delivery the source doesn't make");
        }

        if (request.Delivery == DeliveryRemux)
        {
            var slash = request.MediaType.IndexOf('/', StringComparison.Ordinal);
            var top = slash < 0 ? request.MediaType : request.MediaType[..slash];
            var sub = slash < 0 ? "" : request.MediaType[(slash + 1)..];
            if (!Names.RemuxSubtypes.Contains(sub, StringComparer.Ordinal))
            {
                return RefuseTarget("a remux to a media type of another subtype than mp2t or mp4");
            }

            if (top != (hasVideo ? "video" : "audio"))
            {
                return RefuseTarget("a remux to " + request.MediaType + " of a copy that calls for " + (hasVideo ? "video/" : "audio/"));
            }

            if (makes?.Carries(sub, audio?.Codec, copy.Video?.Codec) == false)
            {
                return RefuseTarget("a remux to a segment format that can't carry the copy's streams");
            }
        }

        if (request.Transcode is { } t)
        {
            var videoCopy = hasVideo && t.VideoCodec == Names.Copy;
            var audioCopy = t.AudioCodec == Names.Copy;
            if (hasVideo && !videoCopy && t.VideoCodec.Length > 0 && makes?.VideoEncoders is { } ve &&
                !ve.Contains(t.VideoCodec, StringComparer.Ordinal))
            {
                return RefuseTarget("a video codec the source doesn't encode");
            }

            if (!audioCopy && t.AudioCodec.Length > 0 && makes?.AudioEncoders is { } ae && !ae.Contains(t.AudioCodec, StringComparer.Ordinal))
            {
                return RefuseTarget("an audio codec the source doesn't encode");
            }

            if ((videoCopy || audioCopy) && makes?.SegmentCodecs is not null)
            {
                var v = videoCopy ? copy.Video!.Codec : null;
                var a = audioCopy ? audio?.Codec : null;
                if (!makes.Carries("mp2t", a, v) && !makes.Carries("mp4", a, v))
                {
                    return RefuseTarget("a copied stream no segment format can carry");
                }
            }

            if (videoCopy && t.BurnSubtitle.Length > 0)
            {
                return RefuseTarget("burnSubtitle with video copy");
            }

            if (hasVideo && !videoCopy && t.Range.Length > 0)
            {
                if (t.Range is not (Names.RangeSdr or Names.RangeKeep))
                {
                    return RefuseTarget("a range other than sdr or keep");
                }

                if (makes?.Ranges is { } ranges && !ranges.Contains(t.Range, StringComparer.Ordinal))
                {
                    return RefuseTarget("a range the source can't make");
                }
            }
        }

        if (request.Delivery == DeliveryFile && copy.Several)
        {
            return Refuse("a copy of several parts is never delivered as file");
        }

        if (request.Delivery is DeliveryFile or DeliveryRemux && request.Purpose is PurposePlay or "" && capKbps > 0 &&
            copy.Size * 8 > (long)capKbps * copy.DurationMs)
        {
            return Refuse("the copy's overall rate is above the cap");
        }

        if (request.Transcode is { BurnSubtitle.Length: > 0 } b && !copy.SubtitleTracks.Any(s => s.Id == b.BurnSubtitle))
        {
            return RefuseTarget("burnSubtitle isn't a subtitle track of the copy");
        }

        return null;
    }

    /// <summary>
    /// What the owning server serves of a copy without delivery (MSFP
    /// Media playing 2.7): the baseline, every stream already within it left
    /// unchanged (<see cref="Names.Copy"/>), else converted to the baseline's
    /// codec; a remux when both are left unchanged, else a transcode. The
    /// video for a copy with video only: H.264 at most High profile and level
    /// 4.1 (by its codecs), 8-bit, progressive, with H.273 primaries,
    /// transfer and matrix 1; the audio AAC-LC (mp4a.40.2), 1 or 2 channels,
    /// at most 48 kHz. A fact the copy doesn't state isn't within.
    /// </summary>
    /// <param name="copy">The copy chosen.</param>
    /// <param name="audioTrack">The audio track chosen; "" for none.</param>
    public static BaselinePlan Baseline(Copy copy, string audioTrack)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var audio = copy.AudioTracks.FirstOrDefault(a => a.Id == audioTrack);
        var a = audio is not null && AudioWithin(audio) ? Names.Copy : Names.BaselineAudioCodec;
        string? v = copy.Video is { } video ? VideoWithin(video) ? Names.Copy : Names.BaselineVideoCodec : null;
        var method = a == Names.Copy && (v is null || v == Names.Copy) ? MethodRemux : MethodTranscode;
        return new BaselinePlan(v, a, method);
    }

    /// <summary>
    /// Whether a play of <paramref name="copy"/> takes a transcode (MSFP
    /// Media playing 2.6, MSFP playback section 6): a delivery transcode, unless both its video and
    /// its audio are copy; the baseline when it converts a stream
    /// (<see cref="Baseline"/>). A file or a remux never does.
    /// </summary>
    public static bool TakesTranscode(PlayRequest request, Copy copy, string audioTrack)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(copy);
        return request.Delivery switch
        {
            DeliveryTranscode => !(copy.Video is not null && request.Transcode?.VideoCodec == Names.Copy && request.Transcode.AudioCodec == Names.Copy),
            "" => Baseline(copy, audioTrack).Method == MethodTranscode,
            _ => false,
        };
    }

    // H.264 up to High and level 4.1, 8-bit 4:2:0, progressive, SDR in
    // BT.709.
    private static bool VideoWithin(VideoStream v) =>
        v.Codec == "h264" && AvcWithin(v.Codecs) && v.BitDepth == 8 && !v.Interlaced &&
        v.Colour is { Primaries: 1, Transfer: 1, Matrix: 1 };

    // An avc1 or avc3 string of Baseline (66), Main (77) or High (100), at a
    // level from 1 to 4.1.
    private static bool AvcWithin(string? codecs)
    {
        if (codecs is null || codecs.Length != 11 || codecs[4] != '.' || codecs[..4] is not ("avc1" or "avc3") ||
            !int.TryParse(codecs.AsSpan(5), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        var profile = b >> 16;
        var level = b & 0xff;
        return profile is 66 or 77 or 100 && level is >= 10 and <= 41;
    }

    // AAC-LC, 1 or 2 channels, at most 48 kHz.
    private static bool AudioWithin(CopyAudio a) =>
        a.Codec == "aac" && a.Codecs == "mp4a.40.2" && a.Channels is >= 1 and <= 2 && a.SampleRate is >= 1 and <= 48_000;
}

/// <summary>
/// What an owning server makes, for step 7 of MSFP Media playing 2.2
/// (<see cref="Playback.RefuseDelivery"/>): a null member is not checked.
/// </summary>
public sealed record OwnerMakes
{
    /// <summary>The video codecs it encodes (registry tokens).</summary>
    public IReadOnlyCollection<string>? VideoEncoders { get; init; }

    /// <summary>The audio codecs it encodes.</summary>
    public IReadOnlyCollection<string>? AudioEncoders { get; init; }

    /// <summary>The ranges it makes (sdr, keep).</summary>
    public IReadOnlyCollection<string>? Ranges { get; init; }

    /// <summary>The codecs each segment format (mp2t, mp4) carries, for a remux or a copied stream.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>>? SegmentCodecs { get; init; }

    /// <summary>Whether segment format <paramref name="subtype"/> carries these codecs (null ones skipped); true when <see cref="SegmentCodecs"/> isn't said.</summary>
    public bool Carries(string subtype, params string?[] codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        if (SegmentCodecs is null)
        {
            return true;
        }

        var carried = SegmentCodecs.GetValueOrDefault(subtype) ?? [];
        return codecs.All(c => c is null || carried.Contains(c, StringComparer.Ordinal));
    }
}

/// <summary>
/// What the baseline serves of a copy (MSFP Media playing 2.7): each stream
/// <see cref="Names.Copy"/>, left unchanged, or the baseline's codec; the
/// method, remux or transcode.
/// </summary>
/// <param name="Video">The video: copy or h264; null for a copy with no video.</param>
/// <param name="Audio">The audio: copy or aac.</param>
/// <param name="Method">remux or transcode.</param>
public sealed record BaselinePlan(string? Video, string Audio, string Method);
