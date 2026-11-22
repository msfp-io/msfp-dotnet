using System.Globalization;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

// play's request (MSFP playback sections 2.1, 2.2 and 2.7, video section
// 5.1): what a viewer's server sends of a device, coarsened, and how an
// owning server reads it, refusing what step 1 of section 2.2 refuses.
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

    /// <summary>The cap of playback section 2.2, step 6, without any other, in kbit/s.</summary>
    public const int DefaultCapKbps = 120_000;

    private const int MaxCapHeight = 100_000;
    private const int MaxCapChannels = 64;
    private const int MaxCapSampleRate = 768_000;
    private const int MaxCapBitDepth = 64;

    /// <summary>The steps maxAudioChannels is coarsened to (playback section 2.1), and transcode.audioChannels's values.</summary>
    public static readonly IReadOnlyList<int> ChannelSteps = [2, 6, 8];

    /// <summary>The steps maxAudioSampleRate is coarsened to (playback section 2.1).</summary>
    public static readonly IReadOnlyList<int> SampleRateSteps = [44_100, 48_000, 96_000, 192_000];

    /// <summary>The steps maxAudioBitDepth is coarsened to (playback section 2.1).</summary>
    public static readonly IReadOnlyList<int> BitDepthSteps = [16, 24, 32];

    // The members of play's request this part and video know: null in any
    // of them, anywhere, refuses the request (core section 2.3).
    private static readonly string[] PlayMembers =
    [
        "title", "purpose", "delivery", "container", "transcode", "audioRenditions", "capabilities",
        "startPositionMs", "maxBitrateKbps", "audioLanguage", "audioTrack", "versionId", "subtitleLanguage", "subtitleTrack",
    ];

    /// <summary>
    /// <paramref name="v"/> stepped down to the greatest of
    /// <paramref name="steps"/> not above it, the first below the first; 0
    /// (or less) is not said, and stays 0, never stepped up.
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
    /// The request's maxBitrateKbps as a viewer's server sends it (playback
    /// section 2.1): rounded down to a whole 1,000 from 1,000 up, kept as it
    /// is below; never above <see cref="MaxKbps"/>, never below 0.
    /// </summary>
    public static int CoarsenBitrate(int kbps) => kbps >= 1000 ? Math.Min(kbps, MaxKbps) / 1000 * 1000 : Math.Max(kbps, 0);

    /// <summary>
    /// Playback section 2.7's table of purpose and delivery: a play takes any
    /// delivery or none; a download file, or a transcode with a size
    /// (<see cref="TranscodeTargets.Sized"/>); a scan file only.
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
    /// (null: <see cref="TitleKey.TryParse"/>'s); purpose, delivery and
    /// container of their lists, container with delivery remux only, and
    /// then required; a transcode object with delivery transcode only, each
    /// target of its values; numbers in range; languages valid tags
    /// (<see cref="Languages.ValidTag"/>); capabilities as section 2.1 reads
    /// them, unknown entries ignored; purpose and delivery together as
    /// <see cref="PurposeAllows"/> says. Track IDs are checked later, at step
    /// 4 (<see cref="PlayRequest.TrackIdsOk"/>).
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
        if (o.ContainsKey("delivery") && delivery is not (DeliveryFile or DeliveryRemux or DeliveryTranscode))
        {
            throw BadPlay("delivery isn't file, remux or transcode");
        }

        var container = Text(o, "container");
        if (container is not null && delivery != DeliveryRemux)
        {
            throw BadPlay("container without delivery remux");
        }

        if (container is not null && !Names.IsContainer(container))
        {
            throw BadPlay("container isn't one of containers");
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
        PlayCapabilities? caps = null;
        if (o.TryGetPropertyValue("capabilities", out var c) && ReadCapabilities(c) is { Empty: false } read)
        {
            caps = read;
        }

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
        if (!PurposeAllows(purpose, delivery, targets))
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
            Capabilities = caps,
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

    // Reads capabilities as an owning server does (playback section 2.1,
    // video section 5.1): a list of the wrong type or with an entry of the
    // wrong type, or a number out of range, refuses; an unknown entry, in
    // another case included, is ignored; dv counts only with dvProfiles;
    // subtitles has no values yet: read for its type, kept empty.
    private static PlayCapabilities ReadCapabilities(JsonNode? v)
    {
        if (v is not JsonObject o)
        {
            throw BadPlay("capabilities isn't an object");
        }

        IReadOnlyList<string> List(string name, IReadOnlyList<string> names)
        {
            if (!o.TryGetPropertyValue(name, out var raw))
            {
                return [];
            }

            if (raw is not JsonArray arr)
            {
                throw BadPlay("capabilities." + name + " isn't an array");
            }

            var entries = new List<string>();
            foreach (var e in arr)
            {
                entries.Add(JsonMembers.AsString(e) ?? throw BadPlay("capabilities." + name + " has an entry that isn't a string"));
            }

            return Listed(entries, names);
        }

        int Number(string name, int top)
        {
            try
            {
                return (int)Integer(o, name, top);
            }
            catch (PlayRefusal)
            {
                throw BadPlay("capabilities." + name + " isn't an integer from 0 to " + top.ToString(CultureInfo.InvariantCulture));
            }
        }

        var containers = List("containers", Names.Containers);
        var videoCodecs = List("videoCodecs", Names.VideoCodecs);
        var audioCodecs = List("audioCodecs", Names.AudioCodecs);
        var audioProfiles = List("audioProfiles", Names.AudioProfiles);
        var hdr = List("hdr", Names.HdrFormats);
        _ = List("subtitles", []);
        IReadOnlyList<int> dv = [];
        if (o.TryGetPropertyValue("dvProfiles", out var rawDv))
        {
            if (rawDv is not JsonArray arr)
            {
                throw BadPlay("capabilities.dvProfiles isn't an array");
            }

            var profiles = new List<int>();
            foreach (var e in arr)
            {
                var n = JsonMembers.AsInteger(e) ?? throw BadPlay("capabilities.dvProfiles has an entry that isn't an integer");
                if (n is >= 0 and <= 10)
                {
                    profiles.Add((int)n);
                }
            }

            dv = DvListed(profiles);
        }

        if (dv.Count == 0)
        {
            hdr = [.. hdr.Where(s => s != "dv")];
        }

        return new PlayCapabilities
        {
            Containers = containers,
            VideoCodecs = videoCodecs,
            AudioCodecs = audioCodecs,
            AudioProfiles = audioProfiles,
            Hdr = hdr,
            DvProfiles = dv,
            MaxHeight = Number("maxHeight", MaxCapHeight),
            MaxAudioChannels = Number("maxAudioChannels", MaxCapChannels),
            MaxAudioSampleRate = Number("maxAudioSampleRate", MaxCapSampleRate),
            MaxAudioBitDepth = Number("maxAudioBitDepth", MaxCapBitDepth),
            MaxBitrateKbps = Number("maxBitrateKbps", MaxKbps),
        };
    }

    // Reads a transcode object (playback section 2.7, video section 5.1):
    // each target present of its values, else refused; Empty says
    // burnSubtitle was "" (malformed, refused at step 4 with the track IDs).
    private static (TranscodeTargets Targets, bool Empty) ReadTargets(JsonNode? v)
    {
        if (v is not JsonObject o)
        {
            throw BadPlay("transcode isn't an object");
        }

        string Codec(string name, IReadOnlyList<string> names)
        {
            var s = Text(o, name);
            if (s is not null && !names.Contains(s, StringComparer.Ordinal))
            {
                throw BadPlay("transcode." + name + " isn't one of its list");
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

        var videoCodec = Codec("videoCodec", Names.VideoCodecs);
        var audioCodec = Codec("audioCodec", Names.AudioCodecs);
        var targets = new TranscodeTargets
        {
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            MaxHeight = Target("maxHeight", n => Video.Heights.Contains((int)n)),
            MaxBitrateKbps = Target("maxBitrateKbps", n => n is >= 1000 and <= MaxKbps && n % 1000 == 0),
            AudioChannels = Target("audioChannels", n => ChannelSteps.Contains((int)n)),
        };
        var burn = Text(o, "burnSubtitle");
        return (targets with { BurnSubtitle = burn ?? "" }, burn == "");
    }

    // Keeps of entries those names holds, each once, sorted.
    internal static IReadOnlyList<string> Listed(IEnumerable<string> entries, IReadOnlyList<string> names) =>
        [.. entries.Where(e => names.Contains(e, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    // Keeps of profiles those Names.DvProfiles holds, each once, in increasing order.
    internal static IReadOnlyList<int> DvListed(IEnumerable<int> profiles) =>
        [.. profiles.Where(Names.DvProfiles.Contains).Distinct().Order()];

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
/// What a viewer's device plays (playback section 2.1, video section 5.1).
/// Each list holds only values of its names list, each once, sorted
/// (DvProfiles in increasing order); 0 is not said.
/// </summary>
public sealed record PlayCapabilities
{
    /// <summary>The containers it plays.</summary>
    public IReadOnlyList<string> Containers { get; init; } = [];

    /// <summary>The video codecs it plays.</summary>
    public IReadOnlyList<string> VideoCodecs { get; init; } = [];

    /// <summary>The audio codecs it plays.</summary>
    public IReadOnlyList<string> AudioCodecs { get; init; } = [];

    /// <summary>The audio profiles it plays in their codec.</summary>
    public IReadOnlyList<string> AudioProfiles { get; init; } = [];

    /// <summary>The HDR formats it shows.</summary>
    public IReadOnlyList<string> Hdr { get; init; } = [];

    /// <summary>The Dolby Vision profiles it shows.</summary>
    public IReadOnlyList<int> DvProfiles { get; init; } = [];

    /// <summary>The tallest picture, in pixels; 0: not said.</summary>
    public int MaxHeight { get; init; }

    /// <summary>The most audio channels; 0: not said.</summary>
    public int MaxAudioChannels { get; init; }

    /// <summary>The highest sample rate, in Hz; 0: not said.</summary>
    public int MaxAudioSampleRate { get; init; }

    /// <summary>The deepest bit depth; 0: not said.</summary>
    public int MaxAudioBitDepth { get; init; }

    /// <summary>The highest bitrate, in kbit/s; 0: not said.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>Whether it says nothing, as capabilities left out.</summary>
    public bool Empty =>
        Containers.Count + VideoCodecs.Count + AudioCodecs.Count + AudioProfiles.Count + Hdr.Count + DvProfiles.Count == 0 &&
        MaxHeight == 0 && MaxAudioChannels == 0 && MaxAudioSampleRate == 0 && MaxAudioBitDepth == 0 && MaxBitrateKbps == 0;

    /// <summary>
    /// What a viewer's server sends of its device's capabilities (playback
    /// section 2.1, video section 5.1): the lists lowercased, only their
    /// listed values, each once, sorted; maxHeight, maxAudioChannels,
    /// maxAudioSampleRate and maxAudioBitDepth stepped down; maxBitrateKbps
    /// rounded down to a whole 1,000, below 1,000 left out; 0 left out.
    /// </summary>
    public PlayCapabilities Coarsen()
    {
        static IReadOnlyList<string> Lower(IReadOnlyList<string> l, IReadOnlyList<string> names) =>
            Playback.Listed(l.Select(e => e.ToLowerInvariant()), names);
        return new PlayCapabilities
        {
            Containers = Lower(Containers, Names.Containers),
            VideoCodecs = Lower(VideoCodecs, Names.VideoCodecs),
            AudioCodecs = Lower(AudioCodecs, Names.AudioCodecs),
            AudioProfiles = Lower(AudioProfiles, Names.AudioProfiles),
            Hdr = Lower(Hdr, Names.HdrFormats),
            DvProfiles = Playback.DvListed(DvProfiles),
            MaxHeight = Playback.StepDown(MaxHeight, Video.Heights),
            MaxAudioChannels = Playback.StepDown(MaxAudioChannels, Playback.ChannelSteps),
            MaxAudioSampleRate = Playback.StepDown(MaxAudioSampleRate, Playback.SampleRateSteps),
            MaxAudioBitDepth = Playback.StepDown(MaxAudioBitDepth, Playback.BitDepthSteps),
            MaxBitrateKbps = MaxBitrateKbps >= 1000 ? Math.Min(MaxBitrateKbps, Playback.MaxKbps) / 1000 * 1000 : 0,
        };
    }

    /// <summary>Writes its members, those empty or 0 left out.</summary>
    public void Write(JsonOut w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Strings("containers", Some(Containers)).Strings("videoCodecs", Some(VideoCodecs))
            .Strings("audioCodecs", Some(AudioCodecs)).Strings("audioProfiles", Some(AudioProfiles)).Strings("hdr", Some(Hdr))
            .Integers("dvProfiles", DvProfiles.Count > 0 ? DvProfiles.Select(p => (long)p) : null)
            .Integer("maxHeight", NonZero(MaxHeight)).Integer("maxAudioChannels", NonZero(MaxAudioChannels))
            .Integer("maxAudioSampleRate", NonZero(MaxAudioSampleRate)).Integer("maxAudioBitDepth", NonZero(MaxAudioBitDepth))
            .Integer("maxBitrateKbps", NonZero(MaxBitrateKbps));
    }

    internal static IReadOnlyList<string>? Some(IReadOnlyList<string> l) => l.Count > 0 ? l : null;

    internal static long? NonZero(long v) => v != 0 ? v : null;
}

/// <summary>
/// A play's transcode object (playback section 2.7, video section 5.1):
/// each left out ("" or 0) is the owning server's choice.
/// </summary>
public sealed record TranscodeTargets
{
    /// <summary>The video codec to convert to.</summary>
    public string VideoCodec { get; init; } = "";

    /// <summary>The tallest picture, one of <see cref="Video.Heights"/>.</summary>
    public int MaxHeight { get; init; }

    /// <summary>The highest bitrate, a whole 1,000 kbit/s.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>The audio codec to convert to.</summary>
    public string AudioCodec { get; init; } = "";

    /// <summary>The audio channels, one of <see cref="Playback.ChannelSteps"/>.</summary>
    public int AudioChannels { get; init; }

    /// <summary>The subtitle track to burn into the picture (s&lt;n&gt;).</summary>
    public string BurnSubtitle { get; init; } = "";

    /// <summary>Whether it names a download's size (playback section 7): maxHeight, maxBitrateKbps or both.</summary>
    public bool Sized => MaxHeight != 0 || MaxBitrateKbps != 0;

    /// <summary>Writes its members, those empty left out.</summary>
    public void Write(JsonOut w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.String("videoCodec", NonEmpty(VideoCodec)).Integer("maxHeight", PlayCapabilities.NonZero(MaxHeight))
            .Integer("maxBitrateKbps", PlayCapabilities.NonZero(MaxBitrateKbps)).String("audioCodec", NonEmpty(AudioCodec))
            .Integer("audioChannels", PlayCapabilities.NonZero(AudioChannels)).String("burnSubtitle", NonEmpty(BurnSubtitle));
    }

    internal static string? NonEmpty(string s) => s.Length > 0 ? s : null;
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

    /// <summary>The delivery asked; "" for the owning server's choice.</summary>
    public string Delivery { get; init; } = "";

    /// <summary>The container of a remux.</summary>
    public string Container { get; init; } = "";

    /// <summary>The targets of a transcode; null when not said.</summary>
    public TranscodeTargets? Transcode { get; init; }

    /// <summary>Whether the audio tracks are asked for as renditions.</summary>
    public bool AudioRenditions { get; init; }

    /// <summary>The device's capabilities, coarsened; null when not said.</summary>
    public PlayCapabilities? Capabilities { get; init; }

    /// <summary>Where to start, in milliseconds.</summary>
    public long StartPositionMs { get; init; }

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
    /// non-zero of the request's maxBitrateKbps, its capabilities' and its
    /// transcode's, the owning server's own cap and the pairing's
    /// bitrateKbps; <see cref="Playback.DefaultCapKbps"/> without any. A
    /// download's and a scan's aren't capped so (sections 7 and 8).
    /// </summary>
    /// <param name="own">The owning server's own cap; 0 for none.</param>
    /// <param name="pairing">The pairing's bitrateKbps; 0 for none.</param>
    public int BitrateCap(int own, int pairing)
    {
        var caps = new List<int> { MaxBitrateKbps, own, pairing };
        if (Capabilities is { } c)
        {
            caps.Add(c.MaxBitrateKbps);
        }

        if (Transcode is { } t)
        {
            caps.Add(t.MaxBitrateKbps);
        }

        var positive = caps.Where(c => c > 0).ToList();
        return positive.Count == 0 ? Playback.DefaultCapKbps : positive.Min();
    }

    /// <summary>The request's body, as a viewer's server sends it: what is empty, 0 or false left out.</summary>
    public byte[] ToJson() => JsonOut.Object(w => w
        .String("title", Title)
        .String("purpose", TranscodeTargets.NonEmpty(Purpose))
        .String("delivery", TranscodeTargets.NonEmpty(Delivery))
        .String("container", TranscodeTargets.NonEmpty(Container))
        .Object("transcode", Transcode is { } t ? t.Write : null)
        .Bool("audioRenditions", AudioRenditions ? true : null)
        .Object("capabilities", Capabilities is { Empty: false } c ? c.Write : null)
        .Integer("startPositionMs", PlayCapabilities.NonZero(StartPositionMs))
        .Integer("maxBitrateKbps", PlayCapabilities.NonZero(MaxBitrateKbps))
        .String("audioLanguage", TranscodeTargets.NonEmpty(AudioLanguage))
        .String("audioTrack", TranscodeTargets.NonEmpty(AudioTrack))
        .String("versionId", TranscodeTargets.NonEmpty(VersionId))
        .String("subtitleLanguage", TranscodeTargets.NonEmpty(SubtitleLanguage))
        .String("subtitleTrack", TranscodeTargets.NonEmpty(SubtitleTrack)));
}
