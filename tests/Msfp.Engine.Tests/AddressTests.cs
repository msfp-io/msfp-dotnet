namespace Msfp.Tests;

/// <summary>Changes of address (MSFP core section 13; msfp-go's address_test.go).</summary>
public sealed class AddressTests
{
    private static Task WaitAll(params TestServer[] servers) => Task.WhenAll(servers.Select(s => s.WaitAsync()));

    private static List<Seen> Hellos(Network net, string host) =>
        net.SeenBy(host).Where(x => x.Target.EndsWith("/hello", StringComparison.Ordinal)).ToList();

    // A moves from A1 to A2. It reaches itself at A2 at once and tells its
    // peers: B confirms A2 by a hello there and moves; C can't reach A2,
    // keeps calling A1, which A still accepts, and is warned after a day; A
    // points C out after 3 days, and after 30 days accepts A2's host only.
    [Fact]
    public async Task MoveConfirmed()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var cNet = new CallerNetwork(net);
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var c = await TestServer.StartAsync(net, "https://c.example.org", clock: clock, transport: cNet);
        var (_, aOnB) = await Kit.LinkAsync(a, b);
        var (cOnA, aOnC) = await Kit.LinkAsync(a, c);
        const string A1 = "https://a.example.org", A2 = "https://a2.example.org";
        net.Add("a2.example.org", a.Engine);
        cNet.Closed("a2.example.org"); // C doesn't reach A2

        await a.Engine.SetAddressAsync(A2);
        await WaitAll(a, b, c);
        Assert.NotEmpty(a.Events.Of<AddressAnnounced>());
        var p = await b.Engine.PeerAsync(aOnB);
        Assert.Equal(A2, p.Address);
        Assert.Null(p.Moving);
        var moved = Assert.Single(b.Events.Of<PeerMoved>());
        Assert.Equal((A1, A2), (moved.From, moved.To));
        p = await c.Engine.PeerAsync(aOnC);
        Assert.Equal(A1, p.Address);
        Assert.Equal(A2, p.Moving?.Address);
        Assert.NotNull(p.Moving!.FailingSince);
        var sv = await a.Engine.ServerAsync();
        Assert.True(sv.Address == A2 && sv.Named == A2 && sv.Former == A1 && !sv.Waiting && sv.PeersMoved == 1 && sv.PeersToMove == 2, sv.ToString());

        // C still calls A1: accepted, and its view says it hasn't moved.
        await c.Engine.CheckAsync(aOnC);
        var v = await a.Engine.PeerAsync(cOnA);
        Assert.True(v.NotMoved && !v.MoveLate);

        // C tries again at most hourly; after a day it is warned, once.
        var tried = p.Moving.FailingSince;
        clock.Advance(TimeSpan.FromMinutes(30));
        await c.Engine.RunOnceAsync();
        p = await c.Engine.PeerAsync(aOnC);
        Assert.False(p.Moving!.Warn);
        for (var i = 0; i < 25; i++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await c.Engine.RunOnceAsync();
        }

        await WaitAll(a, b, c);
        var warned = Assert.Single(c.Events.Of<PeerMoveFailing>());
        Assert.Equal((tried, A2), (warned.Since, warned.Address));
        p = await c.Engine.PeerAsync(aOnC);
        Assert.True(p.Address == A1 && p.Moving is { Warn: true });

        // 3 days after: A points C out.
        clock.Advance(TimeSpan.FromHours(48));
        await a.Engine.RunOnceAsync();
        Assert.NotEmpty(a.Events.Of<NotMovedLate>());
        Assert.True((await a.Engine.PeerAsync(cOnA)).MoveLate);

        // After 30 days, A1's host is no longer accepted.
        clock.Advance(TimeSpan.FromDays(30));
        await a.Engine.RunOnceAsync();
        Assert.Equal(1, Assert.Single(a.Events.Of<MoveEnded>()).NotMoved);
        sv = await a.Engine.ServerAsync();
        Assert.Equal("", sv.Former);
        Assert.Null(sv.MovedAt);
        Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => c.Engine.CheckAsync(aOnC))).Code);
        await b.Engine.CheckAsync(aOnB);
    }

    // The last peer to move ends the move at once; the same address again
    // does nothing; addresses that aren't one are refused.
    [Fact]
    public async Task MoveEndsWhenAllMoved()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        await Kit.LinkAsync(a, b);
        net.Add("a2.example.org", a.Engine);
        await a.Engine.SetAddressAsync("https://a2.example.org");
        await WaitAll(a, b);
        Assert.Equal(0, Assert.Single(a.Events.Of<MoveEnded>()).NotMoved);
        var sv = await a.Engine.ServerAsync();
        Assert.Equal("", sv.Former);
        Assert.Equal("https://a2.example.org", sv.Address);
        await a.Engine.SetAddressAsync("https://a2.example.org");
        Assert.Single(a.Events.Of<MoveEnded>());
        foreach (var bad in new[] { "https://a.example.org/", "http://a.example.org", "https://msfp-4dq6fy7e4xtop2hj5lv6z3po54.invalid" })
        {
            await Assert.ThrowsAsync<MsfpException>(() => a.Engine.SetAddressAsync(bad));
        }
    }

    // A2 is announced only once A reaches itself there; until then A's
    // hellos name A1, and B keeps A1.
    [Fact]
    public async Task AnnounceWaitsForReach()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var c = await TestServer.StartAsync(net, "https://c.example.org", clock: clock);
        var (_, aOnB) = await Kit.LinkAsync(a, b);
        await Kit.LinkAsync(a, c);
        net.Remove(c.Host); // C doesn't move: the move lasts
        // Another server answers there, for now.
        net.Add("a2.example.org", _ => Task.FromResult(new MsfpResponse { Status = 404, Body = MsfpResponse.PlainNotFoundBody }));
        const string A1 = "https://a.example.org", A2 = "https://a2.example.org";
        await a.Engine.SetAddressAsync(A2);
        var sv = await a.Engine.ServerAsync();
        Assert.True(sv.Waiting && sv.Named == A1 && sv.Former == A1, sv.ToString());
        await b.Engine.CheckAsync(aOnB);
        Assert.Contains("\"address\":\"" + A1 + "\"", Hellos(net, a.Host)[^1].ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Hellos(net, b.Host), x => x.RequestBody.Contains(A2, StringComparison.Ordinal));
        var p = await b.Engine.PeerAsync(aOnB);
        Assert.Equal(A1, p.Address);
        Assert.Null(p.Moving);

        // 4 seconds: not tried again yet; 5: tried, A answers there now.
        net.Add("a2.example.org", a.Engine);
        clock.Advance(TimeSpan.FromSeconds(4));
        await a.Engine.RunOnceAsync();
        Assert.True((await a.Engine.ServerAsync()).Waiting);
        clock.Advance(TimeSpan.FromSeconds(1));
        await a.Engine.RunOnceAsync();
        await WaitAll(a, b);
        Assert.Equal(A2, (await b.Engine.PeerAsync(aOnB)).Address);
        // The 30 days of A1 run from when A2 was reached (MSFP core section 13.1, step 1).
        Assert.Equal(clock.GetUtcNow(), (await a.Engine.ServerAsync()).MovedAt);
    }

    // B moves to an address A refuses to connect to (link-local): A keeps
    // B's old address, and tries again later.
    [Fact]
    public async Task MoveToRefusedDestination()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var aNet = new CallerNetwork(net).Refused("169.254.10.10:8443");
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock, transport: aNet);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var (bOnA, _) = await Kit.LinkAsync(a, b);
        net.Add("169.254.10.10:8443", b.Engine); // B reaches itself there
        await b.Engine.SetAddressAsync("https://169.254.10.10:8443");
        await WaitAll(a, b);
        Assert.NotEmpty(b.Events.Of<AddressAnnounced>());
        var p = await a.Engine.PeerAsync(bOnA);
        Assert.Equal(b.Address, p.Address);
        Assert.Equal("https://169.254.10.10:8443", p.Moving?.Address);
        Assert.NotNull(p.Moving!.FailingSince);
        await a.Engine.CheckAsync(bOnA);
    }

    // B announces B2, which A can't reach yet; then B's key is revoked: A
    // suspends B, and its move waits: A never calls B2, even once it could.
    // A hello from B naming its stored address again drops a move.
    [Fact]
    public async Task SuspendedMoveWaits()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var aNet = new CallerNetwork(net).Closed("b2.example.org");
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock, transport: aNet);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var (bOnA, aOnB) = await Kit.LinkAsync(a, b);
        net.Add("b2.example.org", b.Engine);
        await b.Engine.SetAddressAsync("https://b2.example.org");
        await WaitAll(a, b);
        Assert.NotNull((await a.Engine.PeerAsync(bOnA)).Moving);

        // B moves back to its first address: its hello names A's stored one
        // again, and the move is dropped.
        await b.Engine.SetAddressAsync(b.Address);
        await b.Engine.CheckAsync(aOnB);
        await WaitAll(a, b);
        var p = await a.Engine.PeerAsync(bOnA);
        Assert.Null(p.Moving);
        Assert.Equal(b.Address, p.Address);

        // To B2 again; then B's key revoked, with no next key.
        await b.Engine.SetAddressAsync("https://b2.example.org");
        await WaitAll(a, b);
        Assert.NotNull((await a.Engine.PeerAsync(bOnA)).Moving);
        await b.Engine.KeyStolenAsync(withoutNext: true);
        Assert.NotNull((await a.Engine.PeerAsync(bOnA)).Suspended);
        aNet.Open("b2.example.org");
        var before = net.SeenBy("b2.example.org").Count;
        clock.Advance(TimeSpan.FromHours(2));
        await a.Engine.RunOnceAsync();
        await WaitAll(a, b);
        Assert.Equal(before, net.SeenBy("b2.example.org").Count);
        p = await a.Engine.PeerAsync(bOnA);
        Assert.Equal(b.Address, p.Address);
        Assert.NotNull(p.Moving);
    }

    // A's calls to B can't connect: B's next hello answer from A says so,
    // and B shows it.
    [Fact]
    public async Task CallsFailed()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var aNet = new CallerNetwork(net);
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock, transport: aNet);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var (bOnA, aOnB) = await Kit.LinkAsync(a, b);
        aNet.Closed(b.Host);
        Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(bOnA))).Code);
        await b.Engine.CheckAsync(aOnB);
        Assert.NotEmpty(b.Events.Of<CallsFailed>());
        Assert.True((await b.Engine.PeerAsync(aOnB)).CallsFailed);
        // A reaches B again: the next answer clears it.
        aNet.Open(b.Host);
        await a.Engine.CheckAsync(bOnA);
        await b.Engine.CheckAsync(aOnB);
        Assert.False((await b.Engine.PeerAsync(aOnB)).CallsFailed);
    }

    // A peer that names a second new address while its first is checked:
    // the first, once it answers under its key, is stored all the same, and
    // the second is checked next (MSFP core section 13.2); it failing, the first
    // stays.
    [Fact]
    public async Task MoveNamedAgainWhileChecked()
    {
        var net = new Network();
        var gate = new TaskCompletionSource();
        var waiting = new TaskCompletionSource();
        var aNet = new CallerNetwork(net).Held("b2.example.org", gate.Task, waiting).Closed("b3.example.org");
        var b = await TestServer.StartAsync(net, "https://b.example.org");
        var a = await TestServer.StartAsync(net, "https://a.example.org", transport: aNet);
        var (bOnA, _) = await Kit.LinkAsync(a, b);
        net.Add("b2.example.org", b.Engine);
        net.Add("b3.example.org", b.Engine);
        await b.Engine.SetAddressAsync("https://b2.example.org");
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await b.Engine.SetAddressAsync("https://b3.example.org");
        Assert.Equal("https://b3.example.org", (await a.Engine.PeerAsync(bOnA)).Moving?.Address);
        gate.SetResult();
        await WaitAll(a, b);
        var p = await a.Engine.PeerAsync(bOnA);
        Assert.Equal("https://b2.example.org", p.Address);
        Assert.Equal("https://b3.example.org", p.Moving?.Address);
        Assert.NotNull(p.Moving!.FailingSince);
    }
}
