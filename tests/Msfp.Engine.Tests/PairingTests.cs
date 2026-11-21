using System.Text;
using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>Two engines pairing over the in-process network (msfp-go's pairing_test.go and failures_test.go, the start).</summary>
public sealed class PairingTests
{
    private static async Task<(Network Net, TestServer A, TestServer B)> TwoAsync()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A");
        var b = await TestServer.StartAsync(net, "https://b.example.org:8443", "Server B");
        return (net, a, b);
    }

    private static async Task<(Network Net, TestServer A, TestServer B, PeerView OnA, PeerView OnB)> PairedAsync()
    {
        var (net, a, b) = await TwoAsync();
        var (code, _) = await a.Engine.MakeCodeAsync("for B");
        var onB = await b.Engine.RedeemCodeAsync(code);
        var onA = Assert.Single(await a.Engine.PeersAsync());
        await a.Engine.AcceptAsync(onA.Id);
        return (net, a, b, await a.Engine.PeerAsync(onA.Id), await b.Engine.PeerAsync(onB.Id));
    }

    [Fact]
    public async Task PairAcceptHello()
    {
        var (_, a, b) = await TwoAsync();
        var (code, view) = await a.Engine.MakeCodeAsync("  for\tB  ");
        Assert.Equal("for B", view.Note);
        Assert.StartsWith("msfp-pair-0.", code, StringComparison.Ordinal);
        Assert.Single(await a.Engine.CodesAsync());
        var preview = await b.Engine.ReadCodeAsync(code);
        Assert.Equal("https://a.example.org", preview.Address);
        Assert.Equal((await a.Engine.ServerAsync()).Fingerprint, preview.Fingerprint);

        var onB = await b.Engine.RedeemCodeAsync(code);
        Assert.Equal(PeerState.Theirs, onB.State);
        Assert.Equal("Server A", onB.Name);
        Assert.Empty(await a.Engine.CodesAsync()); // used once
        var onA = Assert.Single(await a.Engine.PeersAsync());
        Assert.Equal(PeerState.Ours, onA.State);
        Assert.Equal("Server B", onA.Name);
        Assert.Equal("https://b.example.org:8443", onA.Address);
        // The fingerprints each operator compares.
        Assert.Equal((await b.Engine.ServerAsync()).Fingerprint, onA.Fingerprint);
        Assert.Equal((await a.Engine.ServerAsync()).Fingerprint, onB.Fingerprint);
        var waiting = Assert.Single(a.Events.Of<PeerWaiting>());
        Assert.Equal(onA.Id, waiting.Peer);

        // A accepts: B is told at once by a hello, and both are paired.
        var accepted = await a.Engine.AcceptAsync(onA.Id);
        Assert.Equal(PeerState.Paired, accepted.State);
        Assert.Equal(PeerState.Paired, (await b.Engine.PeerAsync(onB.Id)).State);
        Assert.Single(b.Events.Of<PeerPaired>());
        Assert.Single(a.Events.Of<PeerPaired>());
        // Each kept the other's versions: every part of version 0 on.
        var p = await b.Engine.PeerAsync(onB.Id);
        Assert.Equal(0, p.Core);
        Assert.Equal(6, p.Parts.Count);
        Assert.Equal(0, (await b.Engine.CheckAsync(onB.Id)).Core);

        await Assert.ThrowsAsync<MsfpException>(() => a.Engine.AcceptAsync(onA.Id)); // not waiting now
    }

    [Fact]
    public async Task CommitmentsExchanged()
    {
        var (_, a, b, onA, onB) = await PairedAsync();
        var (fa, _) = await a.Engine.NextKeyAsync();
        var recB = await b.RecordAsync(onB.Id);
        Assert.Equal(fa, Keys.CommitmentFingerprint(recB.Commitment!));
        var (text, fingerprint) = await a.Engine.ExportNextKeyAsync();
        Assert.Equal(fa, fingerprint);
        Assert.Equal(fa, Rotations.ParseNextKey(text).Fingerprint);
        await a.Engine.DeleteNextKeyAsync();
        Assert.False((await a.Engine.NextKeyAsync()).OnServer);
        await Assert.ThrowsAsync<MsfpException>(() => a.Engine.ExportNextKeyAsync());
        Assert.Equal("pair", (await a.RecordAsync(onA.Id)).CommitmentFrom);
    }

    [Fact]
    public async Task EndTellsTheOther()
    {
        var (_, a, b, onA, onB) = await PairedAsync();
        await a.Engine.EndAsync(onA.Id);
        Assert.Empty(await a.Engine.PeersAsync());
        Assert.Empty(await b.Engine.PeersAsync()); // told: forgotten there
        Assert.True(Assert.Single(b.Events.Of<PeerEnded>()).ByThem);
        Assert.False(Assert.Single(a.Events.Of<PeerEnded>()).ByThem);
        _ = onB;
    }

    [Fact]
    public async Task ForgottenKeyGetsSignedUnknown()
    {
        var (net, a, b, onA, onB) = await PairedAsync();
        // A ends while B can't be reached: B still knows A.
        net.Remove(b.Host);
        await a.Engine.EndAsync(onA.Id);
        net.Add(b.Host, b.Engine);
        var e = await Assert.ThrowsAsync<MsfpException>(() => b.Engine.CheckAsync(onB.Id));
        Assert.Equal(MsfpError.Unknown, e.Code);
        Assert.Equal(PeerState.Gone, (await b.Engine.PeerAsync(onB.Id)).State);
        var last = net.SeenBy(a.Host)[^1];
        Assert.Equal(404, last.Status);
        Assert.NotNull(last.ResponseSignature); // signed: A knew B
    }

    [Fact]
    public async Task CodeFailures()
    {
        var (_, a, b) = await TwoAsync();
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        Assert.Equal(MsfpError.OwnCode, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.RedeemCodeAsync(code))).Code);
        Assert.Equal(MsfpError.CodeInvalid, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.RedeemCodeAsync("msfp-pair-1.x"))).Code);
        Assert.Equal(MsfpError.CodeInvalid, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.RedeemCodeAsync(code.Replace("msfp-pair-0.", "msfp-pair-1.", StringComparison.Ordinal)))).Code);
        // Withdrawn: refused there.
        var id = Assert.Single(await a.Engine.CodesAsync()).Id;
        await a.Engine.DeleteCodeAsync(id);
        Assert.Equal(MsfpError.CodeRefused, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.RedeemCodeAsync(code))).Code);
        await Assert.ThrowsAsync<MsfpException>(() => a.Engine.DeleteCodeAsync(id));
        // Expired here, then expired there (B's clock behind).
        (code, _) = await a.Engine.MakeCodeAsync(null);
        b.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(MsfpError.CodeExpired, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.RedeemCodeAsync(code))).Code);
        b.Clock.Advance(TimeSpan.FromHours(-25));
        a.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(MsfpError.CodeRefused, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.RedeemCodeAsync(code))).Code);
        a.Clock.Advance(TimeSpan.FromHours(-25));
        // Used, then again: already a peer (here first).
        (code, _) = await a.Engine.MakeCodeAsync(null);
        await b.Engine.RedeemCodeAsync(code);
        Assert.Equal(MsfpError.AlreadyPeer, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.RedeemCodeAsync(code))).Code);
    }

    [Fact]
    public async Task AlreadyPeerThere()
    {
        var (_, a, b, _, onB) = await PairedAsync();
        // B removed A, A didn't: B redeems a new code of A's: 409 there.
        await b.Store.UpdateAsync(tx => tx.DeletePeerAsync(onB.Id), CancellationToken.None);
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        Assert.Equal(MsfpError.AlreadyPeer, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.RedeemCodeAsync(code))).Code);
        Assert.Single(await a.Engine.CodesAsync()); // the code stays live
    }

    [Fact]
    public async Task StrangersGetThePlain404()
    {
        var (_, a, _) = await TwoAsync();
        var stranger = MsfpKey.Generate();
        async Task<MsfpResponse> Send(string call, string body, MsfpKey key, Action<HeaderList>? edit = null, string? target = null)
        {
            var t = target ?? "/api/v1/federation/" + call;
            var h = new HeaderList().Add("Content-Type", "application/json");
            Signing.SignRequest("POST", "a.example.org", t, Encoding.UTF8.GetBytes(body), key, a.Clock.GetUtcNow()).Apply(h);
            edit?.Invoke(h);
            return await a.Engine.HandleFederationAsync(new MsfpRequest
            {
                Method = "POST", Target = t, Headers = h, Body = new MemoryStream(Encoding.UTF8.GetBytes(body)),
            });
        }

        var res = await Send("hello", "{\"state\":\"paired\"}", stranger);
        Assert.True(res.PlainNotFound);
        Assert.Null(res.Header(Signing.HeaderSignature));
        Assert.Equal(MsfpResponse.PlainNotFoundBody.ToArray(), res.Body.ToArray());
        // pair without a live code: plain too.
        Assert.True((await Send("pair", "{}", stranger)).PlainNotFound);
        // With a live code, a bad pair is still the plain 404 (a stranger).
        await a.Engine.MakeCodeAsync(null);
        Assert.True((await Send("pair", "{\"address\":\"https://x.example.org\"}", stranger)).PlainNotFound);
        // Not MSFP's: plain.
        Assert.True((await Send("nothing", "{}", stranger)).PlainNotFound);
        Assert.True((await Send("hello", "{}", stranger, h => h.Set("Content-Type", "text/plain"))).PlainNotFound);
        Assert.True((await Send("hello", "{}", stranger, h => h.Add("Sec-Fetch-Site", "cross-site"))).PlainNotFound);
        // A body over 64 KiB.
        Assert.True((await Send("pair", "{\"a\":\"" + new string('x', 70_000) + "\"}", stranger)).PlainNotFound);
    }

    [Fact]
    public async Task PeersSignedRefusals()
    {
        var (net, a, b, _, onB) = await PairedAsync();
        // A bad hello from B: a signed 404, nothing kept.
        var before = await a.Engine.PeersAsync();
        var rec = await b.RecordAsync(onB.Id);
        _ = rec;
        var key = MsfpKey.FromSeed(await SeedOf(b));
        var h = new HeaderList().Add("Content-Type", "application/json");
        const string Target = "/api/v1/federation/hello";
        var body = "{\"core\":[1]}"u8.ToArray(); // no state
        var signed = Signing.SignRequest("POST", "a.example.org", Target, body, key, a.Clock.GetUtcNow());
        signed.Apply(h);
        var res = await a.Engine.HandleFederationAsync(new MsfpRequest { Method = "POST", Target = Target, Headers = h, Body = new MemoryStream(body) });
        Assert.Equal(404, res.Status);
        Assert.False(res.PlainNotFound);
        Assert.True(Signing.CheckResponse((await a.Engine.ServerAsync()).Fingerprint.Length > 0 ? await PublicOf(a) : [], signed.Nonce, 404,
            res.Header("Content-Type"), res.Body.Span, res.Header(Signing.HeaderSignature)));
        // The same again: a replay, the plain 404.
        res = await a.Engine.HandleFederationAsync(new MsfpRequest { Method = "POST", Target = Target, Headers = h, Body = new MemoryStream(body) });
        Assert.True(res.PlainNotFound);
        // A call A doesn't serve yet: signed not-supported.
        Assert.Equal(before.Count, (await a.Engine.PeersAsync()).Count);
        _ = net;
    }

    private static async Task<byte[]> SeedOf(TestServer s)
    {
        ServerRecord? r = null;
        await s.Store.ReadAsync(async tx => r = await tx.ServerAsync(), CancellationToken.None);
        return await PlainSealer.Instance.OpenAsync(r!.Current, "msfp current key", CancellationToken.None);
    }

    private static async Task<byte[]> PublicOf(TestServer s) => MsfpKey.FromSeed(await SeedOf(s)).Public();
}
