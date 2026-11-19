using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// A peer's playback here, for the host's live view: the peer (never a
/// person), the title played, how, since when, and whether it was used
/// since it started ("started" or "active"). Handle is the host's own
/// (<see cref="Started.Handle"/>), for a host that ends a playback from its
/// side (<see cref="MsfpEngine.StopPlayback"/> with ID and its reason).
/// </summary>
/// <param name="Id">The playback's ID.</param>
/// <param name="Peer">The peer's ID.</param>
/// <param name="PeerName">The peer's name.</param>
/// <param name="Title">The title's key.</param>
/// <param name="Method">direct, remux or transcode.</param>
/// <param name="Handle">The host's handle.</param>
/// <param name="Since">When it started.</param>
/// <param name="State">"started" or "active".</param>
public sealed record PlaybackView(string Id, string Peer, string PeerName, string Title, string Method, string Handle,
    DateTimeOffset Since, string State);

/// <summary>A peer's live playback here.</summary>
internal sealed class LivePlayback
{
    public string Id { get; init; } = "";

    public string Peer { get; init; } = "";

    public string PeerName { get; init; } = "";

    public string Title { get; init; } = "";

    public string Library { get; init; } = "";

    public string Handle { get; init; } = "";

    public string Method { get; init; } = "";

    public string Protocol { get; init; } = "";

    public bool AudioOnly { get; init; }

    public byte[] Ticket { get; init; } = [];

    public DateTimeOffset Started { get; init; }

    public int Sheets { get; set; }

    /// <summary>Track ID: pictures.</summary>
    public Dictionary<string, bool> Subtitles { get; } = new(StringComparer.Ordinal);

    public CountSlot Slot { get; init; } = null!;

    /// <summary>Cancelled when it ends: its open responses are cut.</summary>
    public CancellationTokenSource Ended { get; } = new();

    // Under the plays' lock.
    public DateTimeOffset Used { get; set; }

    public bool Active { get; set; }

    public int Open { get; set; }
}

/// <summary>
/// A stream counted for a pairing (Admit), and, when transcode, one of its
/// transcodes.
/// </summary>
internal sealed class CountSlot(Plays plays, string peer, bool audio, Limits limits) : ISlot
{
    public string Peer { get; } = peer;

    public bool Audio { get; } = audio;

    public Limits Limits { get; } = limits;

    public bool Transcode { get; set; }

    public bool Released { get; set; }

    public (int Streams, int Transcodes) Caps() =>
        Audio ? (Limits.AudioStreams, Limits.AudioTranscodes) : (Limits.Streams, Limits.Transcodes);

    /// <summary>Frees what was counted; once, however often it is called.</summary>
    public void Release()
    {
        lock (plays)
        {
            if (Released)
            {
                return;
            }

            Released = true;
            if (!plays.Counts.TryGetValue(Peer, out var c))
            {
                return;
            }

            c.Add(Audio, -1, Transcode ? -1 : 0);
            if (c.Empty)
            {
                plays.Counts.Remove(Peer);
            }
        }
    }

    /// <summary>
    /// Counts a remux that turned out to re-encode the video as a transcode
    /// from then on (playback section 6); false when the pairing has none
    /// left: the host then stops it.
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

            var (_, most) = Caps();
            var c = plays.CountsOf(Peer);
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

/// <summary>What a pairing uses of this server now.</summary>
internal sealed class UseCounts
{
    private int _streams, _transcodes, _audioStreams, _audioTranscodes;

    public bool Empty => _streams == 0 && _transcodes == 0 && _audioStreams == 0 && _audioTranscodes == 0;

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
}

/// <summary>The peers' playbacks here, live, and those ended lately; locked on itself.</summary>
internal sealed class Plays
{
    public Dictionary<string, LivePlayback> ById { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, LivePlayback> ByTicket { get; } = new(StringComparer.Ordinal);

    /// <summary>By peer, admitted slots included.</summary>
    public Dictionary<string, UseCounts> Counts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, (string Peer, EndReason Reason, DateTimeOffset At)> Ended { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset Expired { get; set; }

    public UseCounts CountsOf(string peer)
    {
        if (!Counts.TryGetValue(peer, out var c))
        {
            c = new UseCounts();
            Counts[peer] = c;
        }

        return c;
    }
}

// Playback, on the owning side (MSFP playback sections 2 to 6, video
// section 5, music section 5): play, progress and stop from peers, the
// choice of the copy and the tracks, the pairing's limits counted in one
// step with the host's start (Admit), the playbacks' states, tickets and
// endings. Playbacks live in memory: after a restart, a peer's progress
// gets 410 and it starts again.
public sealed partial class MsfpEngine
{
    // Bounds of play's members (playback section 2.1).
    internal const long MaxPositionMs = 8_640_000_000; // 100 days
    internal const long MaxKbps = 10_000_000;
    private const int DefaultKbps = 120_000;

    // Times of a playback (playback section 6.3): a playback with neither
    // progress nor a media request for 2 minutes ends; expired playbacks
    // are looked for every 30 seconds; why one ended is kept an hour; the
    // host's Stop and Progress made in the background get 30 seconds.
    internal static readonly TimeSpan ExpireAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ExpiryEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EndedKept = TimeSpan.FromHours(1);
    private static readonly TimeSpan HostTime = TimeSpan.FromSeconds(30);

    private readonly Plays _plays = new();

    // Counts a stream (and for "transcode" a transcode) for peer, in one
    // step no other play comes between (playback section 6.2).
    private CountSlot AdmitStream(string peer, Limits l, bool audio, string method)
    {
        if (method is not ("direct" or "remux" or "transcode"))
        {
            throw new MsfpException(MsfpError.Internal, "admit of a method MSFP doesn't have");
        }

        var s = new CountSlot(_plays, peer, audio && l.Audio, l);
        var (most, mostTranscodes) = s.Caps();
        lock (_plays)
        {
            var c = _plays.CountsOf(peer);
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

    // How many streams a pairing uses now, of one kind.
    private (int Used, int Most) InUse(string peer, Limits l, bool audio)
    {
        var counted = audio && l.Audio;
        var most = counted ? l.AudioStreams : l.Streams;
        lock (_plays)
        {
            return (_plays.Counts.TryGetValue(peer, out var c) ? c.Of(counted).Streams : 0, most);
        }
    }

    // The form of a language asked for: at most 12 characters of A-Z a-z 0-9 - (playback section 2.1).
    [GeneratedRegex("^[A-Za-z0-9-]{0,12}$")]
    private static partial Regex RequestTagRe();

    // The values capabilities may hold (playback section 2.1, video section
    // 5.1); others are ignored.
    internal static readonly IReadOnlyDictionary<string, string[]> CapabilityValues = new Dictionary<string, string[]>
    {
        ["containers"] = ["mp4", "m4v", "mov", "webm", "mkv", "ts", "mp3", "aac", "m4a", "flac", "ogg", "opus", "wav"],
        ["audioCodecs"] = ["aac", "mp3", "opus", "vorbis", "flac", "ac3", "eac3", "alac", "pcm_s16le"],
        ["videoCodecs"] = ["h264", "hevc", "av1", "vp8", "vp9"],
        ["hdr"] = ["hdr10", "hlg"],
        ["subtitles"] = [],
    };

    // Reads play's capabilities as an owning server does: null (400) when it
    // isn't an object, or a list isn't a list of strings, or a number isn't
    // an integer in range; values MSFP doesn't list ignored, each once, sorted.
    private static Capabilities? ReadCapabilities(JsonNode? raw)
    {
        if (raw is null)
        {
            return new Capabilities();
        }

        if (raw is not JsonObject c)
        {
            return null;
        }

        List<string>? List(string name)
        {
            if (!c.TryGetPropertyValue(name, out var v) || v is null)
            {
                return [];
            }

            if (v is not JsonArray a)
            {
                throw BadJson();
            }

            var output = new List<string>();
            foreach (var e in a)
            {
                var s = JsonMembers.AsString(e) ?? throw BadJson();
                if (CapabilityValues[name].Contains(s) && !output.Contains(s))
                {
                    output.Add(s);
                }
            }

            output.Sort(StringComparer.Ordinal);
            return output.Count > 0 ? output : null;
        }

        try
        {
            var output = new Capabilities
            {
                Containers = List("containers"), AudioCodecs = List("audioCodecs"), VideoCodecs = List("videoCodecs"),
                Hdr = List("hdr"), Subtitles = List("subtitles"),
            };
            var channels = c.GetInteger("maxAudioChannels") ?? 0;
            var height = c.GetInteger("maxHeight") ?? 0;
            var kbps = c.GetInteger("maxBitrateKbps") ?? 0;
            if (channels is < 0 or > 64 || height is < 0 or > 100_000 || kbps is < 0 or > MaxKbps)
            {
                return null;
            }

            output.MaxAudioChannels = (int)channels;
            output.MaxHeight = (int)height;
            output.MaxBitrateKbps = (int)kbps;
            return output;
        }
        catch (ProtocolException)
        {
            return null;
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

    // Picks the copy versionId names, else the first whose audio has the
    // language (same tag, then same base language), else the first
    // (playback section 2.6).
    internal static int ChooseCopy(IReadOnlyList<Copy> copies, Func<Copy, string> version, string versionId, string language)
    {
        if (versionId.Length > 0)
        {
            for (var i = 0; i < copies.Count; i++)
            {
                if (version(copies[i]) == versionId)
                {
                    return i;
                }
            }
        }

        foreach (var exact in new[] { true, false })
        {
            for (var i = 0; i < copies.Count; i++)
            {
                if (Languages.SentTags(copies[i].Audio ?? []).Any(a => SameLanguage(a, language, exact)))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    // Picks the audio track asked, if the copy has it; else the first of the
    // title's own sound (no commentary, no description) in the language,
    // same tag then same base language; else the track marked default, else
    // the first (playback section 2.6).
    internal static string ChooseAudio(IReadOnlyList<Track> tracks, string id, string language)
    {
        if (tracks.Any(t => t.Id == id))
        {
            return id;
        }

        foreach (var exact in new[] { true, false })
        {
            foreach (var t in tracks)
            {
                if (string.IsNullOrEmpty(t.Role) && SameLanguage(t.Language, language, exact))
                {
                    return t.Id;
                }
            }
        }

        return tracks.FirstOrDefault(t => t.Default)?.Id ?? tracks.FirstOrDefault()?.Id ?? "";
    }

    // Picks the subtitles shown (video section 5.4): none for "off"; the
    // track asked, if the copy has it; else the first in the language (same
    // tag then same base language, forced ones last); else none.
    internal static string ChooseSubtitles(IReadOnlyList<Track> tracks, string id, string language)
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
    internal static string ForcedFor(IReadOnlyList<Track> tracks, string? audio)
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

    // Keeps of the host's tracks those MSFP can send: an ID of the kind's
    // form ("a<n>", "s<n>"), once; a label, cleaned. A role other than
    // commentary or description is none.
    internal static List<Track> CleanTracks(IEnumerable<Track>? tracks, char kind)
    {
        var output = new List<Track>();
        foreach (var track in tracks ?? [])
        {
            if (!Playback.TryTrackId(track.Id, kind, out _) || output.Any(o => o.Id == track.Id))
            {
                continue;
            }

            var t = track with { Label = Music.CleanText(track.Label ?? "", 64) };
            if (t.Label.Length == 0)
            {
                continue;
            }

            Languages.TrySentTag(t.Language ?? "", out var tag);
            t.Language = tag.Length > 0 ? tag : null;
            if (t.Role is not ("commentary" or "description"))
            {
                t.Role = null;
            }

            if (t.Channels is < 0 or > 64)
            {
                t.Channels = 0;
            }

            if (kind == 'a')
            {
                t.Pictures = false;
            }

            output.Add(t);
        }

        return output;
    }

    // The skip markers MSFP has (video section 5.3).
    private static readonly string[] SegmentKinds = ["intro", "outro", "recap", "preview", "commercial"];

    // Keeps skip markers of a known kind within 100 days, 32 at most; an end
    // of 0 is durationMs (the host's markers; a peer's are given 0, and
    // such a marker is dropped).
    internal static List<Segment> CleanSegments(IEnumerable<Segment>? segments, long durationMs)
    {
        var output = new List<Segment>();
        foreach (var segment in segments ?? [])
        {
            // An end the host doesn't know: the copy's end (video section 5.3).
            var s = segment.EndMs == 0 ? segment with { EndMs = durationMs } : segment;
            if (output.Count < 32 && SegmentKinds.Contains(s.Kind) && s.StartMs >= 0 && s.EndMs > s.StartMs && s.EndMs <= MaxPositionMs)
            {
                output.Add(s);
            }
        }

        return output;
    }

    internal static void WriteTrack(JsonOut w, Track t) =>
        w.String("id", t.Id).String("label", t.Label).String("language", string.IsNullOrEmpty(t.Language) ? null : t.Language)
            .Integer("channels", t.Channels != 0 ? t.Channels : null).String("role", string.IsNullOrEmpty(t.Role) ? null : t.Role)
            .Bool("default", t.Default ? true : null).Bool("forced", t.Forced ? true : null)
            .Bool("hearingImpaired", t.HearingImpaired ? true : null).Bool("pictures", t.Pictures ? true : null);

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

        JsonObject body;
        string? title;
        long start, kbpsAsked;
        string audioLanguage, audioTrack, versionId, subtitleLanguage, subtitleTrack;
        try
        {
            body = StrictJson.ReadRequest(req.Body,
                ["title", "capabilities", "startPositionMs", "maxBitrateKbps", "audioLanguage", "audioTrack", "versionId", "subtitleLanguage", "subtitleTrack"],
                lenient: ["capabilities"]);
            title = body.GetString("title");
            start = body.GetInteger("startPositionMs") ?? 0;
            kbpsAsked = body.GetInteger("maxBitrateKbps") ?? 0;
            audioLanguage = body.GetString("audioLanguage") ?? "";
            audioTrack = body.GetString("audioTrack") ?? "";
            versionId = body.GetString("versionId") ?? "";
            subtitleLanguage = body.GetString("subtitleLanguage") ?? "";
            subtitleTrack = body.GetString("subtitleTrack") ?? "";
        }
        catch (ProtocolException)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (title is null)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        // 1. A key play takes, of a content part on; numbers and tags in range.
        var keyOn = KeyOn(p, title, out var k);
        var caps = ReadCapabilities(body["capabilities"]);
        if (!keyOn || !k.Playable || caps is null || start is < 0 or > MaxPositionMs || kbpsAsked is < 0 or > MaxKbps ||
            !RequestTagRe().IsMatch(audioLanguage))
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        var video = k.Part == Versions.PartVideo;
        if (video && !RequestTagRe().IsMatch(subtitleLanguage))
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        var audioOnly = k.Part == Versions.PartMusic;
        // 2. No stream at all for this pairing.
        if (InUse(p.Id, p.Limits, audioOnly).Most == 0)
        {
            return Answer.Refuse(ProblemType.StreamLimit);
        }

        // 3. Copies of that key in the libraries offered to it.
        var offers = p.Offers ?? [];
        if (_cfg.Catalog is null || _cfg.Playback is null || offers.Count == 0)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        List<Copy> copies;
        try
        {
            copies = [.. await _cfg.Catalog.CopiesAsync(title, offers, ct).ConfigureAwait(false)];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "msfp: the host's copies couldn't be read");
            return Answer.Refuse(ProblemType.Internal);
        }

        copies.RemoveAll(c => c.Library.Length > 0 && !offers.Contains(c.Library));
        if (copies.Count == 0)
        {
            return Answer.Refuse(ProblemType.NotFound);
        }

        // 4. The copy, then malformed track IDs refused.
        string Version(Copy c) => VersionId(p.Id, c.Handle);
        var chosen = copies[ChooseCopy(copies, Version, versionId, audioLanguage)];
        if (audioTrack.Length > 0 && !Playback.TryTrackId(audioTrack, 'a', out _))
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (video && subtitleTrack.Length > 0 && subtitleTrack != "off" && !Playback.TryTrackId(subtitleTrack, 's', out _))
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        // 5. The pairing's streams all in use: refused before any work (the
        // count that matters is Admit's).
        var (used, most) = InUse(p.Id, p.Limits, audioOnly);
        if (used >= most)
        {
            return Answer.Refuse(ProblemType.StreamLimit);
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

        var audioTracks = CleanTracks(info.AudioTracks, 'a');
        var subtitles = video ? CleanTracks(info.SubtitleTracks, 's') : [];
        var audio = ChooseAudio(audioTracks, audioTrack, audioLanguage);
        var shown = "";
        var forced = "";
        if (video)
        {
            shown = ChooseSubtitles(subtitles, subtitleTrack, subtitleLanguage);
            forced = ForcedFor(subtitles, audioTracks.FirstOrDefault(t => t.Id == audio)?.Language);
        }

        // 6. The bitrate cap: the lowest of the four, 120,000 without one.
        var kbps = 0;
        foreach (var c in new[] { (int)kbpsAsked, caps.MaxBitrateKbps, _cfg.MaxBitrateKbps, p.Limits.BitrateKbps })
        {
            if (c > 0 && (kbps == 0 || c < kbps))
            {
                kbps = c;
            }
        }

        if (kbps == 0)
        {
            kbps = DefaultKbps;
        }

        if (start == 0 && video && info.EpisodeStartMs > 0)
        {
            start = info.EpisodeStartMs;
        }

        // 7. The host starts it, admitted by the pairing's limits first.
        CountSlot? slot = null;
        var sr = new StartRequest
        {
            Copy = chosen.Handle, Title = title, AudioOnly = audioOnly, Parts = Math.Max(info.Parts, 1), Peer = new PeerRef(p.Id, p.Name),
            AudioTrack = audio, SubtitleTrack = shown, StartMs = start, Capabilities = caps, MaxBitrateKbps = kbps,
            Admit = method =>
            {
                if (slot is not null)
                {
                    throw new MsfpException(MsfpError.Internal, "admit called twice");
                }

                slot = AdmitStream(p.Id, p.Limits, audioOnly, method);
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

        if (slot is null)
        {
            // A host that didn't ask: admitted now, its playback stopped when
            // the limits refuse it.
            try
            {
                slot = AdmitStream(p.Id, p.Limits, audioOnly, started.Method);
            }
            catch (MsfpException e)
            {
                HostStop(started.Handle, EndReason.Stopped);
                return HostRefusal(e, "admit");
            }
        }

        var (pb, ticket) = Register(p, title, chosen, info, started, slot, audioOnly);
        if (pb is null)
        {
            return Answer.Refuse(ProblemType.Internal);
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

            if (chosen.Library.Length > 0 && q.Offers?.Contains(chosen.Library) != true)
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

        // 8. The answer.
        var duration = info.DurationMs > 0 && info.DurationMs <= MaxPositionMs ? info.DurationMs : 0;
        var streamBase = StreamBase(req) + ticket + "/";
        var hls = started.Protocol == "hls";
        var versions = copies.Select(c => (c, Name: Music.CleanText(c.Name ?? "", 64))).Where(x => x.Name.Length > 0)
            .Select(x => new Version(Version(x.c), x.Name)).ToList();
        var cleanedInfo = CleanInfo(started.Info);
        var segments = video ? CleanSegments(started.Segments, duration) : [];
        var forcedTracks = subtitles.Where(t => t.Forced).ToList();
        void Subtitle(JsonOut w, Track t)
        {
            WriteTrack(w, t);
            w.String("url", Playback.SubtitleUrl(streamBase, t.Id, t.Pictures));
        }

        Action<JsonOut>? trickplay = null;
        if (video && pb.Sheets > 0)
        {
            var tp = info.Trickplay!;
            trickplay = o => o.Integer("tileWidth", tp.TileWidth).Integer("tileHeight", tp.TileHeight).Integer("columns", tp.Columns)
                .Integer("rows", tp.Rows).Integer("intervalMs", tp.IntervalMs).Integer("tileCount", tp.TileCount)
                .Strings("sheets", Enumerable.Range(0, pb.Sheets).Select(n => Playback.SheetUrl(streamBase, n)));
        }

        var output = JsonOut.Object(o => o.String("id", pb.Id).String("method", started.Method)
            .Object("stream", s => s.String("url", streamBase + (hls ? "master.m3u8" : "file")).String("protocol", hls ? "hls" : "file")
                .String("mimeType", hls ? "application/vnd.apple.mpegurl" : Playback.RelayedMediaType(started.MimeType)))
            .Integer("positionMs", started.PositionMs).Integer("durationMs", NonZero(duration))
            .Objects("versions", versions.Count > 0 ? versions : null, (w, v) => w.String("id", v.Id).String("name", v.Name))
            .String("versionId", Version(chosen))
            .Objects("audioTracks", audioTracks.Count > 0 ? audioTracks : null, WriteTrack)
            .String("audioTrack", Nonempty(audio)).Integer("maxBitrateKbps", NonZero(kbps))
            .Object("info", cleanedInfo is null ? null : w => WriteInfo(w, cleanedInfo))
            .Objects("subtitleTracks", subtitles.Count > 0 ? subtitles : null, Subtitle).String("subtitleTrack", Nonempty(shown))
            .Objects("forcedTracks", forcedTracks.Count > 0 ? forcedTracks : null, Subtitle).String("forcedTrack", Nonempty(forced))
            .Integer("subtitleStretchSeconds", subtitles.Count > 0 ? WebVtt.StretchSeconds : null)
            .Objects("segments", segments.Count > 0 ? segments : null, (w, s) => w.String("kind", s.Kind).Integer("startMs", s.StartMs).Integer("endMs", s.EndMs))
            .Object("trickplay", trickplay));
        _log.LogInformation("msfp: a peer's playback started: {Peer} {Playback} {Method}", p.Id, pb.Id, started.Method);
        Notify(new PlaybackStarted(p.Id, p.Name, pb.Id, pb.Title, pb.Method));
        return Answer.Ok(output);
    }

    // Makes a started playback live: its ID and its ticket, 128 bits and 256
    // bits of randomness, the ticket kept by its hash only (media section 1).
    private (LivePlayback? Playback, string Ticket) Register(PeerRecord p, string title, Copy c, CopyInfo info, Started s, CountSlot slot, bool audioOnly)
    {
        if (s.Method is not ("direct" or "remux" or "transcode") || s.Protocol is not ("hls" or "file"))
        {
            _log.LogWarning("msfp: the host started a playback with a method or protocol MSFP doesn't have");
            slot.Release();
            HostStop(s.Handle, EndReason.Stopped);
            return (null, "");
        }

        if (s.Protocol == "file" && info.Parts > 1)
        {
            // Media section 1: file is for a copy of one file only.
            _log.LogWarning("msfp: the host started a copy of several parts as file; such a copy is served as hls");
            slot.Release();
            HostStop(s.Handle, EndReason.Stopped);
            return (null, "");
        }

        var id = NewId("play_", 16);
        var ticket = Base64UrlText.Encode(RandomBytes(32));
        var now = Now();
        var pb = new LivePlayback
        {
            Id = id, Peer = p.Id, PeerName = p.Name, Title = title, Library = c.Library, Handle = s.Handle, Method = s.Method,
            Protocol = s.Protocol, AudioOnly = audioOnly, Ticket = SHA256.HashData(Encoding.UTF8.GetBytes(ticket)), Started = now,
            Used = now, Slot = slot,
        };
        if (info.Trickplay is { } tp && !audioOnly)
        {
            var t = new Protocol.Trickplay(tp.TileWidth, tp.TileHeight, tp.Columns, tp.Rows, tp.IntervalMs, tp.TileCount, 0);
            t = t with { Sheets = t.SheetCount };
            if (t.InBounds)
            {
                pb.Sheets = t.Sheets;
            }
        }

        if (!audioOnly)
        {
            foreach (var t in CleanTracks(info.SubtitleTracks, 's'))
            {
                pb.Subtitles[t.Id] = t.Pictures;
            }
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

    // A peer's live playback by its ID; null when it isn't live, or isn't that peer's.
    private LivePlayback? LiveOf(string peer, string id)
    {
        lock (_plays)
        {
            return _plays.ById.TryGetValue(id, out var pb) && pb.Peer == peer ? pb : null;
        }
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
            _plays.Ended[pb.Id] = (pb.Peer, why, Now());
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
    // minutes (an open response counts as use), and those longer than
    // LongestPlayback; forgets endings over an hour old.
    private void ExpirePlaybacks(bool force)
    {
        var now = Now();
        var expired = new List<LivePlayback>();
        var tooLong = new List<LivePlayback>();
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

                if (now - pb.Started >= _cfg.LongestPlayback)
                {
                    tooLong.Add(pb);
                }
                else if (now - pb.Used >= ExpireAfter)
                {
                    expired.Add(pb);
                }
            }

            foreach (var (id, ended) in _plays.Ended.ToList())
            {
                if (now - ended.At >= EndedKept)
                {
                    _plays.Ended.Remove(id);
                }
            }
        }

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

    // Reads progress's or stop's request: the playback's ID and a position.
    private static (string? Id, long Position, bool Paused) ReadPlayId(byte[] body, bool paused)
    {
        try
        {
            var o = StrictJson.ReadRequest(body, paused ? ["id", "positionMs", "paused"] : ["id", "positionMs"]);
            var id = o.GetString("id");
            var position = o.GetInteger("positionMs") ?? 0;
            var p = paused && (o.GetBool("paused") ?? false);
            if (id is null || !Playback.PlayId(id) || position is < 0 or > MaxPositionMs)
            {
                return (null, 0, false);
            }

            return (id, position, p);
        }
        catch (ProtocolException)
        {
            return (null, 0, false);
        }
    }

    // A peer telling that a playback is still watched (playback section 3).
    private async Task<Answer> ProgressAsync(CallRequest req, CancellationToken ct)
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

        var (id, position, paused) = ReadPlayId(req.Body, true);
        if (id is null)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (LiveOf(p.Id, id) is not { } pb)
        {
            return Answer.Refuse(ProblemType.Stopped);
        }

        Used(pb);
        try
        {
            await _cfg.Playback!.ProgressAsync(pb.Handle, position, paused, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogInformation(e, "msfp: the host's Progress failed");
        }

        return Answer.Ok("{}"u8.ToArray());
    }

    // A peer telling that a playback is over (playback section 4): 200
    // whether or not it was live; another pairing's changes nothing.
    private async Task<Answer> StopAsync(CallRequest req, CancellationToken ct)
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

        var (id, position, _) = ReadPlayId(req.Body, false);
        if (id is null)
        {
            return Answer.Refuse(ProblemType.Invalid);
        }

        if (LiveOf(p.Id, id) is { } pb)
        {
            if (position > 0)
            {
                try
                {
                    await _cfg.Playback!.ProgressAsync(pb.Handle, position, false, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogInformation(e, "msfp: the host's Progress failed");
                }
            }

            EndPlayback(pb, EndReason.Stopped);
        }

        return Answer.Ok("{}"u8.ToArray());
    }

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
                .Select(pb => new PlaybackView(pb.Id, pb.Peer, pb.PeerName, pb.Title, pb.Method, pb.Handle, pb.Started, pb.Active ? "active" : "started"))
                .OrderBy(v => v.Since).ThenBy(v => v.Id, StringComparer.Ordinal).ToList();
        }
    }

    // Whether a peer may still use a playback here: paired, not suspended
    // (media section 4). Read from storage at each media request, so an
    // ending noticed only then still stops the ticket.
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

        if (pb.Library.Length > 0 && p.Offers?.Contains(pb.Library) != true)
        {
            EndPlayback(pb, EndReason.NotOffered);
            return false;
        }

        return true;
    }
}
