using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>A key stolen without its next key (MSFP core section 12.7, case 2; msfp-go's TestKeyStolenWithoutNext and TestRevokedKeySealedApart).</summary>
public sealed class KeyStolenTests
{
    [Fact]
    public async Task WithoutNext()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A");
        var b = await TestServer.StartAsync(net, "https://b.example.org", "Server B");
        var c = await TestServer.StartAsync(net, "https://c.example.org", "Server C");
        var d = await TestServer.StartAsync(net, "https://d.example.org", "Server D");
        var e = await TestServer.StartAsync(net, "https://e.example.org", "Server E");
        var (_, aOnB) = await Kit.LinkAsync(a, b);
        var (_, aOnC) = await Kit.LinkAsync(a, c);
        var (dOnA, aOnD) = await Kit.LinkAsync(a, d);
        var (_, aOnE) = await Kit.LinkAsync(a, e);
        var ka = await Kit.KeyAsync(a, r => r.Current, "msfp current key");
        var nextBefore = (await a.Engine.ServerAsync()).NextKey;

        net.Remove(d.Host);
        await a.Engine.EndAsync(dOnA);
        net.Remove(c.Host);
        net.Remove(e.Host);
        Assert.False(await a.Engine.KeyStolenAsync(withoutNext: true));
        var sv = await a.Engine.ServerAsync();
        Assert.NotEqual(ka.Fingerprint, sv.Fingerprint);
        Assert.NotEqual(nextBefore, sv.NextKey);
        Assert.NotNull(sv.RevokedAt);
        Assert.True(sv.NextOnServer);
        Assert.Empty(await a.Engine.PeersAsync());
        Assert.Equal(4, a.Events.Of<PeerEnded>().Count);

        // B: suspended. No call goes to it; only rotate and revoke are heard
        // from a, and a rotation to a key it didn't commit to is refused.
        Assert.NotNull((await b.Engine.PeerAsync(aOnB)).Suspended);
        Assert.Single(b.Events.Of<PeerSuspended>());
        Assert.Equal(MsfpError.Suspended, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.CheckAsync(aOnB))).Code);
        var res = await Kit.SendAsync(b, ka, "hello", Kit.Paired);
        Assert.True(res.Status == 404 && res.Signed());
        res = await Kit.SendAsync(b, ka, "revoke", Rotations.SignRevocation(ka, b.Clock.GetUtcNow()).Body());
        Assert.Equal(200, res.Status);
        Assert.Equal("{\"state\":\"suspended\"}", res.Text());
        res = await Kit.SendAsync(b, ka, "rotate", Rotations.SignRotation(ka, MsfpKey.Generate(), null, b.Clock.GetUtcNow()).Body());
        Assert.Equal(409, res.Status);
        var v = await b.Engine.PeerAsync(aOnB);
        Assert.NotNull(v.Suspended);
        Assert.NotNull(v.Refused);

        // C: its hello is answered with the revocation alone, signed with ka.
        net.Add(c.Host, c.Engine);
        Kit.Advance(TimeSpan.FromHours(1), a, b, c, d, e);
        await c.Engine.CheckAsync(aOnC);
        Assert.NotNull((await c.Engine.PeerAsync(aOnC)).Suspended);
        Assert.Single(c.Events.Of<PeerSuspended>());
        var last = net.SeenBy(a.Host).Last(x => x.Target.EndsWith("/hello", StringComparison.Ordinal));
        Assert.StartsWith("{\"revocation\":{\"date\":", last.ResponseBody, StringComparison.Ordinal);

        // D: ended by A's operator before, the 404 of a forgotten key, signed
        // with the new key: unsigned to D.
        net.Add(d.Host, d.Engine);
        Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => d.Engine.CheckAsync(aOnD))).Code);
        Assert.Null((await d.Engine.PeerAsync(aOnD)).Suspended);
        last = net.SeenBy(a.Host).Last(x => x.Target.EndsWith("/hello", StringComparison.Ordinal));
        Assert.Equal(404, last.Status);
        Assert.NotNull(last.ResponseSignature);

        // 30 days.
        Kit.Advance(TimeSpan.FromDays(30), a, b, c, d, e);
        await a.Engine.RunOnceAsync();
        Assert.Null((await a.Engine.ServerAsync()).RevokedAt);
        await b.Engine.RunOnceAsync();
        v = await b.Engine.PeerAsync(aOnB);
        Assert.Equal(PeerState.Gone, v.State);
        Assert.NotNull(v.Suspended);
        Assert.Single(b.Events.Of<PeerGone>());
        res = await Kit.SendAsync(b, ka, "hello", Kit.Paired);
        Assert.Equal(404, res.Status);
        Assert.Equal(PeerState.Gone, (await b.Engine.PeerAsync(aOnB)).State);

        // E, after the 30 days: the plain 404 of an unknown key, not one
        // signed with a key it can't know (section 12.7, case 2).
        net.Add(e.Host, e.Engine);
        Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => e.Engine.CheckAsync(aOnE))).Code);
        Assert.Null((await e.Engine.PeerAsync(aOnE)).Suspended);
        last = net.SeenBy(a.Host).Last(x => x.Target.EndsWith("/hello", StringComparison.Ordinal));
        Assert.Equal(404, last.Status);
        Assert.Null(last.ResponseSignature);
    }

    [Fact]
    public async Task RevokedKeySealedApart()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org");
        var before = await Kit.KeyAsync(a, r => r.Current, "msfp current key");
        await a.Engine.KeyStolenAsync(withoutNext: true);
        var revoked = await Kit.KeyAsync(a, r => r.Revoked, "msfp revoked key");
        Assert.Equal(before.Fingerprint, revoked.Fingerprint);
        Assert.NotNull((await Kit.ServerRecordAsync(a)).RevokedAt);
    }

    [Fact]
    public async Task OnlyHelloGetsTheRevocation()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org");
        var b = await TestServer.StartAsync(net, "https://b.example.org");
        await Kit.LinkAsync(a, b);
        var kb = await Kit.KeyAsync(b, r => r.Current, "msfp current key");
        net.Remove(b.Host);
        await a.Engine.KeyStolenAsync(withoutNext: true);
        // Another call from B: the signed 404 of a forgotten key, with the new key.
        var res = await Kit.SendAsync(a, kb, "libraries", "{}"u8.ToArray());
        Assert.Equal(404, res.Status);
        Assert.True(res.Signed());
        res = await Kit.SendAsync(a, kb, "hello", Kit.Paired);
        Assert.Equal(200, res.Status);
        Assert.StartsWith("{\"revocation\":", res.Text(), StringComparison.Ordinal);
    }
}
