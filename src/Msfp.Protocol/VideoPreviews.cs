using System.Globalization;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>The body of a previews call (video section 5.6): the key of a film, an episode or an extra, and its copy's ID.</summary>
/// <param name="Title">The title's key, canonical, of a film, an episode or an extra.</param>
/// <param name="VersionId">The copy's ID.</param>
public sealed record PreviewsRequest(string Title, string VersionId);

/// <summary>
/// A trickplay object as kept (video section 5.5): its numbers, in bounds
/// (<see cref="Trickplay.Sheets"/> is the count of <see cref="Urls"/>), the
/// ticket its sheets are under, and the sheets' URLs, sheet n's at index n,
/// each exactly <see cref="Playback.SheetUrl"/>(root + ticket + "/", n).
/// The viewer's server fetches them itself and never hands them to its
/// people.
/// </summary>
public sealed record Previews(Trickplay Trickplay, string Ticket, IReadOnlyList<string> Urls);

/// <summary>Seek-bar previews (MSFP video sections 5.5 and 5.6): the previews call's request, and the trickplay object of its answer and of play's.</summary>
public static partial class Video
{
    // The numbers of a trickplay object (video section 5.5).
    private static readonly string[] TrickplayNumbers = ["tileWidth", "tileHeight", "columns", "rows", "intervalMs", "tileCount"];

    /// <summary>
    /// Reads a previews request as the owning server does (video section
    /// 5.6). Whether it offers the caller that title and copy, with
    /// previews, is its own to check (404 otherwise); 404 not-supported when
    /// video is off comes first, before the body is read.
    /// </summary>
    /// <exception cref="ProtocolException">Answered 400: the body isn't an I-JSON object (null refused in title and versionId), the title isn't the canonical key of a film, an episode or an extra, or versionId isn't of a copy ID's form.</exception>
    public static PreviewsRequest ReadPreviewsRequest(ReadOnlySpan<byte> body)
    {
        var o = StrictJson.ReadRequest(body, ["title", "versionId"]);
        var title = o.GetString("title") ?? throw Bad("title missing or not a string");
        if (!VideoKey.TryParse(title, out var k))
        {
            throw Bad("title isn't a canonical video key");
        }

        if (!k.Playable)
        {
            throw Bad("title: a " + k.Kind + " has no copies");
        }

        var version = o.GetString("versionId") ?? throw Bad("versionId missing or not a string");
        if (!Playback.Id(version))
        {
            throw Bad("versionId isn't 1 to 64 characters of A-Z a-z 0-9 _ -");
        }

        return new PreviewsRequest(title, version);
    }

    /// <summary>
    /// Reads a previews answer (video section 5.6) against the owning
    /// server's stream root.
    /// </summary>
    /// <returns>The previews; null when it holds none, or <see cref="ReadTrickplay"/> drops them.</returns>
    /// <exception cref="ProtocolException">The answer is refused: not an object, null at its top, or <see cref="ReadTrickplay"/>'s refusals.</exception>
    public static Previews? ReadPreviewsAnswer(ReadOnlySpan<byte> body, string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var o = StrictJson.ReadAnswer(body, keepNestedNulls: true);
        return o.TryGetPropertyValue("trickplay", out var tp) ? ReadTrickplay(tp, root) : null;
    }

    /// <summary>
    /// Reads a trickplay object, of play's answer or of previews', against
    /// the owning server's stream root (video section 5.5). A refusal wins
    /// over a drop; unknown members are ignored.
    /// </summary>
    /// <returns>The previews; null drops them, and the viewer's server goes on without them: a number missing or out of bounds (<see cref="Trickplay.InBounds"/>), or another number of sheets than the tiles fill.</returns>
    /// <exception cref="ProtocolException">The whole answer is refused: trickplay isn't an object, a number is of another JSON type or isn't an integer, sheets isn't an array of strings, or a sheet's URL isn't exactly &lt;root&gt;&lt;ticket&gt;/trickplay/&lt;n&gt; for its index n, all under one ticket.</exception>
    public static Previews? ReadTrickplay(JsonNode? trickplay, string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (trickplay is not JsonObject o)
        {
            throw Bad("trickplay isn't an object");
        }

        var n = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in TrickplayNumbers)
        {
            if (!o.TryGetPropertyValue(f, out var raw))
            {
                continue;
            }

            // Clamped to int: a number beyond it is out of bounds either way.
            n[f] = JsonMembers.AsInteger(raw) is { } i
                ? (int)Math.Clamp(i, int.MinValue, int.MaxValue)
                : throw Bad(f + " isn't an integer");
        }

        var ticket = "";
        var urls = new List<string>();
        if (o.TryGetPropertyValue("sheets", out var sheets))
        {
            if (sheets is not JsonArray list)
            {
                throw Bad("sheets isn't an array");
            }

            for (var i = 0; i < list.Count; i++)
            {
                var index = i.ToString(CultureInfo.InvariantCulture);
                if (JsonMembers.AsString(list[i]) is not { } s)
                {
                    throw Bad("sheets[" + index + "] isn't a string");
                }

                var rest = s.StartsWith(root, StringComparison.Ordinal) ? s[root.Length..] : s;
                var slash = rest.IndexOf('/', StringComparison.Ordinal);
                var t = slash < 0 ? rest : rest[..slash];
                if (!s.StartsWith(root, StringComparison.Ordinal) || !Playback.Id(t) || s != Playback.SheetUrl(root + t + "/", i))
                {
                    throw Bad("sheets[" + index + "] isn't <stream root><ticket>/trickplay/" + index);
                }

                if (i > 0 && t != ticket)
                {
                    throw Bad("sheets[" + index + "] names another ticket");
                }

                ticket = t;
                urls.Add(s);
            }
        }

        int N(string f) => n.GetValueOrDefault(f);
        var tp = new Trickplay(N("tileWidth"), N("tileHeight"), N("columns"), N("rows"), N("intervalMs"), N("tileCount"), urls.Count);
        return tp.InBounds ? new Previews(tp, ticket, urls) : null;
    }

    private static ProtocolException Bad(string why) => new(ProtocolError.Json, "video: " + why);
}
