using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// An entry of a play request's queue (music section 5.2), as the owning
/// server accepted it: the track, the copy it names ("" for its first),
/// and the delivery it takes: its own, with its own mediaType or
/// transcode, else the request's ("" for the baseline).
/// </summary>
/// <param name="Title">The queued track's key.</param>
/// <param name="VersionId">The copy it names; "" for none.</param>
/// <param name="Delivery">The delivery it takes: its own, else the request's.</param>
/// <param name="OwnDelivery">Whether the entry names its own delivery.</param>
public sealed record QueueAsk(string Title, string VersionId, string Delivery, bool OwnDelivery)
{
    /// <summary>The method an answer's entry must say for <see cref="Delivery"/>: direct for file, remux, transcode; "" for the baseline (any).</summary>
    public string Method => Delivery switch
    {
        Playback.DeliveryFile => Playback.MethodDirect,
        Playback.DeliveryRemux => Playback.MethodRemux,
        Playback.DeliveryTranscode => Playback.MethodTranscode,
        _ => "",
    };

    /// <summary>The protocol an answer's entry must say: file for delivery file, hls otherwise.</summary>
    public string Protocol => Delivery == Playback.DeliveryFile ? Playback.ProtocolFile : Playback.ProtocolHls;

    /// <summary>The entry as a request writes it: title, versionId and delivery, absent ones left out (its own mediaType or transcode are the sender's to add).</summary>
    public void Write(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.String("title", Title).String("versionId", Music.Present(VersionId)).String("delivery", OwnDelivery ? Delivery : null);
    }
}

/// <summary>
/// An entry of a play answer's queue as kept (music section 5.2): a queued
/// track prepared, the copy playing, how, and its protocol. It carries no
/// URL: the viewer's server builds it from the playback's ticket and
/// <see cref="Place"/> (<see cref="MediaUrls.Queue"/>).
/// </summary>
/// <param name="Title">The queued track's key.</param>
/// <param name="VersionId">The copy playing.</param>
/// <param name="Method">direct, remux or transcode.</param>
/// <param name="Protocol">hls or file.</param>
/// <param name="Place">Its place in the request's queue, 0 to 4.</param>
public sealed record QueueEntry(string Title, string VersionId, string Method, string Protocol, int Place)
{
    /// <summary>Writes the entry's members, in the spec's order: title, versionId, method, protocol.</summary>
    public void Write(JsonOut o)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.String("title", Title).String("versionId", VersionId).String("method", Method).String("protocol", Protocol);
    }
}

/// <summary>
/// The queue of a play (MSFP music section 5.2): the tracks a play names
/// after its title, each with its own copy and delivery if it wants, which
/// the owning server prepares under the same playback, and what the
/// viewer's server keeps of the answer's entries.
/// </summary>
public static partial class Music
{
    /// <summary>How many tracks a play's queue names at most, and how many entries of an answer's queue are kept.</summary>
    public const int MaxQueue = 5;

    /// <summary>A track's remux media types: MPEG-TS or fragmented MP4 segments, audio only (music section 5).</summary>
    public static readonly IReadOnlyList<string> RemuxTypes = ["audio/mp2t", "audio/mp4"];

    private static readonly string[] Methods = [Playback.MethodDirect, Playback.MethodRemux, Playback.MethodTranscode];

    /// <summary>
    /// The owning server's check of a music play's title and queue
    /// (<paramref name="request"/>, the play request's object as read): the
    /// queue's entries, none without a queue, each with the delivery it
    /// takes (its own, else the request's). Refused with 400 invalid-request when title isn't a track key,
    /// or queue isn't an array, holds more than <see cref="MaxQueue"/>
    /// entries, or holds an entry that isn't an object, whose title isn't a
    /// track key in canonical form, is title itself or a repeat, whose
    /// versionId isn't a version ID, whose delivery isn't a token, or whose
    /// mediaType or transcode is malformed or out of its combination (a
    /// mediaType with the entry's own delivery remux only, and then
    /// required; a transcode object with its own delivery transcode only).
    /// Then, no entry malformed, refused with 409 delivery-refused for the
    /// first entry whose delivery isn't file, remux or transcode, or whose
    /// remux names another media type than audio/mp2t or audio/mp4. The
    /// request's own delivery members, and an entry's transcode targets, are
    /// playback's (<see cref="Playback.ReadQueueEntry"/>). A queued track the
    /// owning server doesn't offer the caller, or can't play, isn't refused:
    /// it is left out of the answer, its place kept empty.
    /// </summary>
    /// <exception cref="PlayRefusal">The request is refused.</exception>
    public static IReadOnlyList<QueueAsk> CheckQueue(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = JsonMembers.AsString(request["title"]);
        var delivery = JsonMembers.AsString(request["delivery"]) ?? "";
        if (!MusicKey.Is(title, "track"))
        {
            throw Playback.BadPlay("title isn't a track key");
        }

        if (!request.TryGetPropertyValue("queue", out var queue))
        {
            return [];
        }

        if (queue is not JsonArray list)
        {
            throw Playback.BadPlay("queue isn't an array");
        }

        if (list.Count > MaxQueue)
        {
            throw Playback.BadPlay("a queue of more than 5 entries");
        }

        var asked = new List<QueueAsk>(list.Count);
        PlayRefusal? refused = null; // the first 409, thrown once no entry is malformed
        static PlayRefusal Refuse(string why) => new(409, Playback.ProblemDeliveryRefused, why);
        foreach (var e in list)
        {
            if (e is not JsonObject o)
            {
                throw Playback.BadPlay("a queue entry isn't an object");
            }

            if (JsonMembers.AsString(o["title"]) is not { } s)
            {
                throw Playback.BadPlay("a queue entry's title isn't a string");
            }

            if (!MusicKey.Is(s, "track") || s == title || asked.Exists(a => a.Title == s))
            {
                throw Playback.BadPlay("a queue entry isn't a track key in canonical form, or is title itself, or a repeat");
            }

            var versionId = "";
            if (o.TryGetPropertyValue("versionId", out var vid))
            {
                if (JsonMembers.AsString(vid) is not { } v || !Playback.Id(v))
                {
                    throw Playback.BadPlay("a queue entry's versionId isn't a version ID");
                }

                versionId = v;
            }

            var own = o.TryGetPropertyValue("delivery", out var d);
            var entryDelivery = "";
            if (own)
            {
                if (JsonMembers.AsString(d) is not { } x || !Names.IsToken(x))
                {
                    throw Playback.BadPlay("a queue entry's delivery isn't a token");
                }

                if (x is not (Playback.DeliveryFile or Playback.DeliveryRemux or Playback.DeliveryTranscode))
                {
                    refused ??= Refuse("a queue entry's delivery the owning server doesn't make");
                }

                entryDelivery = x;
            }

            if (o.TryGetPropertyValue("mediaType", out var mt))
            {
                if (entryDelivery != Playback.DeliveryRemux)
                {
                    throw Playback.BadPlay("a queue entry's mediaType without its own delivery remux");
                }

                if (JsonMembers.AsString(mt) is not { } m || !Names.IsMediaType(m))
                {
                    throw Playback.BadPlay("a queue entry's mediaType isn't a media type");
                }

                if (!RemuxTypes.Contains(m, StringComparer.Ordinal))
                {
                    refused ??= Refuse("a queue entry's remux into " + m + ", not audio/mp2t or audio/mp4");
                }
            }
            else if (entryDelivery == Playback.DeliveryRemux)
            {
                throw Playback.BadPlay("a queue entry's delivery remux without mediaType");
            }

            if (o.TryGetPropertyValue("transcode", out var tr))
            {
                if (entryDelivery != Playback.DeliveryTranscode)
                {
                    throw Playback.BadPlay("a queue entry's transcode object without its own delivery transcode");
                }

                if (tr is not JsonObject)
                {
                    throw Playback.BadPlay("a queue entry's transcode isn't an object");
                }
            }

            asked.Add(new QueueAsk(s, versionId, own ? entryDelivery : delivery, own));
        }

        if (refused is not null)
        {
            throw refused;
        }

        return asked;
    }

    /// <summary>
    /// What the viewer's server keeps of a play answer's queue (null when
    /// absent, which keeps none, as does a queue that isn't an array), given
    /// the request's queue as accepted (<paramref name="asked"/>). An entry
    /// is dropped alone when it isn't an object; when its title isn't one of
    /// asked, after the place of the entry kept before it (out of order, a
    /// repeat, the request's title); when its versionId hasn't an ID's form,
    /// or isn't the one the request's entry named; when its method isn't
    /// direct, remux or transcode, or not the one its delivery asks (direct
    /// for file); when its protocol isn't file for delivery file, hls
    /// otherwise. Members of earlier drafts (stream, durationMs) are
    /// ignored. At most <see cref="MaxQueue"/> entries are kept.
    /// </summary>
    public static IReadOnlyList<QueueEntry> ReadQueue(JsonNode? queue, IReadOnlyList<QueueAsk> asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        var kept = new List<QueueEntry>();
        if (queue is not JsonArray list)
        {
            return kept;
        }

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
                if (asked[i].Title == title)
                {
                    at = i;
                    break;
                }
            }

            if (at < 0)
            {
                continue;
            }

            var a = asked[at];
            var versionId = JsonMembers.AsString(o["versionId"]);
            var method = JsonMembers.AsString(o["method"]);
            var protocol = JsonMembers.AsString(o["protocol"]);
            if (versionId is null || !Playback.Id(versionId) || (a.VersionId.Length > 0 && versionId != a.VersionId) ||
                method is null || !Methods.Contains(method) || (a.Method.Length > 0 && method != a.Method) || protocol != a.Protocol)
            {
                continue;
            }

            kept.Add(new QueueEntry(title, versionId, method, protocol, at));
            next = at + 1;
        }

        return kept;
    }
}
