using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// A peer's playback here, for the host's live view: the peer (never a
/// person), the title played, how, for what purpose, since when, and
/// whether it was used since it started ("started" or "active"). Handle is
/// the host's own (<see cref="Started.Handle"/>), for a host that ends a
/// playback from its side (<see cref="MsfpEngine.StopPlayback"/> with ID and
/// its reason).
/// </summary>
/// <param name="Id">The playback's ID.</param>
/// <param name="Peer">The peer's ID.</param>
/// <param name="PeerName">The peer's name.</param>
/// <param name="Title">The title's key.</param>
/// <param name="Method">direct, remux or transcode.</param>
/// <param name="Purpose">play, download or scan.</param>
/// <param name="Handle">The host's handle.</param>
/// <param name="Since">When it started.</param>
/// <param name="State">"started" or "active".</param>
public sealed record PlaybackView(string Id, string Peer, string PeerName, string Title, string Method, string Purpose, string Handle,
    DateTimeOffset Since, string State);

/// <summary>How a subtitle track of a playback is served apart: as WebVTT (text), as ASS too (ass), or as PGS (pictures).</summary>
internal readonly record struct SubtitleServed(bool Text, bool Ass, bool Pictures);

/// <summary>
/// A stream of a playback: its own (entry 0) or a queue entry's: how it is
/// served, and for a file its exact size, etag and media type (the copy's,
/// from the catalog).
/// </summary>
internal sealed record EntryServed(string Protocol, long Size, string Etag, string MediaType);

/// <summary>
/// A playback ended lately: whose, why, when; for a download, what its late
/// reports need (playback section 7).
/// </summary>
internal sealed record EndedPlay(string Peer, EndReason Reason, DateTimeOffset At, string Purpose, string Title,
    IReadOnlyList<string> Queue, string Handle, DateTimeOffset PlayedAt)
{
    public DateTimeOffset? LastAt { get; set; }
}

/// <summary>A peer's live playback here.</summary>
internal sealed class LivePlayback
{
    public string Id { get; init; } = "";

    public string Peer { get; init; } = "";

    public string PeerName { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>The request's queue, by place.</summary>
    public IReadOnlyList<string> Queue { get; init; } = [];

    /// <summary>The host's libraries holding the title, offered when it started.</summary>
    public IReadOnlyList<string> Libraries { get; init; } = [];

    public string Handle { get; init; } = "";

    public string Method { get; init; } = "";

    public string Purpose { get; init; } = "";

    public bool AudioOnly { get; init; }

    public byte[] Ticket { get; init; } = [];

    public DateTimeOffset Started { get; init; }

    public int Sheets { get; set; }

    /// <summary>A sheet's bound: its layout's full sheet.</summary>
    public int SheetWidth { get; set; }

    public int SheetHeight { get; set; }

    public int Fonts { get; set; }

    /// <summary>The copy's audio track IDs.</summary>
    public HashSet<string> Audio { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SubtitleServed> Subtitles { get; } = new(StringComparer.Ordinal);

    /// <summary>0: its own stream; n+1: queue entry n.</summary>
    public Dictionary<int, EntryServed> Entries { get; } = [];

    public CountSlot Slot { get; init; } = null!;

    /// <summary>A download's, at downloadKbps; null for none.</summary>
    public Pacer? Pace { get; set; }

    /// <summary>Cancelled when it ends: its open responses are cut.</summary>
    public CancellationTokenSource Ended { get; } = new();

    // Under the plays' lock.
    public DateTimeOffset Used { get; set; }

    public bool Active { get; set; }

    public int Open { get; set; }

    public DateTimeOffset? LastAt { get; set; }

    public long ScanSent { get; set; }

    /// <summary>A scan's: the bytes its ticket serves in all (its pool's scanBytes).</summary>
    public long ScanCap { get; set; }

    /// <summary>The fonts served, by index: 64 MiB in all.</summary>
    public Dictionary<int, int> FontBytes { get; } = [];

    public int ScanReads { get; set; }

    /// <summary>The playlists served, by entry/name: served the same again (media section 3.1).</summary>
    public Dictionary<string, byte[]> Playlists { get; } = new(StringComparer.Ordinal);

    public int PlaylistBytes { get; set; }

    /// <summary>entry/name: the names its playlists gave, the only ones served (media section 1).</summary>
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>Its own stream's facts, for its master playlist.</summary>
    public ServedFacts Facts { get; set; } = new(new Copy("", 0, "", 0, "", []), "", new Served());
}

/// <summary>
/// What a play counted in a pool (Admit): a stream, and when transcode one
/// of its transcodes; or a download; or a scan.
/// </summary>
internal sealed class CountSlot(Plays plays, Func<DateTimeOffset> now, string pool, string purpose, bool audio, Limits limits) : ISlot
{
    public string Pool { get; } = pool;

    public string Purpose { get; } = purpose;

    public bool Audio { get; } = audio;

    public Limits Limits { get; } = limits;

    public bool Transcode { get; set; }

    public bool Released { get; set; }

    public (int Streams, int Transcodes) Caps() =>
        Audio ? (Limits.AudioStreams, Limits.AudioTranscodes) : (Limits.Streams, Limits.Transcodes);

    /// <summary>Frees what was counted; once, however often it is called. A download or a scan stays in the day's count it took.</summary>
    public void Release()
    {
        lock (plays)
        {
            if (Released)
            {
                return;
            }

            Released = true;
            if (!plays.Counts.TryGetValue(Pool, out var c))
            {
                return;
            }

            switch (Purpose)
            {
                case Playback.PurposeDownload:
                    c.Downloads--;
                    break;
                case Playback.PurposeScan:
                    break;
                default:
                    c.Add(Audio, -1, Transcode ? -1 : 0);
                    break;
            }

            c.Trim(now());
            if (c.Empty)
            {
                plays.Counts.Remove(Pool);
            }
        }
    }

    /// <summary>
    /// Counts a remux that turned out to re-encode the video as a transcode
    /// from then on (playback section 6); false when the pairing has none
    /// left: the host then stops it. A download's or a scan's takes none of
    /// the pool's (the server's budget is the host's).
    /// </summary>
    public bool Transcoding()
    {
        lock (plays)
        {
            if (Transcode)
            {
                return true;
            }

            if (Released)
            {
                return false;
            }

            if (Purpose != Playback.PurposePlay)
            {
                return true;
            }

            var (_, most) = Caps();
            var c = plays.CountsOf(Pool);
            if (c.Of(Audio).Transcodes >= most)
            {
                return false;
            }

            c.Add(Audio, 0, 1);
            Transcode = true;
            return true;
        }
    }
}

/// <summary>
/// What a pool uses of this server now: its playbacks of purpose play (video
/// and audio apart when its limits say so), its downloads, and when the
/// downloads and scans of the last 24 hours started.
/// </summary>
internal sealed class UseCounts
{
    private int _streams, _transcodes, _audioStreams, _audioTranscodes;

    public int Downloads { get; set; }

    public List<DateTimeOffset> DownloadDay { get; } = [];

    public List<DateTimeOffset> ScanDay { get; } = [];

    public bool Empty => _streams == 0 && _transcodes == 0 && _audioStreams == 0 && _audioTranscodes == 0 && Downloads == 0 &&
        DownloadDay.Count == 0 && ScanDay.Count == 0;

    public (int Streams, int Transcodes) Of(bool audio) => audio ? (_audioStreams, _audioTranscodes) : (_streams, _transcodes);

    public void Add(bool audio, int streams, int transcodes)
    {
        if (audio)
        {
            _audioStreams += streams;
            _audioTranscodes += transcodes;
        }
        else
        {
            _streams += streams;
            _transcodes += transcodes;
        }
    }

    /// <summary>Keeps of the day's times those of the 24 hours before now.</summary>
    public void Trim(DateTimeOffset now)
    {
        DownloadDay.RemoveAll(t => now >= t + TimeSpan.FromHours(24));
        ScanDay.RemoveAll(t => now >= t + TimeSpan.FromHours(24));
    }
}

/// <summary>The peers' playbacks here, live, and those ended lately; locked on itself.</summary>
internal sealed class Plays
{
    public Dictionary<string, LivePlayback> ById { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, LivePlayback> ByTicket { get; } = new(StringComparer.Ordinal);

    /// <summary>By pool, admitted slots included.</summary>
    public Dictionary<string, UseCounts> Counts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, EndedPlay> Ended { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset Expired { get; set; }

    public UseCounts CountsOf(string pool)
    {
        if (!Counts.TryGetValue(pool, out var c))
        {
            c = new UseCounts();
            Counts[pool] = c;
        }

        return c;
    }
}

/// <summary>
/// A copy of a title as the catalog sends it to a peer (cleaned, in the
/// host's order of preference, its ID the version ID), with the host's handle
/// of it; Copy is it whole (a track's as a copy with no video, its one audio
/// track a0).
/// </summary>
internal sealed record OfferedCopy(string Handle, Copy Copy, string Lyrics = "")
{
    public string Id => Copy.Id;
}

/// <summary>
/// Paces a download's bytes at its speed cap (playback section 7, media
/// section 4.1): over any 10 seconds, never more than the cap allows, all
/// its fetches together; it slows the body, never refuses. It lets a
/// second's worth through at once and refills at nine tenths of the cap, so
/// that a second's burst and nine seconds' refill stay within ten seconds'
/// worth. It follows the engine's clock (Config.TimeProvider), and waits
/// with its timers: a test's clock paces without waiting.
/// </summary>
internal sealed class Pacer(int kbps, TimeProvider time)
{
    private readonly double _rate = kbps * 1000.0 / 8; // bytes per second
    private double _tokens = kbps * 1000.0 / 8;
    private DateTimeOffset _last = time.GetUtcNow();

    /// <summary>Waits until n bytes may go, or ct is cancelled.</summary>
    public Task TakeAsync(int n, CancellationToken ct)
    {
        double debt;
        lock (this)
        {
            var now = time.GetUtcNow();
            if (now > _last)
            {
                _tokens = Math.Min(_rate, _tokens + ((now - _last).TotalSeconds * _rate * 0.9));
                _last = now;
            }

            _tokens -= n;
            debt = -_tokens;
        }

        return debt <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(debt / (_rate * 0.9)), time, ct);
    }
}

// Playback, on the owning side (MSFP playback sections 2 to 8, video
// section 5, music section 5): play, progress and stop from peers, the
// choice of the copy and the tracks among the title's copies as the catalog
// sends them, the delivery and the purpose (a play, a download, a scan),
// the pool's limits counted in one step with the host's start (Admit), the
// playbacks' states, tickets and endings, and a download's late reports.
// Playbacks live in memory: after a restart, a peer's progress gets 410 and
// it starts again; downloads alone are kept in the store for their late
// reports (Downloads.cs).
public sealed partial class MsfpEngine
{
    // Times of a playback (playback section 6.3): expired playbacks are
    // looked for every 30 seconds; why one ended is kept an hour (a
    // download's for its late reports); the host's Stop and Progress made in
    // the background get 30 seconds.
    private static readonly TimeSpan ExpiryEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EndedKept = TimeSpan.FromHours(1);
    private static readonly TimeSpan HostTime = TimeSpan.FromSeconds(30);

    private readonly Plays _plays = new();

    // Counts a playback of purpose in a pool, in one step no other play comes
    // between (playback section 6.2): for a play, a stream (and for
    // "transcode" a transcode); for a download, one of its downloads and of
    // the day's; for a scan, one of the day's.
    private CountSlot AdmitIn(string pool, Limits l, string purpose, bool audio, string method)
    {
        if (method is not ("direct" or "remux" or "transcode"))
        {
            throw new MsfpException(MsfpError.Internal, "admit of a method MSFP doesn't have");
        }

        var s = new CountSlot(_plays, Now, pool, purpose, audio && l.Audio, l);
        lock (_plays)
        {
            var c = _plays.CountsOf(pool);
            var now = Now();
            c.Trim(now);
            switch (purpose)
            {
                case Playback.PurposeDownload:
                    if (c.Downloads >= l.Downloads || (l.DownloadsPerDay > 0 && c.DownloadDay.Count >= l.DownloadsPerDay))
                    {
                        throw new MsfpException(MsfpError.StreamLimitReached);
                    }

                    c.Downloads++;
                    c.DownloadDay.Add(now);
                    return s;
                case Playback.PurposeScan:
                    if (c.ScanDay.Count >= l.ScansPerDay)
                    {
                        throw new MsfpException(MsfpError.StreamLimitReached);
                    }

                    c.ScanDay.Add(now);
                    return s;
            }

            var (most, mostTranscodes) = s.Caps();
            var (streams, transcodes) = c.Of(s.Audio);
            if (streams >= most)
            {
                throw new MsfpException(MsfpError.StreamLimitReached);
            }

            if (method == "transcode" && transcodes >= mostTranscodes)
            {
                throw new MsfpException(MsfpError.ServerBusy);
            }

            s.Transcode = method == "transcode";
            c.Add(s.Audio, 1, s.Transcode ? 1 : 0);
        }

        return s;
    }

    // Whether a pool has room for a playback of purpose now (Room), and
    // whether its limits allow any at all (Open).
    private (bool Room, bool Open) RoomIn(string pool, Limits l, string purpose, bool audio)
    {
        lock (_plays)
        {
            _plays.Counts.TryGetValue(pool, out var c);
            var now = Now();
            switch (purpose)
            {
                case Playback.PurposeDownload:
                    var today = c?.DownloadDay.Count(t => now < t + TimeSpan.FromHours(24)) ?? 0;
                    return ((c?.Downloads ?? 0) < l.Downloads && (l.DownloadsPerDay == 0 || today < l.DownloadsPerDay), l.Downloads > 0);
                case Playback.PurposeScan:
                    var scans = c?.ScanDay.Count(t => now < t + TimeSpan.FromHours(24)) ?? 0;
                    return (scans < l.ScansPerDay, l.ScansPerDay > 0);
            }

            var counted = audio && l.Audio;
            var most = counted ? l.AudioStreams : l.Streams;
            var used = c?.Of(counted).Streams ?? 0;
            return (used < most, most > 0);
        }
    }

    // A copy's version ID toward a peer: a keyed MAC of its handle under the
    // IDs secret, which tells the peer nothing of it (catalog section 2.1),
    // stable as long as the handle is. Its form is Overfin's from before the
    // engine ("ver_", then the first 16 bytes of HMAC-SHA-256 over "version",
    // the peer's ID and the handle, a line feed between each, base64url).
    internal string VersionId(string peer, string handle)
    {
        var mac = HMACSHA256.HashData(OurKeys().Ids, Encoding.UTF8.GetBytes("version\n" + peer + "\n" + handle));
        return "ver_" + Base64UrlText.Encode(mac.AsSpan(0, 16));
    }

    // The copies of exactly that title the peer may play here, as the
    // catalog sends them to it (playback section 2.2, step 3; video section
    // 3.7: in the host's order of preference), and the host's libraries holding it among those
    // offered; none when the title isn't sent to the peer.
    private async Task<(List<OfferedCopy> Copies, List<string> Libraries)> OfferedCopiesAsync(PeerRecord p, string key, CancellationToken ct)
    {
        var none = (new List<OfferedCopy>(), new List<string>());
        if (_cfg.Catalog is null)
        {
            return none;
        }

        var libs = Offered(p).Order(StringComparer.Ordinal).ToList();
        if (await HostTitleAsync(key, libs, ct).ConfigureAwait(false) is not { } t)
        {
            return none;
        }

        var raw = await (await SenderForAsync(p, libs, ct).ConfigureAwait(false)).TitleAsync(t).ConfigureAwait(false);
        if (raw is null)
        {
            return none;
        }

        var output = new List<OfferedCopy>();
        switch (t)
        {
            case VideoTitle v:
                var handles = (v.Copies ?? []).ToDictionary(c => VersionId(p.Id, c.Id), c => c.Id, StringComparer.Ordinal);
                if (Video.ReadTitle(raw) is not { } sent)
                {
                    return none;
                }

                foreach (var c in sent.Copies)
                {
                    output.Add(new OfferedCopy(handles.GetValueOrDefault(c.Id, ""), c));
                }

                break;
            case MusicTitle m:
                var mhandles = (m.Copies ?? []).ToDictionary(c => VersionId(p.Id, c.Id), c => c.Id, StringComparer.Ordinal);
                if (Music.ReadTitle(raw) is not { } msent)
                {
                    return none;
                }

                foreach (var c in msent.Copies)
                {
                    output.Add(new OfferedCopy(mhandles.GetValueOrDefault(c.Id, ""), AsCopy(c), c.Lyrics));
                }

                break;
        }

        output.RemoveAll(c => c.Handle.Length == 0);
        var held = (t.Libraries ?? []).Where(libs.Contains).Distinct(StringComparer.Ordinal).ToList();
        return (output, held);
    }

    // A sender of titles to the peer, for these libraries.
    private async Task<TitleSender> SenderForAsync(PeerRecord p, List<string> libs, CancellationToken ct)
    {
        var ids = await LibraryIdsAsync(ct).ConfigureAwait(false);
        var pub = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in libs)
        {
            if (ids.TryGetValue(l, out var id))
            {
                pub[l] = id;
            }
        }

        return new TitleSender(this, p, libs, LocalOffered(p), pub, ct);
    }

    // A track's copy as a copy with no video, its one audio track a0, the
    // file's default (music section 5).
    internal static Copy AsCopy(MusicCopy c) => new(c.Id, c.DurationMs, c.MediaType, c.Size, c.Etag,
    [
        new CopyAudio("a0", c.Codec)
        {
            Channels = c.Channels == 0 ? null : c.Channels, Default = true, Codecs = c.Codecs.Length > 0 ? c.Codecs : null,
            SampleRate = c.SampleRate == 0 ? null : c.SampleRate, BitDepth = c.BitDepth == 0 ? null : c.BitDepth,
        },
    ]);

    // How a subtitle track of format is served apart (video section 5.3):
    // the form video section 5.4's table gives a listed format, else the one
    // the host says (form), else none; none either when the host says it
    // can't.
    private static SubtitleServed ServedAs(string id, string format, string? form, bool notApart)
    {
        if (notApart)
        {
            return default;
        }

        if (Video.FormOf(id, format) is { } listed)
        {
            return new SubtitleServed(listed.Form == Video.FormText, listed.Ass, listed.Form == Video.FormPicture);
        }

        return new SubtitleServed(form == Video.FormText, false, form == Video.FormPicture);
    }

    // A refusal of play, progress or stop as answered.
    private static Answer RefusalOf(PlayRefusal r) => r.Problem switch
    {
        Playback.ProblemStreamLimit => Answer.Refuse(ProblemType.StreamLimit),
        Playback.ProblemDeliveryRefused => Answer.Refuse(ProblemType.Delivery),
        Playback.ProblemNotPlayable => Answer.Refuse(ProblemType.NotPlayable),
        Playback.ProblemServerBusy => Answer.Refuse(ProblemType.ServerBusy),
        Playback.ProblemStopped => Answer.Refuse(ProblemType.Stopped),
        Playback.ProblemNotFound => Answer.Refuse(ProblemType.NotFound),
        _ => Answer.Refuse(ProblemType.Invalid),
    };

    // A member of the wrong type, in a body read.
    internal static ProtocolException BadJson() => new(ProtocolError.Json);

    private static string? Nonempty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static long? NonZero(long n) => n != 0 ? n : null;

    // Plays a paired peer's title here (playback section 2.2), in its steps.
    private async Task<Answer> PlayAsync(CallRequest req, CancellationToken ct)
    {
        var p = req.Peer;
        if (p is null || p.State != PeerState.Paired)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        if (!On(p, Versions.PartPlayback))
        {
            return Answer.Refuse(ProblemType.NotSupported);
        }

        // 1. The request, as MSFP reads it: a key of a content part on.
        Protocol.PlayRequest @in;
        JsonObject members;
        try
        {
            @in = Playback.ReadPlay(req.Body, t => KeyOn(p, t, out var tk) && tk.Playable, out members);
        }
        catch (PlayRefusal e)
        {
            return RefusalOf(e);
        }

        TitleKey.TryParse(@in.Title, out var k);
        var video = k.Part == Versions.PartVideo;
        var audioOnly = k.Part == Versions.PartMusic;
        IReadOnlyList<QueueAsk> queue = [];
        if (audioOnly)
        {
            try
            {
                queue = Music.CheckQueue(members);
            }
            catch (PlayRefusal e)
            {
                return RefusalOf(e);
            }
        }

        if (!video && @in.Transcode is { } tt)
        {
            // Members of another content part are ignored (playback section 2.4).
            @in = @in with { Transcode = tt with { BurnSubtitle = "" } };
        }

        // 2. No room at all for this purpose in any of the link's pools.
        if (!PoolRoom(p, @in.Purpose, audioOnly).Open)
        {
            return Answer.Refuse(ProblemType.StreamLimit);
        }

        // 3. The title's copies, as the catalog sends them to it.
        if (_cfg.Catalog is null || _cfg.Playback is null)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        List<OfferedCopy> copies;
        List<string> libs;
        try
        {
            (copies, libs) = await OfferedCopiesAsync(p, @in.Title, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the host's title couldn't be read for a play");
            return Answer.Refuse(ProblemType.Internal);
        }

        if (copies.Count == 0)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        // 4. The copy (the one versionId names, else the first, the host's
        // preferred), then malformed track IDs refused; the audio (the one
        // audioTrack names, else the file's default).
        var chosen = copies[Math.Max(Playback.ChooseCopy(copies.Select(c => c.Copy).ToList(), @in.VersionId), 0)];
        if (!@in.TrackIdsOk)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        var audio = Playback.ChooseAudio(chosen.Copy, @in.AudioTrack);

        // 5. The purpose's limits all in use: refused before any work (the
        // count that matters is Admit's).
        if (!PoolRoom(p, @in.Purpose, audioOnly).Room)
        {
            return Answer.Refuse(ProblemType.StreamLimit);
        }

        // 6 and 7. The bitrate cap (0: none), and the deliveries MSFP itself
        // refuses, or what is served: the baseline's streams left unchanged
        // when within it, the targets left out the baseline's.
        var kbps = @in.BitrateCap(_cfg.MaxBitrateKbps, Generous(p).BitrateKbps);
        var enc = _cfg.Encoders ?? new Encoders();
        var (served, refused) = Serve(@in, chosen.Copy, audio, kbps, enc);
        if (refused is not null)
        {
            return RefusalOf(refused);
        }

        if (@in.Delivery == Playback.DeliveryTranscode)
        {
            @in = @in with { Transcode = served!.Transcode };
        }

        CopyInfo info;
        try
        {
            info = await _cfg.Playback.DescribeAsync(chosen.Handle, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HostRefusal(e, "describe");
        }

        var start = @in.StartPositionMs;
        if (@in.Purpose != Playback.PurposePlay)
        {
            start = 0;
        }
        else if (start == 0 && video && chosen.Copy.StartMs is { } episodeStart)
        {
            start = episodeStart;
        }

        // The queue: each entry's copy (the one it names, else its first) and
        // its own delivery, else the request's; one not offered, of no such
        // copy, or that can't be served so keeps its place empty (music
        // section 5.2). A codec not made refuses the request.
        var queued = new List<QueuedCopy>();
        var queueCopies = new Dictionary<int, OfferedCopy>();
        var queueTranscodes = false;
        var entries = members["queue"] as JsonArray;
        for (var n = 0; n < queue.Count; n++)
        {
            var ask = queue[n];
            Protocol.PlayRequest er;
            try
            {
                er = Playback.ReadQueueEntry(members, entries![n]);
            }
            catch (PlayRefusal e)
            {
                return RefusalOf(e);
            }

            if (er.Transcode is { } et && enc.RefuseCodecs(et, false) is { } codecs)
            {
                return RefusalOf(codecs);
            }

            List<OfferedCopy> qc;
            try
            {
                (qc, _) = await OfferedCopiesAsync(p, ask.Title, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "msfp: the host's title couldn't be read for a play's queue");
                return Answer.Refuse(ProblemType.Internal);
            }

            var c = ask.VersionId.Length == 0 ? qc.FirstOrDefault() : qc.FirstOrDefault(x => x.Id == ask.VersionId);
            if (c is null || Serve(er, c.Copy, Playback.ChooseAudio(c.Copy, ""), kbps, enc) is not ({ } s, null))
            {
                continue;
            }

            queueTranscodes |= s.Transcodes;
            queued.Add(new QueuedCopy(n, ask.Title, c.Handle) { Delivery = er.Delivery, MediaType = er.MediaType, Transcode = s.Transcode, Served = s });
            queueCopies[n] = c;
        }

        // The method admitted: a transcode whose video and audio are both
        // copied takes no transcode; a queue with a track converted takes one
        // (playback section 2.6, music section 5.2).
        var takes = served!.Transcodes;
        string Counted(string method) => method switch
        {
            Playback.MethodTranscode when @in.Delivery == Playback.DeliveryTranscode && !takes && !queueTranscodes => Playback.MethodRemux,
            not Playback.MethodTranscode when queueTranscodes => Playback.MethodTranscode,
            _ => method,
        };

        // 8. The host starts it, admitted by the pool's limits first.
        CountSlot? slot = null;
        var purpose = @in.Purpose;
        var sr = new StartRequest
        {
            Copy = chosen.Handle, Title = @in.Title, AudioOnly = audioOnly, Purpose = purpose, Delivery = @in.Delivery,
            MediaType = @in.MediaType, Transcode = @in.Transcode, Served = served, AudioRenditions = @in.AudioRenditions,
            Parts = Math.Max(chosen.Copy.Parts ?? 1, 1), Peer = new PeerRef(p.Id, p.Name), AudioTrack = audio, StartMs = start,
            MaxBitrateKbps = kbps, Queue = queued,
            Admit = method =>
            {
                if (slot is not null)
                {
                    throw new MsfpException(MsfpError.Internal, "admit called twice");
                }

                slot = AdmitPools(p, purpose, audioOnly, Counted(method));
                return slot;
            },
        };
        Started started;
        try
        {
            started = await _cfg.Playback.StartAsync(sr, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            slot?.Release();
            return HostRefusal(e, "start");
        }

        if (StartedBreaks(@in, chosen, served, started, queued) is { } why)
        {
            _log.LogError("msfp: the host started a playback MSFP doesn't allow; it is stopped: {Why} (decided {Decided}, method {Method}, protocol {Protocol})",
                why, served.Method, started.Method, started.Protocol);
            slot?.Release();
            HostStop(started.Handle, EndReason.Stopped);
            return Answer.Refuse(ProblemType.Internal);
        }

        if (slot is null)
        {
            // A host that didn't ask: admitted now, its playback stopped when
            // the limits refuse it.
            try
            {
                slot = AdmitPools(p, purpose, audioOnly, Counted(started.Method));
            }
            catch (MsfpException e)
            {
                HostStop(started.Handle, EndReason.Stopped);
                return HostRefusal(e, "admit");
            }
        }

        var (pb, ticket) = Register(p, @in, queue, libs, chosen, info, audio, served, started, slot, queueCopies, audioOnly);
        if (pb is null)
        {
            return Answer.Refuse(ProblemType.Internal);
        }

        if (pb.Purpose == Playback.PurposeDownload)
        {
            await KeepDownloadAsync(new DownloadRecord
            {
                Id = pb.Id, Peer = pb.Peer, Title = pb.Title, Queue = [.. pb.Queue], Handle = pb.Handle, PlayedAt = pb.Started.ToUniversalTime(),
            }, ct).ConfigureAwait(false);
        }

        // The pairing ended, or the library is no longer offered, while it
        // started: it ends at once, saying which.
        try
        {
            var q = await PeerRecordAsync(p.Id, ct).ConfigureAwait(false);
            if (q.State != PeerState.Paired || q.Suspended is not null)
            {
                EndPlayback(pb, EndReason.PairingEnded);
                return Answer.Refuse(ProblemType.NotFound);
            }

            if (!StillOffered(q, pb.Libraries))
            {
                EndPlayback(pb, EndReason.NotOffered);
                return Answer.Refuse(ProblemType.NotFound);
            }
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            EndPlayback(pb, EndReason.PairingEnded);
            return Answer.Refuse(ProblemType.NotFound);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            EndPlayback(pb, EndReason.Stopped);
            return Answer.Refuse(ProblemType.Internal);
        }

        // 9. The answer: the ticket, and what the catalog can't know
        // (playback section 2.3); the viewer's server builds every URL.
        var hls = started.Protocol == Playback.ProtocolHls;
        var subtitles = new List<ServedSubtitle>();
        foreach (var t in chosen.Copy.SubtitleTracks)
        {
            if (pb.Subtitles.TryGetValue(t.Id, out var how) && (how.Text || how.Pictures))
            {
                subtitles.Add(how.Pictures ? new ServedSubtitle(t.Id, Video.FormPicture) : new ServedSubtitle(t.Id, Video.FormText, how.Ass));
            }
        }

        var answered = (started.Queue ?? []).OrderBy(e => e.Place)
            .Select(e => new QueueEntry(queue[e.Place].Title, queueCopies[e.Place].Id, e.Method, e.Protocol, e.Place)).ToList();
        var output = JsonOut.Object(o => o.String("id", pb.Id).String("ticket", ticket).String("protocol", started.Protocol)
            .String("method", started.Method).String("etag", chosen.Copy.ETag).Integer("positionMs", started.PositionMs)
            .String("versionId", chosen.Id).String("audioTrack", Nonempty(audio))
            // Asked for, never refused: the answer says whether the host made
            // them (playback section 2.3), false when it made none.
            .Bool("audioRenditions", @in.AudioRenditions && hls ? started.AudioRenditions : null)
            .Integer("maxBitrateKbps", purpose == Playback.PurposePlay ? kbps : null)
            .Integer("scanBytes", purpose == Playback.PurposeScan ? pb.ScanCap : null)
            .Objects("subtitles", subtitles.Count > 0 ? subtitles : null,
                (w, t) => w.String("id", t.Id).String("form", t.Form).Bool("ass", t.Ass ? true : null))
            .Integer("fontCount", pb.Fonts > 0 ? pb.Fonts : null)
            .Objects("queue", answered.Count > 0 ? answered : null, (w, e) => e.Write(w)));
        _log.LogInformation("msfp: a peer's playback started: {Peer} {Playback} {Method} {Purpose}", p.Id, pb.Id, started.Method, purpose);
        Notify(new PlaybackStarted(p.Id, p.Name, pb.Id, pb.Title, pb.Method, pb.Purpose));
        return Answer.Ok(output);
    }

    // Whether one of the libraries a playback's title was in is still
    // offered to the peer.
    private static bool StillOffered(PeerRecord p, IReadOnlyList<string> libs) => OfferedIn(libs, Offered(p));

    // Whether one of libs is among those offered.
    internal static bool OfferedIn(IReadOnlyList<string> libs, IEnumerable<string> offered) =>
        libs.Count == 0 || libs.Any(offered.Contains);

    // The method each delivery is served with.
    internal static string MethodOf(string delivery) => delivery switch
    {
        Playback.DeliveryFile => "direct",
        Playback.DeliveryRemux => "remux",
        Playback.DeliveryTranscode => "transcode",
        _ => "",
    };

    // What the host's start breaks of MSFP, null for nothing: a method or
    // protocol MSFP doesn't have, another method than a transcode the engine
    // decided (Served: without delivery, a copy outside the baseline is
    // transcoded; one within it may be served direct, remux or transcode:
    // section 2.3), another delivery than the one asked (file
    // is direct as file; remux, transcode and the baseline without delivery
    // are hls), file of a copy of
    // several parts, file played otherwise than direct; a queue entry of a
    // place not asked, twice, or served against the same rules for its own
    // delivery.
    private static string? StartedBreaks(Protocol.PlayRequest @in, OfferedCopy c, Served served, Started s, List<QueuedCopy> asked)
    {
        static string? Check(string delivery, string decided, string method, string protocol, int parts)
        {
            if (method is not ("direct" or "remux" or "transcode") || protocol is not ("hls" or "file"))
            {
                return "a method or protocol MSFP doesn't have";
            }

            if (delivery.Length > 0 && method != MethodOf(delivery))
            {
                return "another method than the delivery asked";
            }

            if (decided == Playback.MethodTranscode && method != decided)
            {
                return "another method than the transcode decided";
            }

            if ((delivery == Playback.DeliveryFile && protocol != "file") ||
                (delivery != Playback.DeliveryFile && protocol == "file"))
            {
                return "another protocol than the delivery asked";
            }

            if (protocol == "file" && method != "direct")
            {
                return "a file that isn't played as it is";
            }

            return protocol == "file" && parts > 1 ? "a copy of several parts as file" : null;
        }

        if (Check(@in.Delivery, served.Method, s.Method, s.Protocol, Math.Max(c.Copy.Parts ?? 1, 1)) is { } why)
        {
            return why;
        }

        var seen = new HashSet<int>();
        foreach (var q in s.Queue ?? [])
        {
            if (asked.Find(a => a.Place == q.Place) is not { } a || !seen.Add(q.Place))
            {
                return "a queue entry of no place asked";
            }

            if (Check(a.Delivery, a.Served.Method, q.Method, q.Protocol, 1) is { } qwhy)
            {
                return "a queue entry: " + qwhy;
            }
        }

        return null;
    }

    // Makes a started playback live: its ID and its ticket, 128 bits and 256
    // bits of randomness, the ticket kept by its hash only (media section 1).
    // It gives the ticket's text, for the answer only.
    private (LivePlayback? Playback, string Ticket) Register(PeerRecord p, Protocol.PlayRequest @in, IReadOnlyList<QueueAsk> queue,
        List<string> libs, OfferedCopy c, CopyInfo info, string audio, Served served, Started s, CountSlot slot,
        Dictionary<int, OfferedCopy> queueCopies, bool audioOnly)
    {
        var id = NewId("play_", 16);
        var ticket = Base64UrlText.Encode(RandomBytes(32));
        var now = Now();
        var pb = new LivePlayback
        {
            Id = id, Peer = p.Id, PeerName = p.Name, Title = @in.Title, Queue = queue.Select(q => q.Title).ToList(), Libraries = libs,
            Handle = s.Handle, Method = s.Method, Purpose = @in.Purpose, AudioOnly = audioOnly,
            Ticket = SHA256.HashData(Encoding.UTF8.GetBytes(ticket)), Started = now, Used = now, Slot = slot,
            Facts = new ServedFacts(c.Copy, audio, served),
        };
        pb.Entries[0] = new EntryServed(s.Protocol, c.Copy.Size, c.Copy.ETag, c.Copy.MediaType);
        if (@in.Purpose == Playback.PurposeScan)
        {
            pb.ScanCap = Playback.ScanCapOf(slot.Limits.ScanBytes);
        }

        if (@in.Purpose == Playback.PurposeDownload && slot.Limits.DownloadKbps > 0)
        {
            pb.Pace = new Pacer(slot.Limits.DownloadKbps, _time);
        }

        if (!audioOnly)
        {
            pb.Audio.UnionWith(c.Copy.AudioTracks.Select(a => a.Id));
        }

        if (!audioOnly && @in.Purpose != Playback.PurposeScan)
        {
            // A playback's ticket opens its copy's sheets (video section 5.5);
            // a download's opens none. The layout left only with a
            // SheetEncoder (SendCopy).
            if (c.Copy.PreviewLayout is { } t && @in.Purpose == Playback.PurposePlay && _cfg.SheetEncoder is not null)
            {
                pb.Sheets = t.Sheets;
                pb.SheetWidth = t.Columns * t.TileWidth;
                pb.SheetHeight = t.Rows * t.TileHeight;
            }

            var hasAss = false;
            foreach (var st in c.Copy.SubtitleTracks)
            {
                var how = ServedAs(st.Id, st.Format, info.Forms?.GetValueOrDefault(st.Id), (info.NotApart ?? []).Contains(st.Id));
                if (how.Text || how.Pictures)
                {
                    pb.Subtitles[st.Id] = how;
                }

                hasAss |= how.Ass;
            }

            if (hasAss && info.Fonts > 0 && info.Fonts <= Font.MaxFonts)
            {
                pb.Fonts = info.Fonts;
            }
        }

        foreach (var q in s.Queue ?? [])
        {
            var qc = queueCopies[q.Place];
            pb.Entries[q.Place + 1] = new EntryServed(q.Protocol, qc.Copy.Size, qc.Copy.ETag, qc.Copy.MediaType);
        }

        lock (_plays)
        {
            _plays.ById[pb.Id] = pb;
            _plays.ByTicket[Convert.ToHexString(pb.Ticket)] = pb;
        }

        return (pb, ticket);
    }

    // The stream root of the address the request was signed for: the former
    // one during a move, for a peer still on it (media section 1, core
    // section 13.1, step 5).
    private string StreamBase(CallRequest req)
    {
        var a = OurAddress();
        var address = req.Verified?.Former == true && a.Former is not null ? a.Former : a.Address;
        return address + _cfg.Prefix + StreamRoute;
    }

    // The host's error as the peer is told it (engine.md section 6).
    private Answer HostRefusal(Exception e, string what)
    {
        if (e is MsfpException m)
        {
            switch (m.Code)
            {
                case MsfpError.NotPlayable:
                    return Answer.Refuse(ProblemType.NotPlayable);
                case MsfpError.DeliveryRefused:
                    return Answer.Refuse(ProblemType.Delivery);
                case MsfpError.StreamLimitReached:
                    return Answer.Refuse(ProblemType.StreamLimit);
                case MsfpError.ServerBusy:
                    return Answer.Refuse(ProblemType.ServerBusy);
                case MsfpError.NotFound:
                    return Answer.Refuse(ProblemType.NotFound);
            }
        }

        _log.LogWarning(e, "msfp: the host's playback failed: {Step}", what);
        return Answer.Refuse(ProblemType.Internal);
    }

    // Tells the host a playback ended, in the background.
    private void HostStop(string handle, EndReason why)
    {
        if (_cfg.Playback is not { } playback)
        {
            return;
        }

        Background(async ct =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HostTime);
            try
            {
                await playback.StopAsync(handle, why, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "msfp: the host's Stop failed");
            }
        });
    }

    // Ends a live playback, once: its ticket stops working at once, its open
    // responses are cut, its stream is freed, the host is told (playback
    // section 6.3).
    private void EndPlayback(LivePlayback pb, EndReason why)
    {
        lock (_plays)
        {
            if (!_plays.ById.TryGetValue(pb.Id, out var live) || live != pb)
            {
                return;
            }

            _plays.ById.Remove(pb.Id);
            _plays.ByTicket.Remove(Convert.ToHexString(pb.Ticket));
            _plays.Ended[pb.Id] = new EndedPlay(pb.Peer, why, Now(), pb.Purpose, pb.Title, pb.Queue, pb.Handle, pb.Started) { LastAt = pb.LastAt };
        }

        pb.Ended.Cancel();
        pb.Slot.Release();
        HostStop(pb.Handle, why);
        _log.LogInformation("msfp: a peer's playback ended: {Peer} {Playback} {Why}", pb.Peer, pb.Id, why);
        Notify(new PlaybackEnded(pb.Peer, pb.PeerName, pb.Id, pb.Title, why));
    }

    // Ends every live playback for which which says so.
    private void EndPlaybacks(Func<LivePlayback, bool> which, EndReason why)
    {
        List<LivePlayback> end;
        lock (_plays)
        {
            end = _plays.ById.Values.Where(which).ToList();
        }

        foreach (var pb in end)
        {
            EndPlayback(pb, why);
        }
    }

    // Ends the playbacks with neither progress nor a media request for 2
    // minutes (an open response counts as use; never a download), those
    // past their life (a download's 24 hours, a scan's 10 minutes) and those
    // longer than LongestPlayback (a download's 24 hours stand instead);
    // forgets endings past their time.
    private async Task ExpirePlaybacksAsync(bool force, CancellationToken ct)
    {
        var now = Now();
        var expired = new List<LivePlayback>();
        var tooLong = new List<LivePlayback>();
        var past = new List<DownloadRecord>();
        lock (_plays)
        {
            if (!force && now - _plays.Expired < ExpiryEvery)
            {
                return;
            }

            _plays.Expired = now;
            foreach (var pb in _plays.ById.Values)
            {
                if (pb.Open > 0)
                {
                    pb.Used = now;
                }

                var life = _cfg.LongestPlayback;
                var l = Playback.Life(pb.Purpose);
                if (l > TimeSpan.Zero && (pb.Purpose == Playback.PurposeDownload || l < life))
                {
                    life = l;
                }

                if (now - pb.Started >= life)
                {
                    tooLong.Add(pb);
                }
                else if (Playback.Idles(pb.Purpose) && now - pb.Used >= Playback.IdleEnd)
                {
                    expired.Add(pb);
                }
            }

            foreach (var (id, ended) in _plays.Ended.ToList())
            {
                if (ended.Purpose == Playback.PurposeDownload ? now - ended.PlayedAt > Playback.LateReports : now - ended.At >= EndedKept)
                {
                    _plays.Ended.Remove(id);
                    if (ended.Purpose == Playback.PurposeDownload)
                    {
                        past.Add(new DownloadRecord { Id = id });
                    }
                }
            }
        }

        await DropDownloadsAsync(past, ct).ConfigureAwait(false);

        foreach (var pb in tooLong)
        {
            EndPlayback(pb, EndReason.TooLong);
        }

        foreach (var pb in expired)
        {
            EndPlayback(pb, EndReason.Expired);
        }
    }

    // Notes a playback's use: a progress, a media request.
    private void Used(LivePlayback pb)
    {
        lock (_plays)
        {
            pb.Used = Now();
            pb.Active = true;
        }
    }

    // The playback a report names as Protocol's rules see it, and the live
    // playback when it is live: a playback of another pairing, or one never
    // given, has no state.
    private (PlaybackState State, LivePlayback? Live) PlayState(string peer, string id)
    {
        lock (_plays)
        {
            if (_plays.ById.TryGetValue(id, out var pb) && pb.Peer == peer)
            {
                return (new PlaybackState
                {
                    State = pb.Active ? Playback.StateActive : Playback.StateStarted, Purpose = pb.Purpose, Title = pb.Title, Queue = pb.Queue,
                    PlayedAt = pb.Started, LastAt = pb.LastAt,
                }, pb);
            }

            if (_plays.Ended.TryGetValue(id, out var ended) && ended.Peer == peer)
            {
                return (new PlaybackState
                {
                    State = Playback.StateEnded, Purpose = ended.Purpose, Title = ended.Title, Queue = ended.Queue,
                    PlayedAt = ended.PlayedAt, LastAt = ended.LastAt,
                }, null);
            }

            return (new PlaybackState(), null);
        }
    }

    // Answers a peer's progress or stop (playback sections 3, 4 and 7): read
    // as MSFP reads it, applied to the playback's state, kept reports passed
    // on to the host; a stop ends a live playback.
    private async Task<Answer> ReportAsync(CallRequest req, string call, CancellationToken ct)
    {
        var p = req.Peer;
        if (p is null || p.State != PeerState.Paired)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        if (!On(p, Versions.PartPlayback))
        {
            return Answer.Refuse(ProblemType.NotSupported);
        }

        var now = Now();
        PlayReport r;
        ReportOutcome outcome;
        LivePlayback? pb;
        try
        {
            r = Playback.ReadReport(call, req.Body, now);
            PlaybackState state;
            (state, pb) = PlayState(p.Id, r.Id);
            outcome = state.Apply(call, r, now);
        }
        catch (PlayRefusal e)
        {
            return RefusalOf(e);
        }

        if (pb is not null && call == Playback.CallProgress)
        {
            Used(pb);
        }

        if (outcome.Effect is Playback.EffectRecorded or Playback.EffectRecordedAndEnded && _cfg.Playback is { } playback)
        {
            var handle = "";
            DownloadRecord? kept = null; // a download's, its latest at kept across restarts
            lock (_plays)
            {
                if (pb is not null)
                {
                    pb.LastAt = outcome.At;
                    handle = pb.Handle;
                    if (pb.Purpose == Playback.PurposeDownload)
                    {
                        kept = new DownloadRecord
                        {
                            Id = pb.Id, Peer = pb.Peer, Title = pb.Title, Queue = [.. pb.Queue], Handle = pb.Handle,
                            PlayedAt = pb.Started.ToUniversalTime(), LastAt = outcome.At.ToUniversalTime(),
                        };
                    }
                }
                else if (_plays.Ended.TryGetValue(r.Id, out var ended))
                {
                    ended.LastAt = outcome.At;
                    handle = ended.Handle;
                    if (ended.Purpose == Playback.PurposeDownload)
                    {
                        kept = new DownloadRecord
                        {
                            Id = r.Id, Peer = ended.Peer, Title = ended.Title, Queue = [.. ended.Queue], Handle = ended.Handle,
                            PlayedAt = ended.PlayedAt.ToUniversalTime(), LastAt = outcome.At.ToUniversalTime(),
                        };
                    }
                }
            }

            if (kept is not null)
            {
                await KeepDownloadAsync(kept, ct).ConfigureAwait(false);
            }

            var hr = new Report
            {
                Handle = handle, Title = outcome.Title, PositionMs = outcome.PositionMs, HasPosition = outcome.HasPosition,
                At = outcome.At, Stop = call == Playback.CallStop, Late = pb is null,
            };
            try
            {
                await playback.ProgressAsync(hr, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogInformation(e, "msfp: the host's Progress failed");
            }
        }

        if (pb is not null && call == Playback.CallStop)
        {
            EndPlayback(pb, EndReason.Stopped);
        }

        return Answer.Ok("{}"u8.ToArray());
    }

    // A peer telling that a playback is still watched (playback section 3).
    private Task<Answer> ProgressAsync(CallRequest req, CancellationToken ct) => ReportAsync(req, Playback.CallProgress, ct);

    // A peer telling that a playback is over (playback section 4): 200
    // whether or not it was live; another pairing's changes nothing.
    private Task<Answer> StopAsync(CallRequest req, CancellationToken ct) => ReportAsync(req, Playback.CallStop, ct);

    /// <summary>
    /// Ends a peer's playback here from the host's side, for why (playback
    /// section 6.3): <see cref="EndReason.Operator"/> when the operator stops
    /// it, <see cref="EndReason.Expired"/> or <see cref="EndReason.TooLong"/>
    /// when the host's own idle or time rules end it,
    /// <see cref="EndReason.NotOffered"/> when its copy is no longer shared.
    /// </summary>
    /// <param name="id">The playback's ID.</param>
    /// <param name="why">Why; null is Operator.</param>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>, <see cref="MsfpError.InvalidReason"/>
    /// for another reason (the viewer's stop, the pairing's end are the engine's own).</exception>
    public void StopPlayback(string id, EndReason? why = null)
    {
        var reason = why ?? EndReason.Operator;
        if (reason is not (EndReason.Operator or EndReason.Expired or EndReason.TooLong or EndReason.NotOffered))
        {
            throw new MsfpException(MsfpError.InvalidReason);
        }

        LivePlayback? pb;
        lock (_plays)
        {
            _plays.ById.TryGetValue(id, out pb);
        }

        if (pb is null)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        EndPlayback(pb, reason);
    }

    /// <summary>Lists the peers' playbacks here, live, oldest first.</summary>
    public IReadOnlyList<PlaybackView> Playbacks()
    {
        lock (_plays)
        {
            return _plays.ById.Values
                .Select(pb => new PlaybackView(pb.Id, pb.Peer, pb.PeerName, pb.Title, pb.Method, pb.Purpose, pb.Handle, pb.Started,
                    pb.Active ? "active" : "started"))
                .OrderBy(v => v.Since).ThenBy(v => v.Id, StringComparer.Ordinal).ToList();
        }
    }

    // Whether a peer may still use a playback here: paired, not suspended,
    // its title's library still offered (media section 4). Read from storage
    // at each media request, so an ending noticed only then still stops the
    // ticket.
    private async Task<bool> PlayAllowedAsync(LivePlayback pb, CancellationToken ct)
    {
        PeerRecord p;
        try
        {
            p = await PeerRecordAsync(pb.Peer, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            EndPlayback(pb, EndReason.PairingEnded);
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }

        if (p.State != PeerState.Paired || p.Suspended is not null)
        {
            EndPlayback(pb, EndReason.PairingEnded);
            return false;
        }

        if (!StillOffered(p, pb.Libraries))
        {
            EndPlayback(pb, EndReason.NotOffered);
            return false;
        }

        return true;
    }
}
