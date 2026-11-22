using System.Globalization;
using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// An entry of a play answer's queue (music section 5.2): a queued track
/// prepared, the copy playing, how, and its stream.
/// </summary>
/// <param name="Title">The queued track's key.</param>
/// <param name="VersionId">The copy playing.</param>
/// <param name="Method">direct, remux or transcode.</param>
/// <param name="Stream">Its stream.</param>
/// <param name="DurationMs">Its duration; null when absent.</param>
public sealed record QueueEntry(string Title, string VersionId, string Method, QueueStream Stream, long? DurationMs)
{
    /// <summary>Writes the entry's members, in the spec's order.</summary>
    public void Write(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.String("title", Title).String("versionId", VersionId).String("method", Method)
            .Object("stream", Stream.Write).Integer("durationMs", DurationMs);
    }
}

/// <summary>A queue entry's stream: its URL (<see cref="Music.QueueUrl"/>), its protocol (hls or file), and its mimeType when sent ("" otherwise).</summary>
/// <param name="Url">Its URL.</param>
/// <param name="Protocol">hls or file.</param>
/// <param name="MimeType">Its mimeType; "" when absent.</param>
public sealed record QueueStream(string Url, string Protocol, string MimeType)
{
    /// <summary>Writes the stream's members.</summary>
    public void Write(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.String("url", Url).String("protocol", Protocol).String("mimeType", Music.Present(MimeType));
    }
}

/// <summary>
/// The queue of a play (MSFP music section 5.2): the tracks a play names
/// after its title, which the owning server prepares under the same
/// playback, and what the viewer's server keeps of the answer's entries.
/// </summary>
public static partial class Music
{
    /// <summary>How many tracks a play's queue names at most, and how many entries of an answer's queue are kept.</summary>
    public const int MaxQueue = 5;

    /// <summary>The only mimeType an hls stream may name.</summary>
    public const string HlsMimeType = "application/vnd.apple.mpegurl";

    private static readonly string[] Methods = ["direct", "remux", "transcode"];

    /// <summary>
    /// The owning server's check of a music play's title and queue (the
    /// request's member; null when absent): false, to refuse the request
    /// with 400 invalid-request, when title isn't a track key or queue isn't
    /// an array of strings, holds more than <see cref="MaxQueue"/> entries,
    /// or holds an entry that isn't a track key in canonical form, title
    /// itself or a repeat. A queued track the owning server doesn't offer
    /// the caller, or can't play, isn't refused: it is left out of the
    /// answer, its place kept empty.
    /// </summary>
    /// <param name="title">The play's title.</param>
    /// <param name="queue">The request's queue member; null when absent.</param>
    /// <param name="keys">The queue's track keys, in order; empty when absent or false.</param>
    public static bool TryCheckQueue(string? title, JsonNode? queue, out IReadOnlyList<string> keys)
    {
        keys = [];
        if (!MusicKey.Is(title, "track"))
        {
            return false;
        }

        if (queue is null)
        {
            return true;
        }

        if (queue is not JsonArray list || list.Count > MaxQueue)
        {
            return false;
        }

        var out_ = new List<string>(list.Count);
        foreach (var e in list)
        {
            if (JsonMembers.AsString(e) is not { } s || !MusicKey.Is(s, "track") || s == title || out_.Contains(s))
            {
                return false;
            }

            out_.Add(s);
        }

        keys = out_;
        return true;
    }

    /// <summary>
    /// The stream URL of the queued track at place <paramref name="n"/> (0 to
    /// 4) of the request's queue, under the playback's ticket (media section
    /// 1): &lt;stream root&gt;&lt;ticket&gt;/queue/&lt;n&gt;/master.m3u8 for hls,
    /// with the files it names under &lt;stream root&gt;&lt;ticket&gt;/queue/&lt;n&gt;/,
    /// or .../queue/&lt;n&gt;/file for file.
    /// </summary>
    public static string QueueUrl(string root, string ticket, int n, string protocol) =>
        root + ticket + "/queue/" + n.ToString(CultureInfo.InvariantCulture) + "/" + (protocol == "file" ? "file" : "master.m3u8");

    /// <summary>
    /// What the viewer's server keeps of a play answer's queue (null when
    /// absent, which keeps none, as does a queue that isn't an array), given
    /// the request's queue (<paramref name="asked"/>), its delivery ("" when
    /// absent), the owning server's stream root and the answer's own
    /// stream.url. An entry is dropped alone when it isn't an object; when
    /// its title isn't one of asked, after the place of the entry kept
    /// before it (out of order, a repeat, the request's title); when its
    /// versionId hasn't an ID's form, its method isn't direct, remux or
    /// transcode (with a delivery, the one it asks: direct for file), or its
    /// stream isn't an object of string url and protocol (and mimeType, when
    /// sent); when its url isn't exactly <see cref="QueueUrl"/> for its
    /// protocol and its place in asked, under the ticket of
    /// <paramref name="streamUrl"/> (with none, every entry is dropped); when
    /// an hls stream names another mimeType than <see cref="HlsMimeType"/>, a
    /// file stream is a remux or a transcode, or delivery file is served as
    /// hls. durationMs outside 0 to <see cref="MaxDurationMs"/> is kept as 0,
    /// one that isn't an integer as absent. At most <see cref="MaxQueue"/>
    /// entries are kept.
    /// </summary>
    public static IReadOnlyList<QueueEntry> ReadQueue(JsonNode? queue, IReadOnlyList<string> asked, string delivery, string root, string streamUrl)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(streamUrl);
        var kept = new List<QueueEntry>();
        var ticket = QueueTicket(streamUrl, root);
        if (ticket.Length == 0 || queue is not JsonArray list)
        {
            return kept;
        }

        var method = delivery switch
        {
            "file" => "direct",
            "remux" => "remux",
            "transcode" => "transcode",
            _ => "",
        };
        var next = 0; // the first place of asked an entry may take
        foreach (var e in list)
        {
            if (kept.Count == MaxQueue)
            {
                break;
            }

            if (e is not JsonObject o || JsonMembers.AsString(o["title"]) is not { } title)
            {
                continue;
            }

            var at = -1;
            for (var i = next; i < asked.Count; i++)
            {
                if (asked[i] == title)
                {
                    at = i;
                    break;
                }
            }

            if (at < 0 || ReadQueueEntry(o, title, method, delivery) is not { } entry ||
                entry.Stream.Url != QueueUrl(root, ticket, at, entry.Stream.Protocol))
            {
                continue;
            }

            kept.Add(entry);
            next = at + 1;
        }

        return kept;
    }

    // An entry's versionId, method, stream and durationMs; null drops it.
    // method is the one delivery asks, "" for any.
    private static QueueEntry? ReadQueueEntry(JsonObject o, string title, string method, string delivery)
    {
        var versionId = JsonMembers.AsString(o["versionId"]);
        var m = JsonMembers.AsString(o["method"]);
        if (versionId is null || !Playback.Id(versionId) || m is null || !Methods.Contains(m) || (method.Length > 0 && m != method) ||
            o["stream"] is not JsonObject s)
        {
            return null;
        }

        var url = JsonMembers.AsString(s["url"]);
        var protocol = JsonMembers.AsString(s["protocol"]);
        var sent = s.ContainsKey("mimeType");
        var mime = JsonMembers.AsString(s["mimeType"]);
        if ((sent && mime is null) || url is null || protocol is null)
        {
            return null;
        }

        switch (protocol)
        {
            case "hls":
                if ((sent && mime != HlsMimeType) || delivery == "file")
                {
                    return null;
                }

                break;
            case "file":
                if (m != "direct")
                {
                    return null;
                }

                break;
            default:
                return null;
        }

        long? duration = null;
        if (JsonMembers.AsInteger(o["durationMs"]) is { } d)
        {
            duration = d is >= 0 and <= MaxDurationMs ? d : 0;
        }

        return new QueueEntry(title, versionId, m, new QueueStream(url, protocol, mime ?? ""), duration);
    }

    // The ticket of a play answer's own stream URL, <root><ticket>/file or
    // <root><ticket>/master.m3u8; "" when it names none.
    private static string QueueTicket(string streamUrl, string root)
    {
        foreach (var protocol in (string[])["file", "hls"])
        {
            if (Playback.TryStreamBase(streamUrl, root, protocol, out var b))
            {
                return b[root.Length..^1];
            }
        }

        return "";
    }
}
