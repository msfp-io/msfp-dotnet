using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp;

/// <summary>A peer's signed answer: its status; for a refusal, the problem type it gives and its retryAfter; for a 200, its body.</summary>
internal sealed record Reply(int Status, string? Problem, int RetryAfter, byte[] Body, bool Unsigned = false);

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
    // an error. Background marks the calls this server makes by itself:
    // after calls the peer didn't answer, they hold off (core section 8.3).
    // Errors: Uncallable, Suspended, KeyLost, NotSupported, BackingOff,
    // RefusedDestination, CertificateRefused, Unreachable.
    internal async Task<Reply> CallAsync(PeerRecord p, string call, byte[] body, CancellationToken ct, bool background = false)
    {
        if (call != CallPair && p.Inbound.Value.Length > 0 && p.Inbound != Inbound.Yes)
        {
            throw new MsfpException(MsfpError.Uncallable); // no connection at all (core section 7.5)
        }

        if (p.Suspended is not null && !(call == CallHello && background && SaysUncallable()))
        {
            // Nothing is asked of a revoked key (core section 12.6), but the
            // scheduled hello of a server that can't be called, so that a
            // rotation can still reach it.
            throw new MsfpException(MsfpError.Suspended);
        }

        if (p.OurKey == KeyLost && call != CallPair)
        {
            // It never moved to our new key, and knows no key of ours now
            // (core section 12.4).
            throw new MsfpException(MsfpError.KeyLost);
        }

        if (Agreed(p) is null && call is not (CallHello or CallPair))
        {
            throw new MsfpException(MsfpError.NotSupported);
        }

        var key = Base64UrlText.Encode(p.Key);
        if (call != CallPair && !_limits.MayCall(key, background, Now()))
        {
            throw new MsfpException(MsfpError.BackingOff);
        }

        if (p.OurKey == KeyOld && call is not (CallRotate or CallPair))
        {
            // Still on our old key: told of the new one first, at each
            // contact (core section 12.4).
            p = await RotateAtContactAsync(p, ct, background).ConfigureAwait(false);
        }

        if (call is not (CallHello or CallPair) && NotMoved(p, OurHost()))
        {
            // Still on our former address: told of the new one, at each
            // contact (core section 13.1, step 2).
            AnnounceSoon(p);
        }

        Reply? r = null;
        MsfpException? error = null;
        try
        {
            r = await SignedExchangeAsync(p, call, body, SignerFor(p), ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (call != CallPair && e.NotHeard == 404)
        {
            // Maybe it just restarted, and refuses what is dated before: once
            // more a second later, signed again with a new nonce (core section
            // 6.5, "Retrying").
            await Task.Delay(RestartRetry, ct).ConfigureAwait(false);
            try
            {
                r = await SignedExchangeAsync(p, call, body, SignerFor(p), ct).ConfigureAwait(false);
            }
            catch (MsfpException again)
            {
                error = again;
            }
        }
        catch (MsfpException e)
        {
            error = e;
        }

        if (call != CallPair)
        {
            CalledPeer(p, error);
        }

        if (error is { NotHeard: > 0 } && p.OurKey == KeyOld)
        {
            // Our old key no longer heard there: it may have moved without our
            // seeing its answer. A hello under the new key tells; then the
            // call again, under it. A rotate isn't sent again: under the new
            // key its statement wouldn't verify, and moved is what it was for.
            var (q, moved) = await HelloUnderNewAsync(p, ct).ConfigureAwait(false);
            if (moved && call != CallRotate)
            {
                p = q;
                try
                {
                    r = await SignedExchangeAsync(p, call, body, Current(), ct).ConfigureAwait(false);
                    error = null;
                }
                catch (MsfpException again)
                {
                    error = again;
                }
            }
        }

        if (call != CallPair && !ct.IsCancellationRequested)
        {
            _limits.Heard(key, error is null, r?.Status ?? 0, r?.RetryAfter ?? 0, Now());
        }

        return error is null ? r! : throw error;
    }

    // ExchangeAsync, an unsigned refusal (but pair's) thrown as Unreachable
    // with its status in NotHeard: it doesn't count.
    private async Task<Reply> SignedExchangeAsync(PeerRecord p, string call, byte[] body, MsfpKey key, CancellationToken ct)
    {
        var r = await ExchangeAsync(p, call, body, key, ct).ConfigureAwait(false);
        return r.Unsigned
            ? throw new MsfpException(MsfpError.Unreachable, $"an unsigned refusal: {r.Status}") { NotHeard = r.Status }
            : r;
    }

    // P not hearing our old key (an unsigned refusal): a hello signed with
    // the current key, whose signed answer, whatever it says, means p moved
    // (core section 12.4).
    private async Task<(PeerRecord Peer, bool Moved)> HelloUnderNewAsync(PeerRecord p, CancellationToken ct)
    {
        var current = Current();
        try
        {
            var hello = await OurHelloAsync(ViewOf(p), p, answering: false, ct).ConfigureAwait(false);
            await SignedExchangeAsync(p, CallHello, hello, current, ct).ConfigureAwait(false);
        }
        catch (MsfpException)
        {
            return (p, false);
        }

        if (!await PeerMovedAsync(p.Id, current.Public(), ct).ConfigureAwait(false))
        {
            return (p, false);
        }

        try
        {
            return (await PeerRecordAsync(p.Id, ct).ConfigureAwait(false), true);
        }
        catch (MsfpException)
        {
            return (p, false);
        }
    }

    // How long a call refused with an unsigned 404 waits before its one retry.
    internal static readonly TimeSpan RestartRetry = TimeSpan.FromSeconds(1);

    // One call, signed with key. An unsigned refusal (but pair's) comes back
    // marked Unsigned: it doesn't count.
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
            // It couldn't connect, as a hello answer's callsFailed says (core
            // section 7.5).
            throw new MsfpException(MsfpError.Unreachable, "couldn't connect", e) { NoConnection = true };
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new MsfpException(MsfpError.Unreachable, "timed out", e) { NoConnection = true };
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
                    return new Reply(status, null, 0, answer, Unsigned: true);
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

    // A peer's base path, from its discovery document at address (core
    // section 11); null for the default: for a 404, an answer that isn't a
    // valid document, or none at all. A plain GET with no signature, any
    // certificate accepted, no redirects followed, 16 KiB read at most;
    // nothing is fetched at an address of the .invalid form.
    internal async Task<string?> FetchBaseAsync(string address, CancellationToken ct) =>
        (await ReadBaseAsync(address, ct).ConfigureAwait(false)).Base;

    // FetchBaseAsync, and whether address answered at all.
    private async Task<(string? Base, bool Answered)> ReadBaseAsync(string address, CancellationToken ct)
    {
        if (Uncallables.Valid(address) || !Address.Valid(address))
        {
            return (null, false);
        }

        HttpResponseMessage res;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        try
        {
            res = await ClientFor(null, true).GetAsync(new Uri(address + Discovery.Path), HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or MsfpException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, false);
        }

        using (res)
        {
            try
            {
                var headers = new ResponseHeaders(res);
                if ((int)res.StatusCode != 200 || headers.GetValues("Content-Encoding").Any(c => !c.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
                    Signing.MediaType(headers.GetValues("Content-Type").FirstOrDefault()) != "application/json")
                {
                    return (null, true);
                }

                var body = await ReadAtMostAsync(await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), Discovery.MaxDiscovery, cts.Token)
                    .ConfigureAwait(false);
                if (body is null)
                {
                    return (null, true);
                }

                var d = Discovery.Read(body);
                return (d.Base == Discovery.DefaultBase ? null : d.Base, true);
            }
            catch (Exception e) when (e is HttpRequestException or ProtocolException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (null, true);
            }
        }
    }

    // Fetches peer id's discovery document at address and keeps the base it
    // says, if address is still the peer's then. No answer at all keeps the
    // base stored: a moment's outage doesn't undo it; an answered 404 (or
    // any answer that isn't a document) sets it back to the default.
    private async Task KeepBaseAsync(string id, string address, CancellationToken ct)
    {
        var (b, answered) = await ReadBaseAsync(address, ct).ConfigureAwait(false);
        if (!answered)
        {
            return;
        }

        try
        {
            await UpdatePeerAsync(id, p =>
            {
                if (p.Address == address && p.Inbound != Inbound.No)
                {
                    p.Base = b;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
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
