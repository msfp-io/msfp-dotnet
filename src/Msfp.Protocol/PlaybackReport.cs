namespace Msfp.Protocol;

// progress and stop (MSFP playback sections 3, 4, 6.1 and 7, music
// section 5.2): how an owning server reads them, and what they do to the
// playback they name.
public static partial class Playback
{
    /// <summary>The call progress.</summary>
    public const string CallProgress = "progress";

    /// <summary>The call stop.</summary>
    public const string CallStop = "stop";

    /// <summary>How long before the owning server's clock a report's at may be (playback section 3).</summary>
    public static readonly TimeSpan AtBefore = TimeSpan.FromDays(30);

    /// <summary>How long after the owning server's clock a report's at may be.</summary>
    public static readonly TimeSpan AtAfter = TimeSpan.FromMinutes(5);

    private static readonly string[] ProgressMembers = ["id", "positionMs", "paused", "at", "title"];
    private static readonly string[] StopMembers = ["id", "positionMs", "at", "title"];

    /// <summary>
    /// Reads a progress or a stop (<paramref name="call"/>) at
    /// <paramref name="now"/>, refusing what sections 3 and 4 refuse: a body
    /// that isn't an I-JSON object or holds null in a member it knows, an id
    /// that isn't a playback ID, positionMs out of 0 to
    /// <see cref="MaxPositionMs"/>, paused not a boolean, at not an integer
    /// from now − <see cref="AtBefore"/> to now + <see cref="AtAfter"/> (Unix
    /// seconds), a title that isn't a string. Whether title is the
    /// playback's is <see cref="PlaybackState.Apply"/>'s.
    /// </summary>
    /// <exception cref="PlayRefusal">The report is refused (400 invalid-request).</exception>
    public static PlayReport ReadReport(string call, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        var progress = call == CallProgress;
        var o = RequestObject(body, progress ? ProgressMembers : StopMembers);
        string? id;
        try
        {
            id = Text(o, "id");
        }
        catch (PlayRefusal)
        {
            id = null;
        }

        if (id is null || !PlayId(id))
        {
            throw BadPlay("id isn't a playback ID");
        }

        var hasPosition = o.ContainsKey("positionMs");
        var position = Integer(o, "positionMs", MaxPositionMs);
        var paused = progress && (Boolean(o, "paused") ?? false);
        var at = now;
        var atSaid = false;
        if (o.TryGetPropertyValue("at", out var v))
        {
            if (JsonMembers.AsInteger(v) is not { } n || n < now.Add(-AtBefore).ToUnixTimeSeconds() ||
                n > now.Add(AtAfter).ToUnixTimeSeconds())
            {
                throw BadPlay("at isn't an integer from 30 days before to 5 minutes after now");
            }

            at = DateTimeOffset.FromUnixTimeSeconds(n);
            atSaid = true;
        }

        return new PlayReport
        {
            Id = id,
            PositionMs = position,
            HasPosition = hasPosition,
            Paused = paused,
            At = at,
            AtSaid = atSaid,
            Title = Text(o, "title") ?? "",
        };
    }

    /// <summary>A playback given and not yet used (playback section 6.1).</summary>
    public const string StateStarted = "started";

    /// <summary>A playback in use.</summary>
    public const string StateActive = "active";

    /// <summary>A playback ended.</summary>
    public const string StateEnded = "ended";

    /// <summary>The report is kept.</summary>
    public const string EffectRecorded = "recorded";

    /// <summary>The report is kept, and the playback ends (stop).</summary>
    public const string EffectRecordedAndEnded = "recorded and ended";

    /// <summary>The playback ends, nothing kept.</summary>
    public const string EffectEnded = "ended";

    /// <summary>The report is older than one kept already.</summary>
    public const string EffectIgnored = "ignored";

    /// <summary>Nothing changes.</summary>
    public const string EffectUnchanged = "unchanged";
}

/// <summary>A progress or a stop, as <see cref="Playback.ReadReport"/> reads it.</summary>
public sealed record PlayReport
{
    /// <summary>The playback's ID.</summary>
    public string Id { get; init; } = "";

    /// <summary>The position, in milliseconds, when <see cref="HasPosition"/>.</summary>
    public long PositionMs { get; init; }

    /// <summary>Whether the report says a position.</summary>
    public bool HasPosition { get; init; }

    /// <summary>Paused (progress only).</summary>
    public bool Paused { get; init; }

    /// <summary>When the viewer was there: the request's at, when <see cref="AtSaid"/>, else now.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Whether the request said at.</summary>
    public bool AtSaid { get; init; }

    /// <summary>The track playing, of a music queue; "" when not said.</summary>
    public string Title { get; init; } = "";
}

/// <summary>
/// The playback a report names, as the owning server holds it (playback
/// section 6.1): its state (<see cref="Playback.StateStarted"/>,
/// <see cref="Playback.StateActive"/>, <see cref="Playback.StateEnded"/>;
/// any other is a playback of another pairing, or one never given), its
/// purpose, title and music queue, when its play was answered, and the at
/// of the latest report it keeps (null: none).
/// </summary>
public sealed record PlaybackState
{
    /// <summary>Its state.</summary>
    public string State { get; init; } = "";

    /// <summary>Its purpose.</summary>
    public string Purpose { get; init; } = "";

    /// <summary>Its title.</summary>
    public string Title { get; init; } = "";

    /// <summary>Its music queue.</summary>
    public IReadOnlyList<string> Queue { get; init; } = [];

    /// <summary>When its play was answered; null when unknown.</summary>
    public DateTimeOffset? PlayedAt { get; init; }

    /// <summary>The at of the latest report kept; null: none.</summary>
    public DateTimeOffset? LastAt { get; init; }

    /// <summary>Whether the playback's ticket works and it counts against its limits: started or active.</summary>
    public bool Live => State is Playback.StateStarted or Playback.StateActive;

    /// <summary>
    /// What report <paramref name="r"/> (of <paramref name="call"/>, read by
    /// <see cref="Playback.ReadReport"/>) does to the playback at
    /// <paramref name="now"/> (playback sections 3, 4, 6.1 and 7). A playback
    /// of another pairing, or unknown: 410 playback-stopped to progress,
    /// unchanged to stop. A title neither the playback's nor of its queue:
    /// 400. An ended playback: 410 to progress, unchanged to stop, except a
    /// download's late report with at, while now is at most its play plus
    /// <see cref="Playback.LateReports"/>: kept, the playback still ended. A
    /// live scan's progress changes nothing. A report older than the one
    /// kept is ignored; a stop ends the playback anyway.
    /// </summary>
    /// <exception cref="PlayRefusal">The report is refused (410 or 400).</exception>
    public ReportOutcome Apply(string call, PlayReport r, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(r);
        static PlayRefusal Stopped(string why) => new(410, Playback.ProblemStopped, why);
        var unchanged = new ReportOutcome { Effect = Playback.EffectUnchanged, State = State };
        if (!Live && State != Playback.StateEnded)
        {
            return call == Playback.CallProgress ? throw Stopped("not a playback of this pairing") : unchanged;
        }

        var title = r.Title;
        if (title.Length == 0)
        {
            title = Title;
        }
        else if (title != Title && !Queue.Contains(title, StringComparer.Ordinal))
        {
            throw Playback.BadPlay("title is neither the playback's nor of its queue");
        }

        var kept = new ReportOutcome { Title = title, PositionMs = r.PositionMs, HasPosition = r.HasPosition, At = r.At };
        var older = LastAt is { } last && r.At < last;
        if (!Live)
        {
            var late = Purpose == Playback.PurposeDownload && r.AtSaid && PlayedAt is { } played && now <= played + Playback.LateReports;
            if (!late)
            {
                return call == Playback.CallProgress ? throw Stopped("the playback has ended") : unchanged;
            }

            return older
                ? new ReportOutcome { Effect = Playback.EffectIgnored, State = Playback.StateEnded }
                : kept with { Effect = Playback.EffectRecorded, State = Playback.StateEnded };
        }

        if (call == Playback.CallStop)
        {
            return older || Purpose == Playback.PurposeScan
                ? new ReportOutcome { Effect = Playback.EffectEnded, State = Playback.StateEnded }
                : kept with { Effect = Playback.EffectRecordedAndEnded, State = Playback.StateEnded };
        }

        if (Purpose == Playback.PurposeScan)
        {
            return unchanged;
        }

        return older
            ? new ReportOutcome { Effect = Playback.EffectIgnored, State = Playback.StateActive }
            : kept with { Effect = Playback.EffectRecorded, State = Playback.StateActive };
    }
}

/// <summary>
/// What a report does: its effect, the state the playback is in after it,
/// and, when kept, the title, position and time kept.
/// </summary>
public sealed record ReportOutcome
{
    /// <summary>Its effect (<see cref="Playback.EffectRecorded"/> and the others).</summary>
    public string Effect { get; init; } = "";

    /// <summary>The playback's state after it.</summary>
    public string State { get; init; } = "";

    /// <summary>The title kept.</summary>
    public string Title { get; init; } = "";

    /// <summary>The position kept, when <see cref="HasPosition"/>.</summary>
    public long PositionMs { get; init; }

    /// <summary>Whether a position is kept.</summary>
    public bool HasPosition { get; init; }

    /// <summary>The time kept.</summary>
    public DateTimeOffset At { get; init; }
}
