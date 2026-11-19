using System.Text;

namespace Msfp.Tests;

/// <summary>The discovery document (core section 11): served when on, read when pairing, its base used.</summary>
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
