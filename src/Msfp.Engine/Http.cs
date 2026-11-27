using System.Net;
using Msfp.Protocol;

namespace Msfp;

/// <summary>
/// A request to the engine's routes in a neutral form, so that any
/// router can give it: ASP.NET Core (minimal APIs or MVC controllers, as
/// Jellyfin's), plain Kestrel, HttpListener or a test.
/// </summary>
/// <remarks>
/// The path is the raw request target, exactly as sent (path and query):
/// the signature covers it, so never a decoded or rewritten one, and never
/// one with a prefix stripped. In ASP.NET Core:
/// <c>context.Features.Get&lt;IHttpRequestFeature&gt;()!.RawTarget</c>.
/// </remarks>
public sealed class MsfpRequest
{
    /// <summary>The method, upper case ("POST").</summary>
    public required string Method { get; init; }

    /// <summary>The raw request target: path and query as sent.</summary>
    public required string Target { get; init; }

    /// <summary>The request's headers.</summary>
    public required IHeaderReader Headers { get; init; }

    /// <summary>The body; the engine reads at most what MSFP allows (64 KiB for a federation call).</summary>
    public required Stream Body { get; init; }

    /// <summary>
    /// The client's network address, for the limits by address (MSFP
    /// core section 8.3); behind a trusted proxy, the client's own. Null when
    /// unknown: then all such requests share one limit.
    /// </summary>
    public IPAddress? ClientAddress { get; init; }
}

/// <summary>
/// The engine's answer in a neutral form: the host writes its status,
/// headers and body as they are, with no compression and nothing added
/// to the body.
/// </summary>
public sealed class MsfpResponse
{
    /// <summary>The default unsigned 404 body: MSFP's problem document (MSFP core section 3).</summary>
    public static ReadOnlyMemory<byte> PlainNotFoundBody { get; } = "{\"type\":\"not-found\",\"title\":\"Not found\",\"status\":404}\n"u8.ToArray();

    /// <summary>The HTTP status.</summary>
    public int Status { get; init; }

    /// <summary>The headers to send, in order (Content-Type, MSFP-Signature, Retry-After).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>The body, exact bytes.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>
    /// The unsigned 404 of a refused request (to a stranger, or one that
    /// isn't MSFP's): a host may answer its own not-found page instead, as
    /// for any unknown path. Its status, headers and body are MSFP's
    /// default otherwise.
    /// </summary>
    public bool PlainNotFound { get; init; }

    internal static MsfpResponse Plain404 { get; } = new()
    {
        Status = 404,
        PlainNotFound = true,
        Headers = [new("Content-Type", "application/problem+json")],
        Body = PlainNotFoundBody,
    };

    /// <summary>The first value of a header, or null.</summary>
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>An answer a call's handler gives, signed (or not) by the engine.</summary>
internal sealed record Answer(int Status, string? ContentType, byte[] Body, int RetryAfter = 0, bool Plain = false)
{
    public static Answer PlainRefusal { get; } = new(404, null, [], Plain: true);

    /// <summary>A signed problem document of the type, with retryAfter (0: none).</summary>
    public static Answer Refuse(string type, int retryAfter = 0)
    {
        var (status, title) = Problems.ByType[type];
        var body = JsonOut.Object(o => o.String("type", type).String("title", title).Integer("status", status)
            .Integer("retryAfter", retryAfter > 0 ? retryAfter : null));
        return new Answer(status, "application/problem+json", [.. body, (byte)'\n'], retryAfter);
    }

    /// <summary>A signed 200 with this JSON body.</summary>
    public static Answer Ok(byte[] body) => new(200, "application/json", body);
}
