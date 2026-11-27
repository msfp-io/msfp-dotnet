using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// Why this server couldn't read a paired peer's titles: a closed set of
/// codes, the same in every MSFP engine (msfp-go's <c>ReadFailure</c>), for a
/// host's pages to name. A code never holds an address, a URL, a file name,
/// or anything the peer wrote: the detail is in the exception, for the logs.
/// </summary>
public static class ReadFailure
{
    /// <summary>No answer (no route, refused, a name that doesn't resolve, TLS failing), or an address this server never connects to.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The peer, or the reading, took too long (a call's 15 seconds, a scheduled reading's 5 minutes). A reading the host cancels isn't kept.</summary>
    public const string Timeout = "timeout";

    /// <summary>Its certificate is neither publicly trusted nor one it named.</summary>
    public const string CertificateRefused = "certificate-refused";

    /// <summary>
    /// An answer not signed by its key, a refusal or not: it doesn't hear this
    /// server's key (it ended the pairing, or lost its records), or something
    /// else answers at its address.
    /// </summary>
    public const string RefusedUnsigned = "refused-unsigned";

    /// <summary>A refusal signed by its key; <see cref="LastReading.Refusal"/> is its MSFP problem type (MSFP core section 8.2).</summary>
    public const string Refused = "refused";

    /// <summary>It isn't paired with this server (any more), or must pair again (it never moved to this server's new key).</summary>
    public const string NotPaired = "not-paired";

    /// <summary>It revoked its key; nothing is asked of it.</summary>
    public const string Suspended = "suspended";

    /// <summary>It can't be called (its own "this server can't be called", or an address that never resolves).</summary>
    public const string Uncallable = "uncallable";

    /// <summary>The two servers have no core version in common (<see cref="PeerView.Core"/> is -1).</summary>
    public const string NoCommonVersion = "no-common-version";

    /// <summary>The catalog part isn't on between the two.</summary>
    public const string CatalogOff = "catalog-off";

    /// <summary>
    /// Its answers are in the shapes of the previous draft of MSFP version 0
    /// (before proposals 16 and 17): it runs an older engine, and must be
    /// updated for its titles to be read. Both drafts announce core 0 with the
    /// same parts, so the hello can't tell; the reading does, from what the old
    /// shapes look like.
    /// </summary>
    public const string OlderDraft = "older-draft";

    /// <summary>
    /// An answer that couldn't be read: coded, too large, not JSON, not of the
    /// call's form, or a reading that breaks the catalog's rules (pages too
    /// large, changes and whole list mixed).
    /// </summary>
    public const string NotUnderstood = "answer-not-understood";

    /// <summary>This server's own failure (its store, say).</summary>
    public const string Internal = "internal";

    /// <summary>Every code, in the order above.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Unreachable, Timeout, CertificateRefused, RefusedUnsigned, Refused, NotPaired, Suspended, Uncallable,
        NoCommonVersion, CatalogOff, OlderDraft, NotUnderstood, Internal,
    ];
}

/// <summary>How a peer's last reading went (<see cref="PeerView.Reading"/>): kept in memory, from the engine's start.</summary>
public sealed record LastReading
{
    /// <summary>When it ended.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Why it failed, one of <see cref="ReadFailure"/>'s codes; null when it read.</summary>
    public string? Failure { get; init; }

    /// <summary>
    /// For <see cref="ReadFailure.Refused"/>, the problem type of the peer's
    /// refusal, one of MSFP core section 8.2's ("not-found", "rate-limited"…);
    /// null for another type, or another failure.
    /// </summary>
    public string? Refusal { get; init; }
}

public sealed partial class MsfpEngine
{
    // Each peer's last reading, as PeerView.Reading gives it.
    private readonly Dictionary<string, LastReading> _lastReadings = new(StringComparer.Ordinal);

    // Keeps how a reading of p ended, e null for one that read; null when
    // it isn't one to keep: the engine holding off (BackingOff, which keeps
    // the failure before it), or the host cancelling. A cancelled scheduled
    // reading (background) is its 5 minutes over: a timeout.
    private LastReading? ReadEnded(PeerRecord p, Exception? e, bool background)
    {
        var (failure, refusal, keep) = ReadFailureOf(p, e, background);
        if (!keep)
        {
            return null;
        }

        var last = new LastReading { At = Now(), Failure = failure, Refusal = refusal };
        lock (_lastReadings)
        {
            _lastReadings[p.Id] = last;
        }

        return last;
    }

    private LastReading? LastReadingOf(string id)
    {
        lock (_lastReadings)
        {
            return _lastReadings.GetValueOrDefault(id);
        }
    }

    // Why a reading of p failed with e.
    internal (string? Failure, string? Refusal, bool Keep) ReadFailureOf(PeerRecord p, Exception? e, bool background) => e switch
    {
        null => (null, null, true),
        MsfpException { Code: MsfpError.BackingOff } => (null, null, false),
        OperationCanceledException => background ? (ReadFailure.Timeout, null, true) : (null, null, false),
        MsfpException { SignedRefusal: true } m => (ReadFailure.Refused, m.Refusal, true),
        MsfpException { Failure: { } f } => (f, null, true),
        MsfpException { NotHeard: > 0 } => (ReadFailure.RefusedUnsigned, null, true),
        MsfpException m => (m.Code switch
        {
            MsfpError.CertificateRefused => ReadFailure.CertificateRefused,
            MsfpError.Unreachable or MsfpError.RefusedDestination => ReadFailure.Unreachable,
            MsfpError.Suspended => ReadFailure.Suspended,
            MsfpError.Uncallable => ReadFailure.Uncallable,
            MsfpError.NotSupported => Agreed(p) is null ? ReadFailure.NoCommonVersion : ReadFailure.CatalogOff,
            MsfpError.NotPaired or MsfpError.KeyLost or MsfpError.Unknown or MsfpError.NotFound => ReadFailure.NotPaired,
            _ => ReadFailure.Internal,
        }, null, true),
        HttpRequestException or IOException => (ReadFailure.Unreachable, null, true),
        _ => (ReadFailure.Internal, null, true),
    };

    /// <summary>
    /// Tells, from a page of titles of a peer whose libraries all carried a
    /// kind, whether its answers are in the shapes of the previous draft of
    /// version 0. Old, when every title carries a kind that is the one its key
    /// names (the previous draft's required member, gone from this one), and at
    /// least one film, episode or track is on the page and none of them carries
    /// copies (this draft's required member, which the previous one didn't
    /// have). Current, when a film, episode or track carries copies: the page
    /// is this draft's, and nothing more need be checked. A single title
    /// missing copies tells nothing (an owner's mistake): the three marks
    /// together do.
    /// </summary>
    internal static (bool Old, bool Current) OlderDraftPage(JsonArray titles)
    {
        var playable = 0;
        foreach (var raw in titles)
        {
            if (raw is not JsonObject t)
            {
                return (false, false);
            }

            var k = default(TitleKey);
            var parsed = JsonMembers.AsString(t["key"]) is { } key && TitleKey.TryParse(key, out k);
            if (parsed && k.Playable && t.ContainsKey("copies"))
            {
                return (false, true);
            }

            if (!parsed || JsonMembers.AsString(t["kind"]) != k.Kind)
            {
                return (false, false);
            }

            if (k.Playable)
            {
                playable++;
            }
        }

        return (playable > 0, false);
    }
}
