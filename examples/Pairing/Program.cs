// A small MSFP host: a Kestrel server whose operator pairs it with other
// servers by typed commands, its records in JSON files sealed with a key
// file. It makes a self-signed certificate at each start and pins it.
//
// Run two on one machine, in two terminals:
//
//   dotnet run -- --address https://127.0.0.1:8441 --data a --loopback
//   dotnet run -- --address https://127.0.0.1:8442 --data b --loopback
//
// --loopback lets peers on the same machine be called (the engine refuses
// local addresses otherwise). Then type "code" in the first and
// "redeem <code>" in the second, compare the fingerprints both print, and
// "accept <id>" in the first. The commands:
//
//   code [note]      make a pairing code, shown once
//   preview <code>   what a code says, before redeeming it
//   redeem <code>    redeem another server's code
//   accept <id>      accept a peer that redeemed our code
//   peers            list the pairings
//   check <id>       send a peer a hello now
//   end <id>         end a pairing
//   server           this server's fingerprint and next key
//   uncallable on|off  this server can't be called (behind a router), or can again
//   quit
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Features;
using Msfp;
using Msfp.Protocol;
using Msfp.Stores;

var address = Arg("--address") ?? throw new ArgumentException("--address https://host:port is required");
var data = Arg("--data") ?? "data";
var loopback = args.Contains("--loopback");
var port = new Uri(address).Port;

var cert = SelfSigned(new Uri(address).Host);
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, port, l => l.UseHttps(cert)));
var app = builder.Build();

using var store = JsonFileStore.Open(data);
using var engine = await MsfpEngine.OpenAsync(new MsfpConfig
{
    Address = address,
    Store = store,
    AllowLoopback = loopback,
    Pins = [Pins.Pin(cert)],
    Notifier = new ConsoleNotifier(),
    Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("msfp"),
});

// The federation routes: the raw target, no authentication in front, no
// compression of the answers.
app.MapPost("/api/v1/federation/{call}", async (HttpContext ctx) =>
{
    var res = await engine.HandleFederationAsync(new MsfpRequest
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
// The stream route of peers' tickets (playback's media): every method
// goes to the engine, which serves GET only; the raw target, no
// authentication in front, no compression. A ticket that isn't the
// engine's would be the host's own viewers' (none here): 404.
app.Map("/api/v1/stream/{**rest}", async (HttpContext ctx) =>
{
    var outcome = await engine.HandleStreamAsync(new MsfpRequest
    {
        Method = ctx.Request.Method,
        Target = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget,
        Headers = new AspNetHeaders(ctx.Request.Headers),
        Body = Stream.Null,
        ClientAddress = ctx.Connection.RemoteIpAddress,
    }, new AspNetSink(ctx.Response), ctx.RequestAborted);
    if (outcome != StreamOutcome.Served)
    {
        ctx.Response.StatusCode = 404;
    }
});
await app.StartAsync();
// The engine's background work: moves, scheduled hellos, keys' time.
using var stop = new CancellationTokenSource();
_ = engine.RunAsync(stop.Token);
var server = await engine.ServerAsync();
Console.WriteLine($"{address}, fingerprint {server.Fingerprint}");

while (Console.ReadLine() is { } line)
{
    var words = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (words.Length == 0)
    {
        continue;
    }

    var rest = words.Length > 1 ? words[1] : "";
    try
    {
        switch (words[0])
        {
            case "code":
                var (text, code) = await engine.MakeCodeAsync(rest);
                Console.WriteLine($"{text}\n(until {code.Expires:u})");
                break;
            case "preview":
                var p = await engine.ReadCodeAsync(rest);
                Console.WriteLine($"{p.Address}, fingerprint {p.Fingerprint}, until {p.Expires:u}");
                break;
            case "redeem":
                Show(await engine.RedeemCodeAsync(rest));
                break;
            case "accept":
                Show(await engine.AcceptAsync(rest));
                break;
            case "check":
                Show(await engine.CheckAsync(rest));
                break;
            case "end":
                await engine.EndAsync(rest);
                Console.WriteLine("ended");
                break;
            case "peers":
                foreach (var peer in await engine.PeersAsync())
                {
                    Show(peer);
                }

                break;
            case "server":
                var s = await engine.ServerAsync();
                Console.WriteLine($"{s.Address}, fingerprint {s.Fingerprint}, next key {s.NextKey} (on this server: {s.NextOnServer})");
                break;
            case "uncallable":
                await engine.SetUncallableAsync(rest == "on");
                Console.WriteLine($"named {(await engine.ServerAsync()).Named}");
                break;
            case "quit":
                await app.StopAsync();
                return;
            default:
                Console.WriteLine("code, preview, redeem, accept, peers, check, end, server, uncallable, quit");
                break;
        }
    }
    catch (MsfpException e)
    {
        Console.WriteLine($"refused: {e.Code}");
    }
}

await app.StopAsync();

static void Show(PeerView p) =>
    Console.WriteLine($"{p.Id}  {p.Name}  {p.Address}  {p.State}  inbound {p.Inbound}  fingerprint {p.Fingerprint}  core {p.Core}");

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static X509Certificate2 SelfSigned(string host)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var req = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
    var san = new SubjectAlternativeNameBuilder();
    if (IPAddress.TryParse(host, out var ip))
    {
        san.AddIpAddress(ip);
    }
    else
    {
        san.AddDnsName(host);
    }

    req.CertificateExtensions.Add(san.Build());
    using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
}

/// <summary>ASP.NET Core's request headers, read as MSFP reads headers.</summary>
internal sealed class AspNetHeaders(IHeaderDictionary headers) : IHeaderReader
{
    public IReadOnlyList<string> GetValues(string name) =>
        headers.TryGetValue(name, out var v) ? v.Where(s => s is not null).Select(s => s!).ToList() : [];
}

/// <summary>
/// An answer of the engine's media written on ASP.NET Core's response: the
/// status and only the headers MSFP needs, no content coding.
/// </summary>
internal sealed class AspNetSink(HttpResponse response) : IResponseSink
{
    public void Start(int status, string contentType, long length, string contentRange)
    {
        response.StatusCode = status;
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
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => response.Body.WriteAsync(data, ct);
}

/// <summary>Prints what happened.</summary>
internal sealed class ConsoleNotifier : INotifier
{
    public void Notify(MsfpEvent e) => Console.WriteLine($"* {e}");
}
