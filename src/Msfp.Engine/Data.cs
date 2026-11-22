using System.Text.Json;
using System.Text.Json.Serialization;
using Msfp.Protocol;

namespace Msfp;

// The data objects of the host interfaces (engine.md section 7).

/// <summary>
/// One of the host's libraries: its own ID, its name and its kind
/// ("movies", "tvshows", "music", "mixed"…, MSFP catalog section 2).
/// </summary>
public sealed record Library
{
    /// <summary>The host's ID of the library.</summary>
    public string Id { get; set; } = "";

    /// <summary>Its name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Its kind.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Set in the answer of Offers: it is offered.</summary>
    public bool Offered { get; set; }
}

/// <summary>
/// One of a peer's libraries: its public ID, its name and kind, whether
/// this server wants it, and whether it was gone from the peer's latest
/// answer.
/// </summary>
public sealed record WantedLibrary
{
    /// <summary>Its public ID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Its name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Its kind.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>This server wants it.</summary>
    [JsonPropertyName("wanted"), OmitEmpty]
    public bool Wanted { get; set; }

    /// <summary>It was gone from the peer's latest answer.</summary>
    [JsonPropertyName("gone"), OmitEmpty]
    public bool Gone { get; set; }
}

/// <summary>
/// A title of a content part: a <see cref="VideoTitle"/> or a
/// <see cref="MusicTitle"/>, with the members every title has: its key, its
/// kind, and the libraries that hold it (the host's IDs from the Catalog;
/// for a peer's title, the one public ID it was listed in).
/// </summary>
public abstract record Title
{
    /// <summary>Its key.</summary>
    [JsonPropertyName("key"), JsonPropertyOrder(-3)]
    public string Key { get; set; } = "";

    /// <summary>Its kind.</summary>
    [JsonPropertyName("kind"), JsonPropertyOrder(-2)]
    public string Kind { get; set; } = "";

    /// <summary>The libraries that hold it.</summary>
    [JsonPropertyName("libraries"), JsonPropertyOrder(-1)]
    public List<string>? Libraries { get; set; }
}

/// <summary>
/// A film, a series, a season, an episode or an extra (MSFP video section
/// 3): the host's own, as its Catalog gives them, or a peer's, as the
/// engine kept it (video section 3.1). The engine cleans the host's before
/// they leave as a receiver would keep them, so that nothing leaves that
/// MSFP doesn't take.
/// </summary>
public sealed record VideoTitle : Title
{
    /// <summary>
    /// A film's, an episode's or an extra's copies, best first (MSFP video
    /// sections 3.2 to 3.8): what a viewer's server needs to list each as a
    /// file of its own before anyone plays it (its length, size, etag,
    /// container, streams and tracks, chapters, skip markers…). Required for
    /// those kinds: one without a copy left once cleaned isn't sent; series
    /// and seasons carry none.
    /// </summary>
    /// <remarks>
    /// Of the host's own titles, each copy's ID is the host's handle of it,
    /// the one <see cref="IPlayback"/> and <see cref="IMedia"/> are given: it
    /// never leaves; the engine sends instead the version ID play gives for
    /// it, and plays a copy only as it is described here. The engine sends the copies in the order of video section 3.7
    /// (<see cref="Protocol.Copies.Sorted"/>, keeping the host's order between
    /// equal ones), language tags as <see cref="Languages.TrySentTag"/> gives
    /// them, names and track titles only when they are labels (never a file
    /// name or a release group: video section 3.6). Its ETag changes whenever
    /// the copy's bytes change, and only then: a change of a copy is a change
    /// of the title (a changes reading sends it again). Of a peer's titles,
    /// the ID is the peer's version ID.
    /// </remarks>
    [JsonPropertyName("copies"), OmitEmpty]
    public List<Protocol.Copy>? Copies { get; set; }

    /// <summary>
    /// For an episode keyed by its TMDB episode ID
    /// (series/tmdb/&lt;id&gt;/e&lt;episode id&gt;) only, and required for it:
    /// its place in TMDB's numbering now (MSFP video sections 3 and 4.1).
    /// The host sets it for its own such episodes, which aren't sent without
    /// a valid one; a new place is a change of the title. A peer's is the one
    /// it sent, checked. Null for any other title (and ignored there).
    /// </summary>
    [JsonPropertyName("place"), OmitEmpty]
    public Place? Place { get; set; }

    /// <summary>The keys of the titles it goes with.</summary>
    [JsonPropertyName("with"), OmitEmpty]
    public List<string>? With { get; set; }

    /// <summary>Other identifiers, by scheme.</summary>
    [JsonPropertyName("ids"), OmitEmpty]
    public Dictionary<string, string>? Ids { get; set; }

    /// <summary>
    /// An extra's (kind "extra", keyed under its film, series or season:
    /// video section 3.9): trailer, featurette, behindthescenes,
    /// deletedscene, interview, scene, short or clip. Theme songs and theme
    /// videos are never extras.
    /// </summary>
    // P17: extraType is an open token now, themesong and themevideo included.
    [JsonPropertyName("extraType"), OmitEmpty]
    public string? ExtraType { get; set; }

    /// <summary>
    /// An extra's own short name (optional, at most 64 characters), or a
    /// local film's or series' name (required, at most 128: catalog section
    /// 4.3, video section 3.10). It is the owner's words, never a file name,
    /// text never markup; on other titles it isn't sent.
    /// </summary>
    [JsonPropertyName("name"), OmitEmpty]
    public string? Name { get; set; }

    /// <summary>A local film's or series' year (1800 to 9,999; 0 for none); ignored on other titles.</summary>
    [JsonPropertyName("year"), OmitEmpty]
    public int Year { get; set; }

    /// <summary>
    /// A local film or series whose one image the owner shares (the host's
    /// <see cref="IImages"/> gives it; a peer's is fetched with
    /// TitleImageAsync); ignored on other titles.
    /// </summary>
    [JsonPropertyName("image"), OmitEmpty]
    public bool Image { get; set; }

    /// <summary>
    /// The title's best copy: the first of its copies, which are best first
    /// (MSFP video section 3.7); null for a title without one (a series, a
    /// season). A viewer's server shows its picture as the title's.
    /// </summary>
    public Protocol.Copy? BestCopy() => Copies is { Count: > 0 } c ? c[0] : null;

    /// <summary>
    /// The languages of the title's audio tracks, over all its copies, each
    /// once, in the order of its copies and their tracks: what a viewer's
    /// server shows as the title's audio languages (MSFP video section 3.2: a
    /// title carries no summary of them).
    /// </summary>
    public IReadOnlyList<string> AudioLanguages() =>
        (Copies ?? []).SelectMany(c => c.AudioTracks ?? []).Select(a => a.Language)
            .Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.Ordinal).ToList()!;

    /// <summary>
    /// The languages of the title's subtitle tracks, over all its copies,
    /// each once, in the order of its copies and their tracks.
    /// </summary>
    public IReadOnlyList<string> SubtitleLanguages() =>
        (Copies ?? []).SelectMany(c => c.SubtitleTracks ?? []).Select(t => t.Language)
            .Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.Ordinal).ToList()!;
}

/// <summary>
/// An episode's place in TMDB's numbering (MSFP video section 4.1): the
/// season_number and episode_number TMDB gives it in the series' own season
/// list, not in an episode group. A host whose files are numbered another
/// way maps them to TMDB's numbers first.
/// </summary>
public sealed record Place
{
    /// <summary>The season number, 0 (specials) to 9,999.</summary>
    [JsonPropertyName("season")]
    public int Season { get; set; }

    /// <summary>The episode number, 0 to 99,999.</summary>
    [JsonPropertyName("episode")]
    public int Episode { get; set; }
}

/// <summary>
/// One of a title's copies as a peer sees it: an opaque ID and a name (MSFP
/// playback section 2.3).
/// </summary>
/// <param name="Id">The version ID.</param>
/// <param name="Name">Its name; "" for none.</param>
public sealed record Version(string Id, string Name);

/// <summary>
/// An artist, an album or a track, with its description (MSFP music
/// section 3): its name; for albums and tracks, the artist keys credited and
/// their names as written; for albums, the name to sort by, the release
/// type, whether it is a compilation, the dates of this edition and of the
/// first, and genres; for tracks, the album key, the disc and number on it,
/// and its copies; for local artists and albums, whether the owner shares an
/// image; other identifiers by scheme (mbreleasegroup, mbrecording, isrc,
/// acoustid). A peer's are cleaned as music section 3.2 says; the host's own
/// are cleaned the same way before they leave.
/// </summary>
public sealed record MusicTitle : Title
{
    /// <summary>Its name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>For albums, the name to sort by.</summary>
    [JsonPropertyName("sortName"), OmitEmpty]
    public string? SortName { get; set; }

    /// <summary>For albums and tracks, the artist keys credited.</summary>
    [JsonPropertyName("artists"), OmitEmpty]
    public List<string>? Artists { get; set; }

    /// <summary>The artists' names as written.</summary>
    [JsonPropertyName("artistNames"), OmitEmpty]
    public string? ArtistNames { get; set; }

    /// <summary>For albums, the release type: album, single, ep, compilation, soundtrack, live, remix or other.</summary>
    // P17: primaryType and secondaryTypes, MusicBrainz's own, replace it.
    [JsonPropertyName("releaseType"), OmitEmpty]
    public string? ReleaseType { get; set; }

    /// <summary>For albums, whether it is a compilation of several artists; null when not said.</summary>
    [JsonPropertyName("compilation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Compilation { get; set; }

    /// <summary>For albums, this edition's date: YYYY, YYYY-MM or YYYY-MM-DD.</summary>
    [JsonPropertyName("date"), OmitEmpty]
    public string? Date { get; set; }

    /// <summary>For albums, the date of the first release.</summary>
    [JsonPropertyName("originalDate"), OmitEmpty]
    public string? OriginalDate { get; set; }

    /// <summary>For albums, genres.</summary>
    [JsonPropertyName("genres"), OmitEmpty]
    public List<string>? Genres { get; set; }

    /// <summary>For tracks, the album key.</summary>
    [JsonPropertyName("album"), OmitEmpty]
    public string? Album { get; set; }

    /// <summary>For tracks, the disc.</summary>
    [JsonPropertyName("disc"), OmitEmpty]
    public int Disc { get; set; }

    /// <summary>For tracks, the number on the disc.</summary>
    [JsonPropertyName("number"), OmitEmpty]
    public int Number { get; set; }

    /// <summary>
    /// A track's copies, best first, at least one (music section 3.3). Of the
    /// host's own, each ID is its handle, as for
    /// <see cref="VideoTitle.Copies"/>: the engine sends the version ID made
    /// of it.
    /// </summary>
    [JsonPropertyName("copies"), OmitEmpty]
    public List<MusicCopy>? Copies { get; set; }

    /// <summary>A local artist or album whose image the owner shares (the host's <see cref="IImages"/> gives it); ignored on any other title.</summary>
    [JsonPropertyName("image"), OmitEmpty]
    public bool Image { get; set; }

    /// <summary>Other identifiers by scheme (mbreleasegroup, mbrecording, isrc, acoustid).</summary>
    [JsonPropertyName("ids"), OmitEmpty]
    public Dictionary<string, string>? Ids { get; set; }
}

/// <summary>
/// How a title is written for a store: one member naming its content part,
/// <c>{"video":{…}}</c> or <c>{"music":{…}}</c>, as the Go engine's
/// MarshalTitle writes it.
/// </summary>
public static class TitleJson
{
    /// <summary>The title as JSON (UTF-8).</summary>
    /// <param name="title">A <see cref="VideoTitle"/> or a <see cref="MusicTitle"/>.</param>
    public static byte[] Marshal(Title title) => JsonSerializer.SerializeToUtf8Bytes(ToElement(title), MsfpJson.Options);

    /// <summary>Reads what <see cref="Marshal"/> wrote.</summary>
    /// <param name="json">The JSON (UTF-8).</param>
    public static Title Unmarshal(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        return Unmarshal(doc.RootElement);
    }

    internal static JsonElement ToElement(Title title)
    {
        var name = title switch
        {
            VideoTitle => "video",
            MusicTitle => "music",
            _ => throw new ArgumentException("msfp: not a title", nameof(title)),
        };
        var inner = JsonSerializer.SerializeToElement(title, title.GetType(), MsfpJson.Options);
        return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [name] = inner }, MsfpJson.Options);
    }

    internal static Title Unmarshal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
            {
                return video.Deserialize<VideoTitle>(MsfpJson.Options)!;
            }
            if (element.TryGetProperty("music", out var music) && music.ValueKind == JsonValueKind.Object)
            {
                return music.Deserialize<MusicTitle>(MsfpJson.Options)!;
            }
        }
        throw new JsonException("msfp: not a title");
    }
}

/// <summary>A page of the host's titles.</summary>
/// <param name="Titles">The titles.</param>
/// <param name="More">Whether titles remain after the last.</param>
public sealed record TitlePage(IReadOnlyList<Title> Titles, bool More);

/// <summary>
/// A page of what changed in the host's catalog: titles new or changed,
/// keys gone, the page's last key, and whether more remain.
/// </summary>
/// <param name="Present">Titles new or changed.</param>
/// <param name="Gone">Keys gone.</param>
/// <param name="Last">The page's last key.</param>
/// <param name="More">Whether more remain.</param>
public sealed record ChangePage(IReadOnlyList<Title> Present, IReadOnlyList<string> Gone, string Last, bool More);

/// <summary>
/// What <see cref="IPlayback.DescribeAsync"/> tells of a copy, besides what
/// its description in the catalog says (its length, parts, tracks and the
/// rest: the engine takes those from the title's Copies).
/// </summary>
public sealed record CopyInfo
{
    /// <summary>
    /// The layout of the copy's previews, for a copy whose catalog
    /// description says it has some (Previews); null for none.
    /// </summary>
    public Trickplay? Trickplay { get; set; }

    /// <summary>
    /// How many fonts the copy's ASS tracks use (0 to 8), served as
    /// <see cref="MediaKind.Font"/> 0 to Fonts-1 (MSFP video section 6.6).
    /// </summary>
    public int Fonts { get; set; }

    /// <summary>
    /// The subtitle tracks the host can't serve apart (as WebVTT for text, as
    /// PGS for pictures: MSFP video section 5.4): they get no URL; every
    /// other track of a format MSFP serves apart does.
    /// </summary>
    public List<string>? NotApart { get; set; }
}

/// <summary>
/// A copy's seek-bar previews (MSFP video section 5.5): the tiles' size,
/// the grid of a sheet, the time between tiles, the tiles in all, and (in
/// a remote playback) the sheets listed.
/// </summary>
public sealed record Trickplay
{
    /// <summary>A tile's width.</summary>
    public int TileWidth { get; set; }

    /// <summary>A tile's height.</summary>
    public int TileHeight { get; set; }

    /// <summary>Tiles across a sheet.</summary>
    public int Columns { get; set; }

    /// <summary>Tiles down a sheet.</summary>
    public int Rows { get; set; }

    /// <summary>The time between tiles.</summary>
    public int IntervalMs { get; set; }

    /// <summary>The tiles in all.</summary>
    public int TileCount { get; set; }

    /// <summary>The sheets listed.</summary>
    public int Sheets { get; set; }
}

/// <summary>
/// How a playback is served, for the viewer's player information: why it
/// is converted (none when played as it is), the container, the bitrate,
/// the video and the audio.
/// </summary>
public sealed record PlaybackInfo
{
    /// <summary>Why it is converted.</summary>
    [JsonPropertyName("reasons"), OmitEmpty]
    public List<string>? Reasons { get; set; }

    /// <summary>The container.</summary>
    [JsonPropertyName("container"), OmitEmpty]
    public string? Container { get; set; }

    /// <summary>The bitrate.</summary>
    [JsonPropertyName("bitrateKbps"), OmitEmpty]
    public int BitrateKbps { get; set; }

    /// <summary>The video.</summary>
    [JsonPropertyName("video"), OmitEmpty]
    public StreamInfo? Video { get; set; }

    /// <summary>The audio.</summary>
    [JsonPropertyName("audio"), OmitEmpty]
    public StreamInfo? Audio { get; set; }
}

/// <summary>The video or the audio of a playback.</summary>
public sealed record StreamInfo
{
    /// <summary>The codec.</summary>
    [JsonPropertyName("codec"), OmitEmpty]
    public string? Codec { get; set; }

    /// <summary>The codec's profile.</summary>
    [JsonPropertyName("profile"), OmitEmpty]
    public string? Profile { get; set; }

    /// <summary>The width.</summary>
    [JsonPropertyName("width"), OmitEmpty]
    public int Width { get; set; }

    /// <summary>The height.</summary>
    [JsonPropertyName("height"), OmitEmpty]
    public int Height { get; set; }

    /// <summary>The bit depth.</summary>
    [JsonPropertyName("bitDepth"), OmitEmpty]
    public int BitDepth { get; set; }

    /// <summary>The dynamic range.</summary>
    [JsonPropertyName("range"), OmitEmpty]
    public string? Range { get; set; }

    /// <summary>The bitrate.</summary>
    [JsonPropertyName("bitrateKbps"), OmitEmpty]
    public int BitrateKbps { get; set; }

    /// <summary>Audio channels.</summary>
    [JsonPropertyName("channels"), OmitEmpty]
    public int Channels { get; set; }

    /// <summary>The language.</summary>
    [JsonPropertyName("language"), OmitEmpty]
    public string? Language { get; set; }
}

/// <summary>A peer, for the host's live view: its ID and name, never a person.</summary>
/// <param name="Id">The peer's ID.</param>
/// <param name="Name">The peer's name.</param>
public sealed record PeerRef(string Id, string Name);

/// <summary>What the engine asks <see cref="IPlayback.StartAsync"/> to serve.</summary>
public sealed record StartRequest
{
    /// <summary>The handle of the copy chosen (its ID in the title's Copies).</summary>
    public string Copy { get; init; } = "";

    /// <summary>The key played (an episode's, for a copy holding several).</summary>
    public string Title { get; init; } = "";

    /// <summary>It is a track (MSFP music section 5): no video is served.</summary>
    public bool AudioOnly { get; init; }

    /// <summary>
    /// <see cref="Playback.PurposePlay"/>, <see cref="Playback.PurposeDownload"/>
    /// or <see cref="Playback.PurposeScan"/> (MSFP playback sections 7 and 8):
    /// a scan is always of delivery file, and converts nothing.
    /// </summary>
    public string Purpose { get; init; } = Playback.PurposePlay;

    /// <summary>
    /// What the viewer's server asked: <see cref="Playback.DeliveryFile"/> (the
    /// copy as it is: method direct, protocol file),
    /// <see cref="Playback.DeliveryRemux"/> (into <see cref="Container"/>, as
    /// hls), <see cref="Playback.DeliveryTranscode"/> (to
    /// <see cref="Transcode"/>'s targets, as hls), or "" for the host's choice
    /// from the capabilities. With a delivery, the host serves exactly that or
    /// refuses with <see cref="MsfpError.DeliveryRefused"/> (its bandwidth,
    /// its policy, a delivery it can't make of the copy); it never serves
    /// another (MSFP playback section 2.2, step 7). The engine has already
    /// refused file of a copy of several parts, file or remux of a play above
    /// <see cref="MaxBitrateKbps"/>, and a BurnSubtitle the copy hasn't.
    /// </summary>
    public string Delivery { get; init; } = "";

    /// <summary>The container of a remux.</summary>
    public string Container { get; init; } = "";

    /// <summary>The targets of a transcode; null when none were said.</summary>
    public TranscodeTargets? Transcode { get; init; }

    /// <summary>
    /// For hls, every audio track of the copy is asked as an #EXT-X-MEDIA
    /// rendition named by its ID (MSFP media section 3.1).
    /// </summary>
    public bool AudioRenditions { get; init; }

    /// <summary>
    /// The copy's files, as its description says (1 for one file): a copy of
    /// several is started as "hls", never "file".
    /// </summary>
    public int Parts { get; init; }

    /// <summary>The peer playing.</summary>
    public PeerRef Peer { get; init; } = new("", "");

    /// <summary>The engine's choice of audio track ("": none).</summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>
    /// The engine's choice of subtitle track ("": none shown); subtitles never
    /// make a playback transcode, but for <see cref="TranscodeTargets.BurnSubtitle"/>.
    /// </summary>
    public string SubtitleTrack { get; init; } = "";

    /// <summary>Where to start, in the copy; 0 for a download or a scan.</summary>
    public long StartMs { get; init; }

    /// <summary>
    /// What the viewer's device plays, as the peer said, only values MSFP
    /// lists. Without a delivery, nothing said about containers or video
    /// codecs: no direct play (H.264 and AAC in HLS; AAC in HLS for a track);
    /// about audio codecs: AAC only; about hdr: standard range.
    /// </summary>
    public PlayCapabilities Capabilities { get; init; } = new();

    /// <summary>
    /// The cap in effect for a play: 120,000 when none was set. A download's
    /// speed is the engine's to pace (downloadKbps).
    /// </summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>
    /// The tracks a music play names after Title, that the host prepares under
    /// the same playback (MSFP music section 5.2), each with its first copy,
    /// in the request's order; Place is its place in the request's queue (0 to
    /// 4), which a queued track the engine left out (not offered, no copy)
    /// keeps empty.
    /// </summary>
    public IReadOnlyList<QueuedCopy> Queue { get; init; } = [];

    /// <summary>
    /// Counts the pool's streams (and, for "transcode", its transcodes), or the
    /// download, or the scan, in one step: the host calls it once it knows the
    /// method ("direct", "remux", "transcode"), before starting any work (MSFP
    /// playback section 6.2). Throws <see cref="MsfpException"/> with
    /// StreamLimitReached or ServerBusy. Optional for the host; null outside
    /// the engine.
    /// </summary>
    public Func<string, ISlot>? Admit { get; init; }
}

/// <summary>A track of a play's queue: its place in the request's queue, its key, and the handle of its first copy.</summary>
/// <param name="Place">Its place in the request's queue (0 to 4).</param>
/// <param name="Title">Its key.</param>
/// <param name="Copy">The handle of its first copy.</param>
public sealed record QueuedCopy(int Place, string Title, string Copy);

/// <summary>A stream counted by <see cref="StartRequest.Admit"/>.</summary>
public interface ISlot
{
    /// <summary>Frees what was counted, when the host's start fails.</summary>
    void Release();

    /// <summary>
    /// Moves a remux that turned out to re-encode the video into the transcode
    /// count; false when none is left.
    /// </summary>
    bool Transcoding();
}

/// <summary>What <see cref="IPlayback.StartAsync"/> answers.</summary>
public sealed record Started
{
    /// <summary>
    /// The host's handle for this playback (not the copy's): Progress, Stop
    /// and Media's Serve are given it.
    /// </summary>
    public string Handle { get; init; } = "";

    /// <summary>"direct", "remux", "transcode", as admitted.</summary>
    public string Method { get; init; } = "";

    /// <summary>"hls" or "file".</summary>
    public string Protocol { get; init; } = "";

    /// <summary>The file's type, for "file" (a video/ or audio/ type).</summary>
    public string MimeType { get; init; } = "";

    /// <summary>Where it starts, in the copy.</summary>
    public long PositionMs { get; init; }

    /// <summary>How it is served, for the viewer's player information.</summary>
    public PlaybackInfo? Info { get; init; }

    /// <summary>
    /// The queued tracks the host prepared, of those
    /// <see cref="StartRequest.Queue"/> named (by Place); one it can't play is
    /// left out. Their media are served under the same Handle, with
    /// <see cref="MediaPath.Entry"/> their Place + 1.
    /// </summary>
    public IReadOnlyList<StartedEntry>? Queue { get; init; }
}

/// <summary>A queued track the host prepared: its place in the request's queue, and how it is served.</summary>
/// <param name="Place">Its place in the request's queue.</param>
/// <param name="Method">"direct", "remux" or "transcode".</param>
/// <param name="Protocol">"hls" or "file".</param>
/// <param name="MimeType">The file's type, for "file".</param>
public sealed record StartedEntry(int Place, string Method, string Protocol, string MimeType);

/// <summary>
/// A peer's progress or stop of a playback here, passed on to the host (MSFP
/// playback sections 3, 4 and 7).
/// </summary>
public sealed record Report
{
    /// <summary>The playback's handle (<see cref="Started.Handle"/>).</summary>
    public string Handle { get; init; } = "";

    /// <summary>The track playing of a music queue, else the title played.</summary>
    public string Title { get; init; } = "";

    /// <summary>The position, when <see cref="HasPosition"/>.</summary>
    public long PositionMs { get; init; }

    /// <summary>Whether the report says a position.</summary>
    public bool HasPosition { get; init; }

    /// <summary>Paused.</summary>
    public bool Paused { get; init; }

    /// <summary>When the viewer was there: the peer's at, or now.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>It is the stop's own report.</summary>
    public bool Stop { get; init; }

    /// <summary>
    /// It is a download's late report, after its playback ended (the viewer
    /// watched offline): the handle no longer plays.
    /// </summary>
    public bool Late { get; init; }
}

/// <summary>Why a playback ended (MSFP playback section 6.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EndReason>))]
public enum EndReason
{
    /// <summary>"stopped".</summary>
    [JsonStringEnumMemberName("stopped")]
    Stopped,

    /// <summary>"expired".</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,

    /// <summary>"tooLong".</summary>
    [JsonStringEnumMemberName("tooLong")]
    TooLong,

    /// <summary>"pairingEnded".</summary>
    [JsonStringEnumMemberName("pairingEnded")]
    PairingEnded,

    /// <summary>"notOffered".</summary>
    [JsonStringEnumMemberName("notOffered")]
    NotOffered,

    /// <summary>"operator".</summary>
    [JsonStringEnumMemberName("operator")]
    Operator,
}

/// <summary>The kind of a <see cref="MediaPath"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MediaKind>))]
public enum MediaKind
{
    /// <summary>"master": the master playlist.</summary>
    [JsonStringEnumMemberName("master")]
    Master,

    /// <summary>"playlist": a media playlist.</summary>
    [JsonStringEnumMemberName("playlist")]
    Playlist,

    /// <summary>"segment": a segment.</summary>
    [JsonStringEnumMemberName("segment")]
    Segment,

    /// <summary>"file": the file.</summary>
    [JsonStringEnumMemberName("file")]
    File,

    /// <summary>"subtitles": a WebVTT part, or a whole text track.</summary>
    [JsonStringEnumMemberName("subtitles")]
    Subtitles,

    /// <summary>"ass": an ASS part, or a whole ASS track.</summary>
    [JsonStringEnumMemberName("ass")]
    Ass,

    /// <summary>"pictures": a PGS part.</summary>
    [JsonStringEnumMemberName("pictures")]
    Pictures,

    /// <summary>"font": a font of the ASS tracks.</summary>
    [JsonStringEnumMemberName("font")]
    Font,

    /// <summary>"sheet": a preview sheet.</summary>
    [JsonStringEnumMemberName("sheet")]
    Sheet,
}

/// <summary>
/// What a media request asks for. Names are already checked against
/// ^[A-Za-z0-9._-]{1,64}$, never "." or "..".
/// </summary>
public sealed partial record MediaPath
{
    /// <summary>The kind.</summary>
    public MediaKind Kind { get; init; }

    /// <summary>For Playlist and Segment: the name.</summary>
    public string Name { get; init; } = "";

    /// <summary>For Subtitles, Ass and Pictures: the track.</summary>
    public string Track { get; init; } = "";

    /// <summary>For Subtitles, Ass and Pictures: the stretch, unless <see cref="Whole"/>.</summary>
    public int Part { get; init; }

    /// <summary>A whole text track (Subtitles, Ass) is asked, not a stretch.</summary>
    public bool Whole { get; init; }

    /// <summary>For Sheet: the sheet.</summary>
    public int Sheet { get; init; }

    /// <summary>For Font: the font, 0 to 7.</summary>
    public int Font { get; init; }

    /// <summary>
    /// 0 for the playback's own stream, n+1 for the stream of queue entry n
    /// (Master, Playlist, Segment, File).
    /// </summary>
    public int Entry { get; init; }

    /// <summary>
    /// The handle <see cref="IMedia.ServeAsync"/> is given is a copy's, not a
    /// playback's: a Sheet under a previews ticket (MSFP video section 5.6),
    /// which no playback has.
    /// </summary>
    public bool OfCopy { get; init; }
}

/// <summary>
/// One byte range, parsed: First to Last included; Last -1 for "to the
/// end"; Suffix for "the last Suffix bytes" (First and Last unused). For the
/// file, the engine resolves it against the copy's size before
/// <see cref="IMedia.ServeAsync"/>: First to Last, both within the file. Go's
/// Range; named so as not to clash with <see cref="System.Range"/>.
/// </summary>
/// <param name="First">The first byte.</param>
/// <param name="Last">The last byte, included; -1 for the end.</param>
/// <param name="Suffix">The last bytes asked for, when it is a suffix.</param>
public sealed partial record ByteRange(long First, long Last, long Suffix);

/// <summary>
/// What a pairing may use of a server (MSFP playback section 5): streams at
/// once (0 to 50), transcodes among them, a bitrate cap (0: none); downloads
/// at once (0 to 50; 0: none), downloads started in any 24 hours (0 to
/// 10,000; 0: no cap besides Downloads), each download's speed cap in kbit/s
/// (0 to 1,000,000; 0: none), scans started in any 24 hours (0 to 1,000,000;
/// 0: no scan access). With Audio, audio playbacks (tracks) are counted
/// apart, within AudioStreams and AudioTranscodes (music section 5.1);
/// without, they count in Streams and Transcodes.
/// </summary>
public sealed record Limits
{
    /// <summary>Streams at once.</summary>
    [JsonPropertyName("streams")]
    public int Streams { get; set; }

    /// <summary>Transcodes among them.</summary>
    [JsonPropertyName("transcodes")]
    public int Transcodes { get; set; }

    /// <summary>The bitrate cap; 0 for none.</summary>
    [JsonPropertyName("bitrateKbps"), OmitEmpty]
    public int BitrateKbps { get; set; }

    /// <summary>Downloads at once; 0 for none.</summary>
    [JsonPropertyName("downloads"), OmitEmpty]
    public int Downloads { get; set; }

    /// <summary>Downloads started in any 24 hours; 0 for no cap besides Downloads.</summary>
    [JsonPropertyName("downloadsPerDay"), OmitEmpty]
    public int DownloadsPerDay { get; set; }

    /// <summary>Each download's speed cap, in kbit/s; 0 for none.</summary>
    [JsonPropertyName("downloadKbps"), OmitEmpty]
    public int DownloadKbps { get; set; }

    /// <summary>Scans started in any 24 hours; 0 for no scan access.</summary>
    [JsonPropertyName("scansPerDay"), OmitEmpty]
    public int ScansPerDay { get; set; }

    /// <summary>Audio playbacks are counted apart.</summary>
    [JsonPropertyName("audio"), OmitEmpty]
    public bool Audio { get; set; }

    /// <summary>Audio streams at once, with <see cref="Audio"/>.</summary>
    [JsonPropertyName("audioStreams"), OmitEmpty]
    public int AudioStreams { get; set; }

    /// <summary>Audio transcodes among them.</summary>
    [JsonPropertyName("audioTranscodes"), OmitEmpty]
    public int AudioTranscodes { get; set; }

    /// <summary>Throws <see cref="MsfpException"/> with InvalidLimits when the limits are out of range.</summary>
    public void Check()
    {
        if (Streams < 0 || Streams > Playback.MaxStreams || Transcodes < 0 || Transcodes > Streams ||
            BitrateKbps < 0 || BitrateKbps > Playback.MaxLimitKbps ||
            Downloads < 0 || Downloads > Playback.MaxDownloads || DownloadsPerDay < 0 || DownloadsPerDay > Playback.MaxDownloadsPerDay ||
            DownloadKbps < 0 || DownloadKbps > Playback.MaxLimitKbps || ScansPerDay < 0 || ScansPerDay > Playback.MaxScansPerDay ||
            AudioStreams < 0 || AudioStreams > Playback.MaxStreams || AudioTranscodes < 0 || AudioTranscodes > AudioStreams)
        {
            throw new MsfpException(MsfpError.InvalidLimits);
        }
    }
}

/// <summary>
/// One change of the host's own catalog, in the log the engine keeps: a
/// title new or changed (Title set), or gone (Title null).
/// </summary>
/// <param name="Seq">The change number.</param>
/// <param name="Key">The title's key.</param>
/// <param name="Title">The title new or changed; null when gone.</param>
/// <param name="At">When the change was noted.</param>
[JsonConverter(typeof(OwnChangeConverter))]
public sealed record OwnChange(long Seq, string Key, Title? Title, DateTimeOffset At);
