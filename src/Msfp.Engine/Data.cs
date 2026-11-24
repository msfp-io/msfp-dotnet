using System.Text.Json;
using System.Text.Json.Serialization;
using Msfp.Protocol;

namespace Msfp;

// The data objects of the host interfaces (engine.md section 7).

/// <summary>
/// One of the host's libraries: its own ID and its name. A library carries
/// no kind (MSFP catalog section 2): its titles' keys say what it holds.
/// </summary>
public sealed record Library
{
    /// <summary>The host's ID of the library.</summary>
    public string Id { get; set; } = "";

    /// <summary>Its name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Set in the answer of Offers: it is offered.</summary>
    public bool Offered { get; set; }
}

/// <summary>
/// One of a peer's libraries: its public ID, its name, whether this server
/// wants it, and whether it was gone from the peer's latest answer. What it
/// holds is told by its titles' keys: a host types an empty library by its
/// first titles.
/// </summary>
public sealed record WantedLibrary
{
    /// <summary>Its public ID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Its name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

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
    /// A film's, an episode's or an extra's copies, in the host's order of
    /// preference (MSFP video sections 3.2 to 3.8): what a viewer's server needs to list each as a
    /// file of its own before anyone plays it (its length, size, etag, media
    /// type, streams and tracks, chapters, skip markers, the layout of its
    /// seek-bar previews…). Required for those kinds: one without a copy left
    /// once cleaned isn't sent; series and seasons carry none.
    /// </summary>
    /// <remarks>
    /// Of the host's own titles, each copy's ID is the host's handle of it,
    /// the one <see cref="IPlayback"/> and <see cref="IMedia"/> are given: it
    /// never leaves; the engine sends instead the version ID play gives for
    /// it, and plays a copy only as it is described here. The host's order of
    /// its copies is its preference, best first (video section 3.7): the
    /// first is the one a play naming none plays
    /// (<see cref="Protocol.Copies.Sorted"/> is a default order, for a host
    /// that has none). Language tags leave as <see cref="Languages.TrySentTag"/>
    /// gives them. Facts are in outside vocabularies or the MSFP registry
    /// (video section 3.5): the file's media type (read from its first bytes
    /// where it can), the registry's codec and subtitle tokens (ffprobe's
    /// codec_name), RFC 6381 codecs strings (required for h264, hevc, av1,
    /// vp9, aac and dts), H.273 colour code points; a host whose media server
    /// names them otherwise maps them; the engine maps nothing, and sends any
    /// well-formed value. A copy carries no name and its tracks no title.
    /// Track IDs are a&lt;n&gt; and s&lt;n&gt;, n the stream's index in the
    /// file. Its ETag changes whenever the copy's bytes change, and only then:
    /// a change of a copy (its etag, its previewLayout) is a change of the
    /// title (a changes reading sends it again). A previewLayout leaves only
    /// with a <see cref="MsfpConfig.SheetEncoder"/>, which re-encodes the
    /// sheets it opens. Of a peer's titles, the ID is the peer's version ID.
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
    /// video section 3.9): open, a token (trailer, featurette,
    /// behindthescenes, deletedscene, interview, scene, short, clip,
    /// themesong, themevideo…); an extra without one isn't sent. An extra
    /// carries no name: a viewer's server shows it by its type and a number.
    /// Theme songs and videos are extras like the others: whether the host
    /// offers them is its operator's choice; the engine carries what the host
    /// gives.
    /// </summary>
    [JsonPropertyName("extraType"), OmitEmpty]
    public string? ExtraType { get; set; }

    /// <summary>
    /// A local film's or series' name (required, at most 128: catalog section
    /// 4.3, video section 3.10). It is text, never markup; on other titles it
    /// isn't sent.
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
    /// The title's first copy, the owning server's preferred (MSFP video
    /// section 3.7); null for a title without one (a series, a season). A
    /// viewer's server may show its picture as the title's; which copy it
    /// plays is its own choice, from every copy's facts.
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
/// An artist, an album or a track, with its description (MSFP music
/// section 3): its name; for albums and tracks, the artist keys credited and
/// their names as written; for albums, MusicBrainz's primary and secondary
/// types of its release group (lowercased, open), whether it is a
/// compilation; for albums and tracks, the dates of this edition and of the
/// first, and genres; for tracks, the album key, the disc and number on it,
/// and its copies; for local artists and albums, whether the owner shares an
/// image; other identifiers by scheme, on mb titles only (mbreleasegroup on
/// albums, mbrecording on tracks). There are no sort names. A track's
/// artists, artistNames, dates and genres leave only when they differ from
/// its album's (MSFP music section 3): of a peer's, an absent one is its
/// album's. A peer's are cleaned as music section 3.2 says; the host's own
/// are cleaned the same way before they leave.
/// </summary>
public sealed record MusicTitle : Title
{
    /// <summary>Its name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>For albums and tracks, the artist keys credited.</summary>
    [JsonPropertyName("artists"), OmitEmpty]
    public List<string>? Artists { get; set; }

    /// <summary>The artists' names as written.</summary>
    [JsonPropertyName("artistNames"), OmitEmpty]
    public string? ArtistNames { get; set; }

    /// <summary>For albums, MusicBrainz's primary type of its release group, lowercased, open (album, single, ep, broadcast, other…).</summary>
    [JsonPropertyName("primaryType"), OmitEmpty]
    public string? PrimaryType { get; set; }

    /// <summary>For albums, MusicBrainz's secondary types, lowercased, open (compilation, soundtrack, live, remix…), at most 8.</summary>
    [JsonPropertyName("secondaryTypes"), OmitEmpty]
    public List<string>? SecondaryTypes { get; set; }

    /// <summary>For albums, whether it is a compilation of several artists; null when not said.</summary>
    [JsonPropertyName("compilation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Compilation { get; set; }

    /// <summary>For albums and tracks, this edition's date: YYYY, YYYY-MM or YYYY-MM-DD.</summary>
    [JsonPropertyName("date"), OmitEmpty]
    public string? Date { get; set; }

    /// <summary>For albums and tracks, the date of the first release.</summary>
    [JsonPropertyName("originalDate"), OmitEmpty]
    public string? OriginalDate { get; set; }

    /// <summary>For albums and tracks, genres (a track's absent when its album's).</summary>
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
    /// A track's copies, in the owner's order of preference, at least one (music section 3.3). Of the
    /// host's own, each ID is its handle, as for
    /// <see cref="VideoTitle.Copies"/>: the engine sends the version ID made
    /// of it.
    /// </summary>
    [JsonPropertyName("copies"), OmitEmpty]
    public List<MusicCopy>? Copies { get; set; }

    /// <summary>A local artist or album whose image the owner shares (the host's <see cref="IImages"/> gives it); ignored on any other title.</summary>
    [JsonPropertyName("image"), OmitEmpty]
    public bool Image { get; set; }

    /// <summary>Other identifiers by scheme, on mb titles only (mbreleasegroup on albums, mbrecording on tracks).</summary>
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
/// its description in the catalog says (its length, parts, tracks, the
/// layout of its previews and the rest: the engine takes those from the
/// title's Copies).
/// </summary>
public sealed record CopyInfo
{
    /// <summary>
    /// How many fonts the copy's ASS tracks use (0 to 64), served as
    /// <see cref="MediaKind.Font"/> 0 to Fonts-1 (MSFP video section 6.6):
    /// each at most 10 MiB, 64 MiB in all; the host lists none past those
    /// bounds.
    /// </summary>
    public int Fonts { get; set; }

    /// <summary>
    /// The subtitle tracks the host can't serve apart (as WebVTT for text, as
    /// PGS for pictures: MSFP video section 5.4): they get no entry in the
    /// answer's subtitles. Every other track of a format video section 5.4's
    /// table lists is served apart, in the table's form: a host must serve
    /// them so (an external track it can't serve apart it doesn't list in the
    /// catalog). A track of a format the table doesn't list is served apart
    /// only when the host says how, in <see cref="Forms"/>.
    /// </summary>
    public List<string>? NotApart { get; set; }

    /// <summary>
    /// How the host serves apart, by track ID, a track of a format video
    /// section 5.4's table doesn't list: <see cref="Video.FormText"/> or
    /// <see cref="Video.FormPicture"/>; ignored for a listed format.
    /// </summary>
    public Dictionary<string, string>? Forms { get; set; }
}

/// <summary>
/// The peer a host call is made for: its ID and name (for the host's live
/// view; never a person), and the host's libraries offered to it when the
/// call is made. Every host call that is given a copy's handle for a peer is
/// given it too (<see cref="IPlayback.DescribeAsync"/>,
/// <see cref="StartRequest.Peer"/>, <see cref="ILyrics.LyricsAsync"/>,
/// <see cref="IMedia.ServeAsync"/>), so that a handle can name the copy
/// alone, wherever its files are: the host resolves it to a file in one of
/// these libraries, and finds none (<see cref="MsfpError.NotFound"/>) when
/// the copy is only in libraries the peer isn't offered. For a playback's
/// media, they are those of when it started (a playback ends once its title
/// is in none of the libraries offered).
/// </summary>
/// <param name="Id">The peer's ID.</param>
/// <param name="Name">The peer's name.</param>
/// <param name="Libraries">The host's libraries offered to the peer (their IDs, sorted ordinally).</param>
public sealed record PeerRef(string Id, string Name, IReadOnlyList<string> Libraries);

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
    /// <see cref="Playback.DeliveryRemux"/> (into <see cref="MediaType"/>'s
    /// segment format, as hls: video/mp2t or audio/mp2t for MPEG-TS segments,
    /// video/mp4 or audio/mp4 for fragmented MP4),
    /// <see cref="Playback.DeliveryTranscode"/> (to <see cref="Transcode"/>'s
    /// targets, as hls), or "" for the baseline: H.264 at most High 4.1, 8-bit
    /// 4:2:0, progressive, SDR in BT.709, and AAC-LC of at most 2 channels
    /// and 48 kHz, in complete HLS (AAC alone for a copy with no video), each
    /// stream already within it left unchanged (<see cref="Served"/> says
    /// which), never the file. With a delivery, the host serves exactly that
    /// or refuses with <see cref="MsfpError.DeliveryRefused"/> (its bandwidth,
    /// its policy, a delivery it can't make of the copy); it never serves
    /// another (MSFP playback section 2.2, step 7). The engine has already
    /// refused a delivery not of the list, a remux to another media type, or
    /// to a segment format that can't carry the copy's streams
    /// (<see cref="Encoders.Segments"/>), a transcode codec or range not in
    /// <see cref="MsfpConfig.Encoders"/>, a copied stream no segment format
    /// carries, burnSubtitle with video copy, file of a copy of several
    /// parts, file or remux of a play whose overall rate is above
    /// <see cref="MaxBitrateKbps"/>, and a BurnSubtitle the copy hasn't.
    /// </summary>
    /// <remarks>
    /// The host's duties toward what it serves (MSFP playback section 2.7,
    /// media section 3.1): in fragmented MP4 segments, HEVC written hvc1,
    /// Dolby Vision dvh1 (dav1 on AV1), parameter sets out of band, in a remux
    /// and a transcode alike; each #EXT-X-STREAM-INF of its master playlist
    /// with BANDWIDTH, CODECS and, for video, RESOLUTION; VIDEO-RANGE (PQ or
    /// HLG) when the picture served is PQ or HLG; SUPPLEMENTAL-CODECS when
    /// Dolby Vision is kept; CHANNELS "16/JOC" allowed on an E-AC-3 rendition
    /// whose objects is dolby-atmos. The engine fills those it knows when the
    /// host leaves them out, and logs what stays missing.
    /// </remarks>
    public string Delivery { get; init; } = "";

    /// <summary>The media type of a remux: video/mp2t, video/mp4, audio/mp2t or audio/mp4.</summary>
    public string MediaType { get; init; } = "";

    /// <summary>
    /// The targets of a transcode, codecs as registry tokens, or
    /// <see cref="Names.Copy"/> for a stream served unchanged, and the range
    /// (sdr, keep); those the peer left out already hold the baseline's values
    /// (h264, aac, 2 channels, sdr; maxHeight 0, the copy's own height), and a
    /// copied stream, or a copy with no video, carries none of the targets
    /// that would change it. Null for another delivery.
    /// </summary>
    public TranscodeTargets? Transcode { get; init; }

    /// <summary>What the engine decided the play serves of the copy.</summary>
    public Served Served { get; init; } = new();

    /// <summary>
    /// For hls, every audio track of the copy is asked as an #EXT-X-MEDIA
    /// rendition named by its ID (MSFP media section 3.1). It is never
    /// refused: a host that makes none says so in
    /// <see cref="Started.AudioRenditions"/>.
    /// </summary>
    public bool AudioRenditions { get; init; }

    /// <summary>
    /// The copy's files, as its description says (1 for one file): a copy of
    /// several is started as "hls", never "file".
    /// </summary>
    public int Parts { get; init; }

    /// <summary>The peer playing, with the libraries offered to it: the copy is resolved within them.</summary>
    public PeerRef Peer { get; init; } = new("", "", []);

    /// <summary>
    /// The audio track played: the one the request named, else the file's
    /// default (the track it marks default, else its first; "" for none).
    /// Which subtitles a viewer sees is the viewer's server's choice: they
    /// never make a playback transcode, but for
    /// <see cref="TranscodeTargets.BurnSubtitle"/>.
    /// </summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>Where to start, in the copy; 0 for a download or a scan.</summary>
    public long StartMs { get; init; }

    /// <summary>
    /// The cap in effect for a play, 0 for none. A download's speed is the
    /// engine's to pace (downloadKbps).
    /// </summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>
    /// The tracks a music play names after Title, that the host prepares under
    /// the same playback (MSFP music section 5.2), each with the copy its entry
    /// named (else its first) and its own delivery, in the request's order;
    /// Place is its place in the request's queue (0 to 4), which a queued
    /// track the engine left out (not offered, no such copy, a delivery
    /// refused) keeps empty.
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

/// <summary>
/// A track of a play's queue: its place in the request's queue, its key, the
/// handle of its copy, and what it is served as: its entry's own delivery,
/// else the request's, with its media type or targets (as
/// <see cref="StartRequest"/>'s), and what the engine decided it serves.
/// </summary>
/// <param name="Place">Its place in the request's queue (0 to 4).</param>
/// <param name="Title">Its key.</param>
/// <param name="Copy">The handle of its copy.</param>
public sealed record QueuedCopy(int Place, string Title, string Copy)
{
    /// <summary>Its delivery: its entry's own, else the request's ("" for the baseline).</summary>
    public string Delivery { get; init; } = "";

    /// <summary>The media type of a remux.</summary>
    public string MediaType { get; init; } = "";

    /// <summary>The targets of a transcode, as <see cref="StartRequest.Transcode"/>'s.</summary>
    public TranscodeTargets? Transcode { get; init; }

    /// <summary>What the engine decided it serves of the copy.</summary>
    public Served Served { get; init; } = new();
}

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

    /// <summary>Where it starts, in the copy.</summary>
    public long PositionMs { get; init; }

    /// <summary>
    /// The hls playlist carries every audio track as a rendition, as
    /// <see cref="StartRequest.AudioRenditions"/> asked; false when the host
    /// made none (play's answer says so: MSFP playback section 2.3).
    /// </summary>
    public bool AudioRenditions { get; init; }

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
public sealed record StartedEntry(int Place, string Method, string Protocol);

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

    /// <summary>For Font: the font, 0 to 63.</summary>
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
/// once (0 to 1,000), transcodes among them, a bitrate cap (0: none);
/// downloads at once (0 to 1,000; 0: none), downloads started in any 24 hours
/// (0 to 10,000; 0: no cap besides Downloads), each download's speed cap in
/// kbit/s (0 to 1,000,000; 0: none), scans started in any 24 hours (0 to
/// 1,000,000; 0: no scan access), the bytes one scan may read (ScanBytes:
/// the pool's own scan cap, 4 MiB to 1 GiB, 0 for the floor, 4 MiB;
/// <see cref="Playback.ScanBytesRecommended"/>, 32 MiB, is the implementers'
/// notes' suggestion), which no limits object carries: each scan's answer
/// states it (MSFP playback section 8). With Audio, audio playbacks (tracks) are counted
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

    /// <summary>The bytes one scan may read, 4 MiB to 1 GiB; 0 for the floor, 4 MiB. A peer's is never known: each scan's answer states its own (<see cref="RemotePlayback.ScanBytes"/>).</summary>
    [JsonPropertyName("scanBytes"), OmitEmpty]
    public int ScanBytes { get; set; }

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
            (ScanBytes != 0 && (ScanBytes < Playback.ScanBytesFloor || ScanBytes > Playback.ScanBytesTop)) ||
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
