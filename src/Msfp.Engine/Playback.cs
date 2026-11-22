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
/// served, and for a file its exact size and etag.
/// </summary>
internal sealed record EntryServed(string Protocol, long Size, string Etag);

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

    public int ScanReads { get; set; }

    /// <summary>The playlists served, by entry/name: served the same again (media section 3.1).</summary>
    public Dictionary<string, byte[]> Playlists { get; } = new(StringComparer.Ordinal);

    public int PlaylistBytes { get; set; }
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
/// A copy of a title as the catalog sends it to a peer (cleaned, best first,
/// its ID the version ID), with the host's handle of it.
/// </summary>
internal sealed record OfferedCopy
{
    public string Handle { get; init; } = "";

    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Etag { get; init; } = "";

    public long Size { get; init; }

    public int Parts { get; init; } = 1;

    public int Bitrate { get; init; }

    public long DurationMs { get; init; }

    public long? StartMs { get; init; }

    public bool Previews { get; init; }

    public string Lyrics { get; init; } = "";

    public IReadOnlyList<CopyAudio> Audio { get; init; } = [];

    public IReadOnlyList<CopySubtitle> Subtitles { get; init; } = [];

    /// <summary>What the choices of playback section 2.6 need of it.</summary>
    public PlayCopy PlayCopy() => new()
    {
        Id = Id, Parts = Parts, BitrateKbps = Bitrate,
        Audio = Audio.Select(a => new PlayCopyAudio(a.Id, a.Language ?? "", a.Role ?? "", a.Default)).ToList(),
        Subtitles = Subtitles.Select(s => s.Id).ToList(),
    };
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
    // 3.7: best first), and the host's libraries holding it among those
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
                    output.Add(new OfferedCopy
                    {
                        Handle = handles.GetValueOrDefault(c.Id, ""), Id = c.Id, Name = c.Name ?? "", Etag = c.ETag, Size = c.Size,
                        Parts = Math.Max(c.Parts.Count, 1), Bitrate = (int)Math.Min(c.BitrateKbps ?? 0, int.MaxValue), DurationMs = c.DurationMs,
                        StartMs = c.StartMs, Previews = c.Previews, Audio = c.AudioTracks, Subtitles = c.SubtitleTracks,
                    });
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
                    output.Add(new OfferedCopy
                    {
                        Handle = mhandles.GetValueOrDefault(c.Id, ""), Id = c.Id, Name = c.Name, Etag = c.Etag, Size = c.Size, Parts = 1,
                        Bitrate = c.BitrateKbps, DurationMs = c.DurationMs, Lyrics = c.Lyrics,
                        Audio = [new CopyAudio("a0", c.Codec) { Channels = c.Channels == 0 ? null : c.Channels, Default = true }],
                    });
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

    // Whether a tag matches the one asked: exactly (compared without case)
    // when exact, else by base language.
    private static bool SameLanguage(string? tag, string? asked, bool exact)
    {
        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(asked))
        {
            return false;
        }

        if (exact)
        {
            return string.Equals(tag, asked, StringComparison.OrdinalIgnoreCase);
        }

        static string Base(string s) => s.Split('-', 2)[0].ToLowerInvariant();
        return Base(tag) == Base(asked);
    }

    // Picks the subtitles shown (video section 5.4): none for "off"; the
    // track asked, if the copy has it; else the first in the language (same
    // tag then same base language, forced ones last); else none.
    internal static string ChooseSubtitles(IReadOnlyList<CopySubtitle> tracks, string id, string language)
    {
        if (id == "off")
        {
            return "";
        }

        if (tracks.Any(t => t.Id == id))
        {
            return id;
        }

        foreach (var forced in new[] { false, true })
        {
            foreach (var exact in new[] { true, false })
            {
                foreach (var t in tracks)
                {
                    if (t.Forced == forced && SameLanguage(t.Language, language, exact))
                    {
                        return t.Id;
                    }
                }
            }
        }

        return "";
    }

    // The forced subtitle track in the language of the audio played, when
    // the copy has one (video section 5.4).
    internal static string ForcedFor(IReadOnlyList<CopySubtitle> tracks, string? audio)
    {
        foreach (var exact in new[] { true, false })
        {
            foreach (var t in tracks)
            {
                if (t.Forced && SameLanguage(t.Language, audio, exact))
                {
                    return t.Id;
                }
            }
        }

        return "";
    }

    // How each subtitle format is served apart (video section 5.4).
    internal static readonly string[] TextFormats = ["subrip", "webvtt", "mov_text", "ttml", "ass", "ssa"];
    internal static readonly string[] AssFormats = ["ass", "ssa"];
    internal static readonly string[] PictureFormats = ["pgs", "vobsub", "dvb"];

    // How a subtitle track of format is served apart, unless the host says it can't.
    private static SubtitleServed ServedAs(string format, bool notApart) => notApart
        ? default
        : new SubtitleServed(TextFormats.Contains(format), AssFormats.Contains(format), PictureFormats.Contains(format));

    // A copy's previews' layout, in MSFP's bounds; null without, or out of them.
    private static Protocol.Trickplay? TrickplayOf(Trickplay? tp)
    {
        if (tp is null)
        {
            return null;
        }

        var t = new Protocol.Trickplay(tp.TileWidth, tp.TileHeight, tp.Columns, tp.Rows, tp.IntervalMs, tp.TileCount, 0);
        t = t with { Sheets = t.SheetCount };
        return t.InBounds ? t : null;
    }

    // Writes the trickplay object of a layout, its sheets under base.
    private static void WriteSheets(JsonOut o, Protocol.Trickplay t, string streamBase) =>
        o.Integer("tileWidth", t.TileWidth).Integer("tileHeight", t.TileHeight).Integer("columns", t.Columns).Integer("rows", t.Rows)
            .Integer("intervalMs", t.IntervalMs).Integer("tileCount", t.TileCount)
            .Strings("sheets", Enumerable.Range(0, t.Sheets).Select(n => Playback.SheetUrl(streamBase, n)));

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

    private static void WriteStreamInfo(JsonOut w, StreamInfo s) =>
        w.String("codec", Nonempty(s.Codec)).String("profile", Nonempty(s.Profile)).Integer("width", NonZero(s.Width))
            .Integer("height", NonZero(s.Height)).Integer("bitDepth", NonZero(s.BitDepth)).String("range", Nonempty(s.Range))
            .Integer("bitrateKbps", NonZero(s.BitrateKbps)).Integer("channels", NonZero(s.Channels)).String("language", Nonempty(s.Language));

    private static void WriteInfo(JsonOut w, PlaybackInfo i) =>
        w.Strings("reasons", i.Reasons is { Count: > 0 } ? i.Reasons : null).String("container", Nonempty(i.Container))
            .Integer("bitrateKbps", NonZero(i.BitrateKbps))
            .Object("video", i.Video is { } v ? o => WriteStreamInfo(o, v) : null)
            .Object("audio", i.Audio is { } a ? o => WriteStreamInfo(o, a) : null);

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
        IReadOnlyList<string> queue = [];
        if (audioOnly && !Music.TryCheckQueue(@in.Title, members["queue"], out queue))
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (!video)
        {
            // Members of another content part are ignored (playback section 2.4).
            @in = @in with
            {
                SubtitleLanguage = "", SubtitleTrack = "",
                Transcode = @in.Transcode is { } tt ? tt with { BurnSubtitle = "" } : null,
            };
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

        // 4. The copy, then malformed track IDs refused.
        var chosen = copies[Playback.ChooseCopy(copies.Select(c => c.PlayCopy()).ToList(), @in.VersionId, @in.AudioLanguage)];
        if (!@in.TrackIdsOk)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        // 5. The purpose's limits all in use: refused before any work (the
        // count that matters is Admit's).
        if (!PoolRoom(p, @in.Purpose, audioOnly).Room)
        {
            return Answer.Refuse(ProblemType.StreamLimit);
        }

        // 6 and 7. The bitrate cap, and the deliveries MSFP itself refuses.
        var kbps = @in.BitrateCap(_cfg.MaxBitrateKbps, Generous(p).BitrateKbps);
        if (Playback.RefuseDelivery(@in, chosen.PlayCopy(), kbps) is { } refused)
        {
            return RefusalOf(refused);
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

        var audio = Playback.ChooseAudio(chosen.PlayCopy().Audio, @in.AudioTrack, @in.AudioLanguage);
        string shown = "", forced = "", burned = "";
        if (video)
        {
            shown = ChooseSubtitles(chosen.Subtitles, @in.SubtitleTrack, @in.SubtitleLanguage);
            forced = ForcedFor(chosen.Subtitles, chosen.Audio.FirstOrDefault(a => a.Id == audio)?.Language);
            if (@in.Transcode is { BurnSubtitle.Length: > 0 } t)
            {
                shown = burned = t.BurnSubtitle;
            }
        }

        var start = @in.StartPositionMs;
        if (@in.Purpose != Playback.PurposePlay)
        {
            start = 0;
        }
        else if (start == 0 && video && chosen.StartMs is { } episodeStart)
        {
            start = episodeStart;
        }

        // The queue: each entry's first copy; one not offered keeps its place
        // empty (music section 5.2).
        var queued = new List<QueuedCopy>();
        var queueCopies = new Dictionary<int, OfferedCopy>();
        for (var n = 0; n < queue.Count; n++)
        {
            List<OfferedCopy> qc;
            try
            {
                (qc, _) = await OfferedCopiesAsync(p, queue[n], ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "msfp: the host's title couldn't be read for a play's queue");
                return Answer.Refuse(ProblemType.Internal);
            }

            if (qc.Count > 0)
            {
                queued.Add(new QueuedCopy(n, queue[n], qc[0].Handle));
                queueCopies[n] = qc[0];
            }
        }

        // 8. The host starts it, admitted by the pool's limits first.
        CountSlot? slot = null;
        var purpose = @in.Purpose;
        var sr = new StartRequest
        {
            Copy = chosen.Handle, Title = @in.Title, AudioOnly = audioOnly, Purpose = purpose, Delivery = @in.Delivery,
            Container = @in.Container, Transcode = @in.Transcode, AudioRenditions = @in.AudioRenditions, Parts = chosen.Parts,
            Peer = new PeerRef(p.Id, p.Name), AudioTrack = audio, SubtitleTrack = shown, StartMs = start,
            Capabilities = new PlayCapabilities(), MaxBitrateKbps = kbps, Queue = queued, // P17: no capabilities in play now
            Admit = method =>
            {
                if (slot is not null)
                {
                    throw new MsfpException(MsfpError.Internal, "admit called twice");
                }

                slot = AdmitPools(p, purpose, audioOnly, method);
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

        if (StartedBreaks(@in, chosen, started, queueCopies) is { } why)
        {
            _log.LogWarning("msfp: the host started a playback MSFP doesn't allow; it is stopped: {Why}", why);
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
                slot = AdmitPools(p, purpose, audioOnly, started.Method);
            }
            catch (MsfpException e)
            {
                HostStop(started.Handle, EndReason.Stopped);
                return HostRefusal(e, "admit");
            }
        }

        var (pb, ticket) = Register(p, @in, queue, libs, chosen, info, started, slot, queueCopies, audioOnly);
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

        // 9. The answer.
        var root = StreamBase(req);
        var streamBase = root + ticket + "/";
        var hls = started.Protocol == "hls";
        var cleanedInfo = CleanInfo(started.Info);
        var duration = chosen.DurationMs > 0 && chosen.DurationMs <= Playback.MaxPositionMs ? chosen.DurationMs : 0;
        var videoOut = video && purpose != Playback.PurposeScan;
        var subtitles = videoOut ? chosen.Subtitles : [];
        var forcedTracks = subtitles.Where(t => t.Forced).ToList();
        void Subtitle(JsonOut w, CopySubtitle t)
        {
            Copies.WriteSubtitle(w, t);
            var how = pb.Subtitles.GetValueOrDefault(t.Id);
            w.String("url", how.Text || how.Pictures ? Playback.SubtitleUrl(streamBase, t.Id, how.Pictures) : null)
                .String("assUrl", how.Ass ? streamBase + "ass/" + t.Id : null);
        }

        var tp = videoOut && pb.Sheets > 0 ? TrickplayOf(info.Trickplay) : null;
        var entries = new List<QueueEntry>();
        foreach (var e in started.Queue ?? [])
        {
            var qc = queueCopies[e.Place];
            var mime = e.Protocol == "hls" ? Playback.HlsType : Playback.RelayedMediaType(e.MimeType);
            entries.Add(new QueueEntry(queue[e.Place], qc.Id, e.Method, new QueueStream(Music.QueueUrl(root, ticket, e.Place, e.Protocol), e.Protocol, mime),
                qc.DurationMs > 0 && qc.DurationMs <= Music.MaxDurationMs ? qc.DurationMs : null));
        }

        entries.Sort((a, b) => queue.ToList().IndexOf(a.Title).CompareTo(queue.ToList().IndexOf(b.Title)));
        var fonts = videoOut ? Enumerable.Range(0, pb.Fonts).Select(n => streamBase + "fonts/" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList() : [];
        var output = JsonOut.Object(o => o.String("id", pb.Id).String("method", started.Method)
            .Object("stream", s => s.String("url", streamBase + (hls ? "master.m3u8" : "file")).String("protocol", hls ? "hls" : "file")
                .String("mimeType", hls ? Playback.HlsType : Playback.RelayedMediaType(started.MimeType)))
            .String("etag", chosen.Etag)
            .Integer("positionMs", started.PositionMs).Integer("durationMs", NonZero(duration))
            .Objects("versions", copies, (w, c) => w.String("id", c.Id).String("name", Nonempty(c.Name)))
            .String("versionId", chosen.Id)
            .Objects("audioTracks", chosen.Audio.Count > 0 ? chosen.Audio : null, Copies.WriteAudio)
            .String("audioTrack", Nonempty(audio)).Integer("maxBitrateKbps", purpose == Playback.PurposePlay ? kbps : null)
            .Object("info", cleanedInfo is null ? null : w => WriteInfo(w, cleanedInfo))
            .Objects("subtitleTracks", subtitles.Count > 0 ? subtitles : null, Subtitle)
            .String("subtitleTrack", videoOut ? Nonempty(shown) : null).String("burnedTrack", videoOut ? Nonempty(burned) : null)
            .Objects("forcedTracks", forcedTracks.Count > 0 ? forcedTracks : null, Subtitle)
            .String("forcedTrack", videoOut ? Nonempty(forced) : null)
            .Integer("subtitleStretchSeconds", subtitles.Count > 0 ? WebVtt.StretchSeconds : null)
            .Strings("fonts", fonts.Count > 0 ? fonts : null)
            .Object("trickplay", tp is { } sheets ? w => WriteSheets(w, sheets, streamBase) : null)
            .Objects("queue", entries.Count > 0 ? entries : null, (w, e) => e.Write(w)));
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
    // protocol MSFP doesn't have, another delivery than the one asked (file
    // is direct as file; remux and transcode are hls), file of a copy of
    // several parts, file played otherwise than direct; a queue entry of a
    // place not asked, twice, or served against the same rules.
    private static string? StartedBreaks(Protocol.PlayRequest @in, OfferedCopy c, Started s, Dictionary<int, OfferedCopy> asked)
    {
        string? Check(string method, string protocol, int parts)
        {
            if (method is not ("direct" or "remux" or "transcode") || protocol is not ("hls" or "file"))
            {
                return "a method or protocol MSFP doesn't have";
            }

            if (@in.Delivery.Length > 0 && method != MethodOf(@in.Delivery))
            {
                return "another method than the delivery asked";
            }

            if ((@in.Delivery == Playback.DeliveryFile && protocol != "file") ||
                (@in.Delivery.Length > 0 && @in.Delivery != Playback.DeliveryFile && protocol == "file"))
            {
                return "another protocol than the delivery asked";
            }

            if (protocol == "file" && method != "direct")
            {
                return "a file that isn't played as it is";
            }

            return protocol == "file" && parts > 1 ? "a copy of several parts as file" : null;
        }

        if (Check(s.Method, s.Protocol, c.Parts) is { } why)
        {
            return why;
        }

        var seen = new HashSet<int>();
        foreach (var q in s.Queue ?? [])
        {
            if (!asked.ContainsKey(q.Place) || !seen.Add(q.Place))
            {
                return "a queue entry of no place asked";
            }

            if (Check(q.Method, q.Protocol, 1) is { } qwhy)
            {
                return "a queue entry: " + qwhy;
            }
        }

        return null;
    }

    // Makes a started playback live: its ID and its ticket, 128 bits and 256
    // bits of randomness, the ticket kept by its hash only (media section 1).
    // It gives the ticket's text, for the URLs only.
    private (LivePlayback? Playback, string Ticket) Register(PeerRecord p, Protocol.PlayRequest @in, IReadOnlyList<string> queue, List<string> libs,
        OfferedCopy c, CopyInfo info, Started s, CountSlot slot, Dictionary<int, OfferedCopy> queueCopies, bool audioOnly)
    {
        var id = NewId("play_", 16);
        var ticket = Base64UrlText.Encode(RandomBytes(32));
        var now = Now();
        var pb = new LivePlayback
        {
            Id = id, Peer = p.Id, PeerName = p.Name, Title = @in.Title, Queue = queue, Libraries = libs, Handle = s.Handle, Method = s.Method,
            Purpose = @in.Purpose, AudioOnly = audioOnly, Ticket = SHA256.HashData(Encoding.UTF8.GetBytes(ticket)), Started = now,
            Used = now, Slot = slot,
        };
        pb.Entries[0] = new EntryServed(s.Protocol, c.Size, c.Etag);
        if (@in.Purpose == Playback.PurposeDownload && slot.Limits.DownloadKbps > 0)
        {
            pb.Pace = new Pacer(slot.Limits.DownloadKbps, _time);
        }

        if (!audioOnly)
        {
            pb.Audio.UnionWith(c.Audio.Select(a => a.Id));
        }

        if (!audioOnly && @in.Purpose != Playback.PurposeScan)
        {
            // Sheets are re-encoded before they leave: without a SheetEncoder,
            // none are offered.
            if (TrickplayOf(info.Trickplay) is { } t && c.Previews && @in.Purpose == Playback.PurposePlay && _cfg.SheetEncoder is not null)
            {
                pb.Sheets = t.Sheets;
                pb.SheetWidth = t.Columns * t.TileWidth;
                pb.SheetHeight = t.Rows * t.TileHeight;
            }

            var hasAss = false;
            foreach (var st in c.Subtitles)
            {
                pb.Subtitles[st.Id] = ServedAs(st.Format, (info.NotApart ?? []).Contains(st.Id));
                hasAss |= AssFormats.Contains(st.Format);
            }

            if (hasAss && info.Fonts is > 0 and <= 8)
            {
                pb.Fonts = info.Fonts;
            }
        }

        foreach (var q in s.Queue ?? [])
        {
            var qc = queueCopies[q.Place];
            pb.Entries[q.Place + 1] = new EntryServed(q.Protocol, qc.Size, qc.Etag);
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

    // The host's information made plain text, short.
    private static PlaybackInfo? CleanInfo(PlaybackInfo? i)
    {
        if (i is null)
        {
            return null;
        }

        static StreamInfo? Clean(StreamInfo? s)
        {
            if (s is null)
            {
                return null;
            }

            Languages.TrySentTag(s.Language ?? "", out var tag);
            return s with
            {
                Codec = Music.CleanText(s.Codec ?? "", 32), Profile = Music.CleanText(s.Profile ?? "", 32),
                Range = Music.CleanText(s.Range ?? "", 32), Language = tag,
            };
        }

        var reasons = (i.Reasons ?? []).Select(r => Music.CleanText(r ?? "", 64)).Where(r => r.Length > 0).Take(16).ToList();
        return new PlaybackInfo
        {
            Container = Music.CleanText(i.Container ?? "", 32), BitrateKbps = Math.Max(i.BitrateKbps, 0),
            Reasons = reasons.Count > 0 ? reasons : null, Video = Clean(i.Video), Audio = Clean(i.Audio),
        };
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
                Handle = handle, Title = outcome.Title, PositionMs = outcome.PositionMs, HasPosition = outcome.HasPosition, Paused = r.Paused,
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
