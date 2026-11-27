using System.Globalization;

namespace Msfp.Protocol;

/// <summary>
/// What a ticket opens on the owning server (MSFP Media fetching 1 and 4.2): its
/// kind, and for a playback or a download what its answer opened.
/// </summary>
public sealed record TicketScope
{
    /// <summary>A playback's ticket (purpose play).</summary>
    public const string KindPlayback = "playback";

    /// <summary>A download's ticket.</summary>
    public const string KindDownload = "download";

    /// <summary>A scan's ticket: the file only.</summary>
    public const string KindScan = "scan";

    /// <summary>previews' answer's ticket: the copy's sheets only.</summary>
    public const string KindPreviews = "previews";

    /// <summary>image's answer's ticket: the image only.</summary>
    public const string KindImage = "image";

    /// <summary>Its kind: playback, download, scan, previews or image.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The answer's protocol, hls or file.</summary>
    public string Protocol { get; init; } = "";

    /// <summary>The subtitle tracks the answer serves apart.</summary>
    public IReadOnlyList<ServedSubtitle> Subtitles { get; init; } = [];

    /// <summary>The answer's fontCount.</summary>
    public int FontCount { get; init; }

    /// <summary>The sheet count of the copy's previewLayout; 0 without one.</summary>
    public int SheetCount { get; init; }

    /// <summary>The protocol of each queue entry prepared, in the request's order; "" for an entry left out.</summary>
    public IReadOnlyList<string> Queue { get; init; } = [];

    /// <summary>The bare names its streams' playlists give (hls).</summary>
    public IReadOnlyCollection<string> Names { get; init; } = [];
}

// The names an owning server serves under a ticket (MSFP Media fetching 1), and
// the media types a viewer's server relays (MSFP Media fetching 3).
public static partial class Media
{
    /// <summary>
    /// Whether the owning server serves <paramref name="name"/>, the URL's
    /// path after &lt;stream root&gt;&lt;ticket&gt;/, under a ticket of
    /// <paramref name="scope"/> (MSFP Media fetching 1): exactly the names its kind
    /// opens, within their counts (a font from fontCount on, a sheet from the
    /// sheet count on, a subtitle track with no entry of that form, a queue
    /// entry it didn't prepare are not), numbers in decimal without leading
    /// zeros. False is a 404; <paramref name="why"/> says why.
    /// </summary>
    public static bool Serves(TicketScope scope, string name, out string why)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(name);
        why = "";
        var p = name.Split('/');
        switch (scope.Kind)
        {
            case TicketScope.KindImage:
                return name == "image" || Fail("an image ticket opens image only", out why);
            case TicketScope.KindScan:
                return name == "file" || Fail("a scan ticket opens file only", out why);
            case TicketScope.KindPreviews:
                if (p.Length == 2 && p[0] == "trickplay")
                {
                    return Number(p[1], scope.SheetCount, out _) || Fail("a sheet at or past the sheet count", out why);
                }

                return Fail("a previews ticket opens the sheets only", out why);
            case TicketScope.KindPlayback or TicketScope.KindDownload:
                break;
            default:
                return Fail("no ticket of that kind", out why);
        }

        switch (p[0])
        {
            case "subtitles" or "ass" or "pictures":
                if (p.Length != 3)
                {
                    return Fail("a subtitle URL is <kind>/<id>/<part> or /whole", out why);
                }

                if (scope.Subtitles.FirstOrDefault(s => s.Id == p[1]) is not { } e)
                {
                    return Fail("a subtitle track the answer didn't list", out why);
                }

                if ((p[0] == "subtitles" && e.Form != Video.FormText) || (p[0] == "pictures" && e.Form != Video.FormPicture) ||
                    (p[0] == "ass" && !e.Ass))
                {
                    return Fail("a form the answer didn't open for that track", out why);
                }

                if (p[2] == "whole" && p[0] != "pictures")
                {
                    return true;
                }

                return Number(p[2], 10_000, out _) || Fail("a part that isn't a number from 0 to 9,999", out why);
            case "fonts":
                return (p.Length == 2 && Number(p[1], scope.FontCount, out _)) || Fail("a font at or past fontCount", out why);
            case "trickplay":
                if (scope.Kind != TicketScope.KindPlayback)
                {
                    return Fail("a download ticket opens no sheets", out why);
                }

                return (p.Length == 2 && Number(p[1], scope.SheetCount, out _)) || Fail("a sheet at or past the sheet count", out why);
            case "queue":
                if (scope.Kind != TicketScope.KindPlayback)
                {
                    return Fail("a download ticket opens no queue", out why);
                }

                if (p.Length < 2)
                {
                    return Fail("a queue URL is queue/<n>/<name>", out why);
                }

                if (!Number(p[1], scope.Queue.Count, out var n) || scope.Queue[(int)n].Length == 0)
                {
                    return Fail("a queue entry the owning server didn't prepare", out why);
                }

                return StreamName(scope, scope.Queue[(int)n], p[2..], out why);
        }

        return StreamName(scope, scope.Protocol, p, out why);
    }

    /// <summary>
    /// The Content-Type a viewer's server relays a copy's file with (MSFP
    /// Media fetching 3): the copy's own mediaType from the catalog, whatever its
    /// top-level type, when it is a media type (MSFP Media base 3);
    /// application/octet-stream when it has none. A segment's is
    /// <see cref="Playback.RelayedMediaType"/>'s.
    /// </summary>
    public static string RelayFileType(string? mediaType) => Names.IsMediaType(mediaType) ? mediaType! : OctetStream;

    /// <summary>The media type of bytes of no known type.</summary>
    public const string OctetStream = "application/octet-stream";

    // A stream's names: file for file, master.m3u8 and its playlists' bare
    // names for hls.
    private static bool StreamName(TicketScope scope, string protocol, string[] p, out string why)
    {
        why = "";
        if (p.Length == 1 && ((protocol == "file" && p[0] == "file") || (protocol == "hls" && p[0] == "master.m3u8") ||
                              (protocol == "hls" && FileName(p[0]) && scope.Names.Contains(p[0]))))
        {
            return true;
        }

        return Fail("not a name of the stream", out why);
    }

    // A decimal number without leading zeros, below top.
    private static bool Number(string s, long top, out long n)
    {
        n = 0;
        if (s.Length is 0 or > 15 || (s.Length > 1 && s[0] == '0') || !s.All(c => c is >= '0' and <= '9'))
        {
            return false;
        }

        n = long.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
        return n < top;
    }

    private static bool Fail(string reason, out string why)
    {
        why = reason;
        return false;
    }
}
