namespace Msfp;

/// <summary>
/// The code of an engine error: MSFP's shared vocabulary, the same in every
/// MSFP library (engine.md section 9). Each name is the Go engine's code,
/// in .NET's casing (Go's "PlainHTTP" is <see cref="PlainHttp"/>).
/// </summary>
public enum MsfpError
{
    /// <summary>Anything else; logged, never shown to a peer.</summary>
    Internal = 0,

    // A peer's refusals, as the engine reports them for calls it makes
    // (MSFP core section 8.2).

    /// <summary>No such thing here, or a peer's signed 404 not-found (no such title, or not paired yet).</summary>
    NotFound,

    /// <summary>The peer answered a hello with a signed 404: it doesn't know this server, and is now marked gone.</summary>
    Unknown,

    /// <summary>The part isn't on with the peer.</summary>
    NotSupported,

    /// <summary>The peer answered 400: a bug on this side.</summary>
    InvalidRequest,

    /// <summary>The peer already knows this server's key.</summary>
    AlreadyPeer,

    /// <summary>The peer refused a rotation.</summary>
    RotationRefused,

    /// <summary>The pairing's streams are all in use.</summary>
    StreamLimitReached,

    /// <summary>The title can't be played.</summary>
    NotPlayable,

    /// <summary>The playback is over.</summary>
    PlaybackStopped,

    /// <summary>
    /// The owning server won't serve the delivery asked of that copy (MSFP
    /// playback section 2.5): ask again with another delivery, or without one.
    /// </summary>
    DeliveryRefused,

    /// <summary>
    /// A restart's copy isn't the one being read any more (another etag): its
    /// bytes can't follow; read the title again.
    /// </summary>
    CopyChanged,

    /// <summary>The peer asked this server to wait; calls hold until then.</summary>
    RateLimited,

    /// <summary>The peer (or this server) is busy now.</summary>
    ServerBusy,

    /// <summary>The peer answered 500.</summary>
    PeerFailed,

    /// <summary>No answer, an unsigned or badly signed one, or a refused certificate.</summary>
    Unreachable,

    // The engine's own refusals, before or instead of a call.

    /// <summary>The peer asked to wait, or didn't answer lately.</summary>
    BackingOff,

    /// <summary>The peer can't be called.</summary>
    Uncallable,

    /// <summary>The peer's key was revoked.</summary>
    Suspended,

    /// <summary>The peer never moved to this server's new key.</summary>
    KeyLost,

    /// <summary>The peer isn't paired.</summary>
    NotPaired,

    /// <summary>The peer isn't waiting for this server's operator.</summary>
    NotWaiting,

    /// <summary>Not a pairing code.</summary>
    CodeInvalid,

    /// <summary>The code's 24 hours are over.</summary>
    CodeExpired,

    /// <summary>The code is this server's own.</summary>
    OwnCode,

    /// <summary>The code's server refused it: used, withdrawn, or not its own.</summary>
    CodeRefused,

    /// <summary>This server can't be called, so it makes no codes.</summary>
    NoCodes,

    /// <summary>The next key's seed isn't on the server.</summary>
    NoNextKey,

    /// <summary>The text given isn't the committed next key.</summary>
    NotNextKey,

    /// <summary>No rotation waits for the operator.</summary>
    NoPending,

    /// <summary>The key is another peer's.</summary>
    KeyTaken,

    /// <summary>A rotation isn't over yet.</summary>
    RotationUnderWay,

    /// <summary>Not an address in MSFP's form.</summary>
    InvalidAddress,

    /// <summary>An http address: MSFP is HTTPS only. Go's "PlainHTTP".</summary>
    PlainHttp,

    /// <summary>An address the engine never connects to.</summary>
    RefusedDestination,

    /// <summary>A certificate neither trusted nor named.</summary>
    CertificateRefused,

    /// <summary>A new address doesn't answer.</summary>
    MoveFailed,

    /// <summary>Limits out of range.</summary>
    InvalidLimits,

    /// <summary>Not an ending a host may give (StopPlayback).</summary>
    InvalidReason,

    /// <summary>A malformed Range, or out of the file.</summary>
    RangeNotSatisfiable,

    /// <summary>A sealed value didn't open.</summary>
    Sealed,

    /// <summary>A change log no longer goes back that far: the engine then sends the whole list.</summary>
    TooOld,

    /// <summary>A sheet that isn't a JPEG within its size.</summary>
    NotAnImage,

    /// <summary>A code's uses or lifetime out of range, or a group's name or settings.</summary>
    CodeOptions,

    /// <summary>The store keeps no groups (<see cref="IGroupsTx"/>).</summary>
    NoGroups,

    /// <summary>This server's role in the group doesn't allow it.</summary>
    NoRight,

    /// <summary>Not a member of the group (or the group is gone).</summary>
    NotMember,

    /// <summary>This server has no pairing through the group, so a mail it sends can't leave it (MSFP groups section 6, "No way in").</summary>
    NoWayIn,

    /// <summary>Already in the group, banned from it, or waiting to join it (a group's code from a peer already).</summary>
    AlreadyMember,
}

/// <summary>
/// An error of the engine: one code of MSFP's shared vocabulary
/// (<see cref="Code"/>), and, for the logs, what happened (the message's
/// detail and the inner exception). Match it on its code:
/// <c>catch (MsfpException e) when (e.Code == MsfpError.CodeExpired)</c>.
/// </summary>
/// <remarks>
/// The detail may hold what an operator's logs need, never a secret (MSFP
/// core section 14.1); it is never sent to a peer.
/// </remarks>
public sealed class MsfpException : Exception
{
    /// <summary>An error of this code.</summary>
    /// <param name="code">The code.</param>
    public MsfpException(MsfpError code)
        : base(MessageOf(code, null, null)) => Code = code;

    /// <summary>An error of this code, with detail for the logs.</summary>
    /// <param name="code">The code.</param>
    /// <param name="detail">What happened.</param>
    public MsfpException(MsfpError code, string? detail)
        : base(MessageOf(code, detail, null)) => Code = code;

    /// <summary>An error of this code, with what caused it.</summary>
    /// <param name="code">The code.</param>
    /// <param name="detail">What happened; null to take the cause's message.</param>
    /// <param name="inner">What caused it.</param>
    public MsfpException(MsfpError code, string? detail, Exception? inner)
        : base(MessageOf(code, detail, inner), inner) => Code = code;

    /// <summary>For <see cref="MsfpError.Unreachable"/>: the call couldn't connect at all (no route, refused, timed out, a name that doesn't resolve, TLS failing).</summary>
    internal bool NoConnection { get; init; }

    /// <summary>For <see cref="MsfpError.Unreachable"/>: an unsigned refusal, the peer doesn't hear the key the call was signed with; its status.</summary>
    internal int NotHeard { get; init; }

    /// <summary>An error of code Internal (the forms .NET expects of an exception).</summary>
    public MsfpException()
        : this(MsfpError.Internal)
    {
    }

    /// <summary>An error of code Internal, with detail.</summary>
    /// <param name="message">What happened.</param>
    public MsfpException(string? message)
        : this(MsfpError.Internal, message)
    {
    }

    /// <summary>An error of code Internal, with detail and a cause.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">What caused it.</param>
    public MsfpException(string? message, Exception? innerException)
        : this(MsfpError.Internal, message, innerException)
    {
    }

    /// <summary>The code.</summary>
    public MsfpError Code { get; }

    /// <summary>
    /// The code's name in MSFP's vocabulary, as every library writes it
    /// ("PlainHTTP", "NotFound"…).
    /// </summary>
    public static string NameOf(MsfpError code) => code == MsfpError.PlainHttp ? "PlainHTTP" : code.ToString();

    // "msfp: Code: detail", as Go's Error() writes it.
    private static string MessageOf(MsfpError code, string? detail, Exception? inner)
    {
        detail ??= inner?.Message;
        return string.IsNullOrEmpty(detail) ? "msfp: " + NameOf(code) : "msfp: " + NameOf(code) + ": " + detail;
    }
}

/// <summary>A problem document's type, as MSFP core section 8.2 lists them.</summary>
internal static class ProblemType
{
    internal const string NotFound = "not-found";
    internal const string NotSupported = "not-supported";
    internal const string Invalid = "invalid-request";
    internal const string Rotation = "rotation-refused";
    internal const string StreamLimit = "stream-limit-reached";
    internal const string NotPlayable = "not-playable";
    internal const string Delivery = "delivery-refused";
    internal const string Stopped = "playback-stopped";
    internal const string RateLimited = "rate-limited";
    internal const string Internal = "internal-error";
    internal const string ServerBusy = "server-busy";
}

/// <summary>The problem types' statuses and titles, and what a peer's refusals mean.</summary>
internal static class Problems
{
    /// <summary>
    /// Each type's status and title (the titles are examples, as section 8.2
    /// says; these are its own).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (int Status, string Title)> ByType =
        new Dictionary<string, (int Status, string Title)>(StringComparer.Ordinal)
        {
            [ProblemType.NotFound] = (404, "Not found"),
            [ProblemType.NotSupported] = (404, "Not supported"),
            [ProblemType.Invalid] = (400, "Invalid request"),
            [ProblemType.Rotation] = (409, "Rotation refused"),
            [ProblemType.StreamLimit] = (409, "Too many streams on this account"),
            [ProblemType.NotPlayable] = (409, "This item can't be played"),
            [ProblemType.Delivery] = (409, "This delivery isn't served"),
            [ProblemType.Stopped] = (410, "Playback was stopped"),
            [ProblemType.RateLimited] = (429, "Too many requests"),
            [ProblemType.Internal] = (500, "Internal error"),
            [ProblemType.ServerBusy] = (503, "The server is busy; try again shortly"),
        };

    /// <summary>
    /// What a peer's signed refusal means, as an engine error (engine.md
    /// section 6). hello's signed 404 is Unknown, and is handled by its caller.
    /// </summary>
    /// <param name="status">The answer's status.</param>
    /// <param name="type">Its problem type.</param>
    internal static MsfpException RefusalError(int status, string type) => status switch
    {
        404 when type == ProblemType.NotSupported => new MsfpException(MsfpError.NotSupported),
        404 => new MsfpException(MsfpError.NotFound),
        400 => new MsfpException(MsfpError.InvalidRequest),
        409 when type == ProblemType.Rotation => new MsfpException(MsfpError.RotationRefused),
        409 when type == ProblemType.NotPlayable => new MsfpException(MsfpError.NotPlayable),
        409 when type == ProblemType.Delivery => new MsfpException(MsfpError.DeliveryRefused),
        409 => new MsfpException(MsfpError.StreamLimitReached),
        410 => new MsfpException(MsfpError.PlaybackStopped),
        429 => new MsfpException(MsfpError.RateLimited),
        503 => new MsfpException(MsfpError.ServerBusy),
        500 => new MsfpException(MsfpError.PeerFailed),
        _ => new MsfpException(MsfpError.Unreachable, "an unexpected status"),
    };
}
