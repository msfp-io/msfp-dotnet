using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;

namespace Msfp;

/// <summary>A live code, as the panel lists it: never its text, which is shown once, when made.</summary>
/// <param name="Id">The code's ID.</param>
/// <param name="Note">The operator's note.</param>
/// <param name="Created">When it was made.</param>
/// <param name="Expires">When it stops working.</param>
public sealed record CodeView(string Id, string Note, DateTimeOffset Created, DateTimeOffset Expires);

/// <summary>What a code says, for the operator to see before redeeming it.</summary>
/// <param name="Address">The address of the server that made it.</param>
/// <param name="Fingerprint">Its key's fingerprint, to compare with the other operator.</param>
/// <param name="Expires">When it stops working.</param>
public sealed record CodePreview(string Address, string Fingerprint, DateTimeOffset Expires);

public sealed partial class MsfpEngine
{
    private static readonly string[] PairMembers = ["address", "name", "secretHash", "proof", "nextCommitment", "inbound"];

    /// <summary>
    /// Makes a pairing code (core sections 5 and 7.1), noted as note for the
    /// panel, and gives its text, to show once. It works once, for 24 hours.
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NoCodes"/>: this server can't be called (the other operator makes one).</exception>
    public async Task<(string Text, CodeView Code)> MakeCodeAsync(string? note, CancellationToken ct = default)
    {
        var rec = await ServerRecordAsync(ct).ConfigureAwait(false);
        if (rec.Uncallable)
        {
            throw new MsfpException(MsfpError.NoCodes);
        }

        var secret = RandomBytes(PairingCode.SecretSize);
        var id = NewId("c");
        var now = Now();
        var code = new PairingCode(Named(), Current().Public(), secret,
            DateTimeOffset.FromUnixTimeSeconds((now + PairingCode.Lifetime).ToUnixTimeSeconds()));
        var c = new CodeRecord
        {
            Id = id,
            Secret = await _sealer.SealAsync(secret, SealContext.Code, ct).ConfigureAwait(false),
            SecretHash = Signing.SecretHash(secret),
            Note = CleanName(note, ""),
            Created = NowSecond(),
            Expires = code.Expires,
        };
        await _store.UpdateAsync(tx => tx.PutCodeAsync(c), ct).ConfigureAwait(false);
        return (code.ToString(), ToView(c));
    }

    private static CodeView ToView(CodeRecord c) => new(c.Id, c.Note ?? "", c.Created, c.Expires);

    /// <summary>The live codes, soonest to expire first.</summary>
    public async Task<IReadOnlyList<CodeView>> CodesAsync(CancellationToken ct = default)
    {
        var now = Now();
        List<CodeView> output = [];
        await _store.ReadAsync(async tx =>
            output = (await tx.CodesAsync().ConfigureAwait(false)).Where(c => c.Expires > now).Select(ToView).ToList(), ct).ConfigureAwait(false);
        return output.OrderBy(c => c.Expires).ToList();
    }

    /// <summary>Withdraws a code.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.NotFound"/>.</exception>
    public async Task DeleteCodeAsync(string id, CancellationToken ct = default) =>
        await _store.UpdateAsync(async tx =>
        {
            if (!(await tx.CodesAsync().ConfigureAwait(false)).Any(c => c.Id == id))
            {
                throw new MsfpException(MsfpError.NotFound);
            }

            await tx.DeleteCodeAsync(id).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    // Parses a pasted code and checks it isn't ours, nor a peer's.
    private async Task<PairingCode> ParseCodeAsync(string text, CancellationToken ct)
    {
        PairingCode c;
        try
        {
            c = PairingCode.Parse(text);
        }
        catch (ProtocolException e) when (e.Error == ProtocolError.PlainHttp)
        {
            throw new MsfpException(MsfpError.PlainHttp);
        }
        catch (ProtocolException)
        {
            throw new MsfpException(MsfpError.CodeInvalid);
        }

        if (Keys.Same(c.Key, Current().PublicSpan))
        {
            throw new MsfpException(MsfpError.OwnCode);
        }

        if (c.Expires <= Now())
        {
            throw new MsfpException(MsfpError.CodeExpired);
        }

        var known = false;
        await _store.ReadAsync(async tx => known = await tx.PeerByKeyAsync(c.Key).ConfigureAwait(false) is not null, ct).ConfigureAwait(false);
        if (known)
        {
            // To pair again with a peer that is gone, its operator removes it
            // first (core section 7.1, step 2).
            throw new MsfpException(MsfpError.AlreadyPeer);
        }

        return c;
    }

    /// <summary>What a pasted code holds, for the operator to see before redeeming it.</summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.CodeInvalid"/>, <see cref="MsfpError.CodeExpired"/>,
    /// <see cref="MsfpError.OwnCode"/>, <see cref="MsfpError.AlreadyPeer"/>, <see cref="MsfpError.PlainHttp"/>.</exception>
    public async Task<CodePreview> ReadCodeAsync(string text, CancellationToken ct = default)
    {
        var c = await ParseCodeAsync(text, ct).ConfigureAwait(false);
        return new CodePreview(c.Address, Keys.Fingerprint(c.Key), c.Expires);
    }

    /// <summary>
    /// This server's operator pasting another server's code: it fetches that
    /// server's discovery document (its base path), calls its pair, and adds
    /// it as a peer waiting for its operator (state theirs).
    /// </summary>
    /// <exception cref="MsfpException"><see cref="MsfpError.CodeInvalid"/>, <see cref="MsfpError.CodeExpired"/>,
    /// <see cref="MsfpError.OwnCode"/>, <see cref="MsfpError.AlreadyPeer"/>, <see cref="MsfpError.PlainHttp"/>,
    /// <see cref="MsfpError.RefusedDestination"/>, <see cref="MsfpError.Unreachable"/>,
    /// <see cref="MsfpError.CodeRefused"/> (used, withdrawn, expired there, or not its own).</exception>
    public async Task<PeerView> RedeemCodeAsync(string text, CancellationToken ct = default)
    {
        var c = await ParseCodeAsync(text, ct).ConfigureAwait(false);
        var ours = Named();
        var uncallable = Uncallables.Valid(ours);
        var body = JsonOut.Object(o => o.String("address", ours).String("name", Name())
            .Bytes("secretHash", Signing.SecretHash(c.Secret))
            .String("proof", Signing.PairingProof(c.Secret, Current().PublicSpan, ours))
            .Bytes("nextCommitment", OurKeys().Commitment)
            .Bool("inbound", uncallable ? false : null));
        var maker = new PeerRecord { Address = c.Address, Key = c.Key, Inbound = Inbound.Yes };
        maker.Base = await FetchBaseAsync(c.Address, ct).ConfigureAwait(false);
        var r = await CallAsync(maker, CallPair, body, ct).ConfigureAwait(false);
        if (r.Status == 409)
        {
            throw new MsfpException(MsfpError.AlreadyPeer);
        }

        if (r.Status != 200)
        {
            throw new MsfpException(MsfpError.CodeRefused);
        }

        // Its commitment is required (core section 9.1): without it, the
        // answer fails like a badly signed one, and nothing is kept.
        var ans = ReadAnswerBody(r.Body);
        byte[] theirs;
        string? name;
        try
        {
            theirs = ans.GetBytes("nextCommitment", 32) ?? throw new MsfpException(MsfpError.Unreachable,
                "a pair answer without a next key commitment");
            name = ans.GetString("name");
        }
        catch (ProtocolException e)
        {
            throw new MsfpException(MsfpError.Unreachable, e.Message, e);
        }

        var now = NowSecond();
        var p = new PeerRecord
        {
            Id = NewId("p"), Key = c.Key, Name = CleanName(name, c.Address), Created = now, Seen = now,
            State = PeerState.Theirs, Address = c.Address, Base = maker.Base, Inbound = Inbound.Yes,
            Commitment = theirs, CommitmentFrom = "pair", CommitmentTold = true, Limits = _cfg.NewPairing! with { },
        };
        await _store.UpdateAsync(async tx =>
        {
            // Paired meanwhile (its own code, redeemed by it at once)?
            if (await tx.PeerByKeyAsync(c.Key).ConfigureAwait(false) is not null)
            {
                throw new MsfpException(MsfpError.AlreadyPeer);
            }

            // Redeeming a code is consent to pair again (core section 7.1, step 5).
            await tx.UnforgetAsync(SHA256.HashData(c.Key)).ConfigureAwait(false);
            await tx.PutPeerAsync(p).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        _log.LogInformation("msfp: a code redeemed, waiting for the other operator: {Peer} {Name}", p.Id, p.Name);
        return View(p);
    }

    // pair: another server redeeming one of our codes (core sections 7.1,
    // step 4, and 9.1).
    private async Task<Answer> PairAsync(CallRequest req, CancellationToken ct)
    {
        string address, proof;
        string? name;
        byte[] hash, commitment;
        bool uncallable;
        try
        {
            var body = StrictJson.ReadRequest(req.Body, PairMembers);
            address = body.GetString("address") ?? throw StrictJsonBad();
            Address.Check(address);
            name = body.GetString("name");
            uncallable = body.GetBool("inbound") == false;
            Uncallables.CheckInbound(uncallable, address);
            hash = body.GetBytes("secretHash", 32) ?? throw StrictJsonBad();
            commitment = body.GetBytes("nextCommitment", 32) ?? throw StrictJsonBad();
            proof = body.GetString("proof") ?? "";
        }
        catch (ProtocolException)
        {
            return PairRefused(req);
        }

        var key = req.Verified!.Key;
        var now = NowSecond();
        var p = new PeerRecord
        {
            Id = NewId("p"), Key = key, Name = CleanName(name, address), Created = now, Seen = now,
            State = PeerState.Ours, Address = address, OurAddress = req.Ours, Inbound = uncallable ? Inbound.No : Inbound.Yes,
            Commitment = commitment, CommitmentFrom = "pair", CommitmentTold = true, Limits = _cfg.NewPairing! with { },
        };
        var known = false;
        try
        {
            await _store.UpdateAsync(async tx =>
            {
                // The code and the proof first, then whether the key is known
                // (core section 9.1).
                var c = await tx.CodeByHashAsync(hash).ConfigureAwait(false);
                if (c is null || c.Expires <= Now() || c.Secret is null)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                byte[] secret;
                try
                {
                    secret = await _sealer.OpenAsync(c.Secret, SealContext.Code, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new MsfpException(MsfpError.NotFound, null, e);
                }

                if (!Signing.CheckPairingProof(secret, key, address, proof))
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                if (await tx.PeerByKeyAsync(key).ConfigureAwait(false) is not null)
                {
                    known = true;
                    throw new MsfpException(MsfpError.AlreadyPeer); // the code stays live
                }

                await tx.DeleteCodeAsync(c.Id).ConfigureAwait(false);
                await tx.UnforgetAsync(SHA256.HashData(key)).ConfigureAwait(false);
                await tx.PutPeerAsync(p).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (MsfpException e) when (e.Code == MsfpError.NotFound)
        {
            return PairRefused(req);
        }
        catch (MsfpException e) when (e.Code == MsfpError.AlreadyPeer && known)
        {
            // Already a peer: told plainly, signed, with an empty body.
            return new Answer(409, null, []);
        }

        _log.LogInformation("msfp: a server redeemed a code, waiting for the operator: {Peer} {Name}", p.Id, p.Name);
        Notify(new PeerWaiting(p.Id, p.Name, Keys.Fingerprint(p.Key)));
        if (!uncallable)
        {
            // Its base path, from its discovery document (core section 11),
            // for every call to it; in the background.
            _ = Task.Run(async () =>
            {
                var b = await FetchBaseAsync(p.Address, CancellationToken.None).ConfigureAwait(false);
                if (b is not null)
                {
                    try
                    {
                        await UpdatePeerAsync(p.Id, q => q.Base = b, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (MsfpException)
                    {
                    }
                }
            }, CancellationToken.None);
        }

        return Answer.Ok(JsonOut.Object(o => o.String("name", Name()).Bytes("nextCommitment", OurKeys().Commitment)));
    }

    // pair's 404: plain to a stranger, signed to a key this server knows.
    private static Answer PairRefused(CallRequest req) =>
        req.Signer is null ? Answer.PlainRefusal : Answer.Refuse(ProblemType.NotFound);

    private static ProtocolException StrictJsonBad() => new(ProtocolError.Json);
}
