using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// A music title key, parsed (music section 2): its kind (artist, album or
/// track), its scheme (mb or local), and the scheme's part. Under mb,
/// <see cref="Id"/> is an MBID: an artist's, a release's for an album, a
/// release track's for a track. Under local, it is the owning server's
/// opaque part (<see cref="CatalogRules.OpaqueKeyPart"/>).
/// </summary>
/// <param name="Kind">artist, album or track.</param>
/// <param name="Scheme">mb or local.</param>
/// <param name="Id">The MBID, or the opaque part.</param>
public readonly record struct MusicKey(string Kind, string Scheme, string Id)
{
    /// <summary>Whether play takes the title: a track (music section 5).</summary>
    public bool Playable => Kind == "track";

    /// <summary>Whether the key is under the local scheme: a title that never matches another server's (music section 2.2).</summary>
    public bool Local => Scheme == "local";

    /// <summary>
    /// Reads a music title key in its one canonical form,
    /// &lt;kind&gt;/mb/&lt;mbid&gt; or &lt;kind&gt;/local/&lt;opaque&gt;; false for
    /// anything else, an MBID in uppercase or a trailing slash included.
    /// </summary>
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
        var second = rest.IndexOf('/', StringComparison.Ordinal);
        if ((kind != "artist" && kind != "album" && kind != "track") || second < 0)
        {
            return false;
        }

        var scheme = rest[..second];
        var id = rest[(second + 1)..];
        if ((scheme == "mb" && Music.Mbid(id)) || (scheme == "local" && CatalogRules.OpaqueKeyPart(id)))
        {
            parsed = new MusicKey(kind, scheme, id);
            return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="s"/> is a canonical music key of <paramref name="kind"/>, of either scheme.</summary>
    public static bool Is(string? s, string kind) => s is not null && TryParse(s, out var k) && k.Kind == kind;

    /// <summary>The key, as <see cref="TryParse"/> reads it.</summary>
    public override string ToString() => Kind + "/" + Scheme + "/" + Id;
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
    /// <summary>Reads a title key of either content part of version 0, in its canonical form only; false for any other text.</summary>
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
/// A music title as sent in titles, and as a peer's is kept (music section
/// 3): its key, kind and library, and its description, cleaned. Text is ""
/// and numbers 0 when absent, lists empty; <see cref="Compilation"/> and
/// <see cref="Image"/> are null when absent. A track carries no dates and
/// no genres, an artist only a name, an image and ids. An owning server
/// writes its own titles with <see cref="ToJson"/>, and reads them back
/// with <see cref="Music.ReadTitle(ReadOnlySpan{byte})"/> to send only what
/// a receiver keeps.
/// </summary>
public sealed record MusicTitle
{
    /// <summary>Its key, canonical.</summary>
    public required string Key { get; init; }

    /// <summary>Its kind, the key's.</summary>
    public required string Kind { get; init; }

    /// <summary>The library it gives; a reader leaves the caller to check it is one asked for.</summary>
    public required string Library { get; init; }

    /// <summary>Its name, cleaned; never empty.</summary>
    public required string Name { get; init; }

    /// <summary>Its sort name (albums).</summary>
    public string SortName { get; init; } = "";

    /// <summary>Its artists' keys (albums and tracks).</summary>
    public IReadOnlyList<string> Artists { get; init; } = [];

    /// <summary>Its artists' names as credited (albums and tracks).</summary>
    public string ArtistNames { get; init; } = "";

    /// <summary>
    /// Its release group's MusicBrainz primary type, lowercased (album,
    /// single, ep, broadcast, other…), open (<see cref="Music.MusicType"/>):
    /// kept as it is, known or not (albums).
    /// </summary>
    public string PrimaryType { get; init; } = "";

    /// <summary>
    /// Its release group's MusicBrainz secondary types, lowercased
    /// (compilation, soundtrack, live, remix, dj-mix, audio drama,
    /// mixtape/street…), open, each once, at most <see cref="Music.MaxSecondaryTypes"/> (albums).
    /// </summary>
    public IReadOnlyList<string> SecondaryTypes { get; init; } = [];

    /// <summary>Whether it is a compilation of several artists (albums); null when absent.</summary>
    public bool? Compilation { get; init; }

    /// <summary>Its release's date, YYYY, YYYY-MM or YYYY-MM-DD (albums and tracks).</summary>
    public string Date { get; init; } = "";

    /// <summary>The date of its first release, never after <see cref="Date"/>'s year (albums and tracks).</summary>
    public string OriginalDate { get; init; } = "";

    /// <summary>Its genres, cleaned (albums and tracks).</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Its album's key (tracks).</summary>
    public string Album { get; init; } = "";

    /// <summary>Its disc's number (tracks).</summary>
    public int Disc { get; init; }

    /// <summary>Its number on the disc (tracks).</summary>
    public int Number { get; init; }

    /// <summary>Its copies, best first (tracks: at least one).</summary>
    public IReadOnlyList<MusicCopy> Copies { get; init; } = [];

    /// <summary>Whether the image call gives an image of it (local artists and albums); null when absent.</summary>
    public bool? Image { get; init; }

    /// <summary>Its other identifiers, by scheme (music section 3.1).</summary>
    public IReadOnlyDictionary<string, string> Ids { get; init; } = new Dictionary<string, string>();

    /// <summary>The title as titles carries it, its members in the spec's order, absent ones left out.</summary>
    public byte[] ToJson() => JsonOut.Object(Write);

    /// <summary>Writes the title's members, as <see cref="ToJson"/>.</summary>
    public void Write(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.String("key", Key).String("kind", Kind).String("library", Library).String("name", Name)
            .String("sortName", Music.Present(SortName))
            .Strings("artists", Artists.Count > 0 ? Artists : null)
            .String("artistNames", Music.Present(ArtistNames))
            .String("primaryType", Music.Present(PrimaryType))
            .Strings("secondaryTypes", SecondaryTypes.Count > 0 ? SecondaryTypes : null)
            .Bool("compilation", Compilation)
            .String("date", Music.Present(Date))
            .String("originalDate", Music.Present(OriginalDate))
            .Strings("genres", Genres.Count > 0 ? Genres : null)
            .String("album", Music.Present(Album))
            .Integer("disc", Disc != 0 ? Disc : null)
            .Integer("number", Number != 0 ? Number : null)
            .Objects("copies", Copies.Count > 0 ? Copies : null, (w, c) => c.Write(w))
            .Bool("image", Image)
            .Object("ids", Ids.Count > 0 ? w => { foreach (var (k, v) in Ids) { w.String(k, v); } } : null);
    }
}

/// <summary>
/// A track's copy (music section 3.3). The optional numbers are 0 when
/// absent (none of their ranges holds 0), optional text "".
/// </summary>
public sealed record MusicCopy
{
    /// <summary>Its id, of an ID's form.</summary>
    public required string Id { get; init; }

    /// <summary>A quality label, plain text of at most <see cref="Music.MaxCopyName"/> code points.</summary>
    public string Name { get; init; } = "";

    /// <summary>Its duration, 1 to <see cref="Music.MaxDurationMs"/>.</summary>
    public required long DurationMs { get; init; }

    /// <summary>Its container, a token: ffmpeg's muxer name (flac, mp3, mp4, ogg, wav, matroska…), known or not.</summary>
    public required string Container { get; init; }

    /// <summary>Its audio codec, a token: ffmpeg's codec_name (flac, mp3, aac, alac, opus, vorbis, pcm_s16le…), known or not.</summary>
    public required string Codec { get; init; }

    /// <summary>Its RFC 6381 codecs string (mp4a.40.2, fLaC, opus, ec-3), 1 to 64 characters of A-Z a-z 0-9 . -; "" when absent.</summary>
    public string Codecs { get; init; } = "";

    /// <summary>Its size in bytes, 1 to <see cref="Music.MaxCopySize"/>.</summary>
    public required long Size { get; init; }

    /// <summary>Its etag, of an ID's form.</summary>
    public required string Etag { get; init; }

    /// <summary>When it was added, YYYY-MM-DD.</summary>
    public string Added { get; init; } = "";

    /// <summary>Its bitrate in kbit/s, 1 to <see cref="Music.MaxAudioBitrate"/>, exact.</summary>
    public int BitrateKbps { get; init; }

    /// <summary>Its sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Its bit depth.</summary>
    public int BitDepth { get; init; }

    /// <summary>Its channels.</summary>
    public int Channels { get; init; }

    /// <summary>Its loudness values; null when absent.</summary>
    public ReplayGain? ReplayGain { get; init; }

    /// <summary>"plain" or "synced" when the lyrics call gives its lyrics; "" otherwise.</summary>
    public string Lyrics { get; init; } = "";

    /// <summary>Writes the copy's members, in the spec's order.</summary>
    public void Write(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.String("id", Id).String("name", Music.Present(Name)).Integer("durationMs", DurationMs)
            .String("container", Container).String("codec", Codec).String("codecs", Music.Present(Codecs))
            .Integer("size", Size).String("etag", Etag)
            .String("added", Music.Present(Added))
            .Integer("bitrateKbps", BitrateKbps != 0 ? BitrateKbps : null)
            .Integer("sampleRate", SampleRate != 0 ? SampleRate : null)
            .Integer("bitDepth", BitDepth != 0 ? BitDepth : null)
            .Integer("channels", Channels != 0 ? Channels : null)
            .Object("replayGain", ReplayGain is { } g ? g.Write : null)
            .String("lyrics", Music.Present(Lyrics));
    }
}

/// <summary>
/// A copy's loudness values, to the ReplayGain 2.0 reference (music section
/// 3.3): gains in dB, peaks linear; each null when absent. The owning server
/// never applies them; the viewer's player does, lowering the gain to keep
/// the peak under full scale.
/// </summary>
/// <param name="TrackGain">The track's gain, within ±<see cref="Music.MaxGain"/>, to 2 decimals.</param>
/// <param name="TrackPeak">The track's peak, 0 to <see cref="Music.MaxPeak"/>, to 6 decimals.</param>
/// <param name="AlbumGain">The album's gain.</param>
/// <param name="AlbumPeak">The album's peak.</param>
public sealed record ReplayGain(double? TrackGain, double? TrackPeak, double? AlbumGain, double? AlbumPeak)
{
    /// <summary>Writes its members, absent ones left out.</summary>
    public void Write(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.Node("trackGain", Num(TrackGain)).Node("trackPeak", Num(TrackPeak))
            .Node("albumGain", Num(AlbumGain)).Node("albumPeak", Num(AlbumPeak));
    }

    private static JsonValue? Num(double? d) => d is { } v ? JsonValue.Create(v) : null;
}

/// <summary>
/// Music titles (MSFP music, sections 2 and 3): their keys, and what a
/// server keeps of a music title a peer lists in titles, its copies
/// included (catalog section 3.3, music sections 3.2 and 3.3). The queue
/// of a play is in MusicQueue.cs, the lyrics call in MusicLyrics.cs.
/// </summary>
public static partial class Music
{
    /// <summary>The most code points of a music title's name and sortName (music section 3).</summary>
    public const int MaxMusicName = 256;

    /// <summary>The most code points of a music title's artistNames (music section 3).</summary>
    public const int MaxArtistNames = 512;

    /// <summary>The most artists a music title keeps (music section 3).</summary>
    public const int MaxMusicArtists = 16;

    /// <summary>The most genres an album or a track keeps (music section 3.2).</summary>
    public const int MaxGenres = 8;

    /// <summary>The most code points of a genre (music section 3.2); a longer one is skipped, not cut.</summary>
    public const int MaxGenre = 64;

    /// <summary>A track's highest disc and number, both from 1 (music section 3).</summary>
    public const int MaxDiscNumber = 999;

    /// <summary>The most copies a track keeps (music section 3.3).</summary>
    public const int MaxMusicCopies = 8;

    /// <summary>A track copy's longest duration in milliseconds, a day (music section 3.3).</summary>
    public const long MaxDurationMs = 86_400_000;

    /// <summary>A track copy's largest size in bytes (music section 3.3, as a video copy's).</summary>
    public const long MaxCopySize = 999_999_999_999_999;

    /// <summary>The most code points of a track copy's name (music section 3.3, as a video copy's).</summary>
    public const int MaxCopyName = 64;

    /// <summary>A track copy's highest bitrate in kbit/s, from 1 (music section 3.3).</summary>
    public const int MaxAudioBitrate = 100_000;

    /// <summary>The most secondary types an album keeps (music section 3.2).</summary>
    public const int MaxSecondaryTypes = 8;

    /// <summary>A ReplayGain gain's bound in dB, either way (music section 3.3).</summary>
    public const double MaxGain = 64;

    /// <summary>A ReplayGain peak's bound (music section 3.3).</summary>
    public const double MaxPeak = 10;

    /// <summary>
    /// The bounds <see cref="CatalogRules.ReadDescription(JsonNode?, DescriptionBounds)"/>
    /// takes for a music title, of either scheme: a name of at most 256
    /// characters, required. Its year is never read: music titles carry
    /// their dates in date and originalDate.
    /// </summary>
    public static DescriptionBounds MusicDescription { get; } = new(true, MaxMusicName, 1, 9999);

    [GeneratedRegex(@"^[a-z0-9._/-](?:[a-z0-9 ._/-]{0,30}[a-z0-9._/-])?\z")]
    private static partial Regex TypeRe();

    [GeneratedRegex(@"^[A-Za-z0-9.-]{1,64}\z")]
    private static partial Regex CodecsRe();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex MbidRe();

    [GeneratedRegex(@"^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}\z")]
    private static partial Regex IsrcRe();

    [GeneratedRegex(@"^([0-9]{4})(?:-([0-9]{2})(?:-([0-9]{2}))?)?\z")]
    private static partial Regex DateRe();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex DayRe();

    /// <summary>
    /// Whether <paramref name="s"/> is a release-group type as music section
    /// 3 sends it: MusicBrainz's name lowercased, 1 to 32 characters of
    /// a–z 0–9 space . _ - /, with no space at either end. Open: a type the
    /// viewer's server doesn't know is kept as it is; the caller doesn't
    /// lowercase (Album isn't one).
    /// </summary>
    public static bool MusicType(string? s) => s is not null && TypeRe().IsMatch(s);

    /// <summary>Whether <paramref name="s"/> is a MusicBrainz ID in the form music section 2.1 gives: 36 characters, lowercase 8-4-4-4-12 hex. An AcoustID has the same form.</summary>
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
    /// Keeps an album's genres as music section 3.2 says: each cleaned, kept
    /// when 1 to <see cref="MaxGenre"/> characters once cleaned (a longer one
    /// skipped, not cut) and not a repeat of a kept one compared without
    /// case; the first <see cref="MaxGenres"/> kept.
    /// </summary>
    public static IReadOnlyList<string> CleanGenres(IEnumerable<string> genres)
    {
        ArgumentNullException.ThrowIfNull(genres);
        var out_ = new List<string>();
        foreach (var raw in genres)
        {
            if (out_.Count == MaxGenres)
            {
                break;
            }

            var g = CleanText(raw, int.MaxValue);
            var n = g.EnumerateRunes().Count();
            if (n >= 1 && n <= MaxGenre && !out_.Any(k => string.Equals(k, g, StringComparison.OrdinalIgnoreCase)))
            {
                out_.Add(g);
            }
        }

        return out_;
    }

    /// <summary>
    /// Reads an album's date or originalDate (music section 3.2): YYYY,
    /// YYYY-MM or YYYY-MM-DD, a year from 1000 to 9999, a month from 01 to
    /// 12, a day that exists in that month. False for anything else, which
    /// counts as absent.
    /// </summary>
    /// <param name="s">The date's text.</param>
    /// <param name="year">Its year; 0 when false.</param>
    public static bool TryMusicDate(string s, out int year)
    {
        ArgumentNullException.ThrowIfNull(s);
        year = 0;
        var m = DateRe().Match(s);
        if (!m.Success)
        {
            return false;
        }

        var y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (y < 1000)
        {
            return false;
        }

        if (m.Groups[2].Success)
        {
            var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (month is < 1 or > 12)
            {
                return false;
            }

            if (m.Groups[3].Success)
            {
                var day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (day < 1 || day > DateTime.DaysInMonth(y, month))
                {
                    return false;
                }
            }
        }

        year = y;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an identifier of a known scheme
    /// for a title of <paramref name="kind"/>, in its form (music section
    /// 3.1): mbreleasegroup (albums), mbrecording, isrc and acoustid
    /// (tracks).
    /// </summary>
    public static bool MusicId(string kind, string scheme, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return scheme switch
        {
            "mbreleasegroup" => kind == "album" && MbidRe().IsMatch(value),
            "mbrecording" or "acoustid" => kind == "track" && MbidRe().IsMatch(value),
            "isrc" => kind == "track" && IsrcRe().IsMatch(value),
            _ => false,
        };
    }

    /// <summary>
    /// Converts an EBU R128 gain (Opus's R128_TRACK_GAIN: 1/256 dB, relative
    /// to −23 LUFS) to ReplayGain 2.0's reference, as an owning server may
    /// send it (music section 3.3 asks for the reference, not the way): the
    /// value over 256, plus 5 dB.
    /// </summary>
    public static double R128Gain(int q78) => (q78 / 256.0) + 5;

    /// <summary>
    /// Reads one entry of a titles answer as catalog section 3.3 and music
    /// section 3.2 say: null, to skip it, when it isn't an object whose key,
    /// kind and library are strings, when the key isn't a canonical music
    /// key of either scheme, when its kind isn't the key's, when it has no
    /// name once cleaned, and for a track, when it is a track/mb whose album
    /// isn't an album/mb key, or has no copy left (a copies that isn't an
    /// array included). Every other member of the wrong type, out of range
    /// or for another kind counts as absent; a bad entry of artists, genres
    /// or ids is skipped alone. Whether library is one asked for is the
    /// caller's to check.
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

        if (!MusicKey.TryParse(key, out var k) || k.Kind != kind ||
            CatalogRules.ReadDescription(t, MusicDescription) is not { } d)
        {
            return null;
        }

        var artists = new List<string>();
        var artistNames = "";
        if (k.Kind != "artist")
        {
            foreach (var s in StringsOf(t["artists"]))
            {
                if (artists.Count == MaxMusicArtists)
                {
                    break;
                }

                if (MusicKey.Is(s, "artist") && !artists.Contains(s))
                {
                    artists.Add(s);
                }
            }

            artistNames = CleanText(JsonMembers.AsString(t["artistNames"]) ?? "", MaxArtistNames);
        }

        var title = new MusicTitle { Key = key, Kind = kind, Library = library, Name = d.Name, Artists = artists, ArtistNames = artistNames };
        if (k.Kind is "album" or "track")
        {
            string date = "", originalDate = "";
            var dateYear = 0;
            if (JsonMembers.AsString(t["date"]) is { } ds && TryMusicDate(ds, out var y))
            {
                (date, dateYear) = (ds, y);
            }

            if (JsonMembers.AsString(t["originalDate"]) is { } os && TryMusicDate(os, out var oy) && (dateYear == 0 || oy <= dateYear))
            {
                originalDate = os;
            }

            title = title with { Date = date, OriginalDate = originalDate, Genres = CleanGenres(StringsOf(t["genres"])) };
        }

        switch (k.Kind)
        {
            case "album":
                var secondary = new List<string>();
                foreach (var s in StringsOf(t["secondaryTypes"]))
                {
                    if (secondary.Count < MaxSecondaryTypes && MusicType(s) && !secondary.Contains(s, StringComparer.Ordinal))
                    {
                        secondary.Add(s);
                    }
                }

                title = title with
                {
                    SortName = CleanText(JsonMembers.AsString(t["sortName"]) ?? "", MaxMusicName),
                    PrimaryType = JsonMembers.AsString(t["primaryType"]) is { } pt && MusicType(pt) ? pt : "",
                    SecondaryTypes = secondary,
                    Compilation = BoolOf(t["compilation"]),
                };
                break;
            case "track":
                var album = JsonMembers.AsString(t["album"]) is { } a && MusicKey.Is(a, "album") ? a : "";
                if (k.Scheme == "mb" && !album.StartsWith("album/mb/", StringComparison.Ordinal))
                {
                    return null;
                }

                if (ReadCopies(t["copies"]) is not { Count: > 0 } copies)
                {
                    return null;
                }

                title = title with
                {
                    Album = album,
                    Disc = (int)(IntOf(t["disc"], 1, MaxDiscNumber) ?? 0),
                    Number = (int)(IntOf(t["number"], 1, MaxDiscNumber) ?? 0),
                    Copies = copies,
                };
                break;
        }

        if (k.Local && k.Kind != "track")
        {
            title = title with { Image = BoolOf(t["image"]) };
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

        return title with { Ids = ids };
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

    /// <summary>
    /// Reads a track's copies as music section 3.3 says: null when copies
    /// isn't an array; else the copies kept, best first, at most
    /// <see cref="MaxMusicCopies"/>, the first. A copy is skipped alone when
    /// it isn't an object, misses a required member or breaks its form (id
    /// and etag of an ID's form, size from 1 to <see cref="MaxCopySize"/>,
    /// durationMs from 1 to <see cref="MaxDurationMs"/>, container and codec
    /// tokens, core section 8.5, kept as they are, known or not), or repeats
    /// a kept copy's id. Its optional members out of range, out of their form
    /// or of the wrong type count as absent; durationMs and bitrateKbps are
    /// kept exact; video's members of a copy are ignored.
    /// </summary>
    public static IReadOnlyList<MusicCopy>? ReadCopies(JsonNode? copies)
    {
        if (copies is not JsonArray list)
        {
            return null;
        }

        var out_ = new List<MusicCopy>();
        foreach (var e in list)
        {
            if (out_.Count == MaxMusicCopies)
            {
                break;
            }

            if (ReadCopy(e) is { } c && !out_.Any(k => k.Id == c.Id))
            {
                out_.Add(c);
            }
        }

        return out_;
    }

    // One copy; null skips it.
    private static MusicCopy? ReadCopy(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }

        var id = JsonMembers.AsString(o["id"]);
        var etag = JsonMembers.AsString(o["etag"]);
        var container = Names.Token(o["container"]);
        var codec = Names.Token(o["codec"]);
        if (id is null || !Playback.Id(id) || etag is null || !Playback.Id(etag) || container is null || codec is null ||
            IntOf(o["durationMs"], 1, MaxDurationMs) is not { } duration || IntOf(o["size"], 1, MaxCopySize) is not { } size)
        {
            return null;
        }

        ReplayGain? gain = null;
        if (o["replayGain"] is JsonObject rg)
        {
            var g = new ReplayGain(
                NumberOf(rg["trackGain"], -MaxGain, MaxGain, 2),
                NumberOf(rg["trackPeak"], 0, MaxPeak, 6),
                NumberOf(rg["albumGain"], -MaxGain, MaxGain, 2),
                NumberOf(rg["albumPeak"], 0, MaxPeak, 6));
            if (g.TrackGain is not null || g.TrackPeak is not null || g.AlbumGain is not null || g.AlbumPeak is not null)
            {
                gain = g;
            }
        }

        var lyrics = JsonMembers.AsString(o["lyrics"]);
        return new MusicCopy
        {
            Id = id,
            Name = JsonMembers.AsString(o["name"]) is { } name ? CleanText(name, MaxCopyName) : "",
            DurationMs = duration,
            Container = container,
            Codec = codec,
            Codecs = JsonMembers.AsString(o["codecs"]) is { } cs && CodecsRe().IsMatch(cs) ? cs : "",
            Size = size,
            Etag = etag,
            Added = JsonMembers.AsString(o["added"]) is { } added && Day(added) ? added : "",
            BitrateKbps = (int)(IntOf(o["bitrateKbps"], 1, MaxAudioBitrate) ?? 0),
            SampleRate = (int)(IntOf(o["sampleRate"], 8_000, 768_000) ?? 0),
            BitDepth = (int)(IntOf(o["bitDepth"], 8, 32) ?? 0),
            Channels = (int)(IntOf(o["channels"], 1, 64) ?? 0),
            ReplayGain = gain,
            Lyrics = lyrics is "plain" or "synced" ? lyrics : "",
        };
    }

    // Whether s is a valid date YYYY-MM-DD: a copy's added, as for video.
    private static bool Day(string s) =>
        DayRe().IsMatch(s) && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // Text when set, null (left out) when "".
    internal static string? Present(string s) => s.Length > 0 ? s : null;

    // A JSON boolean; null for any other value.
    internal static bool? BoolOf(JsonNode? node) => node is JsonValue v
        ? v.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;

    // An MSFP integer (core section 2.3) from lo to hi; null for any other
    // value.
    internal static long? IntOf(JsonNode? node, long lo, long hi) =>
        JsonMembers.AsInteger(node) is { } n && n >= lo && n <= hi ? n : null;

    // A JSON number from lo to hi, checked as sent, then rounded to places
    // decimals, to the nearest (never −0); null for any other value, a
    // number too large to read included.
    internal static double? NumberOf(JsonNode? node, double lo, double hi, int places)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number ||
            !double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ||
            !double.IsFinite(f) || f < lo || f > hi)
        {
            return null;
        }

        var r = RoundExact(f, places);
        return r == 0 ? 0 : r;
    }

    // f rounded to places decimals as Go's strconv.FormatFloat(f, 'f',
    // places) rounds it: on the double's exact binary value, ties to even.
    private static double RoundExact(double f, int places)
    {
        if (f == 0)
        {
            return 0;
        }

        var bits = BitConverter.DoubleToInt64Bits(f);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = bits & 0xf_ffff_ffff_ffffL;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        exponent -= 1075; // |f| = mantissa × 2^exponent
        BigInteger num = mantissa * BigInteger.Pow(10, places), den = BigInteger.One;
        if (exponent >= 0)
        {
            num <<= exponent;
        }
        else
        {
            den <<= -exponent;
        }

        var q = BigInteger.DivRem(num, den, out var rem);
        var twice = rem * 2;
        if (twice > den || (twice == den && !q.IsEven))
        {
            q += 1;
        }

        var r = double.Parse(q.ToString(CultureInfo.InvariantCulture) + "e-" + places.ToString(CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture);
        return bits < 0 ? -r : r;
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
