using System.Text;

namespace Msfp.Tests;

/// <summary>The discovery document (MSFP core section 11): served when on, read when pairing, its base used.</summary>
public sealed class DiscoveryTests
{
    [Fact]
    public async Task PrefixedServerFoundByItsDocument()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A", prefix: "/srv/media", discovery: true);
        var b = await TestServer.StartAsync(net, "https://b.example.org", "Server B", prefix: "/msfp", discovery: true);
        var res = a.Engine.HandleDiscovery(new MsfpRequest
        {
            Method = "GET", Target = "/.well-known/msfp", Headers = new Protocol.HeaderList(), Body = Stream.Null,
        });
        Assert.Equal(200, res.Status);
        Assert.EndsWith("\"base\":\"/srv/media/api/v1/federation/\"}", Encoding.UTF8.GetString(res.Body.Span), StringComparison.Ordinal);
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var onB = await b.Engine.RedeemCodeAsync(code);
        Assert.Contains(net.SeenBy(a.Host), s => s.Target == "/srv/media/api/v1/federation/pair");
        var onA = Assert.Single(await a.Engine.PeersAsync());
        // A learns B's base in the background after the pair.
        for (var i = 0; i < 50 && (await a.RecordAsync(onA.Id)).Base is null; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("/msfp/api/v1/federation/", (await a.RecordAsync(onA.Id)).Base);
        await a.Engine.AcceptAsync(onA.Id);
        Assert.Contains(net.SeenBy(b.Host), s => s.Target == "/msfp/api/v1/federation/hello");
        Assert.Equal(PeerState.Paired, (await b.Engine.PeerAsync(onB.Id)).State);

        // Off: the plain 404.
        a.Engine.SetDiscovery(false);
        Assert.True(a.Engine.HandleDiscovery(new MsfpRequest
        {
            Method = "GET", Target = "/.well-known/msfp", Headers = new Protocol.HeaderList(), Body = Stream.Null,
        }).PlainNotFound);
    }

    // A moves under another path: B's calls at the stored base fail; after
    // 3 of them it fetches A's document again, and calls it there. Then A
    // serves no document, under the default path: B's next 3 failing calls
    // fetch it again, the answered 404 sets the default base back.
    [Fact]
    public async Task BaseFetchedAgainAfterFailingCalls()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var store = new Stores.MemoryStore();
        var a = await TestServer.StartAsync(net, "https://a.example.org", prefix: "/srv/a", discovery: true, clock: clock, store: store);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var (_, aOnB) = await Kit.LinkAsync(a, b);
        Assert.Equal("/srv/a/api/v1/federation/", (await b.RecordAsync(aOnB)).Base);

        a.Dispose();
        a = await TestServer.StartAsync(net, "https://a.example.org", prefix: "/srv/c", discovery: true, clock: clock, store: store);
        for (var i = 0; i < MsfpEngine.FailingCalls; i++)
        {
            Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => b.Engine.CheckAsync(aOnB))).Code);
        }

        await b.WaitAsync();
        Assert.Equal("/srv/c/api/v1/federation/", (await b.RecordAsync(aOnB)).Base);
        await b.Engine.CheckAsync(aOnB);

        // An hour later (the refetch is hourly at most), with no document.
        a.Dispose();
        a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock, store: store);
        clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < MsfpEngine.FailingCalls; i++)
        {
            await Assert.ThrowsAsync<MsfpException>(() => b.Engine.CheckAsync(aOnB));
        }

        await b.WaitAsync();
        Assert.Null((await b.RecordAsync(aOnB)).Base);
        await b.Engine.CheckAsync(aOnB);
        a.Dispose();
    }

    // No answer at all keeps the base stored: a moment's outage doesn't undo it.
    [Fact]
    public async Task NoAnswerKeepsTheBase()
    {
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var a = await TestServer.StartAsync(net, "https://a.example.org", prefix: "/srv/a", discovery: true, clock: clock);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var (_, aOnB) = await Kit.LinkAsync(a, b);
        net.Remove(a.Host);
        for (var i = 0; i < MsfpEngine.FailingCalls; i++)
        {
            await Assert.ThrowsAsync<MsfpException>(() => b.Engine.CheckAsync(aOnB));
        }

        await b.WaitAsync();
        Assert.Equal("/srv/a/api/v1/federation/", (await b.RecordAsync(aOnB)).Base);
    }

    [Fact]
    public async Task BadPrefixRefused()
    {
        var e = await Assert.ThrowsAsync<MsfpException>(() => MsfpEngine.OpenAsync(new MsfpConfig
        {
            Address = "https://a.example.org", Store = new Stores.MemoryStore(), Prefix = "srv",
        }));
        Assert.Equal(MsfpError.InvalidAddress, e.Code);
        e = await Assert.ThrowsAsync<MsfpException>(() => MsfpEngine.OpenAsync(new MsfpConfig
        {
            Address = "http://a.example.org", Store = new Stores.MemoryStore(),
        }));
        Assert.Equal(MsfpError.PlainHttp, e.Code);
        e = await Assert.ThrowsAsync<MsfpException>(() => MsfpEngine.OpenAsync(new MsfpConfig
        {
            Address = "https://a.example.org/", Store = new Stores.MemoryStore(),
        }));
        Assert.Equal(MsfpError.InvalidAddress, e.Code);
    }
}
