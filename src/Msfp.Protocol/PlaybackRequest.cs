using System.Globalization;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

// play's request (MSFP playback sections 2.1, 2.2 and 2.7, video section
// 5.1): what a viewer's server asks for, and how an owning server reads it,
// refusing what step 1 of section 2.2 refuses. The request says nothing of
// the viewer's device: the viewer's server asks for a delivery, or leaves
// it out for the baseline.
public static partial class Playback
{
    /// <summary>A playback to watch or listen to (playback section 1).</summary>
    public const string PurposePlay = "play";

    /// <summary>A playback to keep a file of (playback section 7).</summary>
    public const string PurposeDownload = "download";

    /// <summary>A playback to read a few bytes of the file (playback section 8).</summary>
    public const string PurposeScan = "scan";

    /// <summary>The copy's file as it is (playback section 2.7).</summary>
    public const string DeliveryFile = "file";

    /// <summary>The copy's streams in another container (playback section 2.7).</summary>
    public const string DeliveryRemux = "remux";

    /// <summary>The copy converted (playback section 2.7).</summary>
    public const string DeliveryTranscode = "transcode";

    /// <summary>A play answer's method for the file as it is (playback section 2.3).</summary>
    public const string MethodDirect = "direct";

    /// <summary>A play answer's method for a remux.</summary>
    public const string MethodRemux = "remux";

    /// <summary>A play answer's method for a transcode.</summary>
    public const string MethodTranscode = "transcode";

    /// <summary>The problem type of a malformed play, progress or stop (400).</summary>
    public const string ProblemInvalid = "invalid-request";

    /// <summary>The problem type of a title or a copy the owning server doesn't have (404).</summary>
    public const string ProblemNotFound = "not-found";

    /// <summary>The problem type of a limit reached (409).</summary>
    public const string ProblemStreamLimit = "stream-limit-reached";

    /// <summary>The problem type of a delivery refused (409).</summary>
    public const string ProblemDeliveryRefused = "delivery-refused";

    /// <summary>The problem type of a copy the owning server can't play (409).</summary>
    public const string ProblemNotPlayable = "not-playable";

    /// <summary>The problem type of a transcode without room (503).</summary>
    public const string ProblemServerBusy = "server-busy";

    /// <summary>The problem type of a report on a playback that has ended (410).</summary>
    public const string ProblemStopped = "playback-stopped";

    /// <summary>The largest position or duration, in milliseconds: 100 days.</summary>
    public const long MaxPositionMs = 8_640_000_000;

    /// <summary>The largest bitrate play takes, in kbit/s.</summary>
    public const int MaxKbps = 10_000_000;

    /// <summary>The values of a transcode's audioChannels (playback section 2.7).</summary>
    public static readonly IReadOnlyList<int> ChannelSteps = [2, 6, 8];

    // The members of play's request this part and video know: null in any
    // of them, anywhere, refuses the request (core section 2.3).
    private static readonly string[] PlayMembers =
    [
        "title", "purpose", "delivery", "container", "transcode", "audioRenditions",
        "startPositionMs", "maxBitrateKbps", "audioLanguage", "audioTrack", "versionId", "subtitleLanguage", "subtitleTrack",
    ];

    /// <summary>
    /// <paramref name="v"/> stepped down to the greatest of
    /// <paramref name="steps"/> not above it, the first below the first: a
    /// transcode target of its steps (<see cref="ChannelSteps"/>,
    /// <see cref="Video.Heights"/>) for a value at most that; 0 (or less) is
    /// not said, and stays 0, never stepped up.
    /// </summary>
    public static int StepDown(int v, IReadOnlyList<int> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (v <= 0)
        {
            return 0;
        }

        var out_ = steps[0];
        foreach (var s in steps)
        {
            if (s <= v)
            {
                out_ = s;
            }
        }

        return out_;
    }

    /// <summary>
    /// Playback section 2.7's table of purpose and a delivery of its list
    /// (file, remux, transcode, or "" for none): a play takes any delivery or
    /// none; a download file, or a transcode with a size
    /// (<see cref="TranscodeTargets.Sized"/>); a scan file only. A delivery
    /// not of the list is refused at step 7 whatever the purpose
    /// (<see cref="RefuseDelivery"/>).
    /// </summary>
    public static bool PurposeAllows(string purpose, string delivery, TranscodeTargets? targets) => purpose switch
    {
        PurposePlay or "" => true,
        PurposeDownload => delivery == DeliveryFile || (delivery == DeliveryTranscode && targets is { Sized: true }),
        PurposeScan => delivery == DeliveryFile,
        _ => false,
    };

    /// <summary>
    /// Reads play's request as step 1 of playback section 2.2 checks it: an
    /// I-JSON object; title a key <paramref name="playable"/> says play takes
    /// (null: <see cref="TitleKey.TryParse"/>'s); purpose of its list;
    /// delivery and container tokens (core section 8.5: an unknown token is
    /// kept, and refused at step 7 by <see cref="RefuseDelivery"/>), container
    /// with delivery remux only, and then required; a transcode object with
    /// delivery transcode only, its codecs tokens, its numbers of their
    /// steps; numbers in range; languages valid tags
    /// (<see cref="Languages.ValidTag"/>); purpose and a delivery of the list
    /// together as <see cref="PurposeAllows"/> says. Unknown members
    /// (capabilities among them, since proposal 17) are ignored. Track IDs
    /// are checked later, at step 4 (<see cref="PlayRequest.TrackIdsOk"/>).
    /// </summary>
    /// <exception cref="PlayRefusal">The request is refused (400 invalid-request).</exception>
    public static PlayRequest ReadPlay(ReadOnlySpan<byte> body, Func<string, bool>? playable = null) => ReadPlay(body, playable, out _);

    /// <summary>
    /// Reads play's request as <see cref="ReadPlay(ReadOnlySpan{byte}, Func{string, bool}?)"/>
    /// does, and gives the request's object as read: the members of a content
    /// part (music's queue: <see cref="Music.TryCheckQueue"/>) are read from
    /// it, the body parsed once.
    /// </summary>
    /// <param name="body">The request's body.</param>
    /// <param name="playable">Whether play takes a key; null: <see cref="TitleKey.TryParse"/>'s.</param>
    /// <param name="members">The request's object, as read.</param>
    /// <exception cref="PlayRefusal">The request is refused (400 invalid-request).</exception>
    public static PlayRequest ReadPlay(ReadOnlySpan<byte> body, Func<string, bool>? playable, out JsonObject members)
    {
        var o = RequestObject(body, PlayMembers);
        members = o;
        playable ??= t => TitleKey.TryParse(t, out var k) && k.Playable;
        if (Text(o, "title") is not { } title || !playable(title))
        {
            throw BadPlay("title isn't the key of a title play takes");
        }

        var purpose = Text(o, "purpose") ?? PurposePlay;
        if (purpose is not (PurposePlay or PurposeDownload or PurposeScan))
        {
            throw BadPlay("purpose isn't play, download or scan");
        }

        var delivery = Text(o, "delivery") ?? "";
        if (o.ContainsKey("delivery") && !Names.IsToken(delivery))
        {
            throw BadPlay("delivery isn't a token");
        }

        var container = Text(o, "container");
        if (container is not null && delivery != DeliveryRemux)
        {
            throw BadPlay("container without delivery remux");
        }

        if (container is not null && !Names.IsToken(container))
        {
            throw BadPlay("container isn't a token");
        }

        if (container is null && delivery == DeliveryRemux)
        {
            throw BadPlay("delivery remux without container");
        }

        TranscodeTargets? targets = null;
        var emptyTrack = false;
        if (o.TryGetPropertyValue("transcode", out var t))
        {
            if (delivery != DeliveryTranscode)
            {
                throw BadPlay("a transcode object without delivery transcode");
            }

            (targets, emptyTrack) = ReadTargets(t);
        }

        var renditions = Boolean(o, "audioRenditions") ?? false;

        var start = Integer(o, "startPositionMs", MaxPositionMs);
        var kbps = (int)Integer(o, "maxBitrateKbps", MaxKbps);
        var audioLanguage = Text(o, "audioLanguage");
        if (audioLanguage is not null && !Languages.ValidTag(audioLanguage))
        {
            throw BadPlay("audioLanguage isn't a valid tag");
        }

        var subtitleLanguage = Text(o, "subtitleLanguage");
        if (subtitleLanguage is not null && !Languages.ValidTag(subtitleLanguage))
        {
            throw BadPlay("subtitleLanguage isn't a valid tag");
        }

        var versionId = Text(o, "versionId") ?? "";
        var audioTrack = Text(o, "audioTrack");
        var subtitleTrack = Text(o, "subtitleTrack");
        emptyTrack |= audioTrack == "" || subtitleTrack == "";
        var known = delivery is "" or DeliveryFile or DeliveryRemux or DeliveryTranscode;
        if (known && !PurposeAllows(purpose, delivery, targets))
        {
            throw BadPlay("purpose " + purpose + " doesn't take that delivery");
        }

        return new PlayRequest
        {
            Title = title,
            Purpose = purpose,
            Delivery = delivery,
            Container = container ?? "",
            Transcode = targets,
            AudioRenditions = renditions,
            StartPositionMs = start,
            MaxBitrateKbps = kbps,
            AudioLanguage = audioLanguage ?? "",
            AudioTrack = audioTrack ?? "",
            VersionId = versionId,
            SubtitleLanguage = subtitleLanguage ?? "",
            SubtitleTrack = subtitleTrack ?? "",
            EmptyTrack = emptyTrack,
        };
    }

    // Reads a transcode object (playback section 2.7, video section 5.1):
    // its codecs tokens (an unknown one is refused at step 7, 409), its
    // numbers of their steps, else refused; Empty says burnSubtitle was "" or not a string
    // (malformed, refused at step 4 with the track IDs).
    private static (TranscodeTargets Targets, bool Empty) ReadTargets(JsonNode? v)
    {
        if (v is not JsonObject o)
        {
            throw BadPlay("transcode isn't an object");
        }

        string Codec(string name)
        {
            var s = Text(o, name);
            if (s is not null && !Names.IsToken(s))
            {
                throw BadPlay("transcode." + name + " isn't a token");
            }

            return s ?? "";
        }

        int Target(string name, Func<long, bool> ok)
        {
            if (!o.TryGetPropertyValue(name, out var raw))
            {
                return 0;
            }

            if (JsonMembers.AsInteger(raw) is not { } n || n < 0 || n > MaxKbps || !ok(n))
            {
                throw BadPlay("transcode." + name + " isn't one of its values");
            }

            return (int)n;
        }

        var videoCodec = Codec("videoCodec");
        var audioCodec = Codec("audioCodec");
        var targets = new TranscodeTargets
        {
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            MaxHeight = Target("maxHeight", n => Video.Heights.Contains((int)n)),
            MaxBitrateKbps = Target("maxBitrateKbps", n => n is >= 1000 and <= MaxKbps && n % 1000 == 0),
            AudioChannels = Target("audioChannels", n => ChannelSteps.Contains((int)n)),
        };
        // burnSubtitle's form, its type included, is step 4's (a string
        // s<n>; anything else is malformed there).
        if (!o.TryGetPropertyValue("burnSubtitle", out var burn))
        {
            return (targets, false);
        }

        var s = JsonMembers.AsString(burn) ?? "";
        return (targets with { BurnSubtitle = s }, s.Length == 0);
    }

    internal static PlayRefusal BadPlay(string why) => new(400, ProblemInvalid, why);

    // Reads body as a request's object: I-JSON, an object, with no null
    // anywhere in the members known (core section 2.3).
    internal static JsonObject RequestObject(ReadOnlySpan<byte> body, IEnumerable<string> known)
    {
        try
        {
            return StrictJson.ReadRequest(body, known);
        }
        catch (ProtocolException)
        {
            throw BadPlay("not an I-JSON object, or null in a member");
        }
    }

    // A string member: null when absent, refused (400) when it isn't a string.
    internal static string? Text(JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var v) ? JsonMembers.AsString(v) ?? throw BadPlay(name + " isn't a string") : null;

    // A boolean member: null when absent, refused (400) when it isn't a boolean.
    internal static bool? Boolean(JsonObject o, string name)
    {
        try
        {
            return o.GetBool(name);
        }
        catch (ProtocolException)
        {
            throw BadPlay(name + " isn't a boolean");
        }
    }

    // An integer member from 0 to top, 0 when absent; refused (400) otherwise.
    internal static long Integer(JsonObject o, string name, long top)
    {
        if (!o.TryGetPropertyValue(name, out var v))
        {
            return 0;
        }

        if (JsonMembers.AsInteger(v) is not { } n || n < 0 || n > top)
        {
            throw BadPlay(name + " isn't an integer from 0 to " + top.ToString(CultureInfo.InvariantCulture));
        }

        return n;
    }
}

/// <summary>
/// An owning server's refusal of a play, a progress or a stop: the status
/// and the problem type of its signed body (playback sections 2.2, 3, 4).
/// </summary>
public sealed class PlayRefusal : Exception
{
    /// <summary>A refusal.</summary>
    /// <param name="status">Its HTTP status.</param>
    /// <param name="problem">Its problem type (<see cref="Playback.ProblemInvalid"/> and the others).</param>
    /// <param name="why">Why, for logs.</param>
    public PlayRefusal(int status, string problem, string why)
        : base("msfp: " + problem + ": " + why)
    {
        Status = status;
        Problem = problem;
        Why = why;
    }

    /// <summary>The HTTP status.</summary>
    public int Status { get; }

    /// <summary>The problem type.</summary>
    public string Problem { get; }

    /// <summary>Why, for logs.</summary>
    public string Why { get; }
}

/// <summary>
/// A play's transcode object (playback section 2.7, video section 5.1):
/// each target left out ("" or 0) takes the baseline's value
/// (<see cref="WithBaseline"/>).
/// </summary>
public sealed record TranscodeTargets
{
    /// <summary>The video codec to convert to, a token: the encoder's codec as ffmpeg names it (h264, hevc, av1, vp9).</summary>
    public string VideoCodec { get; init; } = "";

    /// <summary>The tallest picture, one of <see cref="Video.Heights"/>.</summary>
    public int MaxHeight { get; init; }

    /// <summary>The highest bitrate, a whole 1,000 kbit/s.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>The audio codec to convert to, a token as ffmpeg names it (aac, ac3, eac3, opus, flac).</summary>
    public string AudioCodec { get; init; } = "";

    /// <summary>The audio channels, one of <see cref="Playback.ChannelSteps"/>.</summary>
    public int AudioChannels { get; init; }

    /// <summary>The subtitle track to burn into the picture (s&lt;n&gt;).</summary>
    public string BurnSubtitle { get; init; } = "";

    /// <summary>Whether it names a download's size (playback section 7): maxHeight, maxBitrateKbps or both.</summary>
    public bool Sized => MaxHeight != 0 || MaxBitrateKbps != 0;

    /// <summary>
    /// The targets the owning server converts to (playback section 2.7):
    /// these, those left out taking the baseline's values (videoCodec
    /// <see cref="Names.BaselineVideoCodec"/>, audioCodec
    /// <see cref="Names.BaselineAudioCodec"/>, audioChannels
    /// <see cref="Names.BaselineChannels"/>; maxHeight left out is the copy's
    /// own height, and no cap but step 6's). For a title without a picture
    /// (<paramref name="picture"/> false, a track), the video targets are
    /// ignored: dropped.
    /// </summary>
    public TranscodeTargets WithBaseline(bool picture) => this with
    {
        VideoCodec = !picture ? "" : VideoCodec.Length > 0 ? VideoCodec : Names.BaselineVideoCodec,
        MaxHeight = picture ? MaxHeight : 0,
        BurnSubtitle = picture ? BurnSubtitle : "",
        AudioCodec = AudioCodec.Length > 0 ? AudioCodec : Names.BaselineAudioCodec,
        AudioChannels = AudioChannels != 0 ? AudioChannels : Names.BaselineChannels,
    };

    /// <summary>Writes its members, those empty left out.</summary>
    public void Write(JsonOut w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.String("videoCodec", NonEmpty(VideoCodec)).Integer("maxHeight", NonZero(MaxHeight))
            .Integer("maxBitrateKbps", NonZero(MaxBitrateKbps)).String("audioCodec", NonEmpty(AudioCodec))
            .Integer("audioChannels", NonZero(AudioChannels)).String("burnSubtitle", NonEmpty(BurnSubtitle));
    }

    internal static string? NonEmpty(string s) => s.Length > 0 ? s : null;

    internal static long? NonZero(long v) => v != 0 ? v : null;
}

/// <summary>
/// play's request, as <see cref="Playback.ReadPlay(ReadOnlySpan{byte}, Func{string, bool}?)"/> reads it and as a
/// viewer's server sends it (<see cref="ToJson"/> leaves out what is
/// empty). Purpose is <see cref="Playback.PurposePlay"/> when the request
/// says none. Music's queue is music's.
/// </summary>
public sealed record PlayRequest
{
    /// <summary>The title's key.</summary>
    public string Title { get; init; } = "";

    /// <summary>The purpose: play, download or scan.</summary>
    public string Purpose { get; init; } = "";

    /// <summary>The delivery asked, a token (file, remux or transcode; another is refused at step 7); "" for the baseline.</summary>
    public string Delivery { get; init; } = "";

    /// <summary>The container of a remux, a token: mpegts or mp4 (another is refused at step 7).</summary>
    public string Container { get; init; } = "";

    /// <summary>The targets of a transcode; null when not said.</summary>
    public TranscodeTargets? Transcode { get; init; }

    /// <summary>Whether the audio tracks are asked for as renditions: a request the owning server may decline, never refuse.</summary>
    public bool AudioRenditions { get; init; }

    /// <summary>Where to start, in milliseconds.</summary>
    public long StartPositionMs { get; init; }

    /// <summary>
    /// What a delivery transcode converts to (playback section 2.7): the
    /// request's targets, a transcode object left out included, those left
    /// out taking the baseline's values (<see cref="TranscodeTargets.WithBaseline"/>);
    /// null for any other delivery.
    /// </summary>
    /// <param name="picture">Whether the title has a picture (video's; false for a track).</param>
    public TranscodeTargets? TargetsServed(bool picture) =>
        Delivery == Playback.DeliveryTranscode ? (Transcode ?? new TranscodeTargets()).WithBaseline(picture) : null;

    /// <summary>The bitrate cap asked, in kbit/s; 0 for none.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>The audio language wanted, a valid tag.</summary>
    public string AudioLanguage { get; init; } = "";

    /// <summary>The audio track wanted (a&lt;n&gt;).</summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>The copy wanted.</summary>
    public string VersionId { get; init; } = "";

    /// <summary>The subtitle language wanted (video's).</summary>
    public string SubtitleLanguage { get; init; } = "";

    /// <summary>The subtitle track wanted, s&lt;n&gt; or off (video's).</summary>
    public string SubtitleTrack { get; init; } = "";

    // The request had an audioTrack, a subtitleTrack or a burnSubtitle that
    // was "", which reads as absent but is malformed.
    internal bool EmptyTrack { get; init; }

    /// <summary>
    /// Whether the request's track IDs have their forms, as step 4 of
    /// playback section 2.2 checks them once the copy is chosen: audioTrack
    /// a&lt;n&gt;, subtitleTrack s&lt;n&gt; or off, transcode.burnSubtitle
    /// s&lt;n&gt; (never off); n from 0 to 999 without leading zeros.
    /// </summary>
    public bool TrackIdsOk =>
        !EmptyTrack &&
        (AudioTrack.Length == 0 || Playback.TryTrackId(AudioTrack, 'a', out _)) &&
        (SubtitleTrack.Length == 0 || SubtitleTrack == "off" || Playback.TryTrackId(SubtitleTrack, 's', out _)) &&
        (Transcode is not { BurnSubtitle.Length: > 0 } t || Playback.TryTrackId(t.BurnSubtitle, 's', out _));

    /// <summary>
    /// The cap of playback section 2.2, step 6, for a play: the lowest
    /// non-zero of the request's maxBitrateKbps and its transcode's, the
    /// owning server's own cap and the pairing's bitrateKbps; 0, no cap,
    /// without any. A download's and a scan's aren't capped so (sections 7
    /// and 8).
    /// </summary>
    /// <param name="own">The owning server's own cap; 0 for none.</param>
    /// <param name="pairing">The pairing's bitrateKbps; 0 for none.</param>
    public int BitrateCap(int own, int pairing)
    {
        var caps = new List<int> { MaxBitrateKbps, own, pairing };
        if (Transcode is { } t)
        {
            caps.Add(t.MaxBitrateKbps);
        }

        var positive = caps.Where(c => c > 0).ToList();
        return positive.Count == 0 ? 0 : positive.Min();
    }

    /// <summary>The request's body, as a viewer's server sends it: what is empty, 0 or false left out.</summary>
    public byte[] ToJson() => JsonOut.Object(w => w
        .String("title", Title)
        .String("purpose", TranscodeTargets.NonEmpty(Purpose))
        .String("delivery", TranscodeTargets.NonEmpty(Delivery))
        .String("container", TranscodeTargets.NonEmpty(Container))
        .Object("transcode", Transcode is { } t ? t.Write : null)
        .Bool("audioRenditions", AudioRenditions ? true : null)
        .Integer("startPositionMs", TranscodeTargets.NonZero(StartPositionMs))
        .Integer("maxBitrateKbps", TranscodeTargets.NonZero(MaxBitrateKbps))
        .String("audioLanguage", TranscodeTargets.NonEmpty(AudioLanguage))
        .String("audioTrack", TranscodeTargets.NonEmpty(AudioTrack))
        .String("versionId", TranscodeTargets.NonEmpty(VersionId))
        .String("subtitleLanguage", TranscodeTargets.NonEmpty(SubtitleLanguage))
        .String("subtitleTrack", TranscodeTargets.NonEmpty(SubtitleTrack)));
}
