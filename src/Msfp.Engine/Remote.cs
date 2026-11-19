using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>What the host asks of a peer's title for one viewer.</summary>
public sealed record PlayRequest
{
    /// <summary>The key, as the peer listed it.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// What the viewer's device plays, as the host knows it: the engine sends
    /// only the values MSFP lists, coarsened (MSFP playback section 2.1).
    /// </summary>
    public Capabilities Capabilities { get; init; } = new();

    /// <summary>Where to start.</summary>
    public long StartMs { get; init; }

    /// <summary>The viewer's cap (0: none), rounded down to a whole 1,000 from 1,000 up.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>The audio language wished.</summary>
    public string AudioLanguage { get; init; } = "";

    /// <summary>An audio track of a former <see cref="RemotePlayback"/> of the same title.</summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>The subtitle language wished.</summary>
    public string SubtitleLanguage { get; init; } = "";

    /// <summary>A subtitle track of a former <see cref="RemotePlayback"/> of the same title, or "off".</summary>
    public string SubtitleTrack { get; init; } = "";

    /// <summary>A version of a former <see cref="RemotePlayback"/> of the same title.</summary>
    public string VersionId { get; init; } = "";

    /// <summary>
    /// A RemotePlayback of the same viewer to stop first: to change the
    /// audio, the version or the quality, the old playback stops and a new
    /// one starts; its stop is waited for up to 5 seconds, so that it doesn't
    /// count against the pairing's streams.
    /// </summary>
    public string Replaces { get; init; } = "";
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

    /// <summary>direct, remux or transcode.</summary>
    public string Method { get; init; } = "";

    /// <summary>hls or file.</summary>
    public string Protocol { get; init; } = "";

    /// <summary>For a file, its type.</summary>
    public string MimeType { get; init; } = "";

    /// <summary>Where it starts.</summary>
    public long PositionMs { get; init; }

    /// <summary>Its length; 0 when unknown.</summary>
    public long DurationMs { get; init; }

    /// <summary>The bitrate cap in effect.</summary>
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
    /// Subtitles, video only. Pictures marks PGS tracks; fetch a track's parts
    /// by MediaPath Subtitles or Pictures, each covering
    /// <see cref="SubtitleStretchSeconds"/>.
    /// </summary>
    public IReadOnlyList<Track> SubtitleTracks { get; init; } = [];

    /// <summary>The subtitles shown.</summary>
    public string SubtitleTrack { get; init; } = "";

    /// <summary>The forced tracks.</summary>
    public IReadOnlyList<Track> ForcedTracks { get; init; } = [];

    /// <summary>The forced track shown with the audio played.</summary>
    public string ForcedTrack { get; init; } = "";

    /// <summary>How long each subtitle part covers.</summary>
    public int SubtitleStretchSeconds { get; init; }

    /// <summary>Skip markers.</summary>
    public IReadOnlyList<Segment> Segments { get; init; } = [];

    /// <summary>How it is served.</summary>
    public PlaybackInfo? Info { get; init; }

    /// <summary>
    /// The copy's previews, with the number of sheets to fetch (MediaPath
    /// Sheet); null when the peer sent none, or out of bounds, or this server
    /// has no <see cref="MsfpConfig.SheetEncoder"/>.
    /// </summary>
    public Trickplay? Trickplay { get; init; }
}

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

    /// <summary>Track ID with a URL: pictures.</summary>
    public Dictionary<string, bool> Subtitles { get; } = new(StringComparer.Ordinal);

    public CancellationTokenSource Ended { get; } = new();

    // Under the remotes' lock. Told is when the peer was last sent progress
    // (null: never), Fresh says the host reported since.
    public long Position { get; set; }

    public bool Paused { get; set; }

    public DateTimeOffset? Told { get; set; }

    public bool ToldPaused { get; set; }

    public bool Fresh { get; set; }

    /// <summary>Names a master playlist named.</summary>
    public HashSet<string> Playlists { get; } = new(StringComparer.Ordinal);
}

// Playback, on the viewer's side (MSFP playback sections 2 to 4, video
// section 5, music section 5): this server's people play a peer's title.
// The host decides who may; the engine coarsens what it says of the
// device, calls play, checks and cleans every member of the answer, keeps
// the peer's URLs and ticket to itself, sends progress at its pace and
// stop, and stops after a restart the playbacks left on peers.
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
    private readonly Dictionary<string, int> _stopping = new(StringComparer.Ordinal);
    private int _leftoversStopped;

    // What a peer is told of a device (playback section 2.1, video section
    // 5.1): only the values MSFP lists, lowercase, each once, sorted;
    // channels stepped down to 2, 6 or 8, heights to 480, 720, 1080, 1440,
    // 2160 or 4320, the bitrate to a whole 1,000 (below, none). Null when it
    // says nothing.
    internal static Action<JsonOut>? Coarsen(Capabilities c, bool video)
    {
        static List<string>? List(string name, IEnumerable<string>? values)
        {
            var output = new List<string>();
            foreach (var v in values ?? [])
            {
                var s = v.ToLowerInvariant();
                if (CapabilityValues[name].Contains(s) && !output.Contains(s))
                {
                    output.Add(s);
                }
            }

            output.Sort(StringComparer.Ordinal);
            return output.Count > 0 ? output : null;
        }

        static int Step(int v, int[] steps)
        {
            if (v <= 0)
            {
                return 0;
            }

            var output = steps[0];
            foreach (var s in steps)
            {
                if (v >= s)
                {
                    output = s;
                }
            }

            return output;
        }

        var containers = List("containers", c.Containers);
        var audioCodecs = List("audioCodecs", c.AudioCodecs);
        var channels = Step(c.MaxAudioChannels, [2, 6, 8]);
        var kbps = c.MaxBitrateKbps / 1000 * 1000;
        if (kbps < 0 || c.MaxBitrateKbps > MaxKbps)
        {
            kbps = 0;
        }

        List<string>? videoCodecs = null, hdr = null;
        var height = 0;
        if (video)
        {
            videoCodecs = List("videoCodecs", c.VideoCodecs);
            hdr = List("hdr", c.Hdr);
            height = Protocol.Video.StepHeight(c.MaxHeight);
        }

        if (containers is null && audioCodecs is null && videoCodecs is null && hdr is null && channels == 0 && kbps == 0 && height == 0)
        {
            return null;
        }

        return w => w.Strings("containers", containers).Strings("audioCodecs", audioCodecs).Integer("maxAudioChannels", NonZero(channels))
            .Integer("maxBitrateKbps", NonZero(kbps)).Strings("videoCodecs", videoCodecs).Strings("hdr", hdr).Integer("maxHeight", NonZero(height));
    }

    // The request's bitrate cap as sent: a whole 1,000 from 1,000 up, as it
    // is below (playback section 2.1).
    internal static int RoundKbps(int kbps) => kbps switch
    {
        <= 0 or > (int)MaxKbps => 0,
        < 1000 => kbps,
        _ => kbps / 1000 * 1000,
    };

    /// <summary>
    /// Starts a playback of a peer's title for one of this server's viewers.
    /// The host has decided the viewer may; the engine refuses before calling
    /// when the peer can't be used, or its last limits say no stream.
    /// StreamLimitReached, ServerBusy (no answer, none signed, or a 503) and
    /// BackingOff (a 429, or calls unanswered lately) mean busy now;
    /// NotPlayable and the others, not playable (MSFP playback section 2.5).
    /// </summary>
    /// <param name="peer">The peer's ID.</param>
    /// <param name="request">What to play.</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.NotPaired"/>,
    /// <see cref="MsfpError.Uncallable"/>, <see cref="MsfpError.Suspended"/>, <see cref="MsfpError.KeyLost"/>,
    /// <see cref="MsfpError.NotSupported"/>, <see cref="MsfpError.RefusedDestination"/>,
    /// <see cref="MsfpError.CertificateRefused"/>, <see cref="MsfpError.StreamLimitReached"/>,
    /// <see cref="MsfpError.ServerBusy"/>, <see cref="MsfpError.BackingOff"/>, <see cref="MsfpError.NotPlayable"/>.</exception>
    public async Task<RemotePlayback> PlayAsync(string peer, PlayRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = await PeerRecordAsync(peer, ct).ConfigureAwait(false);
        if (!TitleKey.TryParse(request.Title, out var k) || !k.Playable)
        {
            throw new MsfpException(MsfpError.NotPlayable, "not a key play takes");
        }

        UsablePeer(p, Versions.PartPlayback);
        if (!On(p, Versions.PartMedia) || !On(p, k.Part))
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        var audioOnly = k.Part == Versions.PartMusic;
        if (p.TheirLimits is { } l && (audioOnly && l.Audio ? l.AudioStreams : l.Streams) == 0)
        {
            throw new MsfpException(MsfpError.StreamLimitReached);
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
        var caps = Coarsen(request.Capabilities ?? new Capabilities(), !audioOnly);
        var subtitleLanguage = !audioOnly && Protocol.Video.LanguageTag(request.SubtitleLanguage) ? request.SubtitleLanguage : null;
        var subtitleTrack = !audioOnly && (Playback.TryTrackId(request.SubtitleTrack, 's', out _) || request.SubtitleTrack == "off")
            ? request.SubtitleTrack : null;
        var start = Math.Min(Math.Max(request.StartMs, 0), MaxPositionMs);
        var kbps = RoundKbps(request.MaxBitrateKbps);
        var body = JsonOut.Object(o => o.String("title", request.Title).Object("capabilities", caps)
            .Integer("startPositionMs", NonZero(start)).Integer("maxBitrateKbps", NonZero(kbps))
            .String("audioLanguage", Protocol.Video.LanguageTag(request.AudioLanguage) ? request.AudioLanguage : null)
            .String("audioTrack", Playback.TryTrackId(request.AudioTrack, 'a', out _) ? request.AudioTrack : null)
            .String("versionId", Playback.Id(request.VersionId) ? request.VersionId : null)
            .String("subtitleLanguage", subtitleLanguage).String("subtitleTrack", subtitleTrack));
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

        var (rm, why) = ReadPlay(p, r.Body, request.Title, audioOnly);
        if (rm is null)
        {
            _log.LogWarning("msfp: a peer's play answer broke MSFP's rules; it is stopped: {Peer} {Why}", p.Id, why);
            try
            {
                if (StrictJson.ReadEntry(r.Body).GetString("id") is { } id && Playback.PlayId(id))
                {
                    SendStop(p.Id, id, -1);
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
        }

        return rm.View;
    }

    // Checks and cleans a peer's play answer (playback section 2.3, video
    // sections 5.3 and 5.5, music section 5): null, and why, when it breaks
    // a "must".
    private (RemotePlay? Remote, string Why) ReadPlay(PeerRecord p, byte[] raw, string title, bool audioOnly)
    {
        JsonObject a;
        try
        {
            a = StrictJson.ReadAnswer(raw);
        }
        catch (ProtocolException)
        {
            return (null, "not an object");
        }

        // title and next are dropped, whatever they hold.
        a.Remove("title");
        a.Remove("next");
        if (audioOnly)
        {
            // A track's answer has no subtitles, no skip markers, no previews.
            foreach (var m in new[] { "subtitleTracks", "subtitleTrack", "forcedTracks", "forcedTrack", "subtitleStretchSeconds", "segments", "trickplay" })
            {
                a.Remove(m);
            }
        }

        try
        {
            return ReadPlayMembers(p, a, title, audioOnly);
        }
        catch (ProtocolException)
        {
            return (null, "a member of the wrong type");
        }
    }

    private (RemotePlay? Remote, string Why) ReadPlayMembers(PeerRecord p, JsonObject a, string title, bool audioOnly)
    {
        var id = a.GetString("id") ?? "";
        var method = a.GetString("method") ?? "";
        var stream = a.GetObject("stream");
        string url = "", protocol = "", mimeType = "";
        if (stream is not null)
        {
            url = stream.GetString("url") ?? "";
            protocol = stream.GetString("protocol") ?? "";
            mimeType = stream.GetString("mimeType") ?? "";
        }

        var position = a.GetInteger("positionMs") ?? 0;
        var duration = a.GetInteger("durationMs") ?? 0;
        var versions = ReadList(a, "versions", v => new Version(v.GetString("id") ?? "", v.GetString("name") ?? ""));
        var versionId = a.GetString("versionId") ?? "";
        var audioTracks = ReadList(a, "audioTracks", ReadTrack);
        var audioTrack = a.GetString("audioTrack") ?? "";
        var kbps = a.GetInteger("maxBitrateKbps") ?? 0;
        var info = a.GetObject("info") is { } io ? ReadInfo(io) : null;
        var subtitleTracks = ReadList(a, "subtitleTracks", ReadTrack);
        var subtitleTrack = a.GetString("subtitleTrack") ?? "";
        var forcedTracks = ReadList(a, "forcedTracks", ReadTrack);
        var forcedTrack = a.GetString("forcedTrack") ?? "";
        a.GetInteger("subtitleStretchSeconds");
        var segments = ReadList(a, "segments", s => new Segment(s.GetString("kind") ?? "", s.GetInteger("startMs") ?? 0, s.GetInteger("endMs") ?? 0));
        var trickplay = a.GetObject("trickplay");
        if (!Playback.PlayId(id))
        {
            return (null, "an id of another form");
        }

        if (method is not ("direct" or "remux" or "transcode"))
        {
            return (null, "a method MSFP doesn't have");
        }

        if (stream is null)
        {
            return (null, "no stream");
        }

        var prefix = Discovery.Prefix(string.IsNullOrEmpty(p.Base) ? Discovery.DefaultBase : p.Base);
        var root = p.Address + prefix + StreamRoute;
        if (!Playback.TryStreamBase(url, root, protocol, out var streamBase))
        {
            return (null, "a stream URL not under its stream root");
        }

        var rm = new RemotePlay { Peer = p.Id, PeerName = p.Name, TheirId = id, Base = streamBase, AudioOnly = audioOnly };
        static string KeepId(string s) => Playback.Id(s) || s == "off" ? s : "";
        var view = new RemotePlayback
        {
            Peer = p.Id, Title = title, Method = method, Protocol = protocol,
            MimeType = protocol == "file" ? Playback.RelayedMediaType(mimeType) : "",
            PositionMs = Math.Min(Math.Max(position, 0), MaxPositionMs),
            DurationMs = duration > 0 && duration <= MaxPositionMs ? duration : 0,
            MaxBitrateKbps = kbps > 0 && kbps <= MaxKbps ? (int)kbps : 0,
            Versions = versions.Select(v => v with { Name = Music.CleanText(v.Name, 64) }).Where(v => Playback.Id(v.Id) && v.Name.Length > 0).ToList(),
            VersionId = KeepId(versionId),
            AudioTracks = CleanTracks(audioTracks.Select(t => t.Track), 'a'),
            AudioTrack = KeepId(audioTrack),
            Info = CleanInfo(info),
        };
        if (!audioOnly)
        {
            foreach (var (t, u) in subtitleTracks.Concat(forcedTracks))
            {
                if (u.Length > 0 && u != Playback.SubtitleUrl(streamBase, t.Id, t.Pictures))
                {
                    return (null, "a subtitle URL that isn't its track's");
                }

                if (u.Length > 0)
                {
                    rm.Subtitles[t.Id] = t.Pictures;
                }
            }

            var subs = CleanTracks(subtitleTracks.Select(t => t.Track), 's');
            var forced = CleanTracks(forcedTracks.Select(t => t.Track), 's');
            view = view with
            {
                SubtitleTracks = subs, ForcedTracks = forced, SubtitleTrack = KeepId(subtitleTrack), ForcedTrack = KeepId(forcedTrack),
                SubtitleStretchSeconds = subs.Count + forced.Count > 0 ? WebVtt.StretchSeconds : 0,
                Segments = CleanSegments(segments, 0),
            };
            if (trickplay is not null)
            {
                var sheets = trickplay.GetStrings("sheets") ?? [];
                for (var n = 0; n < sheets.Count; n++)
                {
                    if (sheets[n] != Playback.SheetUrl(streamBase, n))
                    {
                        return (null, "a sheet URL that isn't its own");
                    }
                }

                int I(string name) => trickplay.GetInteger(name) is { } v && v is >= int.MinValue and <= int.MaxValue ? (int)v : 0;
                var t = new Protocol.Trickplay(I("tileWidth"), I("tileHeight"), I("columns"), I("rows"), I("intervalMs"), I("tileCount"), sheets.Count);
                if (t.InBounds && _cfg.SheetEncoder is not null)
                {
                    view = view with
                    {
                        Trickplay = new Trickplay
                        {
                            TileWidth = t.TileWidth, TileHeight = t.TileHeight, Columns = t.Columns, Rows = t.Rows,
                            IntervalMs = t.IntervalMs, TileCount = t.TileCount, Sheets = t.Sheets,
                        },
                    };
                }
            }
        }

        rm.View = view;
        return (rm, "");
    }

    private static List<T> ReadList<T>(JsonObject a, string name, Func<JsonObject, T> read) =>
        (a.GetArray(name) ?? []).Select(e => read(e as JsonObject ?? throw BadJson())).ToList();

    // A track as an answer gives it, with its URL ("" for none).
    private static (Track Track, string Url) ReadTrack(JsonObject t)
    {
        int Channels() => t.GetInteger("channels") is { } c && c is >= int.MinValue and <= int.MaxValue ? (int)c : throw BadJson();
        var track = new Track
        {
            Id = t.GetString("id") ?? "", Label = t.GetString("label") ?? "", Language = t.GetString("language"),
            Channels = t.ContainsKey("channels") ? Channels() : 0, Role = t.GetString("role"), Default = t.GetBool("default") ?? false,
            Forced = t.GetBool("forced") ?? false, HearingImpaired = t.GetBool("hearingImpaired") ?? false, Pictures = t.GetBool("pictures") ?? false,
        };
        return (track, t.GetString("url") ?? "");
    }

    private static PlaybackInfo ReadInfo(JsonObject i)
    {
        static int Int(JsonObject o, string name) =>
            o.GetInteger(name) is { } v ? (v is >= int.MinValue and <= int.MaxValue ? (int)v : throw BadJson()) : 0;
        static StreamInfo Stream(JsonObject s) => new()
        {
            Codec = s.GetString("codec"), Profile = s.GetString("profile"), Width = Int(s, "width"), Height = Int(s, "height"),
            BitDepth = Int(s, "bitDepth"), Range = s.GetString("range"), BitrateKbps = Int(s, "bitrateKbps"),
            Channels = Int(s, "channels"), Language = s.GetString("language"),
        };
        return new PlaybackInfo
        {
            Reasons = i.GetStrings("reasons"), Container = i.GetString("container"), BitrateKbps = Int(i, "bitrateKbps"),
            Video = i.GetObject("video") is { } v ? Stream(v) : null, Audio = i.GetObject("audio") is { } a ? Stream(a) : null,
        };
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
    /// (<see cref="RemotePlaybackEnded"/>).
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
            if (!_remotes.TryGetValue(id, out rm))
            {
                return;
            }

            var now = Now();
            rm.Position = Math.Min(Math.Max(positionMs, 0), MaxPositionMs);
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

    // Notes that the peer is told now where the viewer is, and gives what it
    // is sent. Under the remotes' lock.
    private static byte[] Tell(RemotePlay rm, DateTimeOffset now)
    {
        rm.Told = now;
        rm.ToldPaused = rm.Paused;
        rm.Fresh = false;
        return JsonOut.Object(o => o.String("id", rm.TheirId).Integer("positionMs", rm.Position).Bool("paused", rm.Paused ? true : null));
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
        if (positionMs < 0)
        {
            lock (_remotes)
            {
                positionMs = rm.Position;
            }
        }

        EndRemote(rm, EndReason.Stopped, false);
        var done = Stopping(rm.Peer);
        try
        {
            await StopOnPeerAsync(rm.Peer, rm.TheirId, positionMs, ct).ConfigureAwait(false);
            await ForgetLeftoverAsync(rm, ct).ConfigureAwait(false);
        }
        finally
        {
            done();
        }
    }

    // Sends stop in the background.
    private void SendStop(string peer, string theirId, long positionMs)
    {
        var done = Stopping(peer);
        Background(async ct =>
        {
            try
            {
                await StopOnPeerAsync(peer, theirId, positionMs, ct).ConfigureAwait(false);
            }
            finally
            {
                done();
            }
        });
    }

    // Sends stop, tried again a few times without an answer; not at all to a
    // peer that can't be used now.
    private async Task StopOnPeerAsync(string peer, string theirId, long positionMs, CancellationToken ct)
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

        var body = JsonOut.Object(o => o.String("id", theirId).Integer("positionMs", positionMs > 0 ? positionMs : null));
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
    // suspended, or can't be called.
    private void EndRemotes(string peer, EndReason why)
    {
        List<RemotePlay> end;
        lock (_remotes)
        {
            end = _remotes.Values.Where(rm => rm.Peer == peer).ToList();
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

            await StopOnPeerAsync(l.Peer, l.PlaybackId, -1, ct).ConfigureAwait(false);
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
