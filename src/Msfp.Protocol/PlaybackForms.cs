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
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex IdRe();

    /// <summary>
    /// Whether <paramref name="s"/> is a playback ID (playback section 2.3):
    /// the common opaque form, 1 to 64 characters of A–Z a–z 0–9 _ -. The
    /// owning server makes it from at least 128 bits of a cryptographically
    /// secure random source, as it makes tickets.
    /// </summary>
    public static bool PlayId(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return IdRe().IsMatch(s);
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
/// The media URLs a viewer's server builds (media section 1, video section
/// 6.1, music section 5.2): answers carry no URLs, so every one is the
/// owning server's stream root, a ticket an answer gave and a fixed name,
/// and a viewer's server never fetches an address a peer wrote. The ticket
/// must have been checked against its form (<see cref="Playback.Id"/>); the
/// numbers are in decimal without leading zeros.
/// </summary>
public static class MediaUrls
{
    /// <summary>&lt;root&gt;&lt;ticket&gt;/: the base of every URL under a ticket.</summary>
    public static string Base(string root, string ticket)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(ticket);
        return root + ticket + "/";
    }

    /// <summary>A playback's stream: master.m3u8 for protocol hls, file for file.</summary>
    public static string Stream(string root, string ticket, string protocol) =>
        Base(root, ticket) + (protocol == "file" ? "file" : "master.m3u8");

    /// <summary>
    /// The base of a subtitle track served apart (video section 6.1), to
    /// which /&lt;part&gt; or /whole is added: subtitles/&lt;id&gt; for text
    /// (WebVTT), pictures/&lt;id&gt; for picture (PGS), ass/&lt;id&gt; for its ASS
    /// form (<paramref name="ass"/>).
    /// </summary>
    public static string Subtitle(string root, string ticket, ServedSubtitle track, bool ass = false)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Base(root, ticket) + (ass ? "ass/" : track.Form == Video.FormPicture ? "pictures/" : "subtitles/") + track.Id;
    }

    /// <summary>Font <paramref name="n"/> of a playback, 0 to its answer's fontCount − 1.</summary>
    public static string Font(string root, string ticket, int n) =>
        Base(root, ticket) + "fonts/" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Preview sheet <paramref name="n"/> of a copy, below the sheet count of its previewLayout, under a playback's ticket or previews' answer's.</summary>
    public static string Sheet(string root, string ticket, int n) =>
        Base(root, ticket) + "trickplay/" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Queue entry <paramref name="n"/>'s stream (music section 5.2): queue/&lt;n&gt;/file or queue/&lt;n&gt;/master.m3u8, n its place in the request's queue.</summary>
    public static string Queue(string root, string ticket, int n, string protocol) =>
        Base(root, ticket) + "queue/" + n.ToString(CultureInfo.InvariantCulture) + "/" + (protocol == "file" ? "file" : "master.m3u8");

    /// <summary>A local title's image (catalog section 4.4), under the ticket of image's answer.</summary>
    public static string Image(string root, string ticket) => Base(root, ticket) + "image";
}
