using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>A request being handled: what its check proved.</summary>
internal sealed class CallRequest(MsfpRequest http, byte[] body)
{
    public MsfpRequest Http { get; } = http;

    public byte[] Body { get; } = body;

    public Verified? Verified { get; set; }

    /// <summary>The peer whose key signed it (null for a stranger heard on pair while a code is live).</summary>
    public PeerRecord? Peer { get; set; }

    /// <summary>The key the answer is signed with; null: nothing signed.</summary>
    public MsfpKey? Signer { get; set; }

    /// <summary>The host of ours it was signed for.</summary>
    public string Ours { get; set; } = "";
}

public sealed partial class MsfpEngine
{
    /// <summary>The longest request body (core section 3).</summary>
    internal const int MaxBody = 64 << 10;

    internal const string CallPair = "pair";
    internal const string CallHello = "hello";
    internal const string CallRotate = "rotate";
    internal const string CallRevoke = "revoke";

    // The calls of version 1, by their name under the base path.
    private static readonly HashSet<string> Calls =
        new(StringComparer.Ordinal) { CallPair, CallHello, CallRotate, CallRevoke, CallLibraries, CallTitles, CallPlay, CallProgress, CallStop };

    /// <summary>
    /// The federation routes peers call, under
    /// <c>&lt;Prefix&gt;/api/v1/federation/</c> (core sections 6 and 8):
    /// give every request there to this method, on the raw target (never
    /// with a prefix stripped: the signature covers the path as sent), with
    /// no authentication in front, and write the answer as it is, with no
    /// compression. An answer with <see cref="MsfpResponse.PlainNotFound"/>
    /// may be replaced by the host's own not-found page.
    /// </summary>
    /// <remarks>
    /// Each request is checked in the same order: the limits by network
    /// address, the body's size and media type, the signature and nonce,
    /// who the key is, the limits per peer and in all; then the call.
    /// Refusals to strangers are the plain 404; to a server this one knows,
    /// or knew, they are signed.
    /// </remarks>
    public async Task<MsfpResponse> HandleFederationAsync(MsfpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.Target.Split('?', 2)[0];
        var basePath = _cfg.Prefix + Discovery.DefaultBase;
        if (!path.StartsWith(basePath, StringComparison.Ordinal) || request.Method != "POST" ||
            !Calls.Contains(path[basePath.Length..]))
        {
            return MsfpResponse.Plain404;
        }

        var call = path[basePath.Length..];
        CallRequest? req = null;
        Answer res;
        try
        {
            (req, res, var free) = await CheckAsync(request, call, ct).ConfigureAwait(false);
            if (free is not null)
            {
                try
                {
                    res = await DispatchAsync(req!, call, ct).ConfigureAwait(false);
                }
                finally
                {
                    free();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogError(e, "msfp: a call failed: {Call}", call);
            res = req?.Signer is null ? Answer.PlainRefusal : Answer.Refuse(ProblemType.Internal);
        }

        if (res.Plain)
        {
            return MsfpResponse.Plain404;
        }

        // Every 200 and 202 is signed; a refusal only to a key this server
        // knows, or knew (core section 6.6).
        var signer = req?.Signer;
        if (signer is null && res.Status is 200 or 202)
        {
            signer = Current();
        }

        var headers = new List<KeyValuePair<string, string>>();
        if (res.ContentType is not null)
        {
            headers.Add(new("Content-Type", res.ContentType));
        }

        if (res.RetryAfter > 0)
        {
            headers.Add(new("Retry-After", res.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (signer is not null && req?.Verified is { } v)
        {
            headers.Add(new(Signing.HeaderSignature, Signing.SignResponse(signer, v.Nonce, res.Status, res.ContentType, res.Body)));
        }

        return new MsfpResponse { Status = res.Status, Headers = headers, Body = res.Body };
    }

    private async Task<Answer> DispatchAsync(CallRequest req, string call, CancellationToken ct) => call switch
    {
        CallPair => await PairAsync(req, ct).ConfigureAwait(false),
        CallHello => await HelloAsync(req, ct).ConfigureAwait(false),
        CallRotate => await RotateAsync(req, ct).ConfigureAwait(false),
        CallRevoke => await RevokeAsync(req, ct).ConfigureAwait(false),
        CallLibraries => await LibrariesAsync(req, ct).ConfigureAwait(false),
        CallTitles => await TitlesAsync(req, ct).ConfigureAwait(false),
        CallPlay => await PlayAsync(req, ct).ConfigureAwait(false),
        CallProgress => await ProgressAsync(req, ct).ConfigureAwait(false),
        CallStop => await StopAsync(req, ct).ConfigureAwait(false),
        _ => Answer.Refuse(ProblemType.NotSupported),
    };

    // Reads and checks a request within the limits on calls here (core
    // sections 6.4 and 8.3): free is null when res is the answer, and
    // otherwise frees the request's slots once its call is handled.
    private async Task<(CallRequest? Req, Answer Res, Action? Free)> CheckAsync(MsfpRequest r, string call, CancellationToken ct)
    {
        if (!_limits.FromAddress(r.ClientAddress, Now()))
        {
            return (null, Answer.PlainRefusal, null);
        }

        var body = await ReadAtMostAsync(r.Body, MaxBody, ct).ConfigureAwait(false);
        if (body is null)
        {
            return (null, Answer.PlainRefusal, null);
        }

        // Content-Type application/json, and no browser's cross-site marker
        // (core section 3).
        var cts = r.Headers.GetValues("Content-Type");
        var site = r.Headers.GetValues("Sec-Fetch-Site");
        if (cts.Count != 1 || Signing.MediaType(cts[0]) != "application/json" ||
            (site.Count > 0 && site[0] is not ("" or "same-origin" or "none")))
        {
            return (null, Answer.PlainRefusal, null);
        }

        var req = new CallRequest(r, body);
        Verified v;
        try
        {
            v = await Verifier().CheckAsync(r.Method, r.Target, r.Headers, body, Now(), ct).ConfigureAwait(false);
        }
        catch (ForgottenException f)
        {
            req.Verified = f.Verified;
            // A server whose pairing ended when our key was stolen: its hello
            // gets the revocation, for 30 days (core section 12.7).
            var (revocation, replaced) = await RevocationAnswerAsync(req, call, ct).ConfigureAwait(false);
            if (revocation is not null)
            {
                return (req, revocation, null);
            }

            // One whose pairing ended in a replacement, after those 30 days:
            // it can't know our new key, so "unknown" unsigned (section 12.7).
            if (replaced)
            {
                return (null, Answer.PlainRefusal, null);
            }

            // A server whose pairing ended: "unknown", signed (core section 7.4).
            req.Signer = Current();
            return (req, Answer.Refuse(ProblemType.NotFound), null);
        }
        catch (ProtocolException)
        {
            return (null, Answer.PlainRefusal, null);
        }

        req.Verified = v;
        req.Ours = v.Former ? FormerHost() ?? OurHost() : OurHost();
        var key = "";
        if (v.Known)
        {
            var p = await PeerHeardAsync(v.Key, ct).ConfigureAwait(false);
            if (p is null)
            {
                return (null, Answer.PlainRefusal, null);
            }

            req.Peer = p;
            req.Signer = SignerFor(p);
            // Which of our addresses it calls: one still on our former is told
            // of the new one (core section 13.1).
            await HeardAtAsync(p, v.Former, ct).ConfigureAwait(false);
            // A revoked key is heard for rotate and revoke only (core section 12.6).
            if (p.Suspended is not null && call is not (CallRotate or CallRevoke))
            {
                return (req, Answer.Refuse(ProblemType.NotFound), null);
            }

            // A peer still on our old key: told of the new one again, at each
            // contact (core section 12.4), in the background.
            if (p.OurKey == KeyOld && p.Inbound == Inbound.Yes && p.Suspended is null)
            {
                RotateSoon(p);
            }

            // No core in common: every call but hello is not-supported
            // (sections 10.3, 10.4); pair from a known key gets its own 409.
            if (Agreed(p) is null && call is not (CallHello or CallPair))
            {
                return (req, Answer.Refuse(ProblemType.NotSupported), null);
            }

            key = Base64UrlText.Encode(v.Key);
            if (_limits.Call(key, call == "play", Now()) is { } no)
            {
                return (req, Answer.Refuse(no.Problem, no.RetryAfter), null);
            }
        }

        var free = await _limits.SlotsAsync(key, ct).ConfigureAwait(false);
        if (free is null)
        {
            return v.Known
                ? (req, Answer.Refuse(ProblemType.ServerBusy, 1), null)
                : (null, Answer.PlainRefusal, null);
        }

        return (req, Answer.PlainRefusal, free);
    }

    private static async Task<byte[]?> ReadAtMostAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 << 10];
        while (true)
        {
            var n = await body.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + n > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, n);
        }
    }

    private Verifier Verifier()
    {
        lock (_keysLock)
        {
            return _verifier;
        }
    }

    // A verifier for this server's host now; the nonce memory is the
    // engine's, shared by every verifier it makes.
    private void NewVerifier()
    {
        var v = new Verifier
        {
            Host = OurHost(),
            Seen = _nonces,
            AdmitAsync = AdmitAsync,
            ForgottenAsync = IsForgottenAsync,
            Former = FormerHost,
        };
        lock (_keysLock)
        {
            _verifier = v;
        }
    }

    // Whether a key may be heard (core section 6.5): a peer's current key,
    // in any state; its previous one while still heard; anyone while a
    // code of ours is live (on pair: the check of the call is pair's).
    private async ValueTask<Admission> AdmitAsync(byte[] key, CancellationToken ct)
    {
        if (await PeerHeardAsync(key, ct).ConfigureAwait(false) is not null)
        {
            return new Admission(true, true);
        }

        var live = false;
        var now = Now();
        await _store.ReadAsync(async tx => live = (await tx.CodesAsync().ConfigureAwait(false)).Any(c => c.Expires > now), ct)
            .ConfigureAwait(false);
        return new Admission(live, false);
    }

    // Whether a key is a server's whose pairing with this one ended, or
    // this server's own current key (heard only from itself).
    private async ValueTask<bool> IsForgottenAsync(byte[] key, CancellationToken ct)
    {
        if (Keys.Same(key, Current().PublicSpan))
        {
            return true;
        }

        var hash = SHA256.HashData(key);
        var found = false;
        await _store.ReadAsync(async tx => found = await tx.ForgottenAsync(hash).ConfigureAwait(false) is not null, ct)
            .ConfigureAwait(false);
        return found;
    }

    // The peer whose key this is: its current key, or its previous one
    // while still heard (core section 12.4); null for none.
    internal async ValueTask<PeerRecord?> PeerHeardAsync(byte[] key, CancellationToken ct)
    {
        PeerRecord? found = null;
        var now = Now();
        await _store.ReadAsync(async tx =>
        {
            found = await tx.PeerByKeyAsync(key).ConfigureAwait(false);
            if (found is not null)
            {
                return;
            }

            foreach (var q in await tx.PeersAsync().ConfigureAwait(false))
            {
                if (q.PreviousKey is not null && Keys.Same(q.PreviousKey, key) && q.PreviousUntil is { } until && now < until)
                {
                    found = q;
                    return;
                }
            }
        }, ct).ConfigureAwait(false);
        return found;
    }
}
