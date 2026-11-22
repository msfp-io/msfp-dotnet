using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// What the host asks of a peer's title for one viewer session: one
/// playback serves one viewer session, never two (MSFP playback section
/// 2.7).
/// </summary>
public sealed record PlayRequest
{
    /// <summary>The key, as the peer listed it.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// <see cref="Playback.PurposePlay"/> ("" too), <see cref="Playback.PurposeDownload"/>
    /// or <see cref="Playback.PurposeScan"/>: a download only of a peer whose
    /// limits allow downloads, of delivery file or a sized transcode (MSFP
    /// playback section 7); a scan only of a peer whose limits allow scans,
    /// always of delivery file (section 8), for the host's media server's
    /// reads to list a file, never to watch it.
    /// </summary>
    public string Purpose { get; init; } = "";

    /// <summary>
    /// <see cref="Playback.DeliveryFile"/>, <see cref="Playback.DeliveryRemux"/>
    /// (into <see cref="Container"/>), <see cref="Playback.DeliveryTranscode"/>
    /// (to <see cref="Transcode"/>'s targets) or "": the peer then decides
    /// from the capabilities (MSFP playback section 2.7). A delivery refused
    /// is <see cref="MsfpError.DeliveryRefused"/>: ask again with another one,
    /// or none.
    /// </summary>
    public string Delivery { get; init; } = "";

    /// <summary>The container of a remux.</summary>
    public string Container { get; init; } = "";

    /// <summary>The targets of a transcode.</summary>
    public TranscodeTargets? Transcode { get; init; }

    /// <summary>
    /// For hls, every audio track is asked as a rendition, so that switching
    /// tracks needs no new playback.
    /// </summary>
    public bool AudioRenditions { get; init; }

    /// <summary>
    /// What the viewer's device plays, as the host knows it: the engine sends
    /// only the values MSFP lists, coarsened (MSFP playback section 2.1).
    /// </summary>
    public PlayCapabilities Capabilities { get; init; } = new();

    /// <summary>Where to start.</summary>
    public long StartMs { get; init; }

    /// <summary>The viewer's cap (0: none), rounded down to a whole 1,000 from 1,000 up.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>The audio language wished.</summary>
    public string AudioLanguage { get; init; } = "";

    /// <summary>An audio track of a former <see cref="RemotePlayback"/> of the same title, or of the title's copies kept.</summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>The subtitle language wished.</summary>
    public string SubtitleLanguage { get; init; } = "";

    /// <summary>A subtitle track of a former <see cref="RemotePlayback"/> of the same title, or "off".</summary>
    public string SubtitleTrack { get; init; } = "";

    /// <summary>A version of a former <see cref="RemotePlayback"/> of the same title, or of the title's copies kept.</summary>
    public string VersionId { get; init; } = "";

    /// <summary>
    /// For a track, the track keys to play after it, at most 5 (MSFP music
    /// section 5.2): prepared under the same playback.
    /// </summary>
    public IReadOnlyList<string> Queue { get; init; } = [];

    /// <summary>
    /// A RemotePlayback of the same viewer to stop first: to change the
    /// audio, the version or the quality, the old playback stops and a new
    /// one starts; its stop is waited for up to 5 seconds, so that it doesn't
    /// count against the pairing's streams.
    /// </summary>
    public string Replaces { get; init; } = "";
}

/// <summary>
/// An audio or subtitle track of a playback on a peer, cleaned: its ID
/// (a&lt;n&gt;, s&lt;n&gt;), language tag, channels, role (commentary,
/// description, or "" for the title's own sound), flags; for subtitles, its
/// format, whether it is beside the video (External), and how it is fetched
/// apart: Text as <see cref="MediaKind.Subtitles"/> (WebVTT), Ass as
/// <see cref="MediaKind.Ass"/> too, Pictures as <see cref="MediaKind.Pictures"/>
/// (PGS); none of them, only within the file or burned in.
/// </summary>
public sealed record Track
{
    /// <summary>Its ID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>A language tag.</summary>
    [JsonPropertyName("language"), OmitEmpty]
    public string? Language { get; set; }

    /// <summary>Audio channels.</summary>
    [JsonPropertyName("channels"), OmitEmpty]
    public int Channels { get; set; }

    /// <summary>"commentary" or "description" for audio beside the title's own sound.</summary>
    [JsonPropertyName("role"), OmitEmpty]
    public string? Role { get; set; }

    /// <summary>The track the file marks as default.</summary>
    [JsonPropertyName("default"), OmitEmpty]
    public bool Default { get; set; }

    /// <summary>Forced subtitles.</summary>
    [JsonPropertyName("forced"), OmitEmpty]
    public bool Forced { get; set; }

    /// <summary>Subtitles for the hearing impaired.</summary>
    [JsonPropertyName("hearingImpaired"), OmitEmpty]
    public bool HearingImpaired { get; set; }

    /// <summary>A subtitle track's format (subrip, webvtt, ass, pgs… or other).</summary>
    [JsonPropertyName("format"), OmitEmpty]
    public string? Format { get; set; }

    /// <summary>A subtitle track beside the video.</summary>
    [JsonPropertyName("external"), OmitEmpty]
    public bool External { get; set; }

    /// <summary>Fetched apart as WebVTT.</summary>
    [JsonPropertyName("text"), OmitEmpty]
    public bool Text { get; set; }

    /// <summary>Fetched apart as ASS too.</summary>
    [JsonPropertyName("ass"), OmitEmpty]
    public bool Ass { get; set; }

    /// <summary>Fetched apart as PGS.</summary>
    [JsonPropertyName("pictures"), OmitEmpty]
    public bool Pictures { get; set; }
}

/// <summary>
/// A playback on a peer, as the host shows it: its answer checked and
/// cleaned. It holds no URL: the host serves the media at its own route,
/// and asks the engine for each piece (<see cref="MsfpEngine.FetchAsync"/>).
/// </summary>
public sealed record RemotePlayback
{
    /// <summary>The engine's own ID for it, never the peer's.</summary>
    public string Id { get; init; } = "";

    /// <summary>The peer's ID.</summary>
    public string Peer { get; init; } = "";

    /// <summary>The title's key.</summary>
    public string Title { get; init; } = "";

    /// <summary>The request's purpose.</summary>
    public string Purpose { get; init; } = "";

    /// <summary>The request's delivery.</summary>
    public string Delivery { get; init; } = "";

    /// <summary>direct, remux or transcode.</summary>
    public string Method { get; init; } = "";

    /// <summary>hls or file.</summary>
    public string Protocol { get; init; } = "";

    /// <summary>For a file, its type.</summary>
    public string MimeType { get; init; } = "";

    /// <summary>The copy's etag (the answer's).</summary>
    public string Etag { get; init; } = "";

    /// <summary>
    /// The copy's exact size when the catalog's copy has that etag (-1 when
    /// not known): what a host presenting the copy as a file of its own gives
    /// its clients.
    /// </summary>
    public long Size { get; init; } = -1;

    /// <summary>The answer's etag isn't the catalog's: the answer wins for this playback, and the title is read again.</summary>
    public bool CopyChanged { get; init; }

    /// <summary>Where it starts.</summary>
    public long PositionMs { get; init; }

    /// <summary>Its length; 0 when unknown.</summary>
    public long DurationMs { get; init; }

    /// <summary>The bitrate cap in effect, for a play.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>The title's copies.</summary>
    public IReadOnlyList<Version> Versions { get; init; } = [];

    /// <summary>The copy played.</summary>
    public string VersionId { get; init; } = "";

    /// <summary>The audio tracks.</summary>
    public IReadOnlyList<Track> AudioTracks { get; init; } = [];

    /// <summary>The audio track played.</summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>
    /// Subtitles, video only: fetch a track's stretches, each covering
    /// <see cref="SubtitleStretchSeconds"/>, or the whole track
    /// (<see cref="MediaPath.Whole"/>).
    /// </summary>
    public IReadOnlyList<Track> SubtitleTracks { get; init; } = [];

    /// <summary>The subtitles shown.</summary>
    public string SubtitleTrack { get; init; } = "";

    /// <summary>The track burned into the picture.</summary>
    public string BurnedTrack { get; init; } = "";

    /// <summary>The forced tracks.</summary>
    public IReadOnlyList<Track> ForcedTracks { get; init; } = [];

    /// <summary>The forced track shown with the audio played.</summary>
    public string ForcedTrack { get; init; } = "";

    /// <summary>How long each subtitle part covers.</summary>
    public int SubtitleStretchSeconds { get; init; }

    /// <summary>How many fonts the copy's ASS tracks use, fetched as <see cref="MediaKind.Font"/> 0 to Fonts-1.</summary>
    public int Fonts { get; init; }

    /// <summary>How it is served.</summary>
    public PlaybackInfo? Info { get; init; }

    /// <summary>
    /// The copy's previews, with the number of sheets to fetch (MediaPath
    /// Sheet); null when the peer sent none, or out of bounds, or this server
    /// has no <see cref="MsfpConfig.SheetEncoder"/>.
    /// </summary>
    public Trickplay? Trickplay { get; init; }

    /// <summary>
    /// The queued tracks the peer prepared (music section 5.2), each fetched
    /// with <see cref="MediaPath.Entry"/> its Place + 1.
    /// </summary>
    public IReadOnlyList<RemoteEntry> Queue { get; init; } = [];
}

/// <summary>
/// A queued track of a playback on a peer: its place in the request's queue,
/// its key, its copy, how it is served, its length (0 when not known).
/// </summary>
/// <param name="Place">Its place in the request's queue.</param>
/// <param name="Title">Its key.</param>
/// <param name="VersionId">Its copy.</param>
/// <param name="Method">direct, remux or transcode.</param>
/// <param name="Protocol">hls or file.</param>
/// <param name="MimeType">For a file, its type.</param>
/// <param name="DurationMs">Its length; 0 when not known.</param>
public sealed record RemoteEntry(int Place, string Title, string VersionId, string Method, string Protocol, string MimeType, long DurationMs);

/// <summary>
/// What a viewer did with a download away from this server (MSFP playback
/// sections 3, 4 and 7): where it was, when, in which track of a queue (its
/// key; "" for the title), and whether it stopped there.
/// </summary>
/// <param name="PositionMs">Where it was.</param>
/// <param name="At">When.</param>
/// <param name="Title">The track of a queue; "" for the title.</param>
/// <param name="Stop">It stopped there.</param>
public sealed record OfflineReport(long PositionMs, DateTimeOffset At, string Title = "", bool Stop = false);

/// <summary>
/// A stream of a playback on a peer: its own (0) or a queue entry's (n+1),
/// its protocol, and for a file the size and etag its answers are checked
/// against (-1 and "": learnt from the first).
/// </summary>
internal sealed record RemoteStream(string Protocol, long Size, string Etag);

/// <summary>A download on a peer, kept for its late reports (playback section 7) after it stopped here.</summary>
internal sealed record PastDownload(string Peer, string TheirId, string Title, IReadOnlyList<string> Queue, DateTimeOffset PlayedAt);

/// <summary>A playback on a peer, live.</summary>
internal sealed class RemotePlay
{
    public RemotePlayback View { get; set; } = new();

    public string Peer { get; init; } = "";

    public string PeerName { get; init; } = "";

    public string TheirId { get; init; } = "";

    /// <summary>The peer's stream base: its ticket in it, never shown.</summary>
    public string Base { get; init; } = "";

    public bool AudioOnly { get; init; }

    /// <summary>The playback's audio track IDs.</summary>
    public HashSet<string> Audio { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SubtitleServed> Subtitles { get; } = new(StringComparer.Ordinal);

    public CancellationTokenSource Ended { get; } = new();

    // Under the remotes' lock. Told is when the peer was last sent progress
    // (null: never), Fresh says the host reported since; Track is the queued
    // track playing ("" for the title).
    public Dictionary<int, RemoteStream> Entries { get; } = [];

    public long Position { get; set; }

    public bool Paused { get; set; }

    public DateTimeOffset? Told { get; set; }

    public bool ToldPaused { get; set; }

    public bool Fresh { get; set; }

    public string Track { get; set; } = "";

    /// <summary>entry/name: names a master playlist named.</summary>
    public HashSet<string> Playlists { get; } = new(StringComparer.Ordinal);
}

// Playback, on the viewer's side (MSFP playback sections 2 to 8, video
// section 5, music section 5): this server's people play, download or scan
// a peer's title. The host decides who may; the engine coarsens what it
// says of the device, calls play, checks and cleans every member of the
// answer (its etag against the catalog's), keeps the peer's URLs and ticket
// to itself, sends progress at its pace and stop, sends a download's late
// reports, restarts a file being read on the same etag, and stops after a
// restart the playbacks left on peers.
public sealed partial class MsfpEngine
{
    // How often progress is sent while the viewer watches (playback section
    // 3): never more often, but on a pause or a resume, and every 45 seconds
    // while the host reports.
    internal static readonly TimeSpan TellEvery = TimeSpan.FromSeconds(45);

    // How long a play waits for the stop of the playback it replaces
    // (playback section 2.3); how often a stop is sent when there's no answer.
    private static readonly TimeSpan ReplaceWait = TimeSpan.FromSeconds(5);
    private const int StopTries = 3;

    private readonly Dictionary<string, RemotePlay> _remotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PastDownload> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _stopping = new(StringComparer.Ordinal);
    private int _leftoversStopped;

    // What a peer is told of a device (playback section 2.1, video section
    // 5.1): Protocol's coarsening, without video's members for a track; null
    // when it says nothing.
    internal static PlayCapabilities? Coarsen(PlayCapabilities c, bool video)
    {
        var output = c.Coarsen();
        if (!video)
        {
            output = output with { VideoCodecs = [], Hdr = [], DvProfiles = [], MaxHeight = 0 };
        }

        return output.Empty ? null : output;
    }

    // A transcode's targets as sent: the bitrate a whole 1,000, the height
    // stepped down, as the capabilities are.
    internal static TranscodeTargets? CoarseTargets(TranscodeTargets? t, bool video)
    {
        if (t is null)
        {
            return null;
        }

        var output = t;
        if (output.MaxBitrateKbps > 0)
        {
            output = output with { MaxBitrateKbps = Math.Max(Playback.CoarsenBitrate(output.MaxBitrateKbps) / 1000 * 1000, 1000) };
        }

        output = output with { MaxHeight = Protocol.Video.StepHeight(output.MaxHeight) };
        return video ? output : output with { VideoCodec = "", MaxHeight = 0, BurnSubtitle = "" };
    }

    /// <summary>
    /// Starts a playback of a peer's title for one of this server's viewer
    /// sessions, or a download, or a scan (<see cref="PlayRequest.Purpose"/>).
    /// The host has decided the viewer may; the engine refuses before calling
    /// when the peer can't be used, or its last limits say no stream (no
    /// downloads, no scans). StreamLimitReached, ServerBusy (no answer, none
    /// signed, or a 503) and BackingOff (a 429, or calls unanswered lately)
    /// mean busy now; DeliveryRefused, ask with another delivery; NotPlayable
    /// and the others, not playable (MSFP playback section 2.5).
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="request">What to play.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotPaired"/>,
    /// <see cref="MsfpError.Uncallable"/>, <see cref="MsfpError.Suspended"/>, <see cref="MsfpError.KeyLost"/>,
    /// <see cref="MsfpError.NotSupported"/>, <see cref="MsfpError.RefusedDestination"/>,
    /// <see cref="MsfpError.CertificateRefused"/>, <see cref="MsfpError.StreamLimitReached"/>,
    /// <see cref="MsfpError.ServerBusy"/>, <see cref="MsfpError.BackingOff"/>, <see cref="MsfpError.DeliveryRefused"/>,
    /// <see cref="MsfpError.NotPlayable"/>, <see cref="MsfpError.InvalidRequest"/>.</exception>
    public async Task<RemotePlayback> PlayAsync(string peer, PlayRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        if (!TitleKey.TryParse(request.Title, out var k) || !k.Playable)
        {
            throw new MsfpException(MsfpError.NotPlayable, "not a key play takes");
        }

        var purpose = request.Purpose.Length > 0 ? request.Purpose : Playback.PurposePlay;
        if (purpose == Playback.PurposeScan && request.Delivery.Length == 0)
        {
            request = request with { Delivery = Playback.DeliveryFile };
        }

        if (purpose is not (Playback.PurposePlay or Playback.PurposeDownload or Playback.PurposeScan) ||
            (request.Delivery.Length > 0 && MethodOf(request.Delivery).Length == 0) ||
            !Playback.PurposeAllows(purpose, request.Delivery, request.Transcode) ||
            (request.Transcode is not null && request.Delivery != Playback.DeliveryTranscode) ||
            (request.Container.Length > 0) != (request.Delivery == Playback.DeliveryRemux))
        {
            throw new MsfpException(MsfpError.InvalidRequest, "a purpose, delivery or transcode MSFP doesn't allow");
        }

        UsablePeer(p, Versions.PartPlayback);
        if (!On(p, Versions.PartMedia) || !On(p, k.Part))
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        var audioOnly = k.Part == Versions.PartMusic;
        if (p.TheirLimits is not null || purpose != Playback.PurposePlay)
        {
            var lim = p.TheirLimits ?? new Limits();
            var most = purpose switch
            {
                Playback.PurposeDownload => lim.Downloads,
                Playback.PurposeScan => lim.ScansPerDay,
                _ when audioOnly && lim.Audio => lim.AudioStreams,
                _ => lim.Streams,
            };
            if (most == 0)
            {
                throw new MsfpException(MsfpError.StreamLimitReached);
            }
        }

        if (request.Replaces.Length > 0)
        {
            try
            {
                await StopAsync(request.Replaces, -1, ct).ConfigureAwait(false);
            }
            catch (MsfpException e) when (e.Code != MsfpError.NotFound)
            {
                _log.LogInformation(e, "msfp: the playback replaced couldn't be stopped");
            }
            catch (MsfpException)
            {
            }
        }

        await WaitStopsAsync(peer, ct).ConfigureAwait(false);
        var caps = Coarsen(request.Capabilities ?? new PlayCapabilities(), !audioOnly);
        var targets = CoarseTargets(request.Transcode, !audioOnly);
        var start = purpose == Playback.PurposePlay ? Math.Min(Math.Max(request.StartMs, 0), Playback.MaxPositionMs) : 0;
        var kbps = Playback.CoarsenBitrate(request.MaxBitrateKbps);
        string? subtitleLanguage = null, subtitleTrack = null;
        IReadOnlyList<string>? queue = null;
        if (!audioOnly)
        {
            subtitleLanguage = Languages.ValidTag(request.SubtitleLanguage) ? request.SubtitleLanguage : null;
            subtitleTrack = Playback.TryTrackId(request.SubtitleTrack, 's', out _) || request.SubtitleTrack == "off" ? request.SubtitleTrack : null;
        }
        else if (request.Queue.Count > 0)
        {
            var asked = new JsonArray(request.Queue.Select(q => (JsonNode?)JsonValue.Create(q)).ToArray());
            if (!Music.TryCheckQueue(request.Title, asked, out _))
            {
                throw new MsfpException(MsfpError.InvalidRequest, "a queue MSFP doesn't allow");
            }

            queue = request.Queue;
        }

        var body = JsonOut.Object(o => o.String("title", request.Title)
            .String("purpose", purpose != Playback.PurposePlay ? purpose : null)
            .String("delivery", Nonempty(request.Delivery)).String("container", Nonempty(request.Container))
            .Object("transcode", targets is { } t ? t.Write : null).Bool("audioRenditions", request.AudioRenditions ? true : null)
            .Object("capabilities", caps is { } c ? c.Write : null)
            .Integer("startPositionMs", NonZero(start)).Integer("maxBitrateKbps", NonZero(kbps))
            .String("audioLanguage", Languages.ValidTag(request.AudioLanguage) ? request.AudioLanguage : null)
            .String("audioTrack", Playback.TryTrackId(request.AudioTrack, 'a', out _) ? request.AudioTrack : null)
            .String("versionId", Playback.Id(request.VersionId) ? request.VersionId : null)
            .String("subtitleLanguage", subtitleLanguage).String("subtitleTrack", subtitleTrack)
            .Strings("queue", queue));
        Reply r;
        try
        {
            r = await CallAsync(p, CallPlay, body, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.Unreachable)
        {
            // No answer, or none signed: busy now (playback section 2.5).
            throw new MsfpException(MsfpError.ServerBusy, e.Message, e);
        }

        switch (r.Status)
        {
            case 409 when r.Problem == ProblemType.NotPlayable:
                throw new MsfpException(MsfpError.NotPlayable);
            case 409 when r.Problem == ProblemType.Delivery:
                throw new MsfpException(MsfpError.DeliveryRefused);
            case 409:
                throw new MsfpException(MsfpError.StreamLimitReached);
            case 503:
                throw new MsfpException(MsfpError.ServerBusy);
            case 429:
                throw new MsfpException(MsfpError.BackingOff);
            case 404 when r.Problem == ProblemType.NotSupported:
                throw new MsfpException(MsfpError.NotSupported);
            case not 200:
                if (r.Status == 404)
                {
                    HelloNow(p.Id);
                }

                throw new MsfpException(MsfpError.NotPlayable, "refused", Problems.RefusalError(r.Status, r.Problem ?? ""));
        }

        var (rm, why) = await ReadPlayAsync(p, r.Body, request, purpose, audioOnly, ct).ConfigureAwait(false);
        if (rm is null)
        {
            _log.LogWarning("msfp: a peer's play answer broke MSFP's rules; it is stopped: {Peer} {Why}", p.Id, why);
            try
            {
                if (StrictJson.ReadEntry(r.Body).GetString("id") is { } id && Playback.PlayId(id))
                {
                    SendStop(p.Id, id, -1, "");
                }
            }
            catch (ProtocolException)
            {
            }

            throw new MsfpException(MsfpError.NotPlayable, why);
        }

        rm.View = rm.View with { Id = NewId("r", 16) };
        rm.Position = rm.View.PositionMs;
        // Kept until stopped, so that after a restart the peer is told it's
        // over (playback section 6.3).
        try
        {
            await _store.UpdateAsync(tx => tx.PutLeftoverAsync(new Leftover(p.Id, rm.TheirId)), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: a playback on a peer couldn't be noted");
        }

        lock (_remotes)
        {
            _remotes[rm.View.Id] = rm;
            if (purpose == Playback.PurposeDownload)
            {
                var now = Now();
                foreach (var (id, d) in _downloads.ToList())
                {
                    if (now - d.PlayedAt > Playback.LateReports)
                    {
                        _downloads.Remove(id);
                    }
                }

                _downloads[rm.View.Id] = new PastDownload(p.Id, rm.TheirId, request.Title, rm.View.Queue.Select(q => q.Title).ToList(), now);
            }
        }

        if (rm.View.CopyChanged)
        {
            ReadSoon(p.Id);
        }

        return rm.View;
    }

    // Checks and cleans a peer's play answer (playback section 2.3, video
    // sections 5.3 and 5.5, music section 5.2): null, and why, when it breaks
    // a "must".
    private async Task<(RemotePlay? Remote, string Why)> ReadPlayAsync(PeerRecord p, byte[] raw, PlayRequest req, string purpose, bool audioOnly,
        CancellationToken ct)
    {
        var root = PeerStreamRoot(p);
        if (Playback.ReadPlayAnswer(raw, req.Delivery, root, "") is not { } a)
        {
            return (null, "the playback's members break MSFP's rules");
        }

        JsonObject o;
        try
        {
            o = StrictJson.ReadAnswer(raw, keepNestedNulls: true);
        }
        catch (ProtocolException)
        {
            return (null, "not an object");
        }

        var streamBase = a.Stream.Base;
        var ticket = streamBase[root.Length..].TrimEnd('/');
        var rm = new RemotePlay { Peer = p.Id, PeerName = p.Name, TheirId = a.Id, Base = streamBase, AudioOnly = audioOnly };
        rm.Audio.UnionWith(a.AudioTracks.Select(t => t.Id));
        // The copy as the catalog gave it: its etag and size.
        var (catalogEtag, size) = await CatalogCopyAsync(p.Id, req.Title, a.VersionId.Length > 0 ? a.VersionId : req.VersionId, ct)
            .ConfigureAwait(false);
        var changed = catalogEtag != a.Etag;
        var view = new RemotePlayback
        {
            Peer = p.Id, Title = req.Title, Purpose = purpose, Delivery = req.Delivery, Method = a.Method, Protocol = a.Stream.Protocol,
            MimeType = a.Stream.Protocol == "file" ? Playback.RelayedMediaType(a.Stream.MimeType) : "",
            Etag = a.Etag, Size = changed ? -1 : size, CopyChanged = changed,
            PositionMs = a.PositionMs, DurationMs = a.DurationMs, MaxBitrateKbps = a.MaxBitrateKbps,
            Versions = a.Versions.Select(v => new Version(v.Id, v.Name)).ToList(),
            VersionId = a.VersionId, AudioTrack = a.AudioTrack,
            AudioTracks = a.AudioTracks.Select(t => new Track
            {
                Id = t.Id, Language = Nonempty(t.Language), Channels = t.Channels, Role = Nonempty(t.Role), Default = t.Default, Forced = t.Forced,
            }).ToList(),
            Info = ReadInfo(o["info"]),
        };
        rm.Entries[0] = new RemoteStream(view.Protocol, view.Size, view.Etag);
        if (!audioOnly)
        {
            var (v, why) = ReadVideoAnswer(o, rm, view, streamBase, root, ticket);
            if (v is null)
            {
                return (null, why);
            }

            view = v;
        }
        else if (req.Queue.Count > 0)
        {
            var entries = new List<RemoteEntry>();
            foreach (var q in Music.ReadQueue(o["queue"], req.Queue, req.Delivery, root, a.Stream.Url))
            {
                var place = req.Queue.ToList().IndexOf(q.Title);
                entries.Add(new RemoteEntry(place, q.Title, q.VersionId, q.Method, q.Stream.Protocol,
                    q.Stream.Protocol == "file" ? Playback.RelayedMediaType(q.Stream.MimeType) : "", q.DurationMs ?? 0));
                var (etag, qsize) = await CatalogCopyAsync(p.Id, q.Title, q.VersionId, ct).ConfigureAwait(false);
                rm.Entries[place + 1] = new RemoteStream(q.Stream.Protocol, etag.Length == 0 ? -1 : qsize, etag);
            }

            view = view with { Queue = entries };
        }

        rm.View = view;
        return (rm, "");
    }

    // An answer's info, made plain text, short; null when it isn't an object
    // of its members.
    private static PlaybackInfo? ReadInfo(JsonNode? raw)
    {
        if (raw is not JsonObject i)
        {
            return null;
        }

        static int Int(JsonObject o, string name) =>
            o.GetInteger(name) is { } v ? (v is >= int.MinValue and <= int.MaxValue ? (int)v : throw BadJson()) : 0;
        static StreamInfo Stream(JsonObject s) => new()
        {
            Codec = s.GetString("codec"), Profile = s.GetString("profile"), Width = Int(s, "width"), Height = Int(s, "height"),
            BitDepth = Int(s, "bitDepth"), Range = s.GetString("range"), BitrateKbps = Int(s, "bitrateKbps"),
            Channels = Int(s, "channels"), Language = s.GetString("language"),
        };
        try
        {
            return CleanInfo(new PlaybackInfo
            {
                Reasons = i.GetStrings("reasons")?.ToList(), Container = i.GetString("container"), BitrateKbps = Int(i, "bitrateKbps"),
                Video = i.GetObject("video") is { } v ? Stream(v) : null, Audio = i.GetObject("audio") is { } au ? Stream(au) : null,
            });
        }
        catch (Exception e) when (e is ProtocolException or InvalidOperationException)
        {
            return null;
        }
    }

    // The etag and size of a peer's copy as its catalog gave them; "" when
    // the title or the copy isn't kept.
    private async Task<(string Etag, long Size)> CatalogCopyAsync(string peer, string key, string id, CancellationToken ct)
    {
        Title? t;
        try
        {
            t = await KeptTitleAsync(peer, key, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ("", -1);
        }

        switch (t)
        {
            case VideoTitle v when (v.Copies ?? []).FirstOrDefault(c => c.Id == id) is { } c:
                return (c.ETag, c.Size);
            case MusicTitle m when (m.Copies ?? []).FirstOrDefault(c => c.Id == id) is { } c:
                return (c.Etag, c.Size);
        }

        return ("", -1);
    }

    // Reads video's members of a play answer (video sections 5.3, 5.5 and
    // 6.1): its subtitle tracks, their URLs exactly those of their track
    // under the stream's base, its fonts, its previews; null and why when it
    // is refused.
    private (RemotePlayback? View, string Why) ReadVideoAnswer(JsonObject o, RemotePlay rm, RemotePlayback v, string streamBase, string root,
        string ticket)
    {
        string? bad = null;
        List<Track> Tracks(string name)
        {
            var output = new List<Track>();
            if (!o.TryGetPropertyValue(name, out var raw))
            {
                return output;
            }

            if (raw is not JsonArray list || list.Any(e => e is not JsonObject))
            {
                bad = name + " isn't an array of objects";
                return output;
            }

            foreach (var m in list.Cast<JsonObject>())
            {
                var id = JsonMembers.AsString(m["id"]) ?? "";
                if (!Playback.Id(id) || output.Count == Copies.MaxSubtitleTracks)
                {
                    continue;
                }

                static bool Flag(JsonNode? n) => n is JsonValue jv && jv.GetValueKind() == JsonValueKind.True;
                var t = new Track
                {
                    Id = id, Default = Flag(m["default"]), Forced = Flag(m["forced"]), HearingImpaired = Flag(m["hearingImpaired"]),
                    External = Flag(m["external"]),
                };
                if (JsonMembers.AsString(m["language"]) is { } l && Languages.ValidTag(l))
                {
                    t.Language = l;
                }

                if (JsonMembers.AsString(m["format"]) is { } f && (TextFormats.Contains(f) || PictureFormats.Contains(f) || f == "other"))
                {
                    t.Format = f;
                }

                var pictures = PictureFormats.Contains(t.Format ?? "");
                if (m.TryGetPropertyValue("url", out var u))
                {
                    if (JsonMembers.AsString(u) != Playback.SubtitleUrl(streamBase, id, pictures))
                    {
                        bad = "a subtitle URL that isn't its track's";
                        return output;
                    }

                    t.Text = !pictures;
                    t.Pictures = pictures;
                }

                if (m.TryGetPropertyValue("assUrl", out var au))
                {
                    if (JsonMembers.AsString(au) != streamBase + "ass/" + id || !AssFormats.Contains(t.Format ?? ""))
                    {
                        bad = "an ASS URL that isn't its track's";
                        return output;
                    }

                    t.Ass = true;
                }

                rm.Subtitles[id] = new SubtitleServed(t.Text, t.Ass, t.Pictures);
                output.Add(t);
            }

            return output;
        }

        var subtitles = Tracks("subtitleTracks");
        var forced = Tracks("forcedTracks");
        if (bad is not null)
        {
            return (null, bad);
        }

        string Id(string name) => JsonMembers.AsString(o[name]) is { } s && Playback.Id(s) ? s : "";
        var fonts = 0;
        if (o["fonts"] is JsonArray fl && fl.Count <= 8 &&
            fl.Select((f, n) => JsonMembers.AsString(f) == streamBase + "fonts/" + n.ToString(CultureInfo.InvariantCulture)).All(x => x))
        {
            fonts = fl.Count;
        }

        v = v with
        {
            SubtitleTracks = subtitles, ForcedTracks = forced, SubtitleTrack = Id("subtitleTrack"), ForcedTrack = Id("forcedTrack"),
            BurnedTrack = Id("burnedTrack"), SubtitleStretchSeconds = subtitles.Count + forced.Count > 0 ? WebVtt.StretchSeconds : 0,
            Fonts = fonts,
        };
        if (o.TryGetPropertyValue("trickplay", out var tp))
        {
            Previews? pv;
            try
            {
                pv = Video.ReadTrickplay(tp, root);
            }
            catch (ProtocolException)
            {
                return (null, "previews outside what MSFP allows");
            }

            if (pv is not null && pv.Ticket != ticket)
            {
                return (null, "previews outside what MSFP allows");
            }

            if (pv is not null && _cfg.SheetEncoder is not null)
            {
                var t = pv.Trickplay;
                v = v with
                {
                    Trickplay = new Trickplay
                    {
                        TileWidth = t.TileWidth, TileHeight = t.TileHeight, Columns = t.Columns, Rows = t.Rows,
                        IntervalMs = t.IntervalMs, TileCount = t.TileCount, Sheets = t.Sheets,
                    },
                };
            }
        }

        return (v, "");
    }

    /// <summary>
    /// Starts a new playback of the copy a playback of delivery file was
    /// reading, once it ended (a long pause: MSFP playback section 2.7), so
    /// that the host goes on reading at the same byte: the same title, copy,
    /// purpose and delivery, only if the new answer's etag is the one it was
    /// reading; else the new playback is stopped and
    /// <see cref="MsfpError.CopyChanged"/> says the title must be read again.
    /// The old one is stopped first if it is still live. Make one only for a
    /// read that arrives.
    /// </summary>
    /// <param name="old">The playback that was read.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException">PlayAsync's, <see cref="MsfpError.CopyChanged"/>,
    /// <see cref="MsfpError.InvalidRequest"/> (not a playback of a file).</exception>
    public async Task<RemotePlayback> RestartAsync(RemotePlayback old, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(old);
        if (old.Delivery != Playback.DeliveryFile || old.Etag.Length == 0 || old.VersionId.Length == 0)
        {
            throw new MsfpException(MsfpError.InvalidRequest, "only a playback of delivery file restarts");
        }

        if (LiveRemote(old.Id) is not null)
        {
            try
            {
                await StopAsync(old.Id, -1, ct).ConfigureAwait(false);
            }
            catch (MsfpException e) when (e.Code == MsfpError.NotFound)
            {
            }
        }

        var pb = await PlayAsync(old.Peer, new PlayRequest
        {
            Title = old.Title, Purpose = old.Purpose, Delivery = Playback.DeliveryFile, VersionId = old.VersionId, AudioTrack = old.AudioTrack,
        }, ct).ConfigureAwait(false);
        if (pb.Etag != old.Etag || pb.VersionId != old.VersionId)
        {
            try
            {
                await StopAsync(pb.Id, -1, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                _log.LogInformation(e, "msfp: a restart of another copy couldn't be stopped");
            }

            ReadSoon(old.Peer);
            throw new MsfpException(MsfpError.CopyChanged);
        }

        if (pb.Size < 0)
        {
            pb = pb with { Size = old.Size };
            lock (_remotes)
            {
                if (_remotes.TryGetValue(pb.Id, out var rm))
                {
                    rm.Entries[0] = rm.Entries[0] with { Size = old.Size };
                    rm.View = rm.View with { Size = old.Size };
                }
            }
        }

        return pb;
    }

    // A playback on a peer by the engine's ID; null for none.
    private RemotePlay? LiveRemote(string id)
    {
        lock (_remotes)
        {
            return _remotes.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Notes where a viewer is in a playback on a peer (MSFP playback section
    /// 3): the peer is told at the first report, at once on a pause or a
    /// resume, and otherwise every 45 seconds at most while the host reports,
    /// by RunAsync's work of each second when no report comes at the time. It
    /// returns at once. A 410 from the peer ends the playback
    /// (<see cref="RemotePlaybackEnded"/>). A scan sends none.
    /// <para>
    /// The peer ends a playback it hears nothing of for 2 minutes (playback
    /// section 6.3): while a viewer is on it, paused too, call Progress at
    /// least every minute, whatever the player reports. Calls cost nothing
    /// more: the peer still gets one report per 45 seconds at most.
    /// </para>
    /// </summary>
    /// <param name="id">The engine's ID of the playback.</param>
    /// <param name="positionMs">Where the viewer is.</param>
    /// <param name="paused">The viewer paused.</param>
    public void Progress(string id, long positionMs, bool paused)
    {
        RemotePlay? rm;
        byte[]? body = null;
        lock (_remotes)
        {
            if (!_remotes.TryGetValue(id, out rm) || rm.View.Purpose == Playback.PurposeScan)
            {
                return;
            }

            var now = Now();
            rm.Position = Math.Min(Math.Max(positionMs, 0), Playback.MaxPositionMs);
            rm.Paused = paused;
            rm.Fresh = true;
            if (rm.Told is not { } told || paused != rm.ToldPaused || now - told >= TellEvery)
            {
                body = Tell(rm, now);
            }
        }

        if (body is not null)
        {
            SendProgress(rm, body);
        }
    }

    /// <summary>
    /// Says which track of a playback's queue plays now (its key; the
    /// playback's own title, or one of its Queue): progress and stop carry
    /// it, positions then in that track (MSFP music section 5.2).
    /// </summary>
    /// <param name="id">The engine's ID of the playback.</param>
    /// <param name="title">The track's key.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.InvalidRequest"/> (a key of neither).</exception>
    public void PlayingTrack(string id, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        lock (_remotes)
        {
            if (!_remotes.TryGetValue(id, out var rm))
            {
                throw new MsfpException(MsfpError.NotFound);
            }

            if (title != rm.View.Title && !rm.View.Queue.Any(q => q.Title == title))
            {
                throw new MsfpException(MsfpError.InvalidRequest);
            }

            rm.Track = title == rm.View.Title ? "" : title;
        }
    }

    // Notes that the peer is told now where the viewer is, and gives what it
    // is sent. Under the remotes' lock.
    private static byte[] Tell(RemotePlay rm, DateTimeOffset now)
    {
        rm.Told = now;
        rm.ToldPaused = rm.Paused;
        rm.Fresh = false;
        return JsonOut.Object(o => o.String("id", rm.TheirId).Integer("positionMs", rm.Position).Bool("paused", rm.Paused ? true : null)
            .String("title", Nonempty(rm.Track)));
    }

    // Sends progress for the playbacks on peers the host reported since they
    // were last sent it, 45 seconds ago or more.
    private void TellWatched()
    {
        var now = Now();
        var send = new List<(RemotePlay Remote, byte[] Body)>();
        lock (_remotes)
        {
            foreach (var rm in _remotes.Values)
            {
                if (rm.Fresh && (rm.Told is not { } told || now - told >= TellEvery))
                {
                    send.Add((rm, Tell(rm, now)));
                }
            }
        }

        foreach (var (rm, body) in send)
        {
            SendProgress(rm, body);
        }
    }

    // Sends progress in the background; a 410 ends the playback.
    private void SendProgress(RemotePlay rm, byte[] body) => Background(async ct =>
    {
        PeerRecord p;
        try
        {
            p = await PeerRecordAsync(rm.Peer, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            EndRemote(rm, EndReason.PairingEnded, true);
            return;
        }

        try
        {
            var r = await CallAsync(p, CallProgress, body, ct, background: true).ConfigureAwait(false);
            if (r.Status == 410)
            {
                EndRemote(rm, EndReason.Expired, true);
                await ForgetLeftoverAsync(rm, ct).ConfigureAwait(false);
            }
        }
        catch (MsfpException e) when (e.Code != MsfpError.BackingOff)
        {
            _log.LogInformation("msfp: a peer couldn't be told a playback is watched: {Peer} {Error}", rm.Peer, e.Message);
        }
        catch (MsfpException)
        {
        }
    });

    /// <summary>
    /// Sends a download's late report, as progress or stop with at, when the
    /// viewer's device syncs: for 30 days after its play, even once it
    /// stopped here (MSFP playback section 7). Send them in the order of their
    /// At.
    /// </summary>
    /// <param name="id">The engine's ID of the download.</param>
    /// <param name="report">What the viewer did.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/> (no download of that ID within 30
    /// days), <see cref="MsfpError.InvalidRequest"/> (an At out of its bounds, a Title not of the download),
    /// <see cref="MsfpError.PlaybackStopped"/> (the peer's 410: too late), and the call's.</exception>
    public async Task ReportOfflineAsync(string id, OfflineReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        PastDownload? d;
        lock (_remotes)
        {
            _downloads.TryGetValue(id, out d);
        }

        var now = Now();
        if (d is null || now - d.PlayedAt > Playback.LateReports)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var title = report.Title ?? "";
        if (report.At < now - Playback.AtBefore || report.At > now + Playback.AtAfter ||
            (title.Length > 0 && title != d.Title && !d.Queue.Contains(title)))
        {
            throw new MsfpException(MsfpError.InvalidRequest);
        }

        var p = await PeerRecordAsync(d.Peer, ct).ConfigureAwait(false);
        UsablePeer(p, Versions.PartPlayback);
        var body = JsonOut.Object(o => o.String("id", d.TheirId)
            .Integer("positionMs", Math.Min(Math.Max(report.PositionMs, 0), Playback.MaxPositionMs))
            .Integer("at", report.At.ToUnixTimeSeconds()).String("title", title != d.Title ? Nonempty(title) : null));
        var r = await CallAsync(p, report.Stop ? CallStop : CallProgress, body, ct).ConfigureAwait(false);
        if (r.Status != 200)
        {
            throw Problems.RefusalError(r.Status, r.Problem ?? "");
        }
    }

    /// <summary>
    /// Ends a playback on a peer: the peer is sent stop (again when it doesn't
    /// answer, a few times), and the playback is forgotten.
    /// </summary>
    /// <param name="id">The engine's ID of the playback.</param>
    /// <param name="positionMs">Where the viewer stopped; -1 when not known.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task StopAsync(string id, long positionMs, CancellationToken ct = default)
    {
        var rm = LiveRemote(id) ?? throw new MsfpException(MsfpError.NotFound);
        string track;
        lock (_remotes)
        {
            if (positionMs < 0)
            {
                positionMs = rm.Position;
            }

            track = rm.Track;
        }

        EndRemote(rm, EndReason.Stopped, false);
        var done = Stopping(rm.Peer);
        try
        {
            await StopOnPeerAsync(rm.Peer, rm.TheirId, positionMs, track, ct).ConfigureAwait(false);
            await ForgetLeftoverAsync(rm, ct).ConfigureAwait(false);
        }
        finally
        {
            done();
        }
    }

    // Sends stop in the background.
    private void SendStop(string peer, string theirId, long positionMs, string track)
    {
        var done = Stopping(peer);
        Background(async ct =>
        {
            try
            {
                await StopOnPeerAsync(peer, theirId, positionMs, track, ct).ConfigureAwait(false);
            }
            finally
            {
                done();
            }
        });
    }

    // Sends stop, tried again a few times without an answer; not at all to a
    // peer that can't be used now.
    private async Task StopOnPeerAsync(string peer, string theirId, long positionMs, string track, CancellationToken ct)
    {
        PeerRecord p;
        try
        {
            p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return;
        }

        if (!Usable(p, Versions.PartPlayback))
        {
            return;
        }

        var body = JsonOut.Object(o => o.String("id", theirId).Integer("positionMs", positionMs > 0 ? positionMs : null)
            .String("title", Nonempty(track)));
        for (var attempt = 0; attempt < StopTries; attempt++)
        {
            try
            {
                var r = await CallAsync(p, CallStop, body, ct).ConfigureAwait(false);
                if (r.Status != 200)
                {
                    _log.LogInformation("msfp: a peer refused a stop: {Peer} {Status}", peer, r.Status);
                }

                return;
            }
            catch (MsfpException e) when (e.Code == MsfpError.Unreachable && !ct.IsCancellationRequested)
            {
                if (attempt < StopTries - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
                }
            }
            catch (MsfpException)
            {
                return;
            }
        }
    }

    // Notes a stop on its way to peer, until done.
    private Action Stopping(string peer)
    {
        lock (_remotes)
        {
            _stopping[peer] = _stopping.GetValueOrDefault(peer) + 1;
        }

        var once = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref once, 1) != 0)
            {
                return;
            }

            lock (_remotes)
            {
                if (--_stopping[peer] <= 0)
                {
                    _stopping.Remove(peer);
                }
            }
        };
    }

    // Waits up to 5 seconds for the stops on their way to peer (real time).
    private async Task WaitStopsAsync(string peer, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReplaceWait;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            lock (_remotes)
            {
                if (_stopping.GetValueOrDefault(peer) == 0)
                {
                    return;
                }
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    // Ends a playback on a peer here: its fetches are cut, and, when the peer
    // or the pairing ended it (tell), the host is told.
    private void EndRemote(RemotePlay rm, EndReason why, bool tell)
    {
        lock (_remotes)
        {
            if (!_remotes.TryGetValue(rm.View.Id, out var live) || live != rm)
            {
                return;
            }

            _remotes.Remove(rm.View.Id);
        }

        rm.Ended.Cancel();
        if (tell)
        {
            Notify(new RemotePlaybackEnded(rm.Peer, rm.PeerName, rm.View.Id, why));
        }
    }

    // Ends this server's playbacks on a peer, as the pairing ended, or it was
    // suspended, or can't be called; its downloads' late reports go nowhere
    // then.
    private void EndRemotes(string peer, EndReason why)
    {
        List<RemotePlay> end;
        lock (_remotes)
        {
            end = _remotes.Values.Where(rm => rm.Peer == peer).ToList();
            if (why == EndReason.PairingEnded)
            {
                foreach (var (id, d) in _downloads.ToList())
                {
                    if (d.Peer == peer)
                    {
                        _downloads.Remove(id);
                    }
                }
            }
        }

        foreach (var rm in end)
        {
            EndRemote(rm, why, true);
        }
    }

    private async Task ForgetLeftoverAsync(RemotePlay rm, CancellationToken ct)
    {
        try
        {
            await _store.UpdateAsync(tx => tx.DeleteLeftoverAsync(new Leftover(rm.Peer, rm.TheirId)), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: a playback on a peer couldn't be forgotten");
        }
    }

    // Tells peers the playbacks this server had on them before it started
    // are over (playback section 6.3): once, at the first run.
    private async Task StopLeftoversAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _leftoversStopped, 1) != 0)
        {
            return;
        }

        IReadOnlyList<Leftover> left = [];
        try
        {
            await _store.ReadAsync(async tx => left = await tx.LeftoversAsync().ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the playbacks left on peers couldn't be read");
            return;
        }

        HashSet<Leftover> live;
        lock (_remotes)
        {
            live = _remotes.Values.Select(rm => new Leftover(rm.Peer, rm.TheirId)).ToHashSet();
        }

        foreach (var l in left)
        {
            if (live.Contains(l))
            {
                continue; // started since this run began: not left over
            }

            await StopOnPeerAsync(l.Peer, l.PlaybackId, -1, "", ct).ConfigureAwait(false);
            try
            {
                await _store.UpdateAsync(tx => tx.DeleteLeftoverAsync(l), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "msfp: a playback left on a peer couldn't be forgotten");
            }
        }
    }
}
