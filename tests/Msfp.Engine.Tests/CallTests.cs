namespace Msfp.Tests;

/// <summary>This server's calls to its peers: background calls back off, PanelOpen asks peers in state theirs.</summary>
public sealed class CallTests
{
    // After a call the peer didn't answer, calls made in the background hold
    // off (MSFP core section 8.3), 2 seconds give or take a quarter, then twice
    // as long each time; the operator's calls go all the same.
    [Fact]
    public async Task BackgroundCallsBackOff()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var (bOnA, _) = await Kit.LinkAsync(a, b);
        net.Remove(b.Host);
        async Task<MsfpError> Background() =>
            (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(bOnA, background: true, CancellationToken.None))).Code;

        Assert.Equal(MsfpError.Unreachable, await Background());
        Assert.Equal(MsfpError.BackingOff, await Background());
        Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(bOnA))).Code);
        // Two unanswered: 4 seconds, give or take a quarter.
        clock.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Equal(MsfpError.BackingOff, await Background());
        clock.Advance(TimeSpan.FromSeconds(2.2));
        Assert.Equal(MsfpError.Unreachable, await Background());
        // Three: 8 seconds.
        clock.Advance(TimeSpan.FromSeconds(5.9));
        Assert.Equal(MsfpError.BackingOff, await Background());
        clock.Advance(TimeSpan.FromSeconds(4.2));
        Assert.Equal(MsfpError.Unreachable, await Background());
        // Answered again: no holding off.
        net.Add(b.Host, b.Engine);
        await a.Engine.CheckAsync(bOnA);
        await a.Engine.CheckAsync(bOnA, background: true, CancellationToken.None);
    }

    [Fact]
    public void BackoffGrowsToAnHour()
    {
        for (var n = 1; n <= 30; n++)
        {
            var d = CallLimiter.Backoff(n);
            var full = Math.Min(2 * Math.Pow(2, Math.Min(n - 1, 20)), 3600);
            Assert.InRange(d.TotalSeconds, full * 0.75, Math.Min(full * 1.25, 3600));
        }
    }

    // A's operator accepts while B is down; B's panel open asks A, and B
    // shows the pairing.
    [Fact]
    public async Task PanelOpen()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org");
        var b = await TestServer.StartAsync(net, "https://b.example.org");
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var onB = await b.Engine.RedeemCodeAsync(code);
        net.Remove(b.Host);
        await a.Engine.AcceptAsync(Assert.Single(await a.Engine.PeersAsync()).Id);
        net.Add(b.Host, b.Engine);
        Assert.Equal(PeerState.Theirs, (await b.Engine.PeerAsync(onB.Id)).State);
        b.Engine.PanelOpen();
        await b.WaitAsync();
        Assert.Equal(PeerState.Paired, (await b.Engine.PeerAsync(onB.Id)).State);
        // Again at once: no hello within 20 seconds.
        var hellos = net.SeenBy(a.Host).Count;
        b.Engine.PanelOpen();
        await b.WaitAsync();
        Assert.Equal(hellos, net.SeenBy(a.Host).Count);
    }

    // A peer over its limit is answered 429, signed, with retryAfter; this
    // server's calls to it then wait that long, the operator's too. Over the
    // limit by address, before any signature, even a peer gets the plain 404.
    [Fact]
    public async Task RateLimits()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org", limits: new CallLimits { PeerRate = 0.001, PeerBurst = 2 });
        var b = await TestServer.StartAsync(net, "https://b.example.org");
        var (_, onB) = await Kit.LinkAsync(a, b); // A's hello to B, then B's none: A's accept hello was A's call
        await b.Engine.CheckAsync(onB);
        await b.Engine.CheckAsync(onB);
        Assert.Equal(MsfpError.RateLimited, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.CheckAsync(onB))).Code);
        var last = net.SeenBy(a.Host)[^1];
        Assert.True(last.Status == 429 && last.ResponseSignature is not null && last.ResponseBody.Contains("\"retryAfter\":", StringComparison.Ordinal));
        Assert.Equal(MsfpError.BackingOff, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.CheckAsync(onB))).Code);

        var c = await TestServer.StartAsync(net, "https://c.example.org", limits: new CallLimits { AddressRate = 0.001, AddressBurst = 1 });
        var d = await TestServer.StartAsync(net, "https://d.example.org");
        var (_, onD) = await Kit.LinkAsync(c, d);
        Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => d.Engine.CheckAsync(onD))).Code);
        last = net.SeenBy(c.Host)[^1];
        Assert.True(last.Status == 404 && last.ResponseSignature is null);
    }
}
