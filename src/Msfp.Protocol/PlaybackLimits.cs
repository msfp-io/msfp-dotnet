using System.Text.Json.Nodes;

namespace Msfp.Protocol;

// The limits a libraries answer sends (MSFP playback section 5, MSFP
// Media music 5.1): what a caller keeps of them, and how an owning server
// counts a play against them (MSFP Media playing 2.2 and 6, MSFP playback
// sections 6 and 8).
public static partial class Playback
{
    /// <summary>The top of streams, transcodes, audioStreams and audioTranscodes (MSFP playback section 5).</summary>
    public const int MaxStreams = 1000;

    /// <summary>The top of bitrateKbps and downloadKbps.</summary>
    public const int MaxLimitKbps = 1_000_000;

    /// <summary>The top of downloads.</summary>
    public const int MaxDownloads = 1000;

    /// <summary>The top of downloadsPerDay.</summary>
    public const int MaxDownloadsPerDay = 10_000;

    /// <summary>The top of scansPerDay.</summary>
    public const int MaxScansPerDay = 1_000_000;

    /// <summary>The least progressSeconds a play answer gives (MSFP Media playing 3).</summary>
    public const int MinProgressSeconds = 45;

    /// <summary>The most progressSeconds a play answer gives (MSFP Media playing 3).</summary>
    public const int MaxProgressSeconds = 120;

    /// <summary>The interval of progress when a play answer gives none, or one out of range (MSFP Media playing 3).</summary>
    public const int DefaultProgressSeconds = 60;

    /// <summary>
    /// The idle limit at the default interval, 2 minutes
    /// (<see cref="IdleLimit"/> of <see cref="DefaultProgressSeconds"/>).
    /// </summary>
    public static readonly TimeSpan IdleEnd = TimeSpan.FromSeconds(2 * DefaultProgressSeconds);

    /// <summary>
    /// The interval of a playback whose answer gave
    /// <paramref name="progressSeconds"/> (MSFP Media playing 3): that many
    /// seconds, <see cref="DefaultProgressSeconds"/> when it is out of 45 to
    /// 120 (or 0, absent).
    /// </summary>
    public static TimeSpan ProgressInterval(int progressSeconds) =>
        TimeSpan.FromSeconds(progressSeconds is < MinProgressSeconds or > MaxProgressSeconds ? DefaultProgressSeconds : progressSeconds);

    /// <summary>
    /// The idle limit of a playback whose answer gave
    /// <paramref name="progressSeconds"/> (MSFP Media playing 3 and 5): twice
    /// its interval. A play or a scan with neither a progress nor a media
    /// request for that long ends; never a download.
    /// </summary>
    public static TimeSpan IdleLimit(int progressSeconds) => 2 * ProgressInterval(progressSeconds);

    /// <summary>A download ends that long after its play was answered (MSFP Media playing 6).</summary>
    public static readonly TimeSpan DownloadLife = TimeSpan.FromHours(24);

    /// <summary>A scan ends that long after its play was answered (MSFP playback section 8).</summary>
    public static readonly TimeSpan ScanLife = TimeSpan.FromMinutes(10);

    /// <summary>A download's progress and stop with at are recorded for that long after its play, even once it has ended.</summary>
    public static readonly TimeSpan LateReports = TimeSpan.FromDays(30);

    /// <summary>
    /// The floor of a scan answer's scanBytes, 4 MiB (MSFP playback section 8):
    /// what a scan's caller counts on when its answer states none, or one
    /// out of range (<see cref="ScanCapOf"/>).
    /// </summary>
    public const int ScanBytesFloor = 4 << 20;

    /// <summary>The top of a scan answer's scanBytes, 1 GiB (MSFP playback section 8).</summary>
    public const int ScanBytesTop = 1 << 30;

    /// <summary>The scan cap the implementers' notes suggest to owning servers, 32 MiB.</summary>
    public const int ScanBytesRecommended = 32 << 20;

    /// <summary>How many reads of one scan are served at once.</summary>
    public const int ScanReadsAtOnce = 2;

    /// <summary>The span over which a download's speed is measured against downloadKbps.</summary>
    public static readonly TimeSpan DownloadPace = TimeSpan.FromSeconds(10);

    /// <summary>Whether a playback of <paramref name="purpose"/> ends after its idle limit (<see cref="IdleLimit"/>) unused: a play's and a scan's, never a download's.</summary>
    public static bool Idles(string purpose) => purpose != PurposeDownload;

    /// <summary>
    /// How long a playback of <paramref name="purpose"/> lasts at most from
    /// its answer, as MSFP sets it: <see cref="DownloadLife"/>,
    /// <see cref="ScanLife"/>, or zero for a play (the owning server's own
    /// longest playback, if it has one).
    /// </summary>
    public static TimeSpan Life(string purpose) => purpose switch
    {
        PurposeDownload => DownloadLife,
        PurposeScan => ScanLife,
        _ => TimeSpan.Zero,
    };

    // The members a caller reads, with their ranges.
    private static readonly (string Name, long Floor, long Top)[] LimitTops =
    [
        ("streams", 0, MaxStreams), ("transcodes", 0, MaxStreams), ("bitrateKbps", 0, MaxLimitKbps), ("downloads", 0, MaxDownloads),
        ("downloadsPerDay", 0, MaxDownloadsPerDay), ("downloadKbps", 0, MaxLimitKbps), ("scansPerDay", 0, MaxScansPerDay),
        ("audioStreams", 0, MaxStreams), ("audioTranscodes", 0, MaxStreams),
    ];

    /// <summary>
    /// Reads the limits object of a libraries answer as a caller keeps it
    /// (MSFP playback section 5): null, ignored whole, for one that isn't an
    /// object or one of whose members isn't an integer (null included); a
    /// member out of its range is ignored alone; transcodes above streams is
    /// ignored only when streams is kept, audioTranscodes likewise; unknown
    /// members are ignored, scanBytes among them since proposal 17 (a scan's
    /// answer states its cap).
    /// </summary>
    public static KeptLimits? ReadLimits(ReadOnlySpan<byte> raw)
    {
        try
        {
            return ReadLimits(StrictJson.Parse(raw, nulls: true));
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>Reads the limits object of a libraries answer already parsed, as <see cref="ReadLimits(ReadOnlySpan{byte})"/>.</summary>
    public static KeptLimits? ReadLimits(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }

        var kept = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, floor, top) in LimitTops)
        {
            if (!o.TryGetPropertyValue(name, out var v))
            {
                continue;
            }

            if (JsonMembers.AsInteger(v) is not { } n)
            {
                return null;
            }

            if (n >= floor && n <= top)
            {
                kept[name] = (int)n;
            }
        }

        int Get(string name) => kept.GetValueOrDefault(name);
        if (kept.ContainsKey("transcodes") && kept.ContainsKey("streams") && Get("transcodes") > Get("streams"))
        {
            kept.Remove("transcodes");
        }

        if (kept.ContainsKey("audioTranscodes") && kept.ContainsKey("audioStreams") && Get("audioTranscodes") > Get("audioStreams"))
        {
            kept.Remove("audioTranscodes");
        }

        return new KeptLimits
        {
            Limits = new PlaybackLimits
            {
                Streams = Get("streams"),
                Transcodes = Get("transcodes"),
                BitrateKbps = Get("bitrateKbps"),
                Downloads = Get("downloads"),
                DownloadsPerDay = Get("downloadsPerDay"),
                DownloadKbps = Get("downloadKbps"),
                ScansPerDay = Get("scansPerDay"),
                AudioStreams = Get("audioStreams"),
                AudioTranscodes = Get("audioTranscodes"),
            },
            HasStreams = kept.ContainsKey("streams"),
            HasTranscodes = kept.ContainsKey("transcodes"),
            HasBitrate = kept.ContainsKey("bitrateKbps"),
            HasAudioStreams = kept.ContainsKey("audioStreams"),
            HasAudioTranscodes = kept.ContainsKey("audioTranscodes"),
        };
    }
}

/// <summary>
/// The limits object (MSFP playback section 5, MSFP Media music 5.1): playbacks of
/// purpose play at once, of them transcoded, a bitrate cap (0: none);
/// downloads at once, a day, their speed cap (0: none); scans a day; with
/// music, audio playbacks apart. The bytes one scan reads aren't a limit:
/// each scan's answer states them.
/// </summary>
public sealed record PlaybackLimits
{
    /// <summary>Playbacks of purpose play at once (0 to 1,000).</summary>
    public int Streams { get; init; }

    /// <summary>Of them, transcoded.</summary>
    public int Transcodes { get; init; }

    /// <summary>The bitrate cap, in kbit/s; 0 for none.</summary>
    public int BitrateKbps { get; init; }

    /// <summary>Downloads at once (0 to 1,000; 0: no downloads).</summary>
    public int Downloads { get; init; }

    /// <summary>Downloads started a day (0: no daily cap).</summary>
    public int DownloadsPerDay { get; init; }

    /// <summary>A download's speed cap, in kbit/s; 0 for none.</summary>
    public int DownloadKbps { get; init; }

    /// <summary>Scans a day (0: no scan access).</summary>
    public int ScansPerDay { get; init; }

    /// <summary>Audio playbacks at once, with music.</summary>
    public int AudioStreams { get; init; }

    /// <summary>Of them, transcoded.</summary>
    public int AudioTranscodes { get; init; }

    /// <summary>
    /// Whether an owning server may send them: every member within its
    /// range, transcodes at most streams, audioTranscodes at most
    /// audioStreams.
    /// </summary>
    public bool InRange
    {
        get
        {
            static bool In(int v, int top) => v >= 0 && v <= top;
            return In(Streams, Playback.MaxStreams) && In(Transcodes, Playback.MaxStreams) && In(BitrateKbps, Playback.MaxLimitKbps) &&
                In(Downloads, Playback.MaxDownloads) && In(DownloadsPerDay, Playback.MaxDownloadsPerDay) &&
                In(DownloadKbps, Playback.MaxLimitKbps) && In(ScansPerDay, Playback.MaxScansPerDay) &&
                In(AudioStreams, Playback.MaxStreams) && In(AudioTranscodes, Playback.MaxStreams) &&
                Transcodes <= Streams && AudioTranscodes <= AudioStreams;
        }
    }

    /// <summary>Writes its members, audioStreams and audioTranscodes left out when 0.</summary>
    public void Write(JsonOut w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Integer("streams", Streams).Integer("transcodes", Transcodes).Integer("bitrateKbps", BitrateKbps)
            .Integer("downloads", Downloads).Integer("downloadsPerDay", DownloadsPerDay).Integer("downloadKbps", DownloadKbps)
            .Integer("scansPerDay", ScansPerDay)
            .Integer("audioStreams", TranscodeTargets.NonZero(AudioStreams))
            .Integer("audioTranscodes", TranscodeTargets.NonZero(AudioTranscodes));
    }

    private static PlayRefusal StreamLimit(string why) => new(409, Playback.ProblemStreamLimit, why);

    /// <summary>
    /// Step 2 of MSFP Media playing 2.2: a 409 stream-limit-reached when the
    /// limit of the purpose is 0 (streams for play, downloads for download,
    /// scansPerDay for scan); null otherwise.
    /// </summary>
    public PlayRefusal? Open(string purpose) => purpose switch
    {
        Playback.PurposePlay or "" when Streams == 0 => StreamLimit("streams is 0"),
        Playback.PurposeDownload when Downloads == 0 => StreamLimit("downloads is 0"),
        Playback.PurposeScan when ScansPerDay == 0 => StreamLimit("scansPerDay is 0"),
        _ => null,
    };

    /// <summary>
    /// Step 5 of MSFP Media playing 2.2: a 409 stream-limit-reached when the
    /// limits of the purpose are used: for play, the streams all in use; for
    /// download, the downloads all in use or downloadsPerDay reached (0: no
    /// daily cap); for scan, scansPerDay reached. A download or a scan never
    /// counts against streams. Null otherwise.
    /// </summary>
    public PlayRefusal? Room(string purpose, LimitUse use)
    {
        switch (purpose)
        {
            case Playback.PurposeDownload:
                if (use.Downloads >= Downloads)
                {
                    return StreamLimit("the downloads are all in use");
                }

                return DownloadsPerDay > 0 && use.DownloadsToday >= DownloadsPerDay ? StreamLimit("downloadsPerDay reached") : null;
            case Playback.PurposeScan:
                return use.ScansToday >= ScansPerDay ? StreamLimit("scansPerDay reached") : null;
            default:
                return use.Streams >= Streams ? StreamLimit("the streams are all in use") : null;
        }
    }

    /// <summary>
    /// The transcode a playback that converts needs (MSFP
    /// Media playing 2.6 and 6): for a play, one of the pairing's transcodes and one of the
    /// server's budget; for a download, one of the server's budget only. A
    /// 503 server-busy without; null with.
    /// </summary>
    public PlayRefusal? TranscodeRoom(string purpose, LimitUse use)
    {
        if (purpose is Playback.PurposePlay or "" && use.Transcodes >= Transcodes)
        {
            return new PlayRefusal(503, Playback.ProblemServerBusy, "the pairing's transcodes are all in use, or 0");
        }

        return use.ServerTranscodesFree <= 0 ? new PlayRefusal(503, Playback.ProblemServerBusy, "the server's transcode budget is used") : null;
    }
}

/// <summary>
/// What a caller keeps of a peer's limits (MSFP playback section 5).
/// Downloads, DownloadsPerDay, DownloadKbps and ScansPerDay are 0 when
/// absent or out of range: no downloads, no scan access. The Has members
/// say whether the peer sent Streams, Transcodes, BitrateKbps,
/// AudioStreams and AudioTranscodes, in range.
/// </summary>
public sealed record KeptLimits
{
    /// <summary>The limits kept.</summary>
    public PlaybackLimits Limits { get; init; } = new();

    /// <summary>Whether streams was sent, in range.</summary>
    public bool HasStreams { get; init; }

    /// <summary>Whether transcodes was sent, in range.</summary>
    public bool HasTranscodes { get; init; }

    /// <summary>Whether bitrateKbps was sent, in range.</summary>
    public bool HasBitrate { get; init; }

    /// <summary>Whether audioStreams was sent, in range.</summary>
    public bool HasAudioStreams { get; init; }

    /// <summary>Whether audioTranscodes was sent, in range.</summary>
    public bool HasAudioTranscodes { get; init; }

    /// <summary>Whether the caller shows Download for the peer's titles: downloads above 0 (MSFP Media playing 6).</summary>
    public bool OffersDownloads => Limits.Downloads > 0;

    /// <summary>Whether the caller asks the peer for scans: scansPerDay above 0 (MSFP playback section 8).</summary>
    public bool OffersScans => Limits.ScansPerDay > 0;
}

/// <summary>
/// What a pairing (or the pool a play counts against) uses now: its
/// playbacks of purpose play, of them transcoded, its downloads, the
/// downloads and scans started in the last 24 hours, and how many
/// transcodes the server's budget has free.
/// </summary>
public readonly record struct LimitUse
{
    /// <summary>Playbacks of purpose play.</summary>
    public int Streams { get; init; }

    /// <summary>Of them, transcoded.</summary>
    public int Transcodes { get; init; }

    /// <summary>Downloads now.</summary>
    public int Downloads { get; init; }

    /// <summary>Downloads started in the last 24 hours.</summary>
    public int DownloadsToday { get; init; }

    /// <summary>Scans started in the last 24 hours.</summary>
    public int ScansToday { get; init; }

    /// <summary>Transcodes the server's budget has free.</summary>
    public int ServerTranscodesFree { get; init; }

    /// <summary>
    /// The use with a live playback of <paramref name="purpose"/> counted as
    /// ended: the one a play replaces (MSFP Media playing 2.2, step 8; MSFP
    /// playback section 6). A play frees its stream, and when it holds one
    /// (<paramref name="transcode"/>) its transcode, the pairing's and the
    /// server's; a download frees a download, never the day's count; a scan
    /// frees nothing.
    /// </summary>
    public LimitUse Without(string purpose, bool transcode) => purpose switch
    {
        Playback.PurposePlay or "" => this with
        {
            Streams = Math.Max(Streams - 1, 0),
            Transcodes = transcode ? Math.Max(Transcodes - 1, 0) : Transcodes,
            ServerTranscodesFree = transcode ? ServerTranscodesFree + 1 : ServerTranscodesFree,
        },
        Playback.PurposeDownload => this with
        {
            Downloads = Math.Max(Downloads - 1, 0),
            ServerTranscodesFree = transcode ? ServerTranscodesFree + 1 : ServerTranscodesFree,
        },
        _ => this,
    };
}
