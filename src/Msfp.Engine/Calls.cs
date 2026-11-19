using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp;

/// <summary>A peer's signed answer: its status; for a refusal, the problem type it gives and its retryAfter; for a 200, its body.</summary>
internal sealed record Reply(int Status, string? Problem, int RetryAfter, byte[] Body);

/// <summary>Header lists of HttpClient's answers, read as MSFP reads headers.</summary>
internal sealed class ResponseHeaders(HttpResponseMessage res) : IHeaderReader
{
    public IReadOnlyList<string> GetValues(string name)
    {
        if (res.Headers.TryGetValues(name, out var v) || res.Content.Headers.TryGetValues(name, out v))
        {
            return v.ToList();
        }

        return [];
    }
}

public sealed partial class MsfpEngine
{
    /// <summary>The most read of an answer (core section 3).</summary>
    internal const int MaxAnswer = 4 << 20;

    // Sends p a signed call with this body, and gives its answer: a
    // refusal that counts (signed, or any refusal of pair) is a reply, not
    // an error. Errors: Uncallable, Suspended, KeyLost, NotSupported,
    // RefusedDestination, CertificateRefused, Unreachable.
    internal async Task<Reply> CallAsync(PeerRecord p, string call, byte[] body, CancellationToken ct)
    {
        if (call != CallPair && p.Inbound != Inbound.Yes && p.Inbound.Value.Length > 0)
        {
            throw new MsfpException(MsfpError.Uncallable); // no connection at all (core section 7.5)
        }

        if (p.Suspended is not null)
        {
            throw new MsfpException(MsfpError.Suspended); // nothing is asked of a revoked key (section 12.6)
        }

        if (p.OurKey == KeyLost && call != CallPair)
        {
            throw new MsfpException(MsfpError.KeyLost);
        }

        if (Agreed(p) is null && call is not (CallHello or CallPair))
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        return await ExchangeAsync(p, call, body, SignerFor(p), ct).ConfigureAwait(false);
    }

    // One call, signed with key.
    private async Task<Reply> ExchangeAsync(PeerRecord p, string call, byte[] body, MsfpKey key, CancellationToken ct)
    {
        if (Uncallables.Valid(p.Address))
        {
            throw new MsfpException(MsfpError.Uncallable); // an address that never resolves (core section 5.2)
        }

        var basePath = string.IsNullOrEmpty(p.Base) ? Discovery.DefaultBase : p.Base;
        var target = basePath + call;
        var uri = new Uri(p.Address + target);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        req.Version = System.Net.HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var signed = Signing.SignRequest("POST", Address.Host(p.Address), target, body, key, Now(), _random);
        signed.Apply((n, v) => req.Headers.TryAddWithoutValidation(n, v));
        var anyCert = call is CallPair or CallHello;
        HttpResponseMessage res;
        try
        {
            res = await ClientFor(p.Pins, anyCert).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (Find<MsfpException>(e) is { } m)
        {
            throw new MsfpException(m.Code, p.Address, m);
        }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new MsfpException(MsfpError.CertificateRefused, p.Address, e);
        }
        catch (HttpRequestException e)
        {
            throw new MsfpException(MsfpError.Unreachable, "couldn't connect", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new MsfpException(MsfpError.Unreachable, "timed out", e);
        }

        using (res)
        {
            var headers = new ResponseHeaders(res);
            // No content coding (core section 3): refused like a badly signed answer.
            if (headers.GetValues("Content-Encoding") is { Count: > 0 } ce &&
                !ce.All(c => c.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                throw new MsfpException(MsfpError.Unreachable, "a coded answer");
            }

            var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var answer = await ReadAtMostAsync(stream, MaxAnswer, ct).ConfigureAwait(false)
                         ?? throw new MsfpException(MsfpError.Unreachable, "an answer over 4 MiB");
            var status = (int)res.StatusCode;
            string? contentType = null;
            var ctOk = true;
            try
            {
                contentType = Signing.AnswerMediaType(headers);
            }
            catch (ProtocolException)
            {
                ctOk = false;
            }

            var isSigned = ctOk && Signing.CheckResponse(p.Key, signed.Nonce, status, contentType, answer,
                Signing.OneHeader(headers, Signing.HeaderSignature));
            if (status is not (200 or 202))
            {
                // A refusal counts only signed by the peer's key; but pair's,
                // whose maker doesn't know us yet (core section 6.6).
                if (!isSigned && call != CallPair)
                {
                    throw new MsfpException(MsfpError.Unreachable, $"an unsigned refusal: {status}");
                }

                string? problem = null;
                var retryAfter = 0;
                if (isSigned)
                {
                    try
                    {
                        problem = StrictJson.ReadAnswer(answer).GetString("type") is { Length: <= 64 } t ? t : null;
                    }
                    catch (ProtocolException)
                    {
                    }

                    if (status is 429 or 503)
                    {
                        retryAfter = RetryAfter.Read(answer);
                    }
                }

                return new Reply(status, problem, retryAfter, answer);
            }

            if (!isSigned)
            {
                throw new MsfpException(MsfpError.Unreachable, "an answer not signed by its key");
            }

            if (contentType != "application/json")
            {
                throw new MsfpException(MsfpError.Unreachable, "an answer that isn't JSON");
            }

            return new Reply(status, null, 0, answer);
        }
    }

    // A peer's base path, from its discovery document (core section 11);
    // null for the default, or when it doesn't answer.
    internal async Task<string?> FetchBaseAsync(string address, CancellationToken ct)
    {
        if (Uncallables.Valid(address) || !Address.Valid(address))
        {
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);
            using var res = await ClientFor(null, true).GetAsync(new Uri(address + Discovery.Path), HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var headers = new ResponseHeaders(res);
            if ((int)res.StatusCode != 200 || headers.GetValues("Content-Encoding").Any(c => !c.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
                Signing.MediaType(headers.GetValues("Content-Type").FirstOrDefault()) != "application/json")
            {
                return null;
            }

            var body = await ReadAtMostAsync(await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), Discovery.MaxDiscovery, cts.Token)
                .ConfigureAwait(false);
            if (body is null)
            {
                return null;
            }

            var d = Discovery.Read(body);
            return d.Base == Discovery.DefaultBase ? null : d.Base;
        }
        catch (Exception e) when (e is HttpRequestException or ProtocolException or MsfpException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static T? Find<T>(Exception e)
        where T : Exception
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is T t)
            {
                return t;
            }
        }

        return null;
    }

    internal static JsonObject ReadAnswerBody(byte[] body)
    {
        try
        {
            return StrictJson.ReadAnswer(body);
        }
        catch (ProtocolException e)
        {
            throw new MsfpException(MsfpError.Unreachable, e.Message, e);
        }
    }
}
