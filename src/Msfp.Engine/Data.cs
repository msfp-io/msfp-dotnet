using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>A film, a series, a season or an episode (MSFP video section 3).</summary>
public sealed record VideoTitle : Title
{
    /// <summary>
    /// Audio languages, as tags (the host's may be its media server's codes:
    /// the engine sends them as the toolkit's SentTags gives them).
    /// </summary>
    [JsonPropertyName("audio"), OmitEmpty]
    public List<string>? Audio { get; set; }

    /// <summary>
    /// Subtitle languages: those of the tracks play would list, the ones the
    /// host can serve (<see cref="Track"/>).
    /// </summary>
    [JsonPropertyName("subtitles"), OmitEmpty]
    public List<string>? Subtitles { get; set; }

    /// <summary>
    /// The host's, for its own films and episodes: each copy's picture, as its
    /// files say; a copy whose size isn't known is left out. The engine sends
    /// the best, stepped (MSFP video section 3.2).
    /// </summary>
    [JsonPropertyName("pictures"), OmitEmpty]
    public List<Picture>? Pictures { get; set; }

    /// <summary>
    /// A peer's: the picture it sent of its best copy, cleaned (MSFP video
    /// section 3.1); null when it sent none.
    /// </summary>
    [JsonPropertyName("video"), OmitEmpty]
    public Picture? Video { get; set; }

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
/// What a copy of a film or an episode offers for playing (MSFP video
/// section 3.2): its height in pixels, and its high dynamic range formats,
/// "hdr10", "hlg" or "dv" (none: standard range).
/// </summary>
public sealed record Picture
{
    /// <summary>The height in pixels (for a picture wider than 16:9, a 16:9 picture's as wide).</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>The HDR formats.</summary>
    [JsonPropertyName("hdr"), OmitEmpty]
    public List<string>? Hdr { get; set; }

    /// <summary>
    /// Whether a copy of this picture is better than one of
    /// <paramref name="other"/>, as <see cref="Best"/> ranks them (both
    /// stepped): of a greater height, then with HDR when the other has none,
    /// then with more formats.
    /// </summary>
    /// <param name="other">The other picture.</param>
    public bool Better(Picture other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Height != other.Height ? Height > other.Height : (Hdr?.Count ?? 0) > (other.Hdr?.Count ?? 0);
    }

    /// <summary>
    /// The picture of the best of a title's copies, as MSFP video section 3.2
    /// says, stepped: the greatest height, then HDR before none, then more
    /// formats; null when no copy's size is known. A host lists its copies to
    /// Copies in that order, best first, so that play without a version
    /// plays it. Go's BestPicture.
    /// </summary>
    /// <param name="pictures">Each copy's picture.</param>
    public static Picture? Best(IEnumerable<Picture> pictures)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        Picture? best = null;
        foreach (var p in pictures)
        {
            var hdr = Protocol.Video.CleanHdr(p.Hdr ?? []);
            var w = new Picture { Height = Protocol.Video.StepHeight(p.Height), Hdr = hdr.Count > 0 ? [.. hdr] : null };
            if (w.Height > 0 && (best is null || w.Better(best)))
            {
                best = w;
            }
        }

        return best;
    }
}

/// <summary>
/// One of a title's copies as a peer sees it: an opaque ID and a name (MSFP
/// playback section 2.3).
/// </summary>
/// <param name="Id">The version ID.</param>
/// <param name="Name">Its name.</param>
public sealed record Version(string Id, string Name);

/// <summary>
/// An artist, an album or a track, with its description (MSFP music
/// section 3).
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

    /// <summary>For tracks, the album key.</summary>
    [JsonPropertyName("album"), OmitEmpty]
    public string? Album { get; set; }

    /// <summary>For tracks, the disc.</summary>
    [JsonPropertyName("disc"), OmitEmpty]
    public int Disc { get; set; }

    /// <summary>For tracks, the number on the disc.</summary>
    [JsonPropertyName("number"), OmitEmpty]
    public int Number { get; set; }

    /// <summary>The year.</summary>
    [JsonPropertyName("year"), OmitEmpty]
    public int Year { get; set; }

    /// <summary>For tracks, the length (0: unknown).</summary>
    [JsonPropertyName("durationMs"), OmitEmpty]
    public long DurationMs { get; set; }

    /// <summary>Other identifiers by scheme (mbrelease, mbtrack, isrc).</summary>
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
/// One of the host's copies of a title: its handle (opaque to the engine,
/// never sent: the version ID a peer sees is a keyed MAC of it), its name
/// (for versions), its audio languages as tags or as its media server's
/// codes (for choosing the copy), and the host library holding it (one of
/// those Copies was asked for: a playback ends when that library is no
/// longer offered).
/// </summary>
public sealed record Copy
{
    /// <summary>The host's handle of the copy.</summary>
    public string Handle { get; set; } = "";

    /// <summary>Its name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Its audio languages.</summary>
    public List<string>? Audio { get; set; }

    /// <summary>The host library holding it.</summary>
    public string Library { get; set; } = "";
}

/// <summary>
/// An audio or subtitle track of a copy (MSFP playback section 2.6). A host
/// lists only the subtitle tracks it can serve as MSFP video section 6.2
/// says (WebVTT parts for text, raw PGS parts for pictures).
/// </summary>
public sealed record Track
{
    /// <summary>Its position, "a&lt;n&gt;" or "s&lt;n&gt;" (n from 0 to 999, no leading zeros).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Its language's name in itself ("Français (Canada)").</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>A language tag (or the host's media server's code: "fre" is sent "fr").</summary>
    [JsonPropertyName("language"), OmitEmpty]
    public string? Language { get; set; }

    /// <summary>Audio channels.</summary>
    [JsonPropertyName("channels"), OmitEmpty]
    public int Channels { get; set; }

    /// <summary>"commentary" or "description" for audio beside the title's own sound, best effort.</summary>
    [JsonPropertyName("role"), OmitEmpty]
    public string? Role { get; set; }

    /// <summary>The track the file marks as default, never a choice a person saved.</summary>
    [JsonPropertyName("default"), OmitEmpty]
    public bool Default { get; set; }

    /// <summary>Forced subtitles.</summary>
    [JsonPropertyName("forced"), OmitEmpty]
    public bool Forced { get; set; }

    /// <summary>Subtitles for the hearing impaired.</summary>
    [JsonPropertyName("hearingImpaired"), OmitEmpty]
    public bool HearingImpaired { get; set; }

    /// <summary>A PGS subtitle track.</summary>
    [JsonPropertyName("pictures"), OmitEmpty]
    public bool Pictures { get; set; }
}

/// <summary>What <see cref="IPlayback.DescribeAsync"/> tells of a copy.</summary>
public sealed record CopyInfo
{
    /// <summary>The audio tracks.</summary>
    public List<Track>? AudioTracks { get; set; }

    /// <summary>The subtitle tracks the host can serve (<see cref="Track"/>).</summary>
    public List<Track>? SubtitleTracks { get; set; }

    /// <summary>The whole copy's length, all its parts.</summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// How many files the copy is made of, played one after the other as one
    /// (MSFP playback section 1); 0 or 1: one file. A copy of several parts is
    /// served as "hls", never "file" (MSFP media section 1).
    /// </summary>
    public int Parts { get; set; }

    /// <summary>
    /// Where the episode played starts inside a copy holding several (MSFP
    /// video section 5.2); 0 when unknown.
    /// </summary>
    public long EpisodeStartMs { get; set; }

    /// <summary>The copy's previews, null for none.</summary>
    public Trickplay? Trickplay { get; set; }
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

/// <summary>
/// What a viewer's device plays (MSFP playback section 2.1, video section
/// 5.1). Toward a peer, only the values MSFP lists are sent, coarsened;
/// toward the host, only those values, exactly as MSFP writes them. An
/// empty list, or 0, says nothing.
/// </summary>
public sealed record Capabilities
{
    /// <summary>The containers.</summary>
    public List<string>? Containers { get; set; }

    /// <summary>The audio codecs.</summary>
    public List<string>? AudioCodecs { get; set; }

    /// <summary>The video codecs.</summary>
    public List<string>? VideoCodecs { get; set; }

    /// <summary>The HDR formats.</summary>
    public List<string>? Hdr { get; set; }

    /// <summary>The subtitle formats.</summary>
    public List<string>? Subtitles { get; set; }

    /// <summary>The most audio channels.</summary>
    public int MaxAudioChannels { get; set; }

    /// <summary>The greatest height.</summary>
    public int MaxHeight { get; set; }

    /// <summary>The highest bitrate.</summary>
    public int MaxBitrateKbps { get; set; }
}

/// <summary>A peer, for the host's live view: its ID and name, never a person.</summary>
/// <param name="Id">The peer's ID.</param>
/// <param name="Name">The peer's name.</param>
public sealed record PeerRef(string Id, string Name);

/// <summary>What the engine asks <see cref="IPlayback.StartAsync"/> to serve.</summary>
public sealed record StartRequest
{
    /// <summary>The handle of the copy chosen.</summary>
    public string Copy { get; init; } = "";

    /// <summary>The key played (an episode's, for a copy holding several).</summary>
    public string Title { get; init; } = "";

    /// <summary>It is a track (MSFP music section 5): no video is served.</summary>
    public bool AudioOnly { get; init; }

    /// <summary>
    /// The copy's files, as Describe said (1 for one file): a copy of several
    /// is started as "hls", never "file".
    /// </summary>
    public int Parts { get; init; }

    /// <summary>The peer playing.</summary>
    public PeerRef Peer { get; init; } = new("", "");

    /// <summary>The engine's choice of audio track ("": none).</summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>The engine's choice of subtitle track ("": none shown); subtitles never make a playback transcode.</summary>
    public string SubtitleTrack { get; init; } = "";

    /// <summary>Where to start.</summary>
    public long StartMs { get; init; }

    /// <summary>
    /// What the viewer's device plays, as the peer said, only values MSFP
    /// lists. Nothing said about containers or video codecs: no direct play
    /// (H.264 and AAC in HLS; AAC in HLS for a track); about audio codecs:
    /// AAC only; about hdr: standard range.
    /// </summary>
    public Capabilities Capabilities { get; init; } = new();

    /// <summary>The cap in effect: 120,000 when none was set.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>
    /// Counts the pairing's streams (and, for "transcode", its transcodes) in
    /// one step: the host calls it once it knows the method ("direct",
    /// "remux", "transcode"), before starting any work (MSFP playback section
    /// 6.2). Throws <see cref="MsfpException"/> with StreamLimitReached or
    /// ServerBusy. Optional for the host; null outside the engine.
    /// </summary>
    public Func<string, ISlot>? Admit { get; init; }
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

    /// <summary>The file's type, for "file" (a video/ or audio/ type).</summary>
    public string MimeType { get; init; } = "";

    /// <summary>Where it starts, in the copy.</summary>
    public long PositionMs { get; init; }

    /// <summary>How it is served, for the viewer's player information.</summary>
    public PlaybackInfo? Info { get; init; }

    /// <summary>Skip markers, when the host has them.</summary>
    public IReadOnlyList<Segment>? Segments { get; init; }
}

/// <summary>
/// A skip marker (MSFP video section 5.3). EndMs 0 is an end the host
/// doesn't know (credits that run to the end): the engine sends the copy's
/// DurationMs, and drops the marker without one.
/// </summary>
/// <param name="Kind">intro, outro, recap, preview or commercial; credits are outro.</param>
/// <param name="StartMs">Where it starts.</param>
/// <param name="EndMs">Where it ends; 0 when unknown.</param>
public sealed record Segment(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("startMs")] long StartMs,
    [property: JsonPropertyName("endMs")] long EndMs);

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

    /// <summary>"subtitles": a WebVTT part.</summary>
    [JsonStringEnumMemberName("subtitles")]
    Subtitles,

    /// <summary>"pictures": a PGS part.</summary>
    [JsonStringEnumMemberName("pictures")]
    Pictures,

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

    /// <summary>For Subtitles and Pictures: the track.</summary>
    public string Track { get; init; } = "";

    /// <summary>For Subtitles and Pictures: the part.</summary>
    public int Part { get; init; }

    /// <summary>For Sheet: the sheet.</summary>
    public int Sheet { get; init; }
}

/// <summary>
/// One byte range, parsed: First to Last included; Last -1 for "to the
/// end"; Suffix for "the last Suffix bytes" (First and Last unused). Go's
/// Range; named so as not to clash with <see cref="System.Range"/>.
/// </summary>
/// <param name="First">The first byte.</param>
/// <param name="Last">The last byte, included; -1 for the end.</param>
/// <param name="Suffix">The last bytes asked for, when it is a suffix.</param>
public sealed partial record ByteRange(long First, long Last, long Suffix);

/// <summary>
/// What a pairing may use of a server at once (MSFP playback section 5):
/// streams (0 to 50), transcodes among them, a bitrate cap (0: none). With
/// Audio, audio playbacks (tracks) are counted apart, within AudioStreams
/// and AudioTranscodes (music section 5.1); without, they count in Streams
/// and Transcodes.
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
        if (Streams < 0 || Streams > 50 || Transcodes < 0 || Transcodes > Streams ||
            BitrateKbps < 0 || BitrateKbps > 1_000_000 ||
            AudioStreams < 0 || AudioStreams > 50 || AudioTranscodes < 0 || AudioTranscodes > AudioStreams)
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
