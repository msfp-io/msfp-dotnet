using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Msfp.Protocol;
using Msfp.Stores;

namespace Msfp.Tests;

/// <summary>ASP.NET Core's request headers, read as MSFP reads headers.</summary>
internal sealed class AspNetHeaders(IHeaderDictionary headers) : IHeaderReader
{
    public IReadOnlyList<string> GetValues(string name) =>
        headers.TryGetValue(name, out var v) ? v.Where(s => s is not null).Select(s => s!).ToList() : [];
}

/// <summary>Two engines behind real Kestrel servers over TLS, with self-signed certificates, pairing.</summary>
public sealed class KestrelTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    private sealed class Server : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required MsfpEngine Engine { get; init; }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Engine.Dispose();
        }
    }

    private static async Task<Server> StartAsync(string name)
    {
        var cert = SelfSigned();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert)));
        var app = builder.Build();
        MsfpEngine? engine = null;
        // The federation routes, on the raw target, nothing in front.
        app.MapPost("/api/v1/federation/{call}", async (HttpContext ctx) =>
        {
            var res = await engine!.HandleFederationAsync(new MsfpRequest
            {
                Method = ctx.Request.Method,
                Target = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget,
                Headers = new AspNetHeaders(ctx.Request.Headers),
                Body = ctx.Request.Body,
                ClientAddress = ctx.Connection.RemoteIpAddress,
            }, ctx.RequestAborted);
            ctx.Response.StatusCode = res.Status;
            foreach (var (k, v) in res.Headers)
            {
                ctx.Response.Headers.Append(k, v);
            }

            await ctx.Response.Body.WriteAsync(res.Body, ctx.RequestAborted);
        });
        await app.StartAsync();
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var port = new Uri(bound).Port;
        engine = await MsfpEngine.OpenAsync(new MsfpConfig
        {
            Address = $"https://127.0.0.1:{port}",
            Store = new MemoryStore(),
            Name = () => name,
            AllowLoopback = true,
            Pins = [Pins.Pin(cert)],
        });
        return new Server { App = app, Engine = engine };
    }

    [Fact]
    public async Task PairOverTls()
    {
        await using var a = await StartAsync("Server A");
        await using var b = await StartAsync("Server B");
        // A request dated in the second an engine started is refused (its
        // nonces are in memory only, decision 13): a second later.
        await Task.Delay(1100);
        var (code, _) = await a.Engine.MakeCodeAsync("over TLS");
        var onB = await b.Engine.RedeemCodeAsync(code);
        var onA = Assert.Single(await a.Engine.PeersAsync());
        Assert.Equal("Server B", onA.Name);
        await a.Engine.AcceptAsync(onA.Id);
        Assert.Equal(PeerState.Paired, (await b.Engine.PeerAsync(onB.Id)).State);
        Assert.Equal(PeerState.Paired, (await a.Engine.CheckAsync(onA.Id)).State);
    }

    [Fact]
    public async Task LoopbackRefusedByDefault()
    {
        await using var a = await StartAsync("Server A");
        using var c = await MsfpEngine.OpenAsync(new MsfpConfig { Address = "https://c.example.org", Store = new MemoryStore() });
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var e = await Assert.ThrowsAsync<MsfpException>(() => c.RedeemCodeAsync(code));
        Assert.Equal(MsfpError.RefusedDestination, e.Code);
    }
}
