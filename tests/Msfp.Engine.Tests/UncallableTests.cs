using System.Text;
using System.Text.Json.Nodes;
using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>Servers that can't be called (MSFP core section 7.5; msfp-go's uncallable_test.go).</summary>
public sealed class UncallableTests
{
    // U can't be called: it redeems A's code, is paired by its scheduled
    // hellos, follows A's rotation and A's revoked key from A's answers, and
    // no connection ever reaches it, until it can be called again: A then
    // confirms its address by a hello there, and calls it.
    [Fact]
    public async Task UncallableServer()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var u = await TestServer.StartAsync(net, "https://u.example.org", "Server U", clock: clock);
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A", clock: clock);
        var open = false;
        var reached = 0;
        net.Add(u.Host, r =>
        {
            if (!open)
            {
                Interlocked.Increment(ref reached);
            }

            return u.Engine.HandleFederationAsync(r);
        });
        await u.Engine.SetUncallableAsync(true);
        var sv = await u.Engine.ServerAsync();
        Assert.True(sv.Uncallable && Uncallables.Valid(sv.Address) && sv.Named == sv.Address && sv.Former == "" && sv.MovedAt is null);
        var invalid = sv.Address;
        Assert.Equal(MsfpError.NoCodes, (await Assert.ThrowsAsync<MsfpException>(() => u.Engine.MakeCodeAsync(null))).Code);

        // Pairing: U redeems A's code; A accepts, and can't tell U; U's
        // scheduled hello learns it.
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var aOnU = await u.Engine.RedeemCodeAsync(code);
        var uOnA = Assert.Single(await a.Engine.PeersAsync());
        Assert.Equal(Inbound.No, uOnA.Inbound);
        Assert.Equal(invalid, uOnA.Address);
        await a.Engine.AcceptAsync(uOnA.Id);
        Assert.Equal(MsfpError.Uncallable, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(uOnA.Id))).Code);
        await u.Engine.RunOnceAsync();
        Assert.Equal(PeerState.Paired, (await u.Engine.PeerAsync(aOnU.Id)).State);
        var hellos = Hellos(net, a);
        Assert.Contains("\"inbound\":false,\"knownKey\":\"", hellos[^1].RequestBody, StringComparison.Ordinal);
        // Not due again for 5 minutes, give or take a fifth.
        clock.Advance(TimeSpan.FromMinutes(3));
        await u.Engine.RunOnceAsync();
        Assert.Equal(hellos.Count, Hellos(net, a).Count);

        // A rotates: no rotate to U; U pulls it from A's next answer, then
        // says it knows A's new key, and A's change is over.
        await a.Engine.RotateAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await u.Engine.RunOnceAsync();
        Assert.NotEmpty(u.Events.Of<PeerRotated>());
        await u.Engine.RunOnceAsync(); // the hello sent at once, knownKey the new key
        Assert.Null((await a.Engine.ServerAsync()).RotationSince);
        Assert.Equal((await a.Engine.ServerAsync()).Fingerprint, (await u.Engine.PeerAsync(aOnU.Id)).Fingerprint);

        // A's key stolen: revoked, and A rotates to its next one. U, pulling
        // both, suspends A, then follows the rotation it had committed to.
        await a.Engine.KeyStolenAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        await u.Engine.RunOnceAsync();
        Assert.NotEmpty(u.Events.Of<PeerSuspended>());
        Assert.NotEmpty(u.Events.Of<PeerResumed>());
        await u.Engine.RunOnceAsync();
        var aView = await a.Engine.ServerAsync();
        var p = await u.Engine.PeerAsync(aOnU.Id);
        Assert.Null(p.Suspended);
        Assert.Equal(aView.Fingerprint, p.Fingerprint);
        Assert.Null(aView.RotationSince);

        // U can be called again: it reaches itself, tells A, and A confirms
        // U's address by a hello there before calling it.
        Assert.Equal(0, reached);
        open = true;
        await u.Engine.SetUncallableAsync(false);
        await u.WaitAsync();
        await a.WaitAsync();
        sv = await u.Engine.ServerAsync();
        Assert.False(sv.Uncallable);
        Assert.Equal(u.Address, sv.Address);
        Assert.Equal("", sv.Former);
        Assert.Equal(Inbound.Yes, a.Events.Of<InboundChanged>().Last().Inbound);
        var v = await a.Engine.PeerAsync(uOnA.Id);
        Assert.Equal(Inbound.Yes, v.Inbound);
        Assert.Equal(u.Address, v.Address);
        await a.Engine.CheckAsync(uOnA.Id);

        // And can't again: the same .invalid address, no former, A stores it
        // at once from U's hello.
        await u.Engine.SetUncallableAsync(true);
        open = false;
        sv = await u.Engine.ServerAsync();
        Assert.Equal(invalid, sv.Address);
        Assert.Equal("", sv.Former);
        Assert.Null(sv.MovedAt);
        await u.Engine.RunOnceAsync();
        v = await a.Engine.PeerAsync(uOnA.Id);
        Assert.Equal(Inbound.No, v.Inbound);
        Assert.Equal(invalid, v.Address);
        Assert.Null(v.Moving);
        Assert.Equal(invalid, a.Events.Of<PeerMoved>().Last().To);
        Assert.Equal(0, reached);
    }

    // Turning "can't be called" off: until this server reaches its real
    // address, it still says it can't be called, with its .invalid address,
    // and keeps its scheduled hellos; its move doesn't end meanwhile (MSFP
    // core section 7.5, "Turning it off"). Then its peer confirms its address; an
    // answer there that says inbound false after all puts it back as one
    // that can't be called (back).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffWaitsForItsAddress(bool back)
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var u = await TestServer.StartAsync(net, "https://u.example.org", clock: clock);
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock);
        await u.Engine.SetUncallableAsync(true);
        var invalid = (await u.Engine.ServerAsync()).Address;
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        await u.Engine.RedeemCodeAsync(code);
        var uOnA = Assert.Single(await a.Engine.PeersAsync());
        await a.Engine.AcceptAsync(uOnA.Id);

        net.Remove(u.Host);
        await u.Engine.SetUncallableAsync(false);
        await u.WaitAsync();
        var before = Hellos(net, a).Count;
        for (var i = 0; i < 2; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            await u.Engine.RunOnceAsync();
            await a.WaitAsync();
            await u.WaitAsync();
            Assert.Equal(invalid, (await u.Engine.ServerAsync()).Named);
        }

        var p = await a.Engine.PeerAsync(uOnA.Id);
        Assert.Equal(Inbound.No, p.Inbound);
        Assert.Equal(invalid, p.Address);
        Assert.True(Hellos(net, a).Count >= before + 2, $"U's scheduled hellos stopped: {before} then {Hellos(net, a).Count}");

        net.Add(u.Host, async r =>
        {
            var nonce = r.Headers.GetValues(Signing.HeaderNonce).FirstOrDefault() ?? "";
            var res = await u.Engine.HandleFederationAsync(r);
            if (!back || !r.Target.EndsWith("/hello", StringComparison.Ordinal) || res.Status != 200)
            {
                return res;
            }

            // A hostile peer: its answer changed, and signed again with its key.
            var m = JsonNode.Parse(res.Body.Span)!.AsObject();
            if (m["state"] is null)
            {
                return res;
            }

            m["inbound"] = false;
            m["address"] = invalid;
            var body = Encoding.UTF8.GetBytes(m.ToJsonString());
            var key = await Kit.KeyAsync(u, x => x.Current, "msfp current key");
            return new MsfpResponse
            {
                Status = 200, Body = body,
                Headers = [new("Content-Type", "application/json"), new(Signing.HeaderSignature, Signing.SignResponse(key, nonce, 200, "application/json", body))],
            };
        });
        clock.Advance(TimeSpan.FromMinutes(10));
        await u.Engine.RunOnceAsync();
        await a.WaitAsync();
        await u.WaitAsync();
        Assert.Equal(u.Address, (await u.Engine.ServerAsync()).Named);
        p = await a.Engine.PeerAsync(uOnA.Id);
        Assert.Equal(back ? Inbound.No : Inbound.Yes, p.Inbound);
        Assert.Equal(back ? invalid : u.Address, p.Address);
        Assert.Null(p.Moving);
    }

    private static List<Seen> Hellos(Network net, TestServer s) =>
        net.SeenBy(s.Host).Where(x => x.Target.EndsWith("/hello", StringComparison.Ordinal)).ToList();
}
