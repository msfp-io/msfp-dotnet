using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// The catalog part's own rules (MSFP Media listing 3 and 4), shared by
/// the content parts: pages bounded by size, the opaque parts of title
/// keys, the description of a local title, and its image.
/// </summary>
public static class CatalogRules
{
    /// <summary>How many keys a page of titles holds at most: titles and gone together in a changes answer (MSFP Media listing 3.1).</summary>
    public const int MaxPageTitles = 5000;

    /// <summary>How many bytes of JSON a page's answer body holds at most, 3 MiB (MSFP Media listing 3.1).</summary>
    public const int MaxPageBytes = 3 << 20;

    /// <summary>How many bytes after, and so next, holds at most (MSFP Media listing 3.1).</summary>
    public const int MaxAfter = 512;

    /// <summary>
    /// Room enough in a page for everything but its entries: the object's
    /// members, next (<see cref="MaxAfter"/> bytes), changes and seq. An
    /// owning server writing a page starts its <see cref="PageBudget"/> with
    /// it, unless it counts the rest of its answer itself.
    /// </summary>
    public const int PageReserve = 1024;

    /// <summary>
    /// The caller's stop rule (MSFP Media listing 3.1): it asks for the next
    /// page, with <paramref name="next"/> as after, only when next is set (an
    /// absent one is null or "") and greater than its <paramref name="after"/>
    /// in byte order. A short page with a next is no end.
    /// </summary>
    public static bool AskOn(string? after, string? next) =>
        !string.IsNullOrEmpty(next) && ByteOrder(next, after ?? "") > 0;

    /// <summary>
    /// Whether an owning server may send <paramref name="p"/> (MSFP
    /// Media listing 3.1): at most <see cref="MaxPageTitles"/> keys and
    /// <see cref="MaxPageBytes"/>; in a whole-list answer, no gone, keys after
    /// after, and next the page's last key exactly when titles remain. In a
    /// changes answer, next is the owning server's call. A caller accepts any
    /// answer within the core's bound, whatever this says: it is for owning
    /// servers and their tests.
    /// </summary>
    /// <returns>Null when it may be sent; else why not.</returns>
    public static string? CheckPage(CatalogPage p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var hasNext = p.Next is not null;
        if (p.Titles < 0 || p.Gone < 0 || p.Bytes < 0)
        {
            return "a negative count";
        }

        if (!p.Changes && p.Gone != 0)
        {
            return "gone in a whole-list answer";
        }

        if (p.Titles + p.Gone > MaxPageTitles)
        {
            return p.Changes ? "titles and gone together over 5,000 keys" : "over 5,000 titles";
        }

        if (p.Bytes > MaxPageBytes)
        {
            return "an answer body over 3 MiB (3,145,728 bytes)";
        }

        if (p.Changes)
        {
            return null;
        }

        if (p.Titles > 0 && ByteOrder(p.LastKey, p.After) <= 0)
        {
            return "the page's last key isn't after after";
        }

        if (p.Remain && p.Titles == 0)
        {
            return "an empty page while titles remain";
        }

        if (p.Remain && !hasNext)
        {
            return "next absent while titles remain";
        }

        if (p.Remain && p.Next != p.LastKey)
        {
            return "next isn't the page's last key";
        }

        if (!p.Remain && hasNext)
        {
            return "next set after the last title";
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="s"/> may be an opaque part of a title key: a
    /// local title's identifier, an extra's part after x/ (MSFP
    /// Media listing 4.2). It has a library ID's form, 1 to 64 characters of
    /// A–Z a–z 0–9 _ -, compared byte for byte, and follows a library ID's
    /// rule: chosen by the owning server, never computable from a media
    /// server's ID, a file, a path or a name.
    /// </summary>
    public static bool OpaqueKeyPart(string? s) => s is not null && Playback.Id(s);

    /// <summary>
    /// Reads a local title's description from the title's JSON object, within
    /// <paramref name="bounds"/> (MSFP Media listing 4.3): name cleaned as
    /// <see cref="Music.CleanText"/> does, a name that isn't a string counting
    /// as absent; a year that isn't an integer in range, and an image that
    /// isn't a boolean, absent. An owning server cleans its names the same
    /// way before sending them, and sends no file name.
    /// </summary>
    /// <returns>The description; null when the title is skipped: not an object, or, where the bounds require a name, none or one empty once cleaned.</returns>
    public static LocalDescription? ReadDescription(JsonNode? title, DescriptionBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (title is not JsonObject o)
        {
            return null;
        }

        var name = JsonMembers.AsString(o["name"]) is { } raw ? Music.CleanText(raw, bounds.MaxName) : "";
        if (bounds.NameRequired && name.Length == 0)
        {
            return null;
        }

        var year = (int)Math.Max(Music.IntegerIn(o["year"], Math.Max(bounds.MinYear, 1), bounds.MaxYear), 0);
        var image = o["image"] is JsonValue v && v.GetValueKind() == JsonValueKind.True;
        return new LocalDescription(name, year, image);
    }

    /// <summary>Reads a local title's description from its JSON bytes, as <see cref="ReadDescription(JsonNode?, DescriptionBounds)"/>; null too when they aren't I-JSON.</summary>
    public static LocalDescription? ReadDescription(ReadOnlySpan<byte> title, DescriptionBounds bounds)
    {
        JsonNode? node;
        try
        {
            node = StrictJson.Parse(title, nulls: true);
        }
        catch (ProtocolException)
        {
            return null;
        }

        return ReadDescription(node, bounds);
    }

    /// <summary>
    /// The owning server's answer to an image call (MSFP Media listing 4.4), in
    /// the spec's order: 404 not-supported when no content part with local
    /// titles is on between the two servers (<paramref name="on"/> says, for
    /// <see cref="Versions.PartVideo"/> and <see cref="Versions.PartMusic"/>),
    /// before the body is read; 400 invalid-request when the body isn't
    /// I-JSON, an object, or its title is missing or isn't a string; 404
    /// not-found for a title that isn't a local film, series, artist or album
    /// of a part that's on, or that <paramref name="image"/> doesn't know:
    /// image says whether the owning server offers the caller that key, and
    /// whether its description says image true. Else 200, with the title,
    /// whose image the owning server then gives under a ticket of its own
    /// (<see cref="ImageUrl"/>).
    /// </summary>
    /// <returns>The title ("" but for 200), the status, and the problem's type ("" for 200).</returns>
    public static (string Title, int Status, string Problem) CheckImageRequest(
        Func<string, bool> on, ReadOnlySpan<byte> body, Func<string, bool> image)
    {
        ArgumentNullException.ThrowIfNull(on);
        ArgumentNullException.ThrowIfNull(image);
        if (!on(Versions.PartVideo) && !on(Versions.PartMusic))
        {
            return ("", 404, "not-supported");
        }

        string? title;
        try
        {
            title = StrictJson.ReadRequest(body, ["title"]).GetString("title");
        }
        catch (ProtocolException)
        {
            title = null;
        }

        if (title is null)
        {
            return ("", 400, "invalid-request");
        }

        var k = title.Split('/');
        if (k.Length != 3 || k[1] != "local" || !OpaqueKeyPart(k[2]) || ImagePart(k[0]) is not { } part || !on(part) || !image(title))
        {
            return ("", 404, "not-found");
        }

        return (title, 200, "");
    }

    // The content part of a kind the image call gives an image of (MSFP
    // Media listing 4.4); null for another kind.
    private static string? ImagePart(string kind) => kind switch
    {
        "movie" or "series" => Versions.PartVideo,
        "artist" or "album" => Versions.PartMusic,
        _ => null,
    };

    /// <summary>
    /// The URL of a local title's image a viewer's server builds from the
    /// ticket of image's answer: &lt;stream root&gt;&lt;ticket&gt;/image
    /// (MSFP Media listing 4.4, MSFP Media fetching 1). The ticket works for 10
    /// minutes, for that URL only, and is no playback.
    /// </summary>
    public static string ImageUrl(string root, string ticket) => MediaUrls.Image(root, ticket);

    /// <summary>The body of an image call's answer, as an owning server sends it: {"ticket"}, no URL.</summary>
    public static byte[] WriteImageAnswer(string ticket) => JsonOut.Object(o => o.String("ticket", ticket));

    /// <summary>
    /// Reads an image call's answer (MSFP Media listing 4.4): {"ticket"}, the
    /// ticket of an ID's form (<see cref="Playback.Id"/>); the viewer's server
    /// builds the URL itself (<see cref="ImageUrl"/>). Unknown members (the
    /// url of earlier drafts among them) are ignored. False refuses the
    /// answer: there is no image.
    /// </summary>
    /// <param name="body">The answer's body.</param>
    /// <param name="ticket">The ticket; "" when false.</param>
    public static bool TryReadImageAnswer(ReadOnlySpan<byte> body, out string ticket)
    {
        ticket = "";
        string? t;
        try
        {
            t = JsonMembers.AsString(StrictJson.ReadAnswer(body, keepNestedNulls: true)["ticket"]);
        }
        catch (ProtocolException)
        {
            return false;
        }

        if (t is null || !Playback.Id(t))
        {
            return false;
        }

        ticket = t;
        return true;
    }

    /// <summary>How many bytes a local title's image holds at most, 1 MiB; an image of exactly that size is accepted (MSFP Media listing 4.4).</summary>
    public const int MaxImageBytes = 1 << 20;

    /// <summary>How many pixels wide and tall a local title's image is at most (MSFP Media listing 4.4).</summary>
    public const int MaxImageSide = 4096;

    /// <summary>A JPEG image, as <see cref="TryCheckImage(ReadOnlySpan{byte}, out string, out string)"/> gives its format.</summary>
    public const string ImageJpeg = "jpeg";

    /// <summary>A PNG image, as <see cref="TryCheckImage(ReadOnlySpan{byte}, out string, out string)"/> gives its format.</summary>
    public const string ImagePng = "png";

    /// <summary>
    /// Checks a local title's image, read whole, before it is decoded
    /// (MSFP Media listing 4.4): within <see cref="MaxImageBytes"/>, a JPEG or a
    /// PNG by its content, whatever its Content-Type, whose header (a PNG's
    /// IHDR, a JPEG's frame header) gives a width and a height of 1 to
    /// <see cref="MaxImageSide"/>; a JPEG of height 0, left to a DNL marker,
    /// is refused. The viewer's server then decodes it with these bounds and
    /// re-encodes it in the same format, so that only its pixels reach its
    /// people.
    /// </summary>
    /// <param name="image">The image's bytes.</param>
    /// <param name="format"><see cref="ImageJpeg"/> or <see cref="ImagePng"/>; "" when false.</param>
    /// <param name="reason">Why it is refused; "" when true.</param>
    public static bool TryCheckImage(ReadOnlySpan<byte> image, out string format, out string reason) =>
        TryCheckImage(image, image.Length, out format, out reason);

    // TryCheckImage of an image of size bytes, given its first bytes, head.
    internal static bool TryCheckImage(ReadOnlySpan<byte> head, long size, out string format, out string reason)
    {
        format = "";
        reason = CheckImage(head, size, out var f) ?? "";
        if (reason.Length != 0)
        {
            return false;
        }

        format = f;
        return true;
    }

    private static string? CheckImage(ReadOnlySpan<byte> head, long size, out string format)
    {
        format = "";
        if (size > MaxImageBytes)
        {
            return "over 1 MiB (1,048,576 bytes)";
        }

        int w, h;
        string f;
        ReadOnlySpan<byte> pngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', (byte)'\r', (byte)'\n', 0x1a, (byte)'\n'];
        if (head.StartsWith(pngSignature))
        {
            f = ImagePng;
            var ihdr = head[pngSignature.Length..];
            if (ihdr.Length < 16 || !ihdr[..8].SequenceEqual("\0\0\0\rIHDR"u8))
            {
                return "a PNG whose first chunk isn't IHDR";
            }

            var uw = BinaryPrimitives.ReadUInt32BigEndian(ihdr[8..]);
            var uh = BinaryPrimitives.ReadUInt32BigEndian(ihdr[12..]);
            w = uw > int.MaxValue ? int.MaxValue : (int)uw;
            h = uh > int.MaxValue ? int.MaxValue : (int)uh;
        }
        else if (head.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff]))
        {
            f = ImageJpeg;
            if (JpegSize(head, out w, out h) is { } err)
            {
                return err;
            }
        }
        else
        {
            return "neither a JPEG nor a PNG by its content";
        }

        if (w < 1 || h < 1)
        {
            return "a width or a height of 0";
        }

        if (w > MaxImageSide || h > MaxImageSide)
        {
            return "wider or taller than 4,096 pixels";
        }

        format = f;
        return null;
    }

    // Reads a JPEG's markers, after its SOI, up to its frame header (SOF0 to
    // SOF15, but DHT, JPG and DAC), and gives its width and height; else why
    // not.
    private static string? JpegSize(ReadOnlySpan<byte> b, out int w, out int h)
    {
        w = h = 0;
        var i = 2;
        while (true)
        {
            if (i >= b.Length || b[i] != 0xff)
            {
                return "no marker where one is due";
            }

            while (i < b.Length && b[i] == 0xff)
            {
                i++; // the marker's FF, and fill bytes before it
            }

            if (i >= b.Length)
            {
                return "no frame header in its header";
            }

            var m = b[i];
            i++;
            if (m == 0x01 || m is >= 0xd0 and <= 0xd7)
            {
                continue; // TEM and RSTn have no length
            }

            if (m is 0xd9 or 0xda)
            {
                return "no frame header before the image data";
            }

            if (i + 2 > b.Length)
            {
                return "no frame header in its header";
            }

            var n = (b[i] << 8) | b[i + 1];
            if (n < 2)
            {
                return $"a segment length of {n}";
            }

            if (m is >= 0xc0 and <= 0xcf && m != 0xc4 && m != 0xc8 && m != 0xcc)
            {
                // Length, precision, height, width.
                if (n < 8 || i + 7 > b.Length)
                {
                    return "a frame header cut short";
                }

                h = (b[i + 3] << 8) | b[i + 4];
                w = (b[i + 5] << 8) | b[i + 6];
                return null;
            }

            i += n;
        }
    }

    // Compares two strings by their UTF-8 bytes, as the spec orders keys.
    private static int ByteOrder(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
}

/// <summary>
/// What an owning server's page of titles holds, for
/// <see cref="CatalogRules.CheckPage"/>.
/// </summary>
public sealed record CatalogPage
{
    /// <summary>A changes answer, not a whole-list one.</summary>
    public bool Changes { get; init; }

    /// <summary>How many titles it holds.</summary>
    public int Titles { get; init; }

    /// <summary>How many keys of gone it holds.</summary>
    public int Gone { get; init; }

    /// <summary>Its answer body's length.</summary>
    public int Bytes { get; init; }

    /// <summary>Whole list: titles remain after it on the owning server.</summary>
    public bool Remain { get; init; }

    /// <summary>The request's after.</summary>
    public string After { get; init; } = "";

    /// <summary>The page's last title key.</summary>
    public string LastKey { get; init; } = "";

    /// <summary>Its next; null when absent.</summary>
    public string? Next { get; init; }
}

/// <summary>
/// Counts what an owning server puts in a page of titles, so that it ends
/// the page early when the next entry would pass either bound (MSFP
/// Media listing 3.1).
/// </summary>
/// <param name="reserve">What the answer takes, entries aside (<see cref="CatalogRules.PageReserve"/>, say).</param>
public sealed class PageBudget(int reserve)
{
    private long _bytes = reserve;

    /// <summary>How many entries the page holds.</summary>
    public int Keys { get; private set; }

    /// <summary>
    /// Counts an entry of <paramref name="size"/> bytes (a title as JSON, or a
    /// key of gone, its quotes included), with the comma before it, and says
    /// whether it fits; one that doesn't is not counted, and the page ends
    /// before it. False on an empty page means the entry alone passes the
    /// bound: it is not sent at all, and the owning server goes on with the
    /// next.
    /// </summary>
    public bool TryAdd(int size)
    {
        long s = size;
        if (Keys > 0)
        {
            s++; // the comma
        }

        if (Keys + 1 > CatalogRules.MaxPageTitles || _bytes + s > CatalogRules.MaxPageBytes)
        {
            return false;
        }

        Keys++;
        _bytes += s;
        return true;
    }
}

/// <summary>
/// The bounds of a local title's description: the catalog's
/// (<see cref="Local"/>), or a content part's own for its titles (MSFP
/// Media listing 4.3).
/// </summary>
/// <param name="NameRequired">The kind carries a name; without one the title is skipped.</param>
/// <param name="MaxName">The name's longest, in code points.</param>
/// <param name="MinYear">The earliest year, at least 1.</param>
/// <param name="MaxYear">The latest year.</param>
public sealed record DescriptionBounds(bool NameRequired, int MaxName, int MinYear, int MaxYear)
{
    /// <summary>
    /// Catalog section 4.3's bounds, for a local film or series: a name of at
    /// most 128 characters, required; a year from 1800 to 9999.
    /// </summary>
    public static DescriptionBounds Local { get; } = new(true, 128, 1800, 9999);
}

/// <summary>
/// What a caller keeps of a local title's description (MSFP
/// Media listing 4.3). It is the owning server's, shown as such, never sent on to
/// another peer.
/// </summary>
/// <param name="Name">Cleaned text, never markup; "" when absent.</param>
/// <param name="Year">0 when absent.</param>
/// <param name="Image">The image call gives one.</param>
public sealed record LocalDescription(string Name, int Year, bool Image);

/// <summary>
/// A titles request (MSFP Media listing 3), as a source reads it
/// (<see cref="Read"/>) and a caller sends it (<see cref="ToJson"/>).
/// <see cref="Libraries"/> null is every library the caller may see;
/// <see cref="Filter"/> narrows the listing (listing 3.6).
/// </summary>
public sealed record TitlesRequest
{
    private static readonly string[] Members = ["libraries", "kind", "under", "after", "since"];

    /// <summary>The libraries asked; null: every library the caller may see.</summary>
    public IReadOnlyList<string>? Libraries { get; init; }

    /// <summary>The filters.</summary>
    public TitleFilter Filter { get; init; } = new();

    /// <summary>The key after which the page starts; "" for the first.</summary>
    public string After { get; init; } = "";

    /// <summary>The seq of an earlier reading, for changes since; 0 for none.</summary>
    public long Since { get; init; }

    /// <summary>
    /// Reads a titles request as a source does (MSFP Media listing 3 and
    /// 3.6): an I-JSON object, null in none of its members; libraries, when
    /// present, an array of strings; kind a token; under a string of at most
    /// <see cref="CatalogRules.MaxAfter"/> bytes; after a string of at most
    /// <see cref="CatalogRules.MaxAfter"/> bytes; since an integer, a
    /// negative one read as 0. Anything else is null: a malformed body
    /// (listing 3.3: 404 not-found). Library IDs the caller may not see, and
    /// a kind no content part in use has, aren't refused: they list nothing.
    /// </summary>
    /// <returns>The request; null when malformed.</returns>
    public static TitlesRequest? Read(ReadOnlySpan<byte> body)
    {
        JsonObject o;
        try
        {
            o = StrictJson.ReadRequest(body, Members);
        }
        catch (ProtocolException)
        {
            return null;
        }

        List<string>? libraries = null;
        if (o.TryGetPropertyValue("libraries", out var l))
        {
            if (l is not JsonArray list)
            {
                return null;
            }

            libraries = new List<string>(list.Count);
            foreach (var e in list)
            {
                if (JsonMembers.AsString(e) is not { } s)
                {
                    return null;
                }

                libraries.Add(s);
            }
        }

        static bool Text(JsonObject o, string name, out string? value)
        {
            value = null;
            if (!o.TryGetPropertyValue(name, out var v))
            {
                return true;
            }

            value = JsonMembers.AsString(v);
            return value is not null;
        }

        if (!Text(o, "kind", out var kind) || (kind is not null && !Names.IsToken(kind)) ||
            !Text(o, "under", out var under) || (under is not null && Encoding.UTF8.GetByteCount(under) > CatalogRules.MaxAfter) ||
            !Text(o, "after", out var after) || (after is not null && Encoding.UTF8.GetByteCount(after) > CatalogRules.MaxAfter))
        {
            return null;
        }

        long since = 0;
        if (o.TryGetPropertyValue("since", out var sv))
        {
            try
            {
                if (JsonMembers.AsInteger(sv) is not { } n)
                {
                    return null;
                }

                since = Math.Max(n, 0);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return new TitlesRequest
        {
            Libraries = libraries,
            Filter = new TitleFilter { Kind = kind ?? "", Under = under ?? "", HasUnder = under is not null },
            After = after ?? "",
            Since = since,
        };
    }

    /// <summary>The request as a caller sends it: libraries when not null, kind and under when given, after always, since when above 0.</summary>
    public byte[] ToJson() => JsonOut.Object(w => w
        .Strings("libraries", Libraries)
        .String("kind", TranscodeTargets.NonEmpty(Filter.Kind))
        .String("under", Filter.HasUnder ? Filter.Under : null)
        .String("after", After)
        .Integer("since", Since > 0 ? Since : null));

    /// <summary>
    /// Whether a caller may ask with since the seq of an earlier reading
    /// made with <paramref name="prev"/> (MSFP Media listing 3.2): only when
    /// libraries is the same set (or absent in both), kind is the same, and
    /// neither has under.
    /// </summary>
    public static bool SinceAllowed(TitlesRequest prev, TitlesRequest next)
    {
        ArgumentNullException.ThrowIfNull(prev);
        ArgumentNullException.ThrowIfNull(next);
        if (prev.Filter.Kind != next.Filter.Kind || prev.Filter.HasUnder || next.Filter.HasUnder ||
            (prev.Libraries is null) != (next.Libraries is null))
        {
            return false;
        }

        return new HashSet<string>(prev.Libraries ?? [], StringComparer.Ordinal).SetEquals(next.Libraries ?? []);
    }
}

/// <summary>
/// A titles request's filters (MSFP Media listing 3.6): <see cref="Kind"/>
/// ("" for none) and <see cref="Under"/> (when <see cref="HasUnder"/>: a
/// title key, compared as an exact string). The default filter lists every
/// title.
/// </summary>
public sealed record TitleFilter
{
    /// <summary>The kind of the titles listed; "" for any.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The key of the title the titles listed are directly under, when <see cref="HasUnder"/>.</summary>
    public string Under { get; init; } = "";

    /// <summary>Whether the request gave under.</summary>
    public bool HasUnder { get; init; }

    /// <summary>Whether the filter lists every title.</summary>
    public bool None => Kind.Length == 0 && !HasUnder;

    /// <summary>
    /// Whether a source may answer the request with changes only, as far as
    /// its filters go (MSFP Media listing 3.2): never with under, whose
    /// answer is always the whole list.
    /// </summary>
    public bool Changes => !HasUnder;

    /// <summary>
    /// Whether a title passes the filter (MSFP Media listing 3.6): its key
    /// names <see cref="Kind"/>, when given; and, with under, it is directly
    /// under that title: a film's extras; a series' seasons, extras and
    /// episodes keyed by an absolute number; a season's episodes, those
    /// keyed under it and those of the same series keyed by their TMDB
    /// episode ID whose place.season is its number, and its extras; an
    /// album's tracks (their album); an artist's albums (their artists).
    /// Under any other string, nothing. A caller's whole reading made with a
    /// filter drops, of the titles it kept, only those that pass and the
    /// reading didn't send.
    /// </summary>
    public bool Passes(FilterFacts title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!TitleKey.TryParse(title.Key, out var k) || (Kind.Length > 0 && k.Kind != Kind))
        {
            return false;
        }

        if (!HasUnder)
        {
            return true;
        }

        if (MusicKey.TryParse(Under, out var mu))
        {
            return (mu.Kind, k.Kind) switch
            {
                ("album", "track") => title.Album == Under,
                ("artist", "album") => title.Artists.Contains(Under, StringComparer.Ordinal),
                _ => false,
            };
        }

        if (!VideoKey.TryParse(Under, out var u) || !VideoKey.TryParse(title.Key, out var v))
        {
            return false;
        }

        if (v.Parent is { } parent)
        {
            return parent.ToString() == Under;
        }

        if (v.Series != u.Series)
        {
            return false;
        }

        return u.Kind switch
        {
            "series" => v.Kind == "season" || (v.Kind == "episode" && v.Absolute),
            "season" => v.Kind == "episode" && !v.Absolute &&
                (v.EpisodeId > 0 ? title.PlaceSeason is { } s && s == u.Season : v.Season == u.Season),
            _ => false,
        };
    }

    /// <summary>
    /// The prefix every key under a video title starts with (its film's or
    /// series' key and a slash), for a source that walks its titles in key
    /// order: those under it are among the keys of that prefix, which come
    /// together in byte order. "" when under isn't a video key: a source then
    /// looks at every title.
    /// </summary>
    public string UnderPrefix => HasUnder && VideoKey.TryParse(Under, out var u) ? u.Series + "/" : "";

    /// <summary>
    /// Whether <paramref name="kind"/> is a kind of title of a content part
    /// of version 0: movie, series, season, episode, extra; artist, album,
    /// track.
    /// </summary>
    public static bool KnownKind(string kind) => kind is "movie" or "series" or "season" or "episode" or "extra" or "artist" or "album" or "track";
}

/// <summary>
/// What a filter reads of a title (MSFP Media listing 3.6): its key; an
/// episode's place.season (null when it has none); a track's album key; an
/// album's artists' keys.
/// </summary>
public sealed record FilterFacts
{
    /// <summary>The title's key.</summary>
    public string Key { get; init; } = "";

    /// <summary>An episode's place.season; null for none.</summary>
    public int? PlaceSeason { get; init; }

    /// <summary>A track's album key.</summary>
    public string Album { get; init; } = "";

    /// <summary>An album's artists' keys.</summary>
    public IReadOnlyList<string> Artists { get; init; } = [];
}
