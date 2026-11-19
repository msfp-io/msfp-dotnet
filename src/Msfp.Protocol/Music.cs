using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// A music title key, parsed (music section 2): artist/mb/&lt;mbid&gt;,
/// album/mb/&lt;mbid&gt; (a release group), track/mb/&lt;mbid&gt; (a recording).
/// </summary>
/// <param name="Kind">artist, album or track.</param>
/// <param name="Mbid">Its MusicBrainz ID.</param>
public readonly record struct MusicKey(string Kind, string Mbid)
{
    /// <summary>Whether play takes the title: a track (music section 5).</summary>
    public bool Playable => Kind == "track";

    /// <summary>Reads a music title key in its one canonical form; false for anything else, an MBID in uppercase included.</summary>
    /// <param name="key">The key's text.</param>
    /// <param name="parsed">The key; default when false.</param>
    public static bool TryParse(string key, out MusicKey parsed)
    {
        ArgumentNullException.ThrowIfNull(key);
        parsed = default;
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return false;
        }

        var kind = key[..slash];
        var rest = key[(slash + 1)..];
        if ((kind != "artist" && kind != "album" && kind != "track") || !rest.StartsWith("mb/", StringComparison.Ordinal) ||
            !Music.Mbid(rest[3..]))
        {
            return false;
        }

        parsed = new MusicKey(kind, rest[3..]);
        return true;
    }

    /// <summary>The key, as <see cref="TryParse"/> reads it.</summary>
    public override string ToString() => Kind + "/mb/" + Mbid;
}

/// <summary>
/// A title key of any content part (catalog section 4): its part (video
/// or music), its kind, and whether play takes it.
/// </summary>
/// <param name="Part">The content part: "video" or "music".</param>
/// <param name="Kind">The title's kind.</param>
/// <param name="Playable">Whether play takes it.</param>
public readonly record struct TitleKey(string Part, string Kind, bool Playable)
{
    /// <summary>Reads a title key of either content part of version 1, in its canonical form only; false for any other text.</summary>
    /// <param name="key">The key's text.</param>
    /// <param name="parsed">The key; default when false.</param>
    public static bool TryParse(string key, out TitleKey parsed)
    {
        if (VideoKey.TryParse(key, out var v))
        {
            parsed = new TitleKey(Versions.PartVideo, v.Kind, v.Playable);
            return true;
        }

        if (MusicKey.TryParse(key, out var m))
        {
            parsed = new TitleKey(Versions.PartMusic, m.Kind, m.Playable);
            return true;
        }

        parsed = default;
        return false;
    }
}

/// <summary>
/// A peer's music title, as kept: its key, its kind, the library it gives,
/// and its description, cleaned. Numbers are 0, text empty and lists empty
/// when absent.
/// </summary>
/// <param name="Key">Its key, canonical.</param>
/// <param name="Kind">Its kind, the key's.</param>
/// <param name="Library">The library it gives, still to be checked by the caller.</param>
/// <param name="Name">Its name, cleaned; never empty.</param>
/// <param name="Artists">Its artists' keys (albums and tracks).</param>
/// <param name="ArtistNames">Its artists' names as credited (albums and tracks).</param>
/// <param name="Album">Its album's key (tracks).</param>
/// <param name="Disc">Its disc's number (tracks).</param>
/// <param name="Number">Its number on the disc (tracks).</param>
/// <param name="Year">Its year (albums and tracks).</param>
/// <param name="DurationMs">Its duration in milliseconds (tracks); a durationMs of 0 counts as absent.</param>
/// <param name="Ids">Its other identifiers, by scheme.</param>
public sealed record MusicTitle(
    string Key,
    string Kind,
    string Library,
    string Name,
    IReadOnlyList<string> Artists,
    string ArtistNames,
    string Album,
    int Disc,
    int Number,
    int Year,
    long DurationMs,
    IReadOnlyDictionary<string, string> Ids);

/// <summary>
/// Music titles (MSFP music, sections 2 and 3): their keys, and what a
/// server keeps of a music title a peer lists in titles (catalog section
/// 3.3, music section 3.2).
/// </summary>
public static partial class Music
{
    /// <summary>The most code points of a music title's name (music section 3).</summary>
    public const int MaxMusicName = 256;

    /// <summary>The most code points of a music title's artistNames (music section 3).</summary>
    public const int MaxArtistNames = 512;

    /// <summary>The most artists a music title keeps (music section 3).</summary>
    public const int MaxMusicArtists = 16;

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex MbidRe();

    [GeneratedRegex(@"^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}\z")]
    private static partial Regex IsrcRe();

    /// <summary>Whether <paramref name="s"/> is a MusicBrainz ID in the form music section 2 gives: 36 characters, lowercase 8-4-4-4-12 hex.</summary>
    public static bool Mbid(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return MbidRe().IsMatch(s);
    }

    /// <summary>
    /// Cleans a peer's text as MSFP core section 9.1 cleans a server's name,
    /// cut to <paramref name="max"/> code points: each run of white space one
    /// space, other control and format characters removed, both ends
    /// trimmed, cut, and trimmed again. Text, never markup: whoever shows it
    /// escapes it.
    /// </summary>
    public static string CleanText(string s, int max)
    {
        ArgumentNullException.ThrowIfNull(s);
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var b = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            if (Rune.IsControl(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.Format)
            {
                continue;
            }

            b.Append(r.ToString());
        }

        s = b.ToString().Trim();
        var runes = s.EnumerateRunes().ToList();
        if (runes.Count > max)
        {
            s = string.Concat(runes.Take(max).Select(r => r.ToString())).Trim();
        }

        return s;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an identifier of a known scheme
    /// for a title of <paramref name="kind"/>, in its form (music section
    /// 3.1): mbrelease (albums, tracks), mbtrack and isrc (tracks).
    /// </summary>
    public static bool MusicId(string kind, string scheme, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return scheme switch
        {
            "mbrelease" => MbidRe().IsMatch(value) && kind is "album" or "track",
            "mbtrack" => MbidRe().IsMatch(value) && kind == "track",
            "isrc" => IsrcRe().IsMatch(value) && kind == "track",
            _ => false,
        };
    }

    /// <summary>
    /// Reads one entry of a titles answer as catalog section 3.3 and music
    /// section 3.2 say: null, to skip it, when it isn't an object whose key,
    /// kind and library are strings, when the key isn't a canonical music
    /// key, when its kind isn't the key's, or when it has no name once
    /// cleaned. Every other member of the wrong type, out of range or of
    /// another kind counts as absent; a bad entry of artists or ids is
    /// skipped alone. Whether library is one asked for is the caller's to
    /// check.
    /// </summary>
    /// <remarks>The entry is read as <see cref="StrictJson.ReadEntry(JsonNode?)"/> reads it, its nulls dropped in place.</remarks>
    public static MusicTitle? ReadTitle(JsonNode? entry)
    {
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

        if (!MusicKey.TryParse(key, out var k) || k.Kind != kind)
        {
            return null;
        }

        if (JsonMembers.AsString(t["name"]) is not { } rawName || CleanText(rawName, MaxMusicName) is not { Length: > 0 } name)
        {
            return null;
        }

        var artists = new List<string>();
        var artistNames = "";
        var album = "";
        int disc = 0, number = 0, year = 0;
        long durationMs = 0;
        if (k.Kind is "album" or "track")
        {
            foreach (var s in StringsOf(t["artists"]))
            {
                if (artists.Count == MaxMusicArtists)
                {
                    break;
                }

                if (MusicKey.TryParse(s, out var a) && a.Kind == "artist" && !artists.Contains(s))
                {
                    artists.Add(s);
                }
            }

            if (JsonMembers.AsString(t["artistNames"]) is { } names)
            {
                artistNames = CleanText(names, MaxArtistNames);
            }

            year = (int)IntegerIn(t["year"], 1000, 9999);
        }

        if (k.Kind == "track")
        {
            if (JsonMembers.AsString(t["album"]) is { } a && MusicKey.TryParse(a, out var ak) && ak.Kind == "album")
            {
                album = a;
            }

            disc = (int)IntegerIn(t["disc"], 1, 999);
            number = (int)IntegerIn(t["number"], 1, 999);
            durationMs = Math.Max(IntegerIn(t["durationMs"], 0, 86_400_000), 0);
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (t["ids"] is JsonObject o)
        {
            foreach (var (scheme, value) in o)
            {
                if (JsonMembers.AsString(value) is { } s && MusicId(k.Kind, scheme, s))
                {
                    ids[scheme] = s;
                }
            }
        }

        return new MusicTitle(key, kind, library, name, artists, artistNames, album, disc, number, year, durationMs, ids);
    }

    /// <summary>Reads one entry of a titles answer from its bytes, as <see cref="ReadTitle(JsonNode?)"/>; null too when they aren't I-JSON.</summary>
    public static MusicTitle? ReadTitle(ReadOnlySpan<byte> raw)
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

    // An MSFP integer (core section 2.3: no fraction, no exponent, no minus
    // zero) within lo to hi; 0 otherwise, or -1 when lo is 0 and it isn't
    // one.
    internal static long IntegerIn(JsonNode? node, long lo, long hi)
    {
        var none = lo == 0 ? -1 : 0;
        return JsonMembers.AsInteger(node) is { } n && n >= lo && n <= hi ? n : none;
    }

    // A list's strings: none when it isn't a list, an entry that isn't a
    // string skipped.
    internal static List<string> StringsOf(JsonNode? node)
    {
        var out_ = new List<string>();
        if (node is JsonArray list)
        {
            foreach (var e in list)
            {
                if (JsonMembers.AsString(e) is { } s)
                {
                    out_.Add(s);
                }
            }
        }

        return out_;
    }
}
