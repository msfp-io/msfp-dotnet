using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>What time does (msfp-go's run.go): codes and changes of key ending, suspended peers gone; a call retried after a restart.</summary>
public sealed class RunTests
{
    private static async Task<(Network Net, TestServer A, TestServer B, PeerView OnA, PeerView OnB)> PairedAsync()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org", "Server A");
        var b = await TestServer.StartAsync(net, "https://b.example.org", "Server B");
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var onB = await b.Engine.RedeemCodeAsync(code);
        var onA = Assert.Single(await a.Engine.PeersAsync());
        await a.Engine.AcceptAsync(onA.Id);
        return (net, a, b, onA, onB);
    }

    [Fact]
    public async Task CodesExpire()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org");
        await a.Engine.MakeCodeAsync(null);
        await a.Engine.RunOnceAsync();
        Assert.Single(await a.Engine.CodesAsync());
        a.Clock.Advance(TimeSpan.FromHours(25));
        await a.Engine.RunOnceAsync();
        var left = 0;
        await a.Store.ReadAsync(async tx => left = (await tx.CodesAsync()).Count, CancellationToken.None);
        Assert.Equal(0, left);
    }

    [Fact]
    public async Task ChangeOfKeyEndsAfter30Days()
    {
        var (net, a, _, onA, _) = await PairedAsync();
        net.Remove("b.example.org");
        await a.Engine.RotateAsync();
        Assert.Equal(MsfpEngine.KeyOld, (await a.Engine.PeerAsync(onA.Id)).OurKey);
        a.Clock.Advance(TimeSpan.FromDays(29));
        await a.Engine.RunOnceAsync();
        Assert.Equal(MsfpEngine.KeyOld, (await a.Engine.PeerAsync(onA.Id)).OurKey);
        a.Clock.Advance(TimeSpan.FromDays(1));
        await a.Engine.RunOnceAsync();
        Assert.Equal(MsfpEngine.KeyLost, (await a.Engine.PeerAsync(onA.Id)).OurKey);
        Assert.Equal(1, Assert.Single(a.Events.Of<RotationEnded>()).Lost);
    }

    [Fact]
    public async Task SuspendedPeersGoneAfter30Days()
    {
        var (_, _, b, _, onB) = await PairedAsync();
        // A revoked its key (as RevokeThroughB shows), and never moved since.
        await b.Store.UpdateAsync(async tx =>
        {
            var p = (await tx.PeerAsync(onB.Id))!;
            p.Suspended = b.Clock.GetUtcNow();
            await tx.PutPeerAsync(p);
        }, CancellationToken.None);
        b.Clock.Advance(TimeSpan.FromDays(30));
        await b.Engine.RunOnceAsync();
        Assert.Equal(PeerState.Gone, (await b.Engine.PeerAsync(onB.Id)).State);
        Assert.Single(b.Events.Of<PeerGone>());
    }

    [Fact]
    public async Task UnsignedRefusalRetriedOnce()
    {
        var (net, a, b, onA, _) = await PairedAsync();
        var refused = 0;
        net.Add(b.Host, async r =>
        {
            if (refused++ == 0)
            {
                return new MsfpResponse { Status = 404, Body = MsfpResponse.PlainNotFoundBody, PlainNotFound = true };
            }

            return await b.Engine.HandleFederationAsync(r);
        });
        var v = await a.Engine.CheckAsync(onA.Id);
        Assert.Equal(PeerState.Paired, v.State);
        Assert.Equal(2, refused);
        // Refused twice: unreachable.
        net.Add(b.Host, _ => Task.FromResult(new MsfpResponse { Status = 404, Body = MsfpResponse.PlainNotFoundBody }));
        Assert.Equal(MsfpError.Unreachable, (await Assert.ThrowsAsync<MsfpException>(() => a.Engine.CheckAsync(onA.Id))).Code);
    }

    [Fact]
    public async Task RunAsyncStopsWhenCancelled()
    {
        var net = new Network();
        var a = await TestServer.StartAsync(net, "https://a.example.org");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.Engine.RunAsync(cts.Token));
    }
}
