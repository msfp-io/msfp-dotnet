using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Msfp.Protocol;

/// <summary>
/// Signed requests and answers (core section 6). Every request between
/// paired servers carries four headers: <see cref="HeaderKey"/> (the
/// sender's public key), <see cref="HeaderDate"/> (Unix seconds),
/// <see cref="HeaderNonce"/> (16 random bytes, never used twice) and
/// <see cref="HeaderSignature"/>. The signature covers the method, the
/// server it's meant for, the path, the date, the nonce and the body's
/// SHA-256. The answer carries an MSFP-Signature too, from the server
/// answering, over the request's nonce, the status, the answer's media
/// type and its body.
/// </summary>
public static partial class Signing
{
    /// <summary>The sender's public key, base64url.</summary>
    public const string HeaderKey = "MSFP-Key";

    /// <summary>When it was signed, Unix seconds.</summary>
    public const string HeaderDate = "MSFP-Date";

    /// <summary>16 random bytes, base64url.</summary>
    public const string HeaderNonce = "MSFP-Nonce";

    /// <summary>The Ed25519 signature, base64url.</summary>
    public const string HeaderSignature = "MSFP-Signature";

    /// <summary>How far a request's date may be from the receiver's clock.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    // The signature headers of MSFP's first test version: a request that
    // carries one of them is refused (core section 6.1).
    private static readonly string[] OldHeaders = ["Overfin-Key", "Overfin-Date", "Overfin-Nonce", "Overfin-Signature"];

    // What's signed starts with what it is, so a signature made for one use
    // never passes for another (core sections 6.2, 6.3 and 9.1).
    private const string RequestLabel = "msfp-1 request\n";
    private const string ResponseLabel = "msfp-1 response\n";
    internal const string PairingLabel = "msfp-1 pairing\n";

    [GeneratedRegex("^[1-9][0-9]{0,15}\\z")]
    private static partial Regex DateRe();

    /// <summary>Reads an MSFP-Date (core section 2.2): a positive integer in its one form.</summary>
    public static bool TryParseDate(string text, out long date)
    {
        date = 0;
        return DateRe().IsMatch(text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out date) &&
               date <= StrictJson.MaxInteger;
    }

    /// <summary>
    /// A signature header sent exactly once, its value without a comma
    /// (core section 2.4); null otherwise.
    /// </summary>
    public static string? OneHeader(IHeaderReader headers, string name)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var v = headers.GetValues(name);
        return v.Count != 1 || v[0].Contains(',', StringComparison.Ordinal) ? null : v[0];
    }

    /// <summary>The seven lines a request's signature covers (core section 6.2).</summary>
    public static byte[] RequestMessage(string method, string host, string target, string date, string nonce,
        ReadOnlySpan<byte> body) =>
        Encoding.UTF8.GetBytes(RequestLabel + method + "\n" + host.ToLowerInvariant() + "\n" + target + "\n" + date +
                               "\n" + nonce + "\n" + Convert.ToHexStringLower(SHA256.HashData(body)));

    /// <summary>
    /// The five lines an answer's signature covers (core section 6.3): the
    /// request's nonce, the status, the answer's media type
    /// (<see cref="MediaType"/>) and the body's SHA-256.
    /// </summary>
    public static byte[] ResponseMessage(string nonce, int status, string? contentType, ReadOnlySpan<byte> body) =>
        Encoding.UTF8.GetBytes(ResponseLabel + nonce + "\n" + status.ToString(CultureInfo.InvariantCulture) + "\n" +
                               MediaType(contentType) + "\n" + Convert.ToHexStringLower(SHA256.HashData(body)));

    /// <summary>
    /// A Content-Type header as an answer's signature names it (core
    /// section 6.3): its type and subtype in lower case, without parameters
    /// or white space; "-" when there is none.
    /// </summary>
    public static string MediaType(string? contentType)
    {
        var mt = (contentType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();
        return mt.Length == 0 ? "-" : mt;
    }

    /// <summary>
    /// Reads an answer's Content-Type as its signature names it (core
    /// section 6.3): one header or none. None, or an empty one, is "-".
    /// One sent more than once, or whose value doesn't read as
    /// type/subtype (each an RFC 9110 token), fails like a badly signed
    /// answer, so two implementations never read different media types.
    /// </summary>
    /// <exception cref="ProtocolException">Not one readable media type (<see cref="ProtocolError.Signature"/>).</exception>
    public static string AnswerMediaType(IHeaderReader headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var values = headers.GetValues("Content-Type");
        if (values.Count == 0)
        {
            return "-";
        }

        if (values.Count > 1)
        {
            throw new ProtocolException(ProtocolError.Signature);
        }

        var value = values[0].Trim(' ', '\t');
        if (value.Length == 0)
        {
            return "-";
        }

        var mt = value.Split(';', 2)[0].TrimEnd(' ', '\t');
        var slash = mt.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || !IsToken(mt[..slash]) || !IsToken(mt[(slash + 1)..]))
        {
            throw new ProtocolException(ProtocolError.Signature);
        }

        return mt.ToLowerInvariant();
    }

    // A token of RFC 9110 (section 5.6.2): one or more tchar.
    internal static bool IsToken(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Signs a request to <paramref name="host"/> (the receiver's address's
    /// host, with its port) for <paramref name="target"/> (its path), whose
    /// body is <paramref name="body"/>, with key, at now. The nonce's 16
    /// bytes come from <paramref name="random"/> (a cryptographic source,
    /// or a test's fixed bytes; the system's when null). The result's
    /// headers go on the request; its nonce is what the answer's signature
    /// covers.
    /// </summary>
    public static SignedRequest SignRequest(string method, string host, string target, ReadOnlySpan<byte> body,
        MsfpKey key, DateTimeOffset now, RandomNumberGenerator? random = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var n = new byte[16];
        if (random is null)
        {
            RandomNumberGenerator.Fill(n);
        }
        else
        {
            random.GetBytes(n);
        }

        return SignRequestWithNonce(method, host, target, body, key, now, n);
    }

    /// <summary>As <see cref="SignRequest"/>, with the nonce's 16 bytes given.</summary>
    public static SignedRequest SignRequestWithNonce(string method, string host, string target, ReadOnlySpan<byte> body,
        MsfpKey key, DateTimeOffset now, ReadOnlySpan<byte> nonceBytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        var nonce = Base64UrlText.Encode(nonceBytes);
        var date = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var msg = RequestMessage(method, host, target, date, nonce, body);
        return new SignedRequest(Base64UrlText.Encode(key.PublicSpan), date, nonce, Base64UrlText.Encode(key.Sign(msg)));
    }

    /// <summary>
    /// The signature to put in an answer's MSFP-Signature, for the request
    /// whose nonce it was: contentType is the answer's Content-Type (null
    /// or "" for none).
    /// </summary>
    public static string SignResponse(MsfpKey key, string nonce, int status, string? contentType, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Base64UrlText.Encode(key.Sign(ResponseMessage(nonce, status, contentType, body)));
    }

    /// <summary>
    /// Checks an answer's signature against the key the requester knows the
    /// other server by: contentType is the Content-Type the answer came
    /// with (null for none).
    /// </summary>
    public static bool CheckResponse(ReadOnlySpan<byte> peer, string nonce, int status, string? contentType,
        ReadOnlySpan<byte> body, string? signature)
    {
        var sig = Base64UrlText.Decode(signature, Ed25519Strict.SignatureSize);
        return sig is not null && Ed25519Strict.Verify(peer, ResponseMessage(nonce, status, contentType, body), sig);
    }

    /// <summary>
    /// Proves, without sending it, that the server redeeming a pairing code
    /// knows the code's secret (core section 9.1): HMAC-SHA-256 bound to
    /// the redeeming server's own key and address.
    /// </summary>
    public static string PairingProof(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> redeemer, string address)
    {
        var data = new List<byte>();
        data.AddRange(Encoding.UTF8.GetBytes(PairingLabel));
        data.AddRange(redeemer.ToArray());
        data.AddRange(Encoding.UTF8.GetBytes("\n" + address));
        return Base64UrlText.Encode(HMACSHA256.HashData(secret, data.ToArray()));
    }

    /// <summary>Checks a <see cref="PairingProof"/> in constant time.</summary>
    public static bool CheckPairingProof(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> redeemer, string address, string? proof)
    {
        var got = Base64UrlText.Decode(proof, 32);
        if (got is null)
        {
            return false;
        }

        var want = Base64UrlText.Decode(PairingProof(secret, redeemer, address))!;
        return CryptographicOperations.FixedTimeEquals(got, want);
    }

    /// <summary>A pairing code's secret as the redeeming server names it (pair's secretHash): SHA-256 of the secret.</summary>
    public static byte[] SecretHash(ReadOnlySpan<byte> secret) => SHA256.HashData(secret);

    /// <summary>Whether the headers name no content coding other than identity (core section 3).</summary>
    public static bool Uncoded(IHeaderReader headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var v in headers.GetValues("Content-Encoding"))
        {
            foreach (var c in v.Split(','))
            {
                if (!c.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal static IEnumerable<string> FirstVersionHeaders => OldHeaders;
}

/// <summary>The four headers of a signed request, and its nonce.</summary>
/// <param name="Key">MSFP-Key.</param>
/// <param name="Date">MSFP-Date.</param>
/// <param name="Nonce">MSFP-Nonce: what the answer's signature covers.</param>
/// <param name="Signature">MSFP-Signature.</param>
public sealed record SignedRequest(string Key, string Date, string Nonce, string Signature)
{
    /// <summary>Calls set once per header, to put them on a request.</summary>
    public void Apply(Action<string, string> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        set(Signing.HeaderKey, Key);
        set(Signing.HeaderDate, Date);
        set(Signing.HeaderNonce, Nonce);
        set(Signing.HeaderSignature, Signature);
    }

    /// <summary>Sets the four headers on a list.</summary>
    public void Apply(HeaderList headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Apply((n, v) => headers.Set(n, v));
    }
}

/// <summary>What a valid request proves.</summary>
/// <param name="Key">The sender's public key, checked: whose it is is the caller's to look up.</param>
/// <param name="Nonce">The request's nonce, for the answer's signature.</param>
/// <param name="Known">A server this one knows (paired or not yet), not a stranger heard while a code is live.</param>
/// <param name="Former">Signed for this server's former host: the sender hasn't moved to its new address yet.</param>
public sealed record Verified(byte[] Key, string Nonce, bool Known, bool Former);

/// <summary>What <see cref="Verifier.Admit"/> says of a key.</summary>
/// <param name="Heard">A known server's key, or anyone's while a pairing code is live.</param>
/// <param name="Known">A known server's.</param>
public readonly record struct Admission(bool Heard, bool Known);

/// <summary>Checks the requests a server receives (core section 6.4).</summary>
public sealed class Verifier
{
    /// <summary>
    /// This server's own host, as its address names it: a request signed
    /// for another server is refused, even if sent here.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// The host of this server's address before it moved, accepted too while
    /// the move lasts (core section 13.1), or null/"" once it no longer is.
    /// </summary>
    public Func<string?>? Former { get; init; }

    /// <summary>
    /// The nonces already accepted. Shared by the verifiers a server makes
    /// over its life, so a request can't be replayed across one.
    /// </summary>
    public NonceMemory Seen { get; init; } = new();

    /// <summary>
    /// Whether a key may be heard at all, and whether it's a known
    /// server's: asked once the signature is good, before the nonce is
    /// remembered, so a stranger signing with keys of their own fills no
    /// memory. Null: every key is heard, as a known one.
    /// </summary>
    public Func<byte[], Admission>? Admit { get; init; }

    /// <summary>
    /// Whether a key <see cref="Admit"/> doesn't hear is a server's whose
    /// pairing ended: its request, signed and new, gets
    /// <see cref="ProtocolError.Forgotten"/> (and a signed "unknown").
    /// </summary>
    public Func<byte[], bool>? Forgotten { get; init; }

    /// <summary>
    /// <see cref="Admit"/> for a host whose lookups are asynchronous (a
    /// database): used by <see cref="CheckAsync"/>, before <see cref="Admit"/>.
    /// </summary>
    public Func<byte[], CancellationToken, ValueTask<Admission>>? AdmitAsync { get; init; }

    /// <summary><see cref="Forgotten"/>, asynchronous: used by <see cref="CheckAsync"/>, before <see cref="Forgotten"/>.</summary>
    public Func<byte[], CancellationToken, ValueTask<bool>>? ForgottenAsync { get; init; }

    /// <summary>
    /// Verifies a request: method, target (its path and query as sent),
    /// headers and body, at now. It checks only that the request was
    /// signed by the key it names, for this server, recently, and never
    /// seen before.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Signature"/> for any refusal.</exception>
    /// <exception cref="ForgottenException">A valid, new request from a server whose pairing ended.</exception>
    public Verified Check(string method, string target, IHeaderReader headers, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        var (key, nonce, former) = CheckSignature(method, target, headers, body, now);
        Admission? a = Admit is null ? null : Admit(key);
        return Remember(key, nonce, former, now, a, () => Forgotten is not null && Forgotten(key));
    }

    /// <summary>
    /// <see cref="Check"/>, with <see cref="AdmitAsync"/> and
    /// <see cref="ForgottenAsync"/> asked when they are set (the synchronous
    /// ones otherwise).
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.Signature"/> for any refusal.</exception>
    /// <exception cref="ForgottenException">A valid, new request from a server whose pairing ended.</exception>
    public async ValueTask<Verified> CheckAsync(string method, string target, IHeaderReader headers, ReadOnlyMemory<byte> body,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var (key, nonce, former) = CheckSignature(method, target, headers, body.Span, now);
        Admission? a = AdmitAsync is not null ? await AdmitAsync(key, ct).ConfigureAwait(false) : Admit?.Invoke(key);
        var forgotten = false;
        if (a is { Heard: false })
        {
            forgotten = ForgottenAsync is not null ? await ForgottenAsync(key, ct).ConfigureAwait(false) : Forgotten?.Invoke(key) == true;
        }

        return Remember(key, nonce, former, now, a, () => forgotten);
    }

    // The checks of core section 6.4 up to the signature: the key, the
    // nonce and whether it was signed for our former host.
    private (byte[] Key, string Nonce, bool Former) CheckSignature(string method, string target, IHeaderReader headers,
        ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(headers);
        var bad = new ProtocolException(ProtocolError.Signature);
        // No query (core section 6.4, step 1).
        if (target.Contains('?', StringComparison.Ordinal))
        {
            throw bad;
        }

        // No content coding (section 3).
        if (!Signing.Uncoded(headers))
        {
            throw bad;
        }

        // Never beside the first test version's headers (section 6.1).
        foreach (var h in Signing.FirstVersionHeaders)
        {
            if (headers.GetValues(h).Count > 0)
            {
                throw bad;
            }
        }

        // Each header once, each value in its one form (section 2.4).
        var keyText = Signing.OneHeader(headers, Signing.HeaderKey);
        var date = Signing.OneHeader(headers, Signing.HeaderDate);
        var nonce = Signing.OneHeader(headers, Signing.HeaderNonce);
        var sigText = Signing.OneHeader(headers, Signing.HeaderSignature);
        if (keyText is null || date is null || nonce is null || sigText is null)
        {
            throw bad;
        }

        var key = Base64UrlText.Decode(keyText, Ed25519Strict.KeySize);
        if (key is null || !Ed25519Strict.ValidKey(key) || !Signing.TryParseDate(date, out var when))
        {
            throw bad;
        }

        if (!UnixTime.Within(now, when, Signing.MaxSkew) || Seen.Before(when) ||
            Base64UrlText.Decode(nonce, 16) is null)
        {
            throw bad;
        }

        var sig = Base64UrlText.Decode(sigText, Ed25519Strict.SignatureSize) ?? throw bad;
        // Signed for this server's host, or, while it moves, its former one.
        var former = false;
        if (!Ed25519Strict.Verify(key, Signing.RequestMessage(method, Host, target, date, nonce, body), sig))
        {
            var old = Former?.Invoke();
            if (string.IsNullOrEmpty(old) ||
                !Ed25519Strict.Verify(key, Signing.RequestMessage(method, old, target, date, nonce, body), sig))
            {
                throw bad;
            }

            former = true;
        }

        return (key, nonce, former);
    }

    // Only once the signature is good, and the key one this server hears,
    // is the nonce remembered: nobody can fill the memory with nonces, or
    // keys, they made up.
    private Verified Remember(byte[] key, string nonce, bool former, DateTimeOffset now, Admission? admission, Func<bool> forgotten)
    {
        var bad = new ProtocolException(ProtocolError.Signature);
        var known = true;
        if (admission is { } a)
        {
            known = a.Known;
            if (!a.Heard)
            {
                if (!forgotten())
                {
                    throw bad;
                }

                // Remembered as a stranger's: a replay gets the plain 404.
                if (!Seen.Add(key, nonce, now, known: false))
                {
                    throw bad;
                }

                throw new ForgottenException(new Verified(key, nonce, false, former));
            }
        }

        if (!Seen.Add(key, nonce, now, known))
        {
            throw bad;
        }

        return new Verified(key, nonce, known, former);
    }
}

/// <summary>A valid request from a server whose pairing ended: <see cref="Verified"/> says who, for the answer to be signed.</summary>
public sealed class ForgottenException : Exception
{
    /// <summary>The refusal, with what the request proved.</summary>
    public ForgottenException(Verified verified)
        : base("federation: a server this one no longer knows") => Verified = verified;

    /// <summary>What the request proved.</summary>
    public Verified Verified { get; }

    /// <summary>Always <see cref="ProtocolError.Forgotten"/>.</summary>
    public static ProtocolError Error => ProtocolError.Forgotten;
}
