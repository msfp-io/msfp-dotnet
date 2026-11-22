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

/// <summary>An answer of the engine's media on ASP.NET Core's response.</summary>
internal sealed class AspNetSink(HttpResponse response) : IResponseSink, IETagSink
{
    private string _etag = "";

    public void SetETag(string etag) => _etag = etag;

    public void Start(int status, string contentType, long length, string contentRange)
    {
        response.StatusCode = status;
        if (status == 405)
        {
            response.Headers.Allow = "GET";
        }

        if (contentType.Length > 0)
        {
            response.ContentType = contentType;
        }

        if (length >= 0)
        {
            response.ContentLength = length;
        }

        if (contentRange.Length > 0)
        {
            response.Headers.ContentRange = contentRange;
        }

        if (status is 200 or 206)
        {
            response.Headers.AcceptRanges = "bytes";
            if (_etag.Length > 0)
            {
                response.Headers.ETag = "\"" + _etag + "\"";
            }
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => response.Body.WriteAsync(data, ct);
}

/// <summary>Two engines behind real Kestrel servers over TLS, with self-signed certificates, pairing.</summary>
public sealed class KestrelTests
{
    internal static X509Certificate2 SelfSigned()
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

    private static async Task<Server> StartAsync(string name, FakeHost? host = null)
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
        // The stream route of peers' tickets.
        app.Map("/api/v1/stream/{**rest}", async (HttpContext ctx) =>
        {
            var outcome = await engine!.HandleStreamAsync(new MsfpRequest
            {
                Method = ctx.Request.Method,
                Target = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget,
                Headers = new AspNetHeaders(ctx.Request.Headers),
                Body = Stream.Null,
            }, new AspNetSink(ctx.Response), ctx.RequestAborted);
            if (outcome != StreamOutcome.Served)
            {
                ctx.Response.StatusCode = 404;
            }
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
            Catalog = host,
            Playback = host,
            Media = host,
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

    // A film shared and played over TLS: the catalog read, the media relayed
    // through the pinned media client, a range, and a response held open
    // cut when the owning side ends the playback.
    [Fact]
    public async Task ShareAndPlayOverTls()
    {
        var h = HostKit.FilmHost();
        h.Libs = [new Library { Id = "films", Name = "Films", Kind = "movies" }];
        await using var a = await StartAsync("Server A", h);
        await using var b = await StartAsync("Server B");
        await Task.Delay(1100);
        var (code, _) = await a.Engine.MakeCodeAsync(null);
        var onB = (await b.Engine.RedeemCodeAsync(code)).Id;
        var onA = Assert.Single(await a.Engine.PeersAsync()).Id;
        await a.Engine.AcceptAsync(onA);
        await a.Engine.CheckAsync(onA);
        await b.Engine.CheckAsync(onB);
        await a.Engine.SetOffersAsync(onA, ["films"]);
        var libs = await b.Engine.TheirLibrariesAsync(onB);
        await b.Engine.SetWantsAsync(onB, [libs[0].Id]);
        Assert.Equal(1, await b.Engine.ReadNowAsync(onB));
        var pb = await b.Engine.PlayAsync(onB, new PlayRequest { Title = "movie/tmdb/949" });
        var master = new MemorySink();
        await b.Engine.FetchAsync(pb.Id, new MediaPath { Kind = MediaKind.Master }, "", master);
        Assert.Equal(HostKit.Master, master.Text);
        var part = new MemorySink();
        await b.Engine.FetchAsync(pb.Id, new MediaPath { Kind = MediaKind.Segment, Name = "seg0.m4s" }, "bytes=2-5", part);
        Assert.Equal((206, "2345", "bytes 2-5/20"), (part.Status, part.Text, part.ContentRange));
        // A response held open, as a live transcode: cut at the end.
        lock (h.Lock)
        {
            h.Serve = async (_, _, output, ct) =>
            {
                output.Start(200, "video/mp4", -1, "");
                while (true)
                {
                    await output.WriteAsync("xxxx"u8.ToArray(), ct);
                    await Task.Delay(5, ct);
                }
            };
        }

        var held = new MemorySink();
        var fetch = b.Engine.FetchAsync(pb.Id, new MediaPath { Kind = MediaKind.Segment, Name = "seg1.m4s" }, "", held);
        while (held.Body.Length < 40)
        {
            await Task.Delay(10);
        }

        a.Engine.StopPlayback(Assert.Single(a.Engine.Playbacks()).Id);
        // The owning side ends its answer there; the relay ends with it.
        await fetch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, held.Status);
        Assert.Empty(a.Engine.Playbacks());
    }
}
