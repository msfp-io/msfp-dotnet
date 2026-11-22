using System.Text.Json.Nodes;

namespace Msfp.Protocol;

// play's answer (MSFP playback section 2.3, video section 5.3), as a
// viewer's server checks and cleans it, and the restart of a file being
// read (section 2.7). The answer carries no URLs: the viewer's server builds
// them from the stream root and the ticket (MediaUrls).
public static partial class Playback
{
    /// <summary>The only mimeType an hls stream is relayed as.</summary>
    public const string HlsType = "application/vnd.apple.mpegurl";

    /// <summary>The protocol of a stream served as HLS.</summary>
    public const string ProtocolHls = "hls";

    /// <summary>The protocol of the file as it is, by byte ranges.</summary>
    public const string ProtocolFile = "file";

    /// <summary>
    /// Checks and cleans play's answer (playback section 2.3, video section
    /// 5.3) against what the viewer's server asked and knows
    /// (<paramref name="asked"/>). Refused (null): a body that isn't an
    /// I-JSON object, null at its top; id or ticket not of the opaque form;
    /// protocol not hls or file, or not the delivery's (file for file, hls
    /// otherwise); method not direct, remux or transcode, or not the
    /// delivery's (direct for file); etag not 1 to 64 characters of A–Z a–z
    /// 0–9 - _; positionMs absent or out of 0 to <see cref="MaxPositionMs"/>;
    /// versionId absent or out of its form; audioTrack not a string;
    /// audioRenditions not a boolean; maxBitrateKbps not an integer. Cleaned:
    /// an audioTrack of another form cleared; audioRenditions true with file
    /// dropped; maxBitrateKbps out of range dropped; scanBytes (scans only)
    /// <see cref="ScanBytesFloor"/> when missing or out of range; video's
    /// subtitles (<see cref="Video.ReadServed"/>) and fontCount
    /// (<see cref="Video.ReadFontCount"/>) for a video title only. Unknown
    /// members (the stream, tracks, length, fonts and trickplay of earlier
    /// drafts among them) are ignored. A refused answer: the viewer's server
    /// sends stop for its id, when it has one, and gives up.
    /// </summary>
    /// <returns>The answer kept; null when refused.</returns>
    public static PlayAnswer? ReadPlayAnswer(ReadOnlySpan<byte> body, PlayAsked asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        JsonObject o;
        try
        {
            o = StrictJson.ReadAnswer(body, keepNestedNulls: true);
        }
        catch (ProtocolException)
        {
            return null;
        }

        var delivery = asked.Delivery ?? "";
        var wantProtocol = delivery == DeliveryFile ? ProtocolFile : ProtocolHls;
        var wantMethod = delivery switch
        {
            DeliveryFile => MethodDirect,
            DeliveryRemux => MethodRemux,
            DeliveryTranscode => MethodTranscode,
            _ => "",
        };
        if (JsonMembers.AsString(o["id"]) is not { } id || !PlayId(id) ||
            JsonMembers.AsString(o["ticket"]) is not { } ticket || !Id(ticket) ||
            JsonMembers.AsString(o["protocol"]) is not { } protocol || protocol is not (ProtocolHls or ProtocolFile) || protocol != wantProtocol ||
            JsonMembers.AsString(o["method"]) is not { } method || method is not (MethodDirect or MethodRemux or MethodTranscode) ||
            (wantMethod.Length > 0 && method != wantMethod) ||
            JsonMembers.AsString(o["etag"]) is not { } etag || !Id(etag) ||
            JsonMembers.AsInteger(o["positionMs"]) is not { } pos || pos < 0 || pos > MaxPositionMs ||
            JsonMembers.AsString(o["versionId"]) is not { } versionId || !Id(versionId))
        {
            return null;
        }

        var audioTrack = "";
        if (o.TryGetPropertyValue("audioTrack", out var at))
        {
            if (JsonMembers.AsString(at) is not { } t)
            {
                return null;
            }

            audioTrack = TryTrackId(t, 'a', out _) ? t : "";
        }

        var renditions = false;
        if (o.TryGetPropertyValue("audioRenditions", out var ar))
        {
            if (Music.BoolOf(ar) is not { } b)
            {
                return null;
            }

            renditions = b && protocol == ProtocolHls;
        }

        int? kbps = null;
        if (o.TryGetPropertyValue("maxBitrateKbps", out var k))
        {
            if (JsonMembers.AsInteger(k) is not { } n)
            {
                return null;
            }

            if (n is >= 0 and <= MaxKbps)
            {
                kbps = (int)n;
            }
        }

        long scanBytes = 0;
        if (asked.Purpose == PurposeScan)
        {
            scanBytes = ScanCapOf(JsonMembers.AsInteger(o["scanBytes"]) ?? 0);
        }

        var video = asked.Video;
        return new PlayAnswer
        {
            Id = id,
            Ticket = ticket,
            Protocol = protocol,
            Method = method,
            Etag = etag,
            PositionMs = pos,
            VersionId = versionId,
            AudioTrack = audioTrack,
            AudioRenditions = renditions,
            MaxBitrateKbps = kbps,
            ScanBytes = scanBytes,
            Subtitles = video ? Video.ReadServed(o["subtitles"], asked.Subtitles) : [],
            FontCount = video ? Video.ReadFontCount(o["fontCount"]) : 0,
            ReadTitleAgain = !string.IsNullOrEmpty(asked.CatalogEtag) && etag != asked.CatalogEtag,
        };
    }

    /// <summary>
    /// The bytes a scan's ticket may read in all (playback section 8), from
    /// the scanBytes its answer states: that, from <see cref="ScanBytesFloor"/>
    /// to <see cref="ScanBytesTop"/>; the floor when it is missing (0) or
    /// out of range.
    /// </summary>
    public static long ScanCapOf(long scanBytes) => scanBytes is >= ScanBytesFloor and <= ScanBytesTop ? scanBytes : ScanBytesFloor;

    /// <summary>
    /// Whether the bytes of a new playback may follow those of
    /// <paramref name="reading"/> (playback section 2.7): only for a reading
    /// of delivery file, a new play of the same copy (its versionId),
    /// purpose and delivery, answered 200 (<paramref name="status"/>) with an
    /// answer <see cref="ReadPlayAnswer"/> keeps, of the same etag, compared
    /// byte for byte. The engine's RestartAsync follows these rules with its own
    /// playbacks; this is for a host or another engine that keeps its
    /// readings itself (Go's wire.Restart, the vectors' restarts).
    /// </summary>
    public static RestartOutcome Restart(FileReading reading, PlayRequest request, int status, ReadOnlySpan<byte> answer)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(request);
        var fail = new RestartOutcome { FailRead = true, ReadTitles = true };
        static string P(string p) => string.IsNullOrEmpty(p) ? PurposePlay : p;
        if (reading.Delivery != DeliveryFile || request.Delivery != DeliveryFile || request.VersionId != reading.VersionId ||
            P(request.Purpose) != P(reading.Purpose) || status != 200)
        {
            return fail;
        }

        if (ReadPlayAnswer(answer, new PlayAsked { Purpose = P(reading.Purpose), Delivery = DeliveryFile }) is not { } a || a.Etag != reading.Etag)
        {
            return fail with { StopNew = true };
        }

        return new RestartOutcome { Join = true, Offset = reading.Offset };
    }
}

/// <summary>
/// What a viewer's server asked and knows when it reads play's answer: the
/// request's purpose and delivery, whether the title is video's, the copy's
/// etag and subtitle tracks in the catalog.
/// </summary>
public sealed record PlayAsked
{
    /// <summary>The request's purpose; "" for play.</summary>
    public string Purpose { get; init; } = "";

    /// <summary>The request's delivery; "" for the baseline.</summary>
    public string Delivery { get; init; } = "";

    /// <summary>Whether the title is video's: its subtitles and fontCount are read.</summary>
    public bool Video { get; init; }

    /// <summary>The copy's etag in the catalog; "" when unknown.</summary>
    public string CatalogEtag { get; init; } = "";

    /// <summary>The copy's subtitle tracks in the catalog (their formats tell the forms video section 5.4 fixes).</summary>
    public IReadOnlyList<CopySubtitle> Subtitles { get; init; } = [];
}

/// <summary>
/// What a viewer's server keeps of play's answer (playback section 2.3,
/// video section 5.3), checked and cleaned (<see cref="Playback.ReadPlayAnswer"/>).
/// It says only what the catalog can't: the playback, its ticket and what
/// plays. Every URL is built from the ticket (<see cref="Urls"/>,
/// <see cref="MediaUrls"/>). Music's queue is music's to read.
/// </summary>
public sealed record PlayAnswer
{
    /// <summary>The playback's ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>The playback's ticket.</summary>
    public string Ticket { get; init; } = "";

    /// <summary>hls or file.</summary>
    public string Protocol { get; init; } = "";

    /// <summary>direct, remux or transcode.</summary>
    public string Method { get; init; } = "";

    /// <summary>The copy's etag.</summary>
    public string Etag { get; init; } = "";

    /// <summary>Where the playback starts, in the copy, in milliseconds.</summary>
    public long PositionMs { get; init; }

    /// <summary>The copy playing.</summary>
    public string VersionId { get; init; } = "";

    /// <summary>The audio track playing; "" when not said.</summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>
    /// Whether the playlist carries every audio track as a rendition (media
    /// section 3.1), so that switching tracks needs no new playback; false
    /// when the owning server made none (or the stream is a file).
    /// </summary>
    public bool AudioRenditions { get; init; }

    /// <summary>The bitrate cap in effect; null when not said.</summary>
    public int? MaxBitrateKbps { get; init; }

    /// <summary>A scan's: the bytes its ticket may read in all (<see cref="Playback.ScanCapOf"/>); 0 for another purpose.</summary>
    public long ScanBytes { get; init; }

    /// <summary>Video's: the subtitle tracks served apart.</summary>
    public IReadOnlyList<ServedSubtitle> Subtitles { get; init; } = [];

    /// <summary>Video's: the copy's fonts, 0 to 64.</summary>
    public int FontCount { get; init; }

    /// <summary>The etag isn't the catalog's: the answer wins for this playback; the title is read again from titles.</summary>
    public bool ReadTitleAgain { get; init; }

    /// <summary>The stream's URL under the owning server's stream root.</summary>
    public string StreamUrl(string root) => MediaUrls.Stream(root, Ticket, Protocol);

    /// <summary>
    /// The URLs the answer opens under the owning server's stream root, in
    /// order: the stream; each subtitle track's base (subtitles/, ass/ or
    /// pictures/, to which /&lt;part&gt; or /whole is added); each font. The
    /// copy's sheets are the catalog's (<see cref="MediaUrls.Sheet"/>).
    /// </summary>
    public IReadOnlyList<string> Urls(string root)
    {
        var out_ = new List<string> { StreamUrl(root) };
        foreach (var s in Subtitles)
        {
            out_.Add(MediaUrls.Subtitle(root, Ticket, s));
            if (s.Ass)
            {
                out_.Add(MediaUrls.Subtitle(root, Ticket, s, ass: true));
            }
        }

        for (var n = 0; n < FontCount; n++)
        {
            out_.Add(MediaUrls.Font(root, Ticket, n));
        }

        return out_;
    }
}

/// <summary>A playback's file a viewer's server is reading (playback section 2.7).</summary>
/// <param name="VersionId">Its copy.</param>
/// <param name="Etag">The copy's etag.</param>
/// <param name="Purpose">The playback's purpose.</param>
/// <param name="Delivery">Its delivery.</param>
/// <param name="Offset">The byte it was at.</param>
public sealed record FileReading(string VersionId, string Etag, string Purpose, string Delivery, long Offset);

/// <summary>
/// What a viewer's server does with a new play of a file it was reading
/// (playback section 2.7): Join, going on at Offset; else StopNew (a new
/// playback was started: stop it), FailRead and ReadTitles (read the title
/// again from titles).
/// </summary>
public sealed record RestartOutcome
{
    /// <summary>The read goes on.</summary>
    public bool Join { get; init; }

    /// <summary>At this byte, when joined.</summary>
    public long Offset { get; init; }

    /// <summary>Stop the new playback.</summary>
    public bool StopNew { get; init; }

    /// <summary>Fail the read.</summary>
    public bool FailRead { get; init; }

    /// <summary>Read the title again from titles.</summary>
    public bool ReadTitles { get; init; }
}
