using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>A lyrics call's request: a track key, as the owning server sent it in titles, and the copy's id.</summary>
/// <param name="Title">The track's key.</param>
/// <param name="VersionId">The copy's id.</param>
public sealed record LyricsRequest(string Title, string VersionId)
{
    /// <summary>The request's body.</summary>
    public byte[] ToJson() => JsonOut.Object(o => o.String("title", Title).String("versionId", VersionId));
}

/// <summary>
/// A set of lyrics, one per language, kind and timing (music section 6).
/// </summary>
/// <param name="Lang">A language tag (<see cref="Languages.ValidTag"/>); "" when unknown.</param>
/// <param name="Kind">OpenSubsonic's main, translation or pronunciation, open: a token, kept as it is, known or not; a viewer's server never shows a set of a kind it doesn't know as the main lyrics.</param>
/// <param name="Synced">Whether its lines carry their start.</param>
/// <param name="Offset">The milliseconds to add to every line's start.</param>
/// <param name="DisplayArtist">The artist to show, cleaned; "" when absent.</param>
/// <param name="DisplayTitle">The title to show, cleaned; "" when absent.</param>
/// <param name="Line">Its lines, at least one.</param>
public sealed record LyricsSet(
    string Lang,
    string Kind,
    bool Synced,
    long Offset,
    string DisplayArtist,
    string DisplayTitle,
    IReadOnlyList<LyricsLine> Line)
{
    /// <summary>Writes the set's members, as structuredLyrics carries them.</summary>
    public void Write(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.String("lang", Music.Present(Lang)).String("kind", Kind).Bool("synced", Synced).Integer("offset", Offset)
            .String("displayArtist", Music.Present(DisplayArtist)).String("displayTitle", Music.Present(DisplayTitle))
            .Objects("line", Line, (w, l) => w.Integer("start", l.Start).String("value", l.Value));
    }
}

/// <summary>
/// A line of lyrics: plain text, never markup, possibly empty (a pause),
/// and for synced lyrics when it starts, in milliseconds from the track's
/// start (null for plain lyrics).
/// </summary>
/// <param name="Start">When it starts; null for plain lyrics.</param>
/// <param name="Value">Its text, cleaned.</param>
public sealed record LyricsLine(long? Start, string Value);

/// <summary>
/// The lyrics call (MSFP music section 6): a signed call, with no ticket,
/// for a copy whose lyrics is plain or synced, answered with lyrics shaped
/// as OpenSubsonic's structuredLyrics, times in milliseconds.
/// </summary>
public static partial class Music
{
    /// <summary>The most bytes of a lyrics answer; an answer of exactly this size is read.</summary>
    public const int MaxLyricsBytes = 1 << 20;

    /// <summary>The most sets a lyrics answer keeps.</summary>
    public const int MaxLyricsSets = 8;

    /// <summary>The most lines a set keeps.</summary>
    public const int MaxLyricsLines = 2000;

    /// <summary>The most code points of a line's value.</summary>
    public const int MaxLyricsLine = 512;

    /// <summary>A set's offset's bound in milliseconds, either way.</summary>
    public const long MaxLyricsOffset = 600_000;

    /// <summary>The most code points of a set's displayTitle; displayArtist takes <see cref="MaxArtistNames"/>.</summary>
    public const int MaxDisplayTitle = 256;

    /// <summary>
    /// A lyrics set's kinds as OpenSubsonic names them; the field is open
    /// (core section 8.5): another token is kept, a value that isn't one
    /// (or none) reads as the first, main.
    /// </summary>
    public static readonly IReadOnlyList<string> LyricsKinds = ["main", "translation", "pronunciation"];

    /// <summary>
    /// Reads a lyrics request; false means 400 invalid-request: a body that
    /// isn't I-JSON or an object, a title that isn't a track key, a
    /// versionId that isn't of an ID's form. Whether the owning server offers
    /// the caller that track and copy, with lyrics, is its own check (404
    /// not-found otherwise).
    /// </summary>
    /// <param name="body">The request's body.</param>
    /// <param name="request">The request; null when false.</param>
    public static bool TryReadLyricsRequest(ReadOnlySpan<byte> body, out LyricsRequest? request)
    {
        request = null;
        string? title, versionId;
        try
        {
            var o = StrictJson.ReadRequest(body, ["title", "versionId"]);
            title = o.GetString("title");
            versionId = o.GetString("versionId");
        }
        catch (ProtocolException)
        {
            return false;
        }

        if (!MusicKey.Is(title, "track") || versionId is null || !Playback.Id(versionId))
        {
            return false;
        }

        request = new LyricsRequest(title!, versionId);
        return true;
    }

    /// <summary>
    /// What the viewer's server keeps of a lyrics answer (music section 6):
    /// per set, synced a boolean, else the set is skipped; lang a valid tag,
    /// else absent; kind a token, kept as it is, else main; offset
    /// an integer within ±<see cref="MaxLyricsOffset"/>, else 0;
    /// displayArtist and displayTitle cleaned (<see cref="CleanText"/>),
    /// empty counting as absent. Per line, value a string, cleaned and cut
    /// to <see cref="MaxLyricsLine"/> but kept when empty; for synced lyrics,
    /// start an integer from 0 to <see cref="MaxDurationMs"/>, not before the
    /// line kept before it, else the line is skipped; for plain lyrics,
    /// start ignored. The first <see cref="MaxLyricsLines"/> lines and
    /// <see cref="MaxLyricsSets"/> sets are kept, counting kept ones only; a
    /// set with no line left is skipped.
    /// </summary>
    /// <returns>The sets kept; null for a failed fetch: an answer past <see cref="MaxLyricsBytes"/>, not I-JSON or not an object, whose structuredLyrics isn't an array, or with no set left.</returns>
    public static IReadOnlyList<LyricsSet>? ReadLyrics(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxLyricsBytes)
        {
            return null;
        }

        JsonObject a;
        try
        {
            a = StrictJson.ReadAnswer(body);
        }
        catch (ProtocolException)
        {
            return null;
        }

        if (a["structuredLyrics"] is not JsonArray sets)
        {
            return null;
        }

        var out_ = new List<LyricsSet>();
        foreach (var raw in sets)
        {
            if (out_.Count == MaxLyricsSets)
            {
                break;
            }

            if (ReadLyricsSet(raw) is { } s)
            {
                out_.Add(s);
            }
        }

        return out_.Count > 0 ? out_ : null;
    }

    /// <summary>A lyrics answer's body, as an owning server sends it: structuredLyrics, its sets in order.</summary>
    public static byte[] WriteLyrics(IEnumerable<LyricsSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        return JsonOut.Object(o => o.Objects("structuredLyrics", sets, (w, s) => s.Write(w)));
    }

    // One set; null skips it.
    private static LyricsSet? ReadLyricsSet(JsonNode? node)
    {
        if (node is not JsonObject o || BoolOf(o["synced"]) is not { } synced)
        {
            return null;
        }

        var lang = JsonMembers.AsString(o["lang"]) is { } l && Languages.ValidTag(l) ? l : "";
        var kind = Names.Token(o["kind"]) ?? LyricsKinds[0];
        var offset = IntOf(o["offset"], -MaxLyricsOffset, MaxLyricsOffset) ?? 0;
        var artist = CleanText(JsonMembers.AsString(o["displayArtist"]) ?? "", MaxArtistNames);
        var title = CleanText(JsonMembers.AsString(o["displayTitle"]) ?? "", MaxDisplayTitle);
        var lines = new List<LyricsLine>();
        long last = 0;
        if (o["line"] is JsonArray list)
        {
            foreach (var raw in list)
            {
                if (lines.Count == MaxLyricsLines)
                {
                    break;
                }

                if (raw is not JsonObject lo || JsonMembers.AsString(lo["value"]) is not { } value)
                {
                    continue;
                }

                long? start = null;
                if (synced)
                {
                    if (IntOf(lo["start"], 0, MaxDurationMs) is not { } s || s < last)
                    {
                        continue;
                    }

                    last = s;
                    start = s;
                }

                lines.Add(new LyricsLine(start, CleanText(value, MaxLyricsLine)));
            }
        }

        return lines.Count > 0 ? new LyricsSet(lang, kind, synced, offset, artist, title, lines) : null;
    }
}
