using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Msfp.Protocol;

namespace Msfp.Tests;

/// <summary>This server's own pins, read at its addresses (core section 3.2; msfp-go's TestBothPinsDuringMove).</summary>
public sealed class PinsTests
{
    // Connections to each host serve that host's certificate over TLS, on a
    // loopback socket; the engine reads its own pins through it.
    private sealed class TlsDialer(Dictionary<string, X509Certificate2> certs) : IDialer
    {
        public async ValueTask<Stream> DialAsync(DnsEndPoint endpoint, CancellationToken ct)
        {
            if (!certs.TryGetValue(endpoint.Host, out var cert))
            {
                throw new SocketException((int)SocketError.ConnectionRefused);
            }

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var server = await listener.AcceptTcpClientAsync(ct);
            await connecting;
            _ = Task.Run(async () =>
            {
                using (server)
                using (var tls = new SslStream(server.GetStream()))
                {
                    try
                    {
                        await tls.AuthenticateAsServerAsync(cert);
                        _ = await tls.ReadAsync(new byte[1]); // until the client closes
                    }
                    catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException)
                    {
                    }
                }
            }, CancellationToken.None);
            return client.GetStream();
        }
    }

    // During a move, A's hellos name the pins of both addresses, read from
    // each; once it is over, A2's only.
    [Fact]
    public async Task BothPinsDuringMove()
    {
        using var c1 = KestrelTests.SelfSigned();
        using var c2 = KestrelTests.SelfSigned();
        var (pin1, pin2) = (Pins.Pin(c1), Pins.Pin(c2));
        var both = Pins.Clean([pin1, pin2]);
        var net = new Network();
        var clock = new FakeTime(1800000000);
        var dialer = new TlsDialer(new() { ["a.example.org"] = c1, ["a2.example.org"] = c2 });
        var a = await TestServer.StartAsync(net, "https://a.example.org", clock: clock, dialer: dialer);
        var b = await TestServer.StartAsync(net, "https://b.example.org", clock: clock);
        var cNet = new CallerNetwork(net);
        var c = await TestServer.StartAsync(net, "https://c.example.org", clock: clock, transport: cNet);
        var (_, aOnB) = await Kit.LinkAsync(a, b);
        var (_, aOnC) = await Kit.LinkAsync(a, c);
        Assert.Equal([pin1], (await b.RecordAsync(aOnB)).Pins!);

        net.Add("a2.example.org", a.Engine);
        cNet.Closed("a2.example.org"); // C stays: the move lasts
        await a.Engine.SetAddressAsync("https://a2.example.org");
        await Task.WhenAll(a.WaitAsync(), b.WaitAsync(), c.WaitAsync());
        Assert.Equal(both, (await b.RecordAsync(aOnB)).Pins!);
        await c.Engine.CheckAsync(aOnC);
        Assert.Equal(both, (await c.RecordAsync(aOnC)).Pins!);

        clock.Advance(TimeSpan.FromDays(31));
        await a.Engine.RunOnceAsync();
        await b.Engine.CheckAsync(aOnB);
        Assert.Equal([pin2], (await b.RecordAsync(aOnB)).Pins!);
    }
}
