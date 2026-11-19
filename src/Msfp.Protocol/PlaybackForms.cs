using System.Globalization;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// Playback (MSFP playback, video section 5) and its media's URLs (media
/// section 1, video section 6.1): the forms of what a play answer gives,
/// as the viewer's server checks them.
/// </summary>
public static partial class Playback
{
    [GeneratedRegex(@"^play_[a-z2-7]{26}\z")]
    private static partial Regex PlayIdRe();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex IdRe();

    /// <summary>Whether <paramref name="s"/> is a playback ID (playback section 2.3).</summary>
    public static bool PlayId(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return PlayIdRe().IsMatch(s);
    }

    /// <summary>
    /// Whether <paramref name="s"/> has the form of a library's, a track's
    /// or a version's ID (catalog section 2, playback section 2.3), and of a
    /// ticket (media section 1): 1 to 64 characters of A–Z a–z 0–9 _ -.
    /// </summary>
    public static bool Id(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return IdRe().IsMatch(s);
    }

    /// <summary>
    /// Reads a track ID of the given kind ('a' for audio, 's' for
    /// subtitles; playback section 2.1, video section 5.1): the letter and a
    /// number from 0 to 999 without leading zeros (a01 is malformed); false
    /// for anything else.
    /// </summary>
    /// <param name="id">The track ID.</param>
    /// <param name="kind">The letter its kind gives.</param>
    /// <param name="number">Its number; 0 when false.</param>
    public static bool TryTrackId(string id, char kind, out int number)
    {
        ArgumentNullException.ThrowIfNull(id);
        number = 0;
        if (id.Length < 2 || id.Length > 4 || id[0] != kind || (id.Length > 2 && id[1] == '0'))
        {
            return false;
        }

        foreach (var c in id.AsSpan(1))
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        number = int.Parse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>A server's stream root (media section 1) at <paramref name="address"/>, without a discovery document: &lt;address&gt;/api/v1/stream/.</summary>
    public static string StreamRoot(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return (address.EndsWith('/') ? address[..^1] : address) + "/api/v1/stream/";
    }

    /// <summary>
    /// Checks a play answer's stream URL against the owning server's stream
    /// root (media section 1): &lt;root&gt;&lt;ticket&gt;/master.m3u8 for hls,
    /// &lt;root&gt;&lt;ticket&gt;/file for file, compared exactly as sent (no %,
    /// no query or fragment, no empty, "." or ".." part). It gives the
    /// stream's base, &lt;root&gt;&lt;ticket&gt;/, under which every other URL of
    /// the answer must be.
    /// </summary>
    /// <param name="url">The stream URL the answer gives.</param>
    /// <param name="root">The owning server's stream root.</param>
    /// <param name="protocol">The answer's protocol: hls or file.</param>
    /// <param name="streamBase">The stream's base; empty when false.</param>
    public static bool TryStreamBase(string url, string root, string protocol, out string streamBase)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(root);
        streamBase = "";
        if (!url.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = url[root.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var want = protocol switch
        {
            "hls" => "master.m3u8",
            "file" => "file",
            _ => "",
        };
        if (slash < 0 || want.Length == 0)
        {
            return false;
        }

        var ticket = rest[..slash];
        if (!Id(ticket) || rest[(slash + 1)..] != want)
        {
            return false;
        }

        streamBase = root + ticket + "/";
        return true;
    }

    /// <summary>A subtitle track's URL under a stream's base (video section 6.1): subtitles/&lt;track&gt; for text, pictures/&lt;track&gt; for PGS.</summary>
    public static string SubtitleUrl(string streamBase, string track, bool pictures) =>
        streamBase + (pictures ? "pictures/" : "subtitles/") + track;

    /// <summary>The URL of sheet <paramref name="n"/> of a playback's previews (video section 6.1).</summary>
    public static string SheetUrl(string streamBase, int n) =>
        streamBase + "trickplay/" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The Content-Type a viewer's server gives the file or a segment it
    /// relays (media section 3): the owning server's when it is a video/ or
    /// audio/ type made of letters, digits and .+- only (never a
    /// playlist's), application/octet-stream otherwise.
    /// </summary>
    public static string RelayedMediaType(string? contentType)
    {
        var t = (contentType ?? "").Trim().ToLowerInvariant();
        var semi = t.IndexOf(';', StringComparison.Ordinal);
        if (semi >= 0)
        {
            t = t[..semi];
        }

        t = t.Trim();
        var slash = t.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            var kind = t[..slash];
            var sub = t[(slash + 1)..];
            if ((kind == "video" || kind == "audio") && sub.Length > 0 &&
                sub.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '+' or '-') &&
                !sub.Contains("mpegurl", StringComparison.Ordinal))
            {
                return t;
            }
        }

        return "application/octet-stream";
    }
}

/// <summary>
/// A playback's seek-bar previews, as play's answer gives them (video
/// section 5.5).
/// </summary>
/// <param name="TileWidth">A tile's width, in pixels.</param>
/// <param name="TileHeight">A tile's height, in pixels.</param>
/// <param name="Columns">Tiles across a sheet.</param>
/// <param name="Rows">Tiles down a sheet.</param>
/// <param name="IntervalMs">The time between two tiles, in milliseconds.</param>
/// <param name="TileCount">How many tiles there are.</param>
/// <param name="Sheets">How many sheet URLs the answer lists.</param>
public readonly record struct Trickplay(int TileWidth, int TileHeight, int Columns, int Rows, int IntervalMs, int TileCount, int Sheets)
{
    /// <summary>The most sheets a playback's previews may have.</summary>
    public const int MaxSheets = 1000;

    /// <summary>The most a sheet may be, as read (media section 2.4): 8 MiB.</summary>
    public const int MaxSheetBytes = 8 << 20;

    private const int MaxTile = 2048;
    private const int MaxGrid = 100;
    private const int MaxSheetSide = 4096;
    private const int MinInterval = 100;
    private const int MaxInterval = 3_600_000;

    /// <summary>How many sheets the tiles fill: ⌈tileCount / (columns × rows)⌉, 0 when either is 0.</summary>
    public int SheetCount
    {
        get
        {
            var per = (long)Columns * Rows;
            if (per <= 0 || TileCount <= 0)
            {
                return 0;
            }

            return (int)((TileCount + per - 1) / per);
        }
    }

    /// <summary>
    /// Whether the previews are within video section 5.5's bounds, one sheet
    /// listed for each the tiles fill: else the viewer's server plays on
    /// without them.
    /// </summary>
    public bool InBounds
    {
        get
        {
            static bool In(int v, int lo, int hi) => v >= lo && v <= hi;
            if (!In(TileWidth, 1, MaxTile) || !In(TileHeight, 1, MaxTile) || !In(Columns, 1, MaxGrid) ||
                !In(Rows, 1, MaxGrid) || Columns * TileWidth > MaxSheetSide || Rows * TileHeight > MaxSheetSide ||
                !In(IntervalMs, MinInterval, MaxInterval) || TileCount < 1)
            {
                return false;
            }

            var n = SheetCount;
            return n <= MaxSheets && Sheets == n;
        }
    }
}
