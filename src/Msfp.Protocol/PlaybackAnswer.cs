using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

// play's answer (MSFP playback section 2.3), as a viewer's server checks
// and cleans it, and the restart of a file being read (section 2.7).
public static partial class Playback
{
    /// <summary>How long a version's name is kept, in code points (playback section 2.3, video section 3.6).</summary>
    public const int MaxVersionName = 32;

    /// <summary>The only mimeType an hls stream may name.</summary>
    public const string HlsType = "application/vnd.apple.mpegurl";

    /// <summary>
    /// Checks and cleans play's answer: <paramref name="delivery"/> is what
    /// the request asked ("" for none), <paramref name="root"/> the owning
    /// server's stream root, <paramref name="catalogEtag"/> the etag the
    /// catalog gave the copy ("" when unknown). Refused (null): a body that
    /// isn't an I-JSON object, null at its top, a member of the wrong type;
    /// id not a playback ID; method not direct, remux or transcode, or not
    /// the delivery's (direct for file); stream not under root as its
    /// protocol says (<see cref="TryStreamBase"/>), an hls mimeType other
    /// than <see cref="HlsType"/>, a file stream for a remux or a transcode,
    /// an hls one for delivery file; etag not 1 to 64 characters of A–Z a–z
    /// 0–9 - _; positionMs absent or out of 0 to <see cref="MaxPositionMs"/>.
    /// Cleaned: durationMs out of range set to 0; versions and tracks with
    /// another ID dropped, names made plain and cut; other IDs cleared; track
    /// members of the wrong type or value dropped; maxBitrateKbps out of
    /// range dropped. A refused answer: the viewer's server sends stop for
    /// its id, when it has one, and gives up.
    /// </summary>
    /// <returns>The answer kept; null when refused.</returns>
    public static PlayAnswer? ReadPlayAnswer(ReadOnlySpan<byte> body, string delivery, string root, string catalogEtag)
    {
        ArgumentNullException.ThrowIfNull(root);
        delivery ??= "";
        catalogEtag ??= "";
        JsonObject o;
        try
        {
            o = StrictJson.ReadAnswer(body, keepNestedNulls: true);
        }
        catch (ProtocolException)
        {
            return null;
        }

        // A string member: whether it is one, or absent and not required.
        bool Str(string name, bool required, out string s)
        {
            var there = o.TryGetPropertyValue(name, out var v);
            s = JsonMembers.AsString(v) ?? "";
            return there ? JsonMembers.AsString(v) is not null : !required;
        }

        var want = delivery switch
        {
            DeliveryFile => MethodDirect,
            DeliveryRemux => MethodRemux,
            DeliveryTranscode => MethodTranscode,
            _ => "",
        };
        if (!Str("id", true, out var id) || !PlayId(id) || !Str("method", true, out var method) ||
            method is not (MethodDirect or MethodRemux or MethodTranscode) || (delivery.Length > 0 && method != want) ||
            !Str("etag", true, out var etag) || !Id(etag))
        {
            return null;
        }

        if (o["stream"] is not JsonObject stream)
        {
            return null;
        }

        var url = JsonMembers.AsString(stream["url"]) ?? "";
        var protocol = JsonMembers.AsString(stream["protocol"]) ?? "";
        var mimeType = "";
        if (stream.TryGetPropertyValue("mimeType", out var mt) && mt is not null)
        {
            if (JsonMembers.AsString(mt) is not { } m)
            {
                return null;
            }

            mimeType = m;
        }

        var file = protocol == "file";
        if (!TryStreamBase(url, root, protocol, out var streamBase) ||
            (!file && mimeType.Length > 0 && mimeType != HlsType) ||
            (file && method != MethodDirect) || (delivery == DeliveryFile && !file))
        {
            return null;
        }

        if (JsonMembers.AsInteger(o["positionMs"]) is not { } pos || pos < 0 || pos > MaxPositionMs)
        {
            return null;
        }

        long duration = 0;
        if (o.TryGetPropertyValue("durationMs", out var d))
        {
            if (JsonMembers.AsInteger(d) is not { } n)
            {
                return null;
            }

            if (n is >= 0 and <= MaxPositionMs)
            {
                duration = n;
            }
        }

        var versions = new List<PlayVersion>();
        if (o.TryGetPropertyValue("versions", out var vs))
        {
            if (vs is not JsonArray list)
            {
                return null;
            }

            foreach (var e in list)
            {
                var eo = e as JsonObject;
                var vid = JsonMembers.AsString(eo?["id"]) ?? "";
                if (!Id(vid))
                {
                    continue;
                }

                versions.Add(new PlayVersion(vid, Music.CleanText(JsonMembers.AsString(eo!["name"]) ?? "", MaxVersionName)));
            }
        }

        var tracks = new List<PlayTrack>();
        if (o.TryGetPropertyValue("audioTracks", out var ts))
        {
            if (ts is not JsonArray list)
            {
                return null;
            }

            foreach (var e in list)
            {
                if (ReadPlayTrack(e) is { } t)
                {
                    tracks.Add(t);
                }
            }
        }

        if (!Str("versionId", false, out var versionId) || !Str("audioTrack", false, out var audioTrack))
        {
            return null;
        }

        var kbps = 0;
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

        return new PlayAnswer
        {
            Id = id,
            Method = method,
            Stream = new PlayStream(url, protocol, mimeType, streamBase),
            Etag = etag,
            PositionMs = pos,
            DurationMs = duration,
            Versions = versions,
            VersionId = Id(versionId) ? versionId : "",
            AudioTracks = tracks,
            AudioTrack = Id(audioTrack) ? audioTrack : "",
            MaxBitrateKbps = kbps,
            ReadTitleAgain = catalogEtag.Length > 0 && etag != catalogEtag,
        };
    }

    // Reads a track of an answer: null, dropped, without an ID of its form;
    // each other member of the wrong type or value dropped alone.
    private static PlayTrack? ReadPlayTrack(JsonNode? e)
    {
        var o = e as JsonObject;
        var id = JsonMembers.AsString(o?["id"]) ?? "";
        if (!Id(id))
        {
            return null;
        }

        static bool Flag(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.True;
        var language = JsonMembers.AsString(o!["language"]);
        var role = JsonMembers.AsString(o["role"]);
        return new PlayTrack
        {
            Id = id,
            Language = Languages.ValidTag(language) ? language! : "",
            Channels = JsonMembers.AsInteger(o["channels"]) is { } n && n >= 0 ? (int)Math.Min(n, int.MaxValue) : 0,
            Role = role is "commentary" or "description" ? role : "",
            Default = Flag(o["default"]),
            Forced = Flag(o["forced"]),
            HearingImpaired = Flag(o["hearingImpaired"]),
        };
    }

    /// <summary>
    /// Whether the bytes of a new playback may follow those of
    /// <paramref name="reading"/> (playback section 2.7): only for a reading
    /// of delivery file, a new play of the same copy (its versionId),
    /// purpose and delivery, answered 200 (<paramref name="status"/>) with an
    /// answer <see cref="ReadPlayAnswer"/> keeps, of the same etag, compared
    /// byte for byte. <paramref name="root"/> is the owning server's stream
    /// root.
    /// </summary>
    public static RestartOutcome Restart(FileReading reading, PlayRequest request, int status, ReadOnlySpan<byte> answer, string root)
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

        if (ReadPlayAnswer(answer, DeliveryFile, root, "") is not { } a || a.Etag != reading.Etag)
        {
            return fail with { StopNew = true };
        }

        return new RestartOutcome { Join = true, Offset = reading.Offset };
    }
}

/// <summary>
/// What a viewer's server keeps of play's answer (playback section 2.3):
/// the playback's own members, checked and cleaned
/// (<see cref="Playback.ReadPlayAnswer"/>). The content parts' members
/// (video's subtitles and previews, music's queue) are theirs to read;
/// info, title and next aren't kept.
/// </summary>
public sealed record PlayAnswer
{
    /// <summary>The playback's ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>direct, remux or transcode.</summary>
    public string Method { get; init; } = "";

    /// <summary>The stream.</summary>
    public PlayStream Stream { get; init; } = new("", "", "", "");

    /// <summary>The copy's etag.</summary>
    public string Etag { get; init; } = "";

    /// <summary>Where the playback starts, in milliseconds.</summary>
    public long PositionMs { get; init; }

    /// <summary>The duration, in milliseconds; 0 when unknown.</summary>
    public long DurationMs { get; init; }

    /// <summary>The title's copies, to choose another.</summary>
    public IReadOnlyList<PlayVersion> Versions { get; init; } = [];

    /// <summary>The copy playing; "" when not said.</summary>
    public string VersionId { get; init; } = "";

    /// <summary>The copy's audio tracks.</summary>
    public IReadOnlyList<PlayTrack> AudioTracks { get; init; } = [];

    /// <summary>The audio track playing; "" when not said.</summary>
    public string AudioTrack { get; init; } = "";

    /// <summary>The bitrate cap the owning server applied; 0 when not said.</summary>
    public int MaxBitrateKbps { get; init; }

    /// <summary>The etag isn't the catalog's: the answer wins for this playback; the title is read again from titles.</summary>
    public bool ReadTitleAgain { get; init; }
}

/// <summary>An answer's stream.</summary>
/// <param name="Url">Its URL, under the owning server's stream root.</param>
/// <param name="Protocol">hls or file.</param>
/// <param name="MimeType">Its mimeType; "" when not said.</param>
/// <param name="Base">The stream's base (<see cref="Playback.TryStreamBase"/>), under which every other URL of the answer must be.</param>
public sealed record PlayStream(string Url, string Protocol, string MimeType, string Base);

/// <summary>An entry of an answer's versions.</summary>
/// <param name="Id">The version's ID.</param>
/// <param name="Name">Its name, plain and cut; "" when none.</param>
public sealed record PlayVersion(string Id, string Name);

/// <summary>A track of an answer, of playback section 2.3's members (video's, its codec and the rest, are video's).</summary>
public sealed record PlayTrack
{
    /// <summary>Its ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>Its language tag; "" when none or invalid.</summary>
    public string Language { get; init; } = "";

    /// <summary>Its channels; 0 when not said.</summary>
    public int Channels { get; init; }

    /// <summary>commentary, description, or "".</summary>
    public string Role { get; init; } = "";

    /// <summary>Whether it is the default.</summary>
    public bool Default { get; init; }

    /// <summary>Whether it is forced.</summary>
    public bool Forced { get; init; }

    /// <summary>Whether it is for the hearing impaired.</summary>
    public bool HearingImpaired { get; init; }
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
