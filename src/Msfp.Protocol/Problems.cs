using System.Text.Json.Nodes;

namespace Msfp.Protocol;

/// <summary>
/// Problems: the types of a problem document, with their status, a title
/// (titles are examples; callers branch on the type) and whether the
/// refusal is final or temporary. The language's own are MSFP Media base
/// 4's; MSFP adds, as a carrier, its own (MSFP core section 8.2):
/// not-supported, rotation-refused, stream-limit-reached and rate-limited.
/// And how a caller reads a refusal: by its type and status
/// (<see cref="Meaning"/>), and, for play, what it tells its viewer
/// (<see cref="ReadPlayRefusal"/>).
/// </summary>
public static class Problems
{
    /// <summary>MSFP's own: the part isn't on with the caller (404).</summary>
    public const string NotSupported = "not-supported";

    /// <summary>MSFP's own: a rotation refused (409).</summary>
    public const string RotationRefused = "rotation-refused";

    /// <summary>MSFP's own: a limit reached (409, temporary).</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>The language's: anything else gone wrong at the source (500, temporary).</summary>
    public const string Internal = "internal-error";

    /// <summary>How many entries of a refusal's instead a caller reads (MSFP Media base 4).</summary>
    public const int MaxInstead = 4;

    /// <summary>What a refusal means to its caller (<see cref="Meaning"/>): a bad ask, 400; it gives up.</summary>
    public const string MeansBadAsk = "bad ask";

    /// <summary>404: the title isn't held.</summary>
    public const string MeansNotFound = "not found";

    /// <summary>404 gone: drop the title, read the titles again.</summary>
    public const string MeansGone = "gone";

    /// <summary>409 on play: another delivery, other targets (<see cref="ReadInstead"/>).</summary>
    public const string MeansAskElse = "ask else";

    /// <summary>409 not-playable on play.</summary>
    public const string MeansNotPlayable = "not playable";

    /// <summary>410: the playback is over.</summary>
    public const string MeansStopped = "stopped";

    /// <summary>409 of a carrier on play, 429, 500, 503: busy now, try later.</summary>
    public const string MeansBusy = "busy";

    /// <summary>None of these: as the call's own text says.</summary>
    public const string MeansOther = "other";

    /// <summary>What a caller tells its viewer of a refused play (<see cref="PlayRefusalReading.Tells"/>): delivery-refused, target-refused; another delivery or targets.</summary>
    public const string TellsAskAgain = "asks again";

    /// <summary>not-playable; any 400, 404 or 500.</summary>
    public const string TellsNotPlayable = "not playable";

    /// <summary>Any other 409, 429, 503, an unsigned refusal.</summary>
    public const string TellsBusy = "busy now";

    private readonly record struct Entry(int Status, string Title, bool Temporary, bool Language);

    private static readonly Dictionary<string, Entry> Table = new(StringComparer.Ordinal)
    {
        [Playback.ProblemInvalid] = new(400, "Invalid request", false, true),
        [Playback.ProblemNotFound] = new(404, "Not found", false, true),
        [Playback.ProblemGone] = new(404, "No longer held", false, true),
        [Playback.ProblemDeliveryRefused] = new(409, "This delivery is refused", false, true),
        [Playback.ProblemTargetRefused] = new(409, "This target is refused", false, true),
        [Playback.ProblemNotPlayable] = new(409, "This item can't be played", false, true),
        [Playback.ProblemStopped] = new(410, "Playback was stopped", false, true),
        [Internal] = new(500, "Internal error", true, true),
        [Playback.ProblemServerBusy] = new(503, "The server is busy; try again shortly", true, true),
        [Playback.ProblemUnreachable] = new(503, "The media can't be reached now", true, true),
        [Playback.ProblemTicketRefused] = new(404, "Ticket refused", false, true),

        [NotSupported] = new(404, "Not supported", false, false),
        [RotationRefused] = new(409, "Rotation refused", false, false),
        [Playback.ProblemStreamLimit] = new(409, "Too many streams on this account", true, false),
        [RateLimited] = new(429, "Too many requests", true, false),
    };

    /// <summary>Every type MSFP Media and MSFP list, with its status and title.</summary>
    public static IReadOnlyDictionary<string, (int Status, string Title)> All { get; } =
        Table.ToDictionary(p => p.Key, p => (p.Value.Status, p.Value.Title), StringComparer.Ordinal);

    /// <summary>A problem type's status and title; false for a type neither MSFP Media nor MSFP lists.</summary>
    public static bool TryGet(string type, out int status, out string title)
    {
        var ok = Table.TryGetValue(type, out var e);
        (status, title) = (e.Status, e.Title ?? "");
        return ok;
    }

    /// <summary>Whether a refusal of that type may be retried (MSFP Media base 4: after retryAfter when it carries one); false for a final one and for a type not listed.</summary>
    public static bool Temporary(string type) => Table.TryGetValue(type, out var e) && e.Temporary;

    /// <summary>Whether <paramref name="type"/> is one of MSFP Media's own refusals (base 4), not a carrier's.</summary>
    public static bool LanguageProblem(string type) => Table.TryGetValue(type, out var e) && e.Language;

    /// <summary>
    /// What a refusal of <paramref name="status"/> and <paramref name="type"/>
    /// means to its caller (MSFP Media base 4; for play,
    /// <paramref name="onPlay"/>, playing 2.5): a type the table lists, with
    /// its own status, means what the table says; any other is read by its
    /// status alone: 400 a bad ask, 404 not found, 410 no such playback,
    /// 429, 500 and 503 temporary, and a 409 of play busy now. A carrier's
    /// own types are read so too, but MSFP's not-supported and
    /// rotation-refused, whose meanings are MSFP's (<see cref="MeansOther"/>).
    /// </summary>
    public static string Meaning(int status, string type, bool onPlay)
    {
        if (Table.TryGetValue(type, out var e) && e.Status == status)
        {
            switch (type)
            {
                case Playback.ProblemGone:
                    return MeansGone;
                case Playback.ProblemDeliveryRefused or Playback.ProblemTargetRefused when onPlay:
                    return MeansAskElse;
                case Playback.ProblemNotPlayable when onPlay:
                    return MeansNotPlayable;
                case NotSupported or RotationRefused:
                    return MeansOther;
            }
        }

        return status switch
        {
            400 => MeansBadAsk,
            404 => MeansNotFound,
            410 => MeansStopped,
            409 when onPlay => MeansBusy,
            429 or 500 or 503 => MeansBusy,
            _ => MeansOther,
        };
    }

    /// <summary>
    /// Reads the instead of a refusal's body (MSFP Media base 4, on
    /// delivery-refused and target-refused), for a play of
    /// <paramref name="purpose"/> ("" for play): none when the body isn't an
    /// I-JSON object or its instead isn't an array; of its first
    /// <see cref="MaxInstead"/> entries, those that are objects whose members
    /// keep the forms of play's request (delivery present, a token, mediaType
    /// a media type with delivery remux only and then required, a transcode
    /// object of its forms with delivery transcode only, no null) and whose
    /// delivery the purpose takes (<see cref="Playback.PurposeAllows"/>); the
    /// others skipped.
    /// </summary>
    public static IReadOnlyList<Instead> ReadInstead(ReadOnlySpan<byte> body, string purpose)
    {
        JsonNode? tree;
        try
        {
            tree = StrictJson.Parse(body, nulls: true);
        }
        catch (ProtocolException)
        {
            return [];
        }

        if (tree is not JsonObject o || o["instead"] is not JsonArray list)
        {
            return [];
        }

        if (string.IsNullOrEmpty(purpose))
        {
            purpose = Playback.PurposePlay;
        }

        var out_ = new List<Instead>();
        for (var i = 0; i < list.Count && i < MaxInstead; i++)
        {
            if (list[i] is not JsonObject m || StrictJson.HoldsNull(m) || !m.ContainsKey("delivery"))
            {
                continue;
            }

            try
            {
                var (delivery, mediaType, targets, empty) = Playback.ReadDelivery(m);
                var r = new PlayRequest { Delivery = delivery, MediaType = mediaType ?? "", Transcode = targets, EmptyTrack = empty };
                if (r.TrackIdsOk && Playback.PurposeAllows(purpose, delivery, targets))
                {
                    out_.Add(new Instead(delivery, mediaType ?? "", targets));
                }
            }
            catch (PlayRefusal)
            {
                // Not of play's forms: skipped alone.
            }
        }

        return out_;
    }

    /// <summary>
    /// Reads a refusal of play of <paramref name="status"/> and
    /// <paramref name="body"/>, for a play of <paramref name="purpose"/> (""
    /// for play), as MSFP Media playing 2.5 and base 4 read it.
    /// <paramref name="signed"/> says whether the carrier vouches for it
    /// (MSFP: signed by the peer's key): one it doesn't is busy now, as are a
    /// 429 and a 503. A 409 is read by its type: delivery-refused and
    /// target-refused ask again (with <see cref="ReadInstead"/>), not-playable
    /// is not playable, any other type (a carrier's, one not known, none, a
    /// body that isn't JSON) busy now. Any other refusal is not playable;
    /// gone drops the title too.
    /// </summary>
    public static PlayRefusalReading ReadPlayRefusal(int status, bool signed, ReadOnlySpan<byte> body, string purpose)
    {
        var type = "";
        try
        {
            if (StrictJson.Parse(body, nulls: true) is JsonObject o)
            {
                type = JsonMembers.AsString(o["type"]) ?? "";
            }
        }
        catch (ProtocolException)
        {
            // No type.
        }

        if (!signed)
        {
            return new PlayRefusalReading { Tells = TellsBusy };
        }

        if (status is 429 or 503 || (status == 409 && type is not (Playback.ProblemDeliveryRefused or Playback.ProblemTargetRefused or Playback.ProblemNotPlayable)))
        {
            return new PlayRefusalReading { Tells = TellsBusy, RetryAfter = Msfp.Protocol.RetryAfter.Read(body) };
        }

        if (status == 409 && type != Playback.ProblemNotPlayable)
        {
            return new PlayRefusalReading { Tells = TellsAskAgain, Instead = ReadInstead(body, purpose) };
        }

        return new PlayRefusalReading { Tells = TellsNotPlayable, DropTitle = status == 404 && type == Playback.ProblemGone };
    }
}

/// <summary>
/// An entry of a refusal's instead (MSFP Media base 4, on delivery-refused
/// and target-refused): the members of a play request that name what to
/// serve, which the source would serve of the same copy and track, for the
/// same purpose. It is a hint: a play asking for it may still be refused.
/// </summary>
/// <param name="Delivery">The delivery ("" never: an entry has one).</param>
/// <param name="MediaType">A remux's media type; "" otherwise.</param>
/// <param name="Transcode">A transcode's targets; null when not said.</param>
public sealed record Instead(string Delivery, string MediaType, TranscodeTargets? Transcode);

/// <summary>
/// What a caller makes of a refused play (<see cref="Problems.ReadPlayRefusal"/>;
/// MSFP Media playing 2.5, base 4).
/// </summary>
public sealed record PlayRefusalReading
{
    /// <summary>What it tells the viewer: <see cref="Problems.TellsAskAgain"/>, <see cref="Problems.TellsNotPlayable"/> or <see cref="Problems.TellsBusy"/>.</summary>
    public string Tells { get; init; } = "";

    /// <summary>gone: the title dropped, the titles read again.</summary>
    public bool DropTitle { get; init; }

    /// <summary>On delivery-refused and target-refused: what it may ask instead.</summary>
    public IReadOnlyList<Instead> Instead { get; init; } = [];

    /// <summary>On a refusal read as busy now: 1 to 3,600 seconds when the body gives one; 0 otherwise.</summary>
    public int RetryAfter { get; init; }
}
