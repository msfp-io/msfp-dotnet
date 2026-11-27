using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// A video title key, parsed (MSFP Media video 4): movie/tmdb/949,
/// series/tmdb/1399, series/tmdb/1399/s1, series/tmdb/1399/e63056 (an
/// episode by TMDB's ID of it), series/tmdb/1399/s1/e2 (an episode by its
/// numbers), series/tvdb/81189/a27 (by its absolute number),
/// movie/local/q3Zp0Lw8, movie/tmdb/348/x/Tr7aQ1 (an extra).
/// </summary>
/// <param name="Kind">movie, series, season, episode or extra.</param>
/// <param name="Scheme"><see cref="Video.SchemeTmdb"/>, <see cref="Video.SchemeTvdb"/> or <see cref="Video.SchemeLocal"/>.</param>
/// <param name="Id">The film's or the series' TMDB or TheTVDB ID; 0 under the local scheme.</param>
/// <param name="Local">The film's or the series' opaque identifier under the local scheme; empty otherwise.</param>
/// <param name="Season">A season's number, or an episode's season when it is named by its numbers.</param>
/// <param name="Episode">An episode's number, when it is named by its numbers; with <paramref name="Absolute"/>, TheTVDB's absolute number.</param>
/// <param name="Absolute">Whether the episode is named by its absolute number (series/tvdb/&lt;id&gt;/a&lt;number&gt;).</param>
/// <param name="EpisodeId">TMDB's ID of an episode named by it; 0 for one named by its numbers.</param>
/// <param name="Extra">An extra's opaque part, after x/; the other members are then those of the title it belongs to, of kind <paramref name="ParentKind"/>.</param>
/// <param name="ParentKind">For an extra, the kind of the title it belongs to: movie, series or season; empty otherwise.</param>
public readonly record struct VideoKey(
    string Kind,
    string Scheme,
    long Id = 0,
    string Local = "",
    int Season = 0,
    int Episode = 0,
    bool Absolute = false,
    long EpisodeId = 0,
    string Extra = "",
    string ParentKind = "")
{
    /// <summary>Whether play takes the title, and whether it carries copies: a film, an episode or an extra (MSFP Media video 5).</summary>
    public bool Playable => Kind is "movie" or "episode" or "extra";

    /// <summary>
    /// Whether the key names an episode by TMDB's ID of it
    /// (series/tmdb/&lt;id&gt;/e&lt;episode id&gt;): such an episode carries
    /// its <see cref="Protocol.Place"/> (MSFP Media video 3.1 and 4.1).
    /// </summary>
    public bool ByEpisodeId => Kind == "episode" && EpisodeId > 0;

    /// <summary>
    /// The key of the film or the series this key is, or is under: its
    /// first three parts (movie/tmdb/949, series/tvdb/81189).
    /// </summary>
    public string Series
    {
        get
        {
            var kind = Kind == "movie" || (Kind == "extra" && ParentKind == "movie") ? "movie" : "series";
            var id = Scheme == Video.SchemeLocal ? Local : Id.ToString(CultureInfo.InvariantCulture);
            return kind + "/" + Scheme + "/" + id;
        }
    }

    /// <summary>For an extra, the title it belongs to (MSFP Media video 4.6); null for any other title.</summary>
    public VideoKey? Parent => Kind == "extra" ? this with { Kind = ParentKind, ParentKind = "", Extra = "" } : null;

    /// <summary>
    /// Reads a video title key in its one canonical form (MSFP Media video 4):
    /// the kind, the scheme (tmdb, tvdb for series only, local), the ID (1
    /// to 9,999,999,999) or the local identifier
    /// (<see cref="CatalogRules.OpaqueKeyPart"/>), then for a season
    /// s&lt;season&gt; (0, or 1 to 4 digits), for an episode
    /// e&lt;episode id&gt; (tmdb only, 1 to 10 digits),
    /// s&lt;season&gt;/e&lt;episode&gt; (episode 0, or 1 to 5 digits) or
    /// a&lt;number&gt; (tvdb only, as episodes); never a leading zero. An
    /// extra is the key of a film, a series or a season, then x/&lt;opaque&gt;.
    /// Any other key is false: another form of the same title is another
    /// string, which a receiver skips.
    /// </summary>
    /// <param name="key">The key's text.</param>
    /// <param name="parsed">The key; default when false.</param>
    public static bool TryParse(string key, out VideoKey parsed)
    {
        ArgumentNullException.ThrowIfNull(key);
        parsed = default;
        var parts = key.Split('/');
        var n = parts.Length;
        if (n >= 5 && parts[n - 2] == "x")
        {
            if (!TryParse(string.Join('/', parts[..^2]), out var parent) || !CatalogRules.OpaqueKeyPart(parts[n - 1]) ||
                parent.Kind is not ("movie" or "series" or "season"))
            {
                return false;
            }

            parsed = parent with { Kind = "extra", ParentKind = parent.Kind, Extra = parts[n - 1] };
            return true;
        }

        if (n is < 3 or > 5)
        {
            return false;
        }

        var k = new VideoKey(parts[0], parts[1]);
        bool ok;
        switch (k.Scheme)
        {
            case Video.SchemeTmdb or Video.SchemeTvdb:
                ok = Decimal(parts[2], 10, false, out var id);
                k = k with { Id = id };
                break;
            case Video.SchemeLocal:
                ok = CatalogRules.OpaqueKeyPart(parts[2]);
                k = k with { Local = parts[2] };
                break;
            default:
                return false;
        }

        if (!ok)
        {
            return false;
        }

        if (n == 3 && ((k.Kind == "movie" && k.Scheme != Video.SchemeTvdb) || k.Kind == "series"))
        {
            parsed = k;
            return true;
        }

        if (k.Kind != "series" || n == 3)
        {
            return false;
        }

        if (n == 4 && parts[3].StartsWith('e') && k.Scheme == Video.SchemeTmdb)
        {
            if (!Decimal(parts[3][1..], 10, false, out var episodeId))
            {
                return false;
            }

            parsed = k with { Kind = "episode", EpisodeId = episodeId };
            return true;
        }

        if (n == 4 && parts[3].StartsWith('a') && k.Scheme == Video.SchemeTvdb)
        {
            if (!Decimal(parts[3][1..], 5, true, out var absolute))
            {
                return false;
            }

            parsed = k with { Kind = "episode", Episode = (int)absolute, Absolute = true };
            return true;
        }

        if (!parts[3].StartsWith('s') || !Decimal(parts[3][1..], 4, true, out var season))
        {
            return false;
        }

        if (n == 4)
        {
            parsed = k with { Kind = "season", Season = (int)season };
            return true;
        }

        if (!parts[4].StartsWith('e') || !Decimal(parts[4][1..], 5, true, out var episode))
        {
            return false;
        }

        parsed = k with { Kind = "episode", Season = (int)season, Episode = (int)episode };
        return true;
    }

    /// <summary>The key, as <see cref="TryParse"/> reads it.</summary>
    public override string ToString()
    {
        if (Parent is { } parent)
        {
            return parent + "/x/" + Extra;
        }

        var series = Series;
        return Kind switch
        {
            "movie" or "series" => series,
            "season" => series + "/s" + Season.ToString(CultureInfo.InvariantCulture),
            _ when EpisodeId > 0 => series + "/e" + EpisodeId.ToString(CultureInfo.InvariantCulture),
            _ when Absolute => series + "/a" + Episode.ToString(CultureInfo.InvariantCulture),
            _ => series + "/s" + Season.ToString(CultureInfo.InvariantCulture) + "/e" +
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
/// An episode's place in TMDB's numbering (MSFP Media video 3 and 4.1): the
/// season and episode numbers TMDB gives it now, in the series' own season
/// list. Carried by episodes keyed by their TMDB episode ID only, and
/// required for them.
/// </summary>
/// <param name="Season">The season number, 0 (specials) to <see cref="Video.MaxSeason"/>.</param>
/// <param name="Episode">The episode number, 0 (a pilot or prologue) to <see cref="Video.MaxEpisode"/>.</param>
public sealed record Place(int Season, int Episode)
{
    /// <summary>Whether both numbers are in range (MSFP Media video 3.1): what a sender sends, and a receiver keeps.</summary>
    public bool Valid => Season is >= 0 and <= Video.MaxSeason && Episode is >= 0 and <= Video.MaxEpisode;
}

/// <summary>
/// A peer's video title, as kept (MSFP Media video 3.1): its key, the kind the
/// key names (a title carries no kind member), the library it gives, and
/// the members video adds, cleaned; a list empty, a string empty and an
/// object null when absent.
/// </summary>
/// <param name="Key">Its key, canonical.</param>
/// <param name="Kind">The kind its key names.</param>
/// <param name="Library">The library it gives, still to be checked by the caller.</param>
/// <param name="Copies">Its copies (films, episodes and extras, which always keep at least one), in the owning server's order.</param>
/// <param name="Place">Its place in TMDB's numbering: for episodes keyed by their TMDB episode ID, and always for them; null for any other title.</param>
/// <param name="With">The other episodes it holds (episodes).</param>
/// <param name="Ids">Its other identifiers, by scheme in byte order (episodes of a public database, series keyed under tvdb).</param>
/// <param name="ExtraType">What an extra is, a token (trailer, featurette, behindthescenes, deletedscene, interview, scene, short, clip, themesong, themevideo…), known or not; empty for any other title. An extra carries no name: it is shown by its type and a number.</param>
/// <param name="Description">A local film's or series' description (MSFP Media video 3.10), and always for them; null for any other title.</param>
public sealed record VideoTitle(
    string Key,
    string Kind,
    string Library,
    IReadOnlyList<Copy> Copies,
    Place? Place,
    IReadOnlyList<string> With,
    IReadOnlyDictionary<string, string> Ids,
    string ExtraType,
    LocalDescription? Description);

/// <summary>
/// Video titles (MSFP video, sections 3 and 4): their keys, of the tmdb,
/// tvdb and local schemes and of extras, and what a server keeps of a title
/// a peer lists in titles (MSFP Media listing 3.3, MSFP Media video 3.1). Copies
/// are in <see cref="Copies"/>; the previews call is in VideoPreviews.cs.
/// </summary>
public static partial class Video
{
    /// <summary>The scheme of TMDB's IDs (MSFP Media video 4).</summary>
    public const string SchemeTmdb = "tmdb";

    /// <summary>The scheme of TheTVDB's IDs, for series only (MSFP Media video 4.4).</summary>
    public const string SchemeTvdb = "tvdb";

    /// <summary>The scheme of a title of no public database, under an opaque identifier (MSFP Media video 4.5).</summary>
    public const string SchemeLocal = "local";

    /// <summary>How many language tags a list of them holds at most (<see cref="Languages.SentTags"/>).</summary>
    public const int MaxTags = 12;

    /// <summary>How many other episodes' keys an episode's with lists at most (MSFP Media video 4.2).</summary>
    public const int MaxWith = 16;

    /// <summary>The greatest season number of a <see cref="Place"/> (MSFP Media video 3.1).</summary>
    public const int MaxSeason = 9999;

    /// <summary>The greatest episode number of a <see cref="Place"/> (MSFP Media video 3.1).</summary>
    public const int MaxEpisode = 99999;

    /// <summary>The values of a transcode's maxHeight (MSFP Media playing 2.7).</summary>
    public static readonly IReadOnlyList<int> Heights = [480, 720, 1080, 1440, 2160, 4320];

    [GeneratedRegex(@"^tt[0-9]{7,10}\z")]
    private static partial Regex ImdbRe();

    [GeneratedRegex(@"^[1-9][0-9]{0,9}\z")]
    private static partial Regex TvdbRe();

    /// <summary>
    /// Keeps of an episode's with the keys of other episodes of the same
    /// series (the same scheme and ID), each once, at most
    /// <see cref="MaxWith"/>; a bad key is skipped alone.
    /// </summary>
    public static IReadOnlyList<string> CleanWith(VideoKey episode, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var self = episode.ToString();
        var series = episode.Series;
        var out_ = new List<string>();
        foreach (var s in keys)
        {
            if (out_.Count == MaxWith)
            {
                break;
            }

            if (s is null || !VideoKey.TryParse(s, out var k) || k.Kind != "episode" || k.Series != series || s == self ||
                out_.Contains(s, StringComparer.Ordinal))
            {
                continue;
            }

            out_.Add(s);
        }

        return out_;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an identifier of a known scheme
    /// (MSFP Media video 4.3, the registry's ids schemes: imdb, tvdb), in its
    /// form; tmdb, which only a title keyed under another scheme carries, is
    /// <see cref="OtherId(VideoKey, string, string)"/>'s.
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
    /// Whether <paramref name="value"/> is an identifier a title of
    /// <paramref name="key"/> carries in ids (MSFP Media video 4.3, the
    /// registry's ids schemes): only episodes of the tmdb and tvdb schemes
    /// (imdb, tvdb, and tmdb when keyed under tvdb) and series keyed under
    /// tvdb (imdb, tmdb); never films and series keyed under tmdb, whose
    /// other identifiers TMDB's lookup by key gives, nor local titles. What a
    /// sender sends, and a receiver keeps.
    /// </summary>
    public static bool OtherId(VideoKey key, string scheme, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var episode = key.Kind == "episode" && key.Scheme != SchemeLocal;
        var tvdbSeries = key.Kind == "series" && key.Scheme == SchemeTvdb;
        return scheme switch
        {
            "imdb" => (episode || tvdbSeries) && ImdbRe().IsMatch(value),
            "tvdb" => episode && TvdbRe().IsMatch(value),
            SchemeTmdb => (episode || tvdbSeries) && key.Scheme == SchemeTvdb && TvdbRe().IsMatch(value),
            _ => false,
        };
    }

    /// <summary>
    /// Steps a height down to the greatest of <see cref="Heights"/> not above
    /// it, 480 below 480: a transcode's maxHeight for a picture at most that
    /// tall (MSFP Media playing 2.7); 0 stays 0, not said.
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

    /// <summary>
    /// Keeps of a video stream's hdr entries (MSFP Media video 3.3 and 3.8)
    /// the tokens, known or not, each once, sorted in byte order, the first
    /// <see cref="Copies.MaxHdr"/>; an entry that isn't a token is skipped.
    /// </summary>
    public static IReadOnlyList<string> CleanHdr(IEnumerable<string> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        return formats.Where(Names.IsToken).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(Copies.MaxHdr).ToList();
    }

    /// <summary>
    /// Reads one entry of a titles answer as MSFP Media listing 3.3 and MSFP
    /// Media video 3.1, 3.8, 3.9 and 3.10 say: null, to skip it, when it isn't
    /// an object whose key and library are strings, when the key isn't a
    /// canonical video key of a known scheme, when it is a film, an episode or an extra whose copies isn't an
    /// array or keeps no copy (<see cref="Copies.Read(JsonNode?)"/>), an
    /// extra whose extraType is missing or isn't a token, an episode keyed by
    /// its TMDB episode ID without a valid place, or a local film or series
    /// without a name. A member of the wrong type otherwise
    /// counts as absent (null among them); a bad entry of one of its lists is
    /// skipped alone; members a title may not carry are ignored. Whether
    /// library is one asked for is the caller's to check.
    /// </summary>
    /// <remarks>The entry isn't changed.</remarks>
    public static VideoTitle? ReadTitle(JsonNode? entry)
    {
        if (entry is not JsonObject o ||
            JsonMembers.AsString(o["key"]) is not { } key ||
            JsonMembers.AsString(o["library"]) is not { } library)
        {
            return null;
        }

        // A kind member is unknown, ignored: the key names the kind.
        if (!VideoKey.TryParse(key, out var k))
        {
            return null;
        }

        // Extras (section 3.9): no name.
        var extraType = "";
        if (k.Kind == "extra")
        {
            if (Names.Token(o["extraType"]) is not { } et)
            {
                return null;
            }

            extraType = et;
        }

        // The description of a local film or series (section 3.10).
        LocalDescription? description = null;
        if (k.Scheme == SchemeLocal && (k.Kind is "movie" or "series") &&
            (description = CatalogRules.ReadDescription(o, DescriptionBounds.Local)) is null)
        {
            return null;
        }

        // An episode keyed by its TMDB episode ID can't be placed from its
        // key: without a place, it is skipped (section 4.1).
        Place? place = null;
        if (k.EpisodeId > 0 && (place = ReadPlace(o["place"])) is null)
        {
            return null;
        }

        IReadOnlyList<string> with = k.Kind == "episode" ? CleanWith(k, Copies.Strings(o["with"])) : [];

        // ids (section 4.3): episodes of a public database, series keyed
        // under tvdb.
        var ids = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (o["ids"] is JsonObject idList)
        {
            foreach (var (scheme, v) in idList)
            {
                if (JsonMembers.AsString(v) is { } value && OtherId(k, scheme, value))
                {
                    ids[scheme] = value;
                }
            }
        }

        // Copies (sections 3.2 and 3.8).
        IReadOnlyList<Copy> copies = [];
        if (k.Playable)
        {
            if (o.TryGetPropertyValue("copies", out var list) && list is not JsonArray)
            {
                return null;
            }

            if ((copies = Copies.ReadList(list as JsonArray)).Count == 0)
            {
                return null;
            }
        }

        return new VideoTitle(key, k.Kind, library, copies, place, with, ids, extraType, description);
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

    // Reads place as a receiver keeps it (MSFP Media video 3.1): null unless it
    // is an object whose season and episode are MSFP integers in range
    // (MSFP Media base 2.2: 1.0 and "1" aren't); other members are ignored.
    private static Place? ReadPlace(JsonNode? node)
    {
        if (node is not JsonObject o || !Copies.Integer(o["season"], 0, MaxSeason, out var season) ||
            !Copies.Integer(o["episode"], 0, MaxEpisode, out var episode))
        {
            return null;
        }

        return new Place((int)season, (int)episode);
    }

    // Whether a node is JSON true.
    internal static bool True(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;
}
