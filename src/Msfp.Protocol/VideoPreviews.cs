using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>The body of a previews call (MSFP Media video 5.6): the key of a film, an episode or an extra, and its copy's ID.</summary>
/// <param name="Title">The title's key, canonical, of a film, an episode or an extra.</param>
/// <param name="VersionId">The copy's ID.</param>
public sealed record PreviewsRequest(string Title, string VersionId);

/// <summary>
/// A previews answer as kept (MSFP Media video 5.6): the ticket the copy's
/// sheets are fetched under (<see cref="MediaUrls.Sheet"/>), for 10 minutes,
/// and the copy's etag as the owning server holds it now. When the etag
/// isn't the catalog's, the viewer's server reads the title again before it
/// fetches any sheet. The layout is the catalog's (<see cref="Copy.PreviewLayout"/>).
/// </summary>
public sealed record PreviewsAnswer(string Ticket, string ETag);

/// <summary>
/// A subtitle track an owning server serves apart (MSFP Media video 5.3), as
/// play's answer lists it: as WebVTT (<see cref="Form"/> text, at
/// subtitles/&lt;id&gt;), and also as ASS when <see cref="Ass"/> (ass/&lt;id&gt;),
/// or as PGS (picture, at pictures/&lt;id&gt;).
/// </summary>
/// <param name="Id">The track's ID in the catalog (s&lt;n&gt;).</param>
/// <param name="Form">text or picture.</param>
/// <param name="Ass">Whether it is also served as ASS; only with text.</param>
public sealed record ServedSubtitle(string Id, string Form, bool Ass = false);

/// <summary>
/// Seek-bar previews and the video members of play's answer (MSFP
/// Media video 5.3 to 5.6): the previews call's request and answer, the
/// subtitles served apart and the copy's fonts.
/// </summary>
public static partial class Video
{
    /// <summary>A subtitle track served as WebVTT (MSFP Media video 5.3).</summary>
    public const string FormText = "text";

    /// <summary>A subtitle track served as PGS (MSFP Media video 5.3).</summary>
    public const string FormPicture = "picture";

    /// <summary>The most entries play's answer's subtitles keeps.</summary>
    public const int MaxServed = 64;

    // Video section 5.4's table: the form, and ass, a track of a listed
    // format must be served in.
    private static readonly Dictionary<string, (string Form, bool Ass)> ServedForms = new(StringComparer.Ordinal)
    {
        ["subrip"] = (FormText, false), ["webvtt"] = (FormText, false), ["mov_text"] = (FormText, false), ["ttml"] = (FormText, false),
        ["ass"] = (FormText, true), ["ssa"] = (FormText, true),
        ["hdmv_pgs_subtitle"] = (FormPicture, false), ["dvd_subtitle"] = (FormPicture, false), ["dvb_subtitle"] = (FormPicture, false),
    };

    /// <summary>
    /// How a track of <paramref name="format"/> is served, known before play
    /// (MSFP Media video 5.4): subrip, webvtt, mov_text and ttml as text; ass and
    /// ssa as text with ass; hdmv_pgs_subtitle, dvd_subtitle and dvb_subtitle
    /// as picture. Null for another format, or none: such a track waits for
    /// the answer to know its form, and may not be served apart.
    /// </summary>
    public static ServedSubtitle? FormOf(string id, string format)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ServedForms.TryGetValue(format ?? "", out var f) ? new ServedSubtitle(id, f.Form, f.Ass) : null;
    }

    /// <summary>
    /// Reads play's answer's subtitles (MSFP Media video 5.3) against the
    /// copy's subtitle tracks in the catalog: at most <see cref="MaxServed"/>
    /// entries; an entry that isn't an object, whose id isn't a track of the
    /// copy or repeats an earlier entry's, or whose form is neither text nor
    /// picture, is dropped alone; an ass that isn't a boolean, or comes with
    /// picture, counts as absent; for a track of a format section 5.4 lists,
    /// an entry whose form or ass isn't the table's is dropped alone. None
    /// when it isn't an array.
    /// </summary>
    public static IReadOnlyList<ServedSubtitle> ReadServed(JsonNode? subtitles, IReadOnlyList<CopySubtitle> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var out_ = new List<ServedSubtitle>();
        if (subtitles is not JsonArray list)
        {
            return out_;
        }

        foreach (var e in list)
        {
            if (out_.Count == MaxServed)
            {
                break;
            }

            if (e is not JsonObject o || JsonMembers.AsString(o["id"]) is not { } id ||
                tracks.FirstOrDefault(t => t.Id == id) is not { } track || out_.Exists(s => s.Id == id) ||
                JsonMembers.AsString(o["form"]) is not { } form || form is not (FormText or FormPicture))
            {
                continue;
            }

            var served = new ServedSubtitle(id, form, form == FormText && Music.BoolOf(o["ass"]) == true);
            if (FormOf(id, track.Format) is { } want && want != served)
            {
                continue;
            }

            out_.Add(served);
        }

        return out_;
    }

    /// <summary>Reads play's answer's fontCount (MSFP Media video 5.3): 0 to <see cref="Font.MaxFonts"/>; absent, or anything else, 0.</summary>
    public static int ReadFontCount(JsonNode? fontCount) =>
        Copies.Integer(fontCount, 0, Font.MaxFonts, out var n) ? (int)n : 0;

    /// <summary>
    /// Reads a previews request as the owning server does (MSFP
    /// Media video 5.6). Whether it offers the caller that title and copy, with a
    /// previewLayout in the catalog, is its own to check (404 otherwise); 404
    /// not-supported when video is off comes first, before the body is read.
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

    /// <summary>The body of a previews answer, as an owning server sends it: {"ticket", "etag"}.</summary>
    public static byte[] WritePreviewsAnswer(PreviewsAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return JsonOut.Object(o => o.String("ticket", answer.Ticket).String("etag", answer.ETag));
    }

    /// <summary>
    /// Reads a previews answer (MSFP Media video 5.6): its ticket and etag, each
    /// 1 to 64 characters of A-Z a-z 0-9 _ -. Unknown members (the layout of
    /// earlier drafts among them) are ignored.
    /// </summary>
    /// <exception cref="ProtocolException">The answer is refused, and no sheet fetched: not an I-JSON object, or a ticket or an etag missing or out of its form.</exception>
    public static PreviewsAnswer ReadPreviewsAnswer(ReadOnlySpan<byte> body)
    {
        var o = StrictJson.ReadAnswer(body, keepNestedNulls: true);
        if (JsonMembers.AsString(o["ticket"]) is not { } ticket || !Playback.Id(ticket))
        {
            throw Bad("ticket missing or not 1 to 64 characters of A-Z a-z 0-9 _ -");
        }

        if (JsonMembers.AsString(o["etag"]) is not { } etag || !Playback.Id(etag))
        {
            throw Bad("etag missing or not 1 to 64 characters of A-Z a-z 0-9 _ -");
        }

        return new PreviewsAnswer(ticket, etag);
    }

    private static ProtocolException Bad(string why) => new(ProtocolError.Json, "video: " + why);
}
