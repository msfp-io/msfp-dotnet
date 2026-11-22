using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// A video title key, parsed (video section 4): movie/tmdb/949,
/// series/tmdb/1399, series/tmdb/1399/s1, series/tmdb/1399/e63056 (an
/// episode by TMDB's ID of it), series/tmdb/1399/s1/e2 (an episode by its
/// numbers, for one TMDB doesn't list).
/// </summary>
/// <param name="Kind">movie, series, season or episode.</param>
/// <param name="Tmdb">The film's or the series' TMDB ID.</param>
/// <param name="Season">A season's number, or an episode's season when it is named by its numbers.</param>
/// <param name="Episode">An episode's number, when it is named by its numbers.</param>
/// <param name="EpisodeId">TMDB's ID of an episode named by it; 0 for one named by its numbers.</param>
public readonly record struct VideoKey(string Kind, long Tmdb, int Season = 0, int Episode = 0, long EpisodeId = 0)
{
    /// <summary>Whether play takes the title: a film or an episode (video section 5).</summary>
    public bool Playable => Kind is "movie" or "episode";

    /// <summary>
    /// Whether the key names an episode by TMDB's ID of it
    /// (series/tmdb/&lt;id&gt;/e&lt;episode id&gt;): such an episode carries
    /// its <see cref="Place"/> (video sections 3.1 and 4.1).
    /// </summary>
    public bool ByEpisodeId => Kind == "episode" && EpisodeId > 0;

    /// <summary>
    /// Reads a video title key in its one canonical form (video section 4):
    /// the kind, the scheme tmdb, the TMDB ID (1 to 9,999,999,999), then for
    /// a season s&lt;season&gt; (0, or 1 to 4 digits), for an episode
    /// e&lt;episode id&gt; (1 to 10 digits) or s&lt;season&gt;/e&lt;episode&gt;
    /// (episode 0, or 1 to 5 digits); never a leading zero. Any other key is
    /// false: another form of the same title is another string, which a
    /// receiver skips.
    /// </summary>
    /// <param name="key">The key's text.</param>
    /// <param name="parsed">The key; default when false.</param>
    public static bool TryParse(string key, out VideoKey parsed)
    {
        ArgumentNullException.ThrowIfNull(key);
        parsed = default;
        var parts = key.Split('/');
        if (parts.Length is < 3 or > 5 || parts[1] != "tmdb")
        {
            return false;
        }

        if (!Decimal(parts[2], 10, false, out var id) || id > 9_999_999_999)
        {
            return false;
        }

        var kind = parts[0];
        if ((kind == "movie" || kind == "series") && parts.Length == 3)
        {
            parsed = new VideoKey(kind, id);
            return true;
        }

        if (kind != "series")
        {
            return false;
        }

        if (parts[3].StartsWith('e') && parts.Length == 4)
        {
            if (!Decimal(parts[3][1..], 10, false, out var episodeId))
            {
                return false;
            }

            parsed = new VideoKey("episode", id, EpisodeId: episodeId);
            return true;
        }

        if (!parts[3].StartsWith('s') || !Decimal(parts[3][1..], 4, true, out var season))
        {
            return false;
        }

        if (parts.Length == 4)
        {
            parsed = new VideoKey("season", id, (int)season);
            return true;
        }

        if (!parts[4].StartsWith('e') || !Decimal(parts[4][1..], 5, true, out var episode))
        {
            return false;
        }

        parsed = new VideoKey("episode", id, (int)season, (int)episode);
        return true;
    }

    /// <summary>The key, as <see cref="TryParse"/> reads it.</summary>
    public override string ToString()
    {
        var id = Tmdb.ToString(CultureInfo.InvariantCulture);
        return Kind switch
        {
            "movie" => "movie/tmdb/" + id,
            "series" => "series/tmdb/" + id,
            "season" => "series/tmdb/" + id + "/s" + Season.ToString(CultureInfo.InvariantCulture),
            _ when EpisodeId > 0 => "series/tmdb/" + id + "/e" + EpisodeId.ToString(CultureInfo.InvariantCulture),
            _ => "series/tmdb/" + id + "/s" + Season.ToString(CultureInfo.InvariantCulture) + "/e" +
                 Episode.ToString(CultureInfo.InvariantCulture),
        };
    }

    // Reads 1 to most digits without a leading zero; "0" itself only when
    // zero is allowed.
    private static bool Decimal(string s, int most, bool zero, out long n)
    {
        n = 0;
        if (s.Length == 0 || s.Length > most || !s.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (s[0] == '0' && (s.Length > 1 || !zero))
        {
            return false;
        }

        n = long.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }
}

/// <summary>
/// What a film's or an episode's copy offers for playing (video section
/// 3.2): its stepped height, and its HDR formats (none for standard range).
/// </summary>
/// <param name="Height">One of <see cref="Video.Heights"/>.</param>
/// <param name="Hdr">Its HDR formats, in <see cref="Video.HdrFormats"/>' order; empty for standard range.</param>
public sealed record Picture(int Height, IReadOnlyList<string> Hdr)
{
    /// <summary>
    /// Whether this is a better copy than <paramref name="other"/> (video
    /// section 3.2): of a greater height, then with HDR when the other has
    /// none, then with more formats.
    /// </summary>
    public bool Better(Picture other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Height != other.Height ? Height > other.Height : Hdr.Count > other.Hdr.Count;
    }
}

/// <summary>
/// An episode's place in TMDB's numbering (video sections 3 and 4.1): the
/// season and episode numbers TMDB gives it now, in the series' own season
/// list. Carried by episodes keyed by their TMDB episode ID only, and
/// required for them.
/// </summary>
/// <param name="Season">The season number, 0 (specials) to <see cref="Video.MaxSeason"/>.</param>
/// <param name="Episode">The episode number, 0 to <see cref="Video.MaxEpisode"/>.</param>
public sealed record Place(int Season, int Episode)
{
    /// <summary>Whether both numbers are in range (video section 3.1): what a sender sends, and a receiver keeps.</summary>
    public bool Valid => Season is >= 0 and <= Video.MaxSeason && Episode is >= 0 and <= Video.MaxEpisode;
}

/// <summary>
/// A peer's video title, as kept: its key, its kind, the library it gives,
/// and the members video adds, cleaned (lists empty when absent).
/// </summary>
/// <param name="Key">Its key, canonical.</param>
/// <param name="Kind">Its kind, the key's.</param>
/// <param name="Library">The library it gives, still to be checked by the caller.</param>
/// <param name="Audio">Its audio languages (films and episodes).</param>
/// <param name="Subtitles">Its subtitle languages (films and episodes).</param>
/// <param name="Video">Its picture (films and episodes); null when absent.</param>
/// <param name="Place">Its place in TMDB's numbering (episodes keyed by their TMDB episode ID, which a receiver skips without one); null for any other title.</param>
/// <param name="With">The other episodes it holds (episodes).</param>
/// <param name="Ids">Its other identifiers, by scheme (not seasons).</param>
public sealed record VideoTitle(
    string Key,
    string Kind,
    string Library,
    IReadOnlyList<string> Audio,
    IReadOnlyList<string> Subtitles,
    Picture? Video,
    Place? Place,
    IReadOnlyList<string> With,
    IReadOnlyDictionary<string, string> Ids);

/// <summary>
/// Video titles (MSFP video, sections 3 and 4): what a server keeps of a
/// title a peer lists in titles (catalog section 3.3, video section 3.1).
/// </summary>
public static partial class Video
{
    /// <summary>
    /// How many tags a title's language list keeps at most (video section
    /// 3.1): each a valid tag (<see cref="Languages.ValidTag"/>), each once.
    /// </summary>
    public const int MaxTags = 12;

    /// <summary>How many other episodes' keys an episode's with lists at most (video section 4.2).</summary>
    public const int MaxWith = 16;

    /// <summary>The greatest season number of a <see cref="Place"/> (video section 3.1).</summary>
    public const int MaxSeason = 9999;

    /// <summary>The greatest episode number of a <see cref="Place"/> (video section 3.1).</summary>
    public const int MaxEpisode = 99999;

    /// <summary>The heights a picture is stepped to (video sections 3.2 and 5.1).</summary>
    public static readonly IReadOnlyList<int> Heights = [480, 720, 1080, 1440, 2160, 4320];

    /// <summary>The HDR formats a picture may list, in their order.</summary>
    public static readonly IReadOnlyList<string> HdrFormats = ["hdr10", "hlg", "dv"];

    [GeneratedRegex(@"^tt[0-9]{7,10}\z")]
    private static partial Regex ImdbRe();

    [GeneratedRegex(@"^[1-9][0-9]{0,9}\z")]
    private static partial Regex TvdbRe();

    /// <summary>Keeps of a peer's tags those <see cref="Languages.ValidTag"/> allows, each once, at most <see cref="MaxTags"/>; a bad tag is skipped alone.</summary>
    public static IReadOnlyList<string> CleanTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var out_ = new List<string>();
        foreach (var t in tags)
        {
            if (out_.Count == MaxTags)
            {
                break;
            }

            if (Languages.ValidTag(t) && !out_.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                out_.Add(t);
            }
        }

        return out_;
    }

    /// <summary>
    /// Keeps of an episode's with the keys of other episodes of the same
    /// series, each once, at most <see cref="MaxWith"/>; a bad key is skipped
    /// alone.
    /// </summary>
    public static IReadOnlyList<string> CleanWith(VideoKey episode, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var self = episode.ToString();
        var out_ = new List<string>();
        foreach (var s in keys)
        {
            if (out_.Count == MaxWith)
            {
                break;
            }

            if (!VideoKey.TryParse(s, out var k) || k.Kind != "episode" || k.Tmdb != episode.Tmdb || s == self ||
                out_.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            out_.Add(s);
        }

        return out_;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an identifier of a known scheme
    /// (video section 4.3: imdb, tvdb), in its form: what a sender sends, and
    /// a receiver keeps.
    /// </summary>
    public static bool OtherId(string scheme, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return scheme switch
        {
            "imdb" => ImdbRe().IsMatch(value),
            "tvdb" => TvdbRe().IsMatch(value),
            _ => false,
        };
    }

    /// <summary>
    /// Steps a height down to the greatest of <see cref="Heights"/> not above
    /// it, 480 below 480 (video section 5.1's coarsening); 0 stays 0, unknown.
    /// </summary>
    public static int StepHeight(int height)
    {
        if (height <= 0)
        {
            return 0;
        }

        var out_ = Heights[0];
        foreach (var s in Heights)
        {
            if (s <= height)
            {
                out_ = s;
            }
        }

        return out_;
    }

    /// <summary>Keeps of formats those <see cref="HdrFormats"/> lists, each once, in its order; none for standard range.</summary>
    public static IReadOnlyList<string> CleanHdr(IEnumerable<string> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var given = formats.ToHashSet(StringComparer.Ordinal);
        return HdrFormats.Where(given.Contains).ToList();
    }

    /// <summary>
    /// Reads one entry of a titles answer as catalog section 3.3 and video
    /// section 3.1 say: null, to skip it, when it isn't an object whose key,
    /// kind and library are strings, when the key isn't a canonical video
    /// key, when its kind isn't the key's, or when it is an episode keyed by
    /// its TMDB episode ID without a valid place. A member video adds of the
    /// wrong type counts as absent; a bad entry of one of its lists is
    /// skipped alone. Whether library is one asked for is the caller's to
    /// check.
    /// </summary>
    /// <remarks>The entry is read as <see cref="StrictJson.ReadEntry(JsonNode?)"/> reads it, its nulls dropped in place.</remarks>
    public static VideoTitle? ReadTitle(JsonNode? entry)
    {
        // hdr null is of the wrong type (core section 2.3): the picture
        // counts as absent, which dropping the null would hide.
        var hdrNull = entry is JsonObject e && e.TryGetPropertyValue("video", out var v) && v is JsonObject vo &&
                      vo.TryGetPropertyValue("hdr", out var h) && h is null;
        if (StrictJson.ReadEntry(entry) is not { } t)
        {
            return null;
        }

        string key, kind, library;
        try
        {
            key = t.GetString("key") ?? "";
            kind = t.GetString("kind") ?? "";
            library = t.GetString("library") ?? "";
        }
        catch (ProtocolException)
        {
            return null;
        }

        if (!VideoKey.TryParse(key, out var k) || k.Kind != kind)
        {
            return null;
        }

        IReadOnlyList<string> audio = [], subtitles = [], with = [];
        Picture? picture = null;
        Place? place = null;

        // Languages for films and episodes only, with for episodes only.
        if (k.Playable)
        {
            audio = CleanTags(Music.StringsOf(t["audio"]));
            subtitles = CleanTags(Music.StringsOf(t["subtitles"]));
            picture = hdrNull ? null : ReadPicture(t["video"]);
        }

        if (k.Kind == "episode")
        {
            with = CleanWith(k, Music.StringsOf(t["with"]));
        }

        // An episode keyed by its TMDB ID is placed by place, required
        // (video section 4.1); place on any other title is ignored.
        if (k.ByEpisodeId && (place = ReadPlace(t["place"])) is null)
        {
            return null;
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (k.Kind != "season" && t["ids"] is JsonObject o)
        {
            foreach (var (scheme, value) in o)
            {
                if (JsonMembers.AsString(value) is { } s && OtherId(scheme, s))
                {
                    ids[scheme] = s;
                }
            }
        }

        return new VideoTitle(key, kind, library, audio, subtitles, picture, place, with, ids);
    }

    /// <summary>Reads one entry of a titles answer from its bytes, as <see cref="ReadTitle(JsonNode?)"/>; null too when they aren't I-JSON.</summary>
    public static VideoTitle? ReadTitle(ReadOnlySpan<byte> raw)
    {
        try
        {
            return ReadTitle(StrictJson.Parse(raw, nulls: true));
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    // Reads place as a receiver keeps it (video section 3.1): null unless it
    // is an object whose season and episode are MSFP integers in range;
    // other members are ignored.
    private static Place? ReadPlace(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }

        var season = Music.IntegerIn(o["season"], 0, MaxSeason);
        var episode = Music.IntegerIn(o["episode"], 0, MaxEpisode);
        return season < 0 || episode < 0 ? null : new Place((int)season, (int)episode);
    }

    // Reads video as a receiver keeps it (video section 3.1): null, absent,
    // unless it is an object whose height is one of Heights as an MSFP
    // integer, and whose hdr, when present, is a list; in hdr, an entry
    // that isn't a string or a format of HdrFormats is skipped alone.
    private static Picture? ReadPicture(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }

        var height = Music.IntegerIn(o["height"], 1, 4320);
        if (!Heights.Contains((int)height))
        {
            return null;
        }

        IReadOnlyList<string> hdr = [];
        if (o.TryGetPropertyValue("hdr", out var list))
        {
            if (list is not JsonArray)
            {
                return null;
            }

            hdr = CleanHdr(Music.StringsOf(list));
        }

        return new Picture((int)height, hdr);
    }
}
