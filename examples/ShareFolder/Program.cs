// A small MSFP host: one server, its records in JSON files, sharing a
// folder of films (Folder.cs) with the servers it pairs with by hand, and
// playing theirs. msfp-go's examples/share-folder, in C#.
//
// Two of them on one machine, in two terminals:
//
//   dotnet run -- --address https://127.0.0.1:8441 --listen :8441 --data a --films ~/films --loopback
//   dotnet run -- --address https://127.0.0.1:8442 --listen :8442 --data b --loopback
//
// --loopback lets peers on the same machine be called (the engine refuses
// local addresses otherwise); --listen defaults to the address's port, on
// every interface. Then type "code" in the first, "redeem <code>" in the
// second, compare the fingerprints both print, and "accept <id>" in the
// first. The commands:
//
//   code [note]        make a pairing code, shown once
//   preview <code>     what a code says, before redeeming it
//   redeem <code>      redeem another server's code
//   accept <id>        accept a peer that redeemed our code
//   peers              list the pairings
//   check <id>         send a peer a hello now
//   end <id>           end a pairing
//   server             this server's fingerprint and next key
//   rotate [key]       move this server to its next key (or to this exported key)
//   stolen [key|none]  revoke this server's key and rotate ("none": without the next key, every pairing ends)
//   confirm <id>       accept a peer's new key, after comparing fingerprints
//   address <url>      move this server (listen there too, so it reaches itself)
//   uncallable on|off  this server can't be called (behind a router), or can again
//   offer <id>         share the films with a peer
//   want <id>          read a peer's titles, all its libraries
//   titles <id>        list a peer's titles, as read
//   play <id> <key>    play a peer's title: prints a URL that curl -k or ffplay -tls_verify 0 plays
//                      through this server (anyone who has it may, for as long as the playback
//                      lasts: an example, not a viewer's page)
//   stop <playback>    end a playback
//   rescan             tell the engine the folder changed
//   quit
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Features;
using Msfp;
using Msfp.Examples.ShareFolder;
using Msfp.Protocol;
using Msfp.Stores;

var address = Arg("--address") ?? throw new ArgumentException("--address https://host[:port] is required");
var listen = Arg("--listen") ?? ":" + new Uri(address).Port;
var data = Arg("--data") ?? "msfp-data";
var films = Arg("--films");
var loopback = args.Contains("--loopback");

var cert = SelfSigned(new Uri(address).Host);
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.Listen(ListenAt(listen), l => l.UseHttps(cert)));
var app = builder.Build();

using var store = JsonFileStore.Open(data);
var folder = films is null ? null : new Folder(films);
using var engine = await MsfpEngine.OpenAsync(new MsfpConfig
{
    Address = address,
    Store = store,
    AllowLoopback = loopback,
    Pins = [Pins.Pin(cert)],
    Notifier = new ConsoleNotifier(),
    Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("msfp"),
    Parts = ["catalog", "playback", "media", "video"],
    Catalog = folder,
    Playback = folder,
    Media = folder,
});

// The federation routes: the raw target, no authentication in front, no
// compression of the answers.
app.MapPost("/api/v1/federation/{call}", async (HttpContext ctx) =>
{
    var res = await engine.HandleFederationAsync(Request(ctx, ctx.Request.Body), ctx.RequestAborted);
    ctx.Response.StatusCode = res.Status;
    foreach (var (k, v) in res.Headers)
    {
        ctx.Response.Headers.Append(k, v);
    }

    await ctx.Response.Body.WriteAsync(res.Body, ctx.RequestAborted);
});
// The stream route of peers' tickets (playback's media): every method goes
// to the engine, which serves GET only. A ticket that isn't the engine's
// would be the host's own viewers' (none here): 404.
app.Map("/api/v1/stream/{**rest}", async (HttpContext ctx) =>
{
    var outcome = await engine.HandleStreamAsync(Request(ctx, Stream.Null), new AspNetSink(ctx.Response), ctx.RequestAborted);
    if (outcome != StreamOutcome.Served)
    {
        ctx.Response.StatusCode = 404;
    }
});
// This server's viewers' route: the media of a playback on a peer, fetched
// through the engine piece by piece. The path is read as sent.
app.MapGet("/watch/{id}/{**rest}", async (HttpContext ctx, string id) =>
{
    var raw = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget;
    var prefix = "/watch/" + id + "/";
    if (!raw.StartsWith(prefix, StringComparison.Ordinal) || !MediaPath.TryParse(raw[prefix.Length..], out var path))
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    try
    {
        await engine.FetchAsync(id, path, ctx.Request.Headers.Range.ToString(), new AspNetSink(ctx.Response), ctx.RequestAborted);
    }
    catch (MsfpException e) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = e.Code switch
        {
            MsfpError.RangeNotSatisfiable => 416,
            MsfpError.ServerBusy => 503,
            _ => 404,
        };
    }
});
await app.StartAsync();
// The engine's background work: moves, scheduled hellos, readings, keys' time.
using var stop = new CancellationTokenSource();
_ = engine.RunAsync(stop.Token);
var me = await engine.ServerAsync();
Console.WriteLine($"this server: {me.Address} {me.Fingerprint}");

for (Console.Write("> "); Console.ReadLine() is { } line; Console.Write("> "))
{
    var words = line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
    var (cmd, arg) = (words[0], words.Length > 1 ? words[1] : "");
    try
    {
        switch (cmd)
        {
            case "code":
                var (text, code) = await engine.MakeCodeAsync(arg);
                Console.WriteLine($"{text}\n(until {code.Expires:u})");
                break;
            case "preview":
                var preview = await engine.ReadCodeAsync(arg);
                Console.WriteLine($"{preview.Address}, fingerprint {preview.Fingerprint}, until {preview.Expires:u}");
                break;
            case "redeem":
                var redeemed = await engine.RedeemCodeAsync(arg);
                Console.WriteLine($"waiting for {redeemed.Name}'s operator; its fingerprint: {redeemed.Fingerprint}");
                break;
            case "accept":
                Console.WriteLine("paired with " + (await engine.AcceptAsync(arg)).Name);
                break;
            case "check":
                Console.WriteLine((await engine.CheckAsync(arg)).State.Value);
                break;
            case "end":
                await engine.EndAsync(arg);
                Console.WriteLine("ended");
                break;
            case "server":
                var s = await engine.ServerAsync();
                Console.WriteLine($"{s.Address}, fingerprint {s.Fingerprint}, next key {s.NextKey} (on this server: {s.NextOnServer})");
                break;
            case "rotate":
                await engine.RotateAsync(arg.Length > 0 ? arg : null);
                Console.WriteLine("key changed; peers told");
                break;
            case "stolen":
                var rotated = await engine.KeyStolenAsync(arg is "" or "none" ? null : arg, withoutNext: arg == "none");
                Console.WriteLine($"key revoked; rotated to the next key: {rotated}");
                break;
            case "confirm":
                Console.WriteLine("new key confirmed: " + (await engine.ConfirmRotationAsync(arg)).Fingerprint);
                break;
            case "address":
                await engine.SetAddressAsync(arg);
                Console.WriteLine("moving; peers are told once this server answers there");
                break;
            case "uncallable":
                await engine.SetUncallableAsync(arg == "on");
                Console.WriteLine("can't be called: " + arg);
                break;
            case "offer":
                await engine.SetOffersAsync(arg, ["films"]);
                Console.WriteLine("offered");
                break;
            case "want":
                var libraries = await engine.TheirLibrariesAsync(arg);
                await engine.SetWantsAsync(arg, libraries.Select(l => l.Id));
                Console.WriteLine($"{await engine.ReadNowAsync(arg)} titles");
                break;
            case "titles":
                await store.ReadAsync(async tx =>
                {
                    foreach (var t in await tx.PeerTitlesAsync(arg, "", 1000))
                    {
                        Console.WriteLine(t.Key);
                    }
                }, default);
                break;
            case "play":
                var peerAndKey = arg.Split(' ', 2);
                var pb = await engine.PlayAsync(peerAndKey[0], new PlayRequest
                {
                    Title = peerAndKey.Length > 1 ? peerAndKey[1] : "",
                    Capabilities = new Capabilities
                    {
                        Containers = ["mp4", "mkv", "webm", "ts"],
                        VideoCodecs = ["h264", "hevc", "vp9", "av1"],
                        AudioCodecs = ["aac", "mp3", "opus", "ac3"],
                    },
                });
                var name = pb.Protocol == "hls" ? "master.m3u8" : "file";
                Console.WriteLine($"{address}/watch/{pb.Id}/{name}  ({pb.Method}, {pb.Protocol}; stop {pb.Id})");
                break;
            case "stop":
                await engine.StopAsync(arg, -1);
                Console.WriteLine("stopped");
                break;
            case "rescan":
                engine.CatalogChanged();
                break;
            case "peers":
                engine.PanelOpen();
                foreach (var p in await engine.PeersAsync())
                {
                    var pending = p.Pending is null ? "" : "  new key to confirm: " + p.Pending.Fingerprint;
                    Console.WriteLine($"{p.Id}  {p.State.Value,-7} {p.Fingerprint}  {p.Name}  {p.Address}{pending}");
                }

                break;
            case "quit":
                await stop.CancelAsync();
                await app.StopAsync();
                return;
            case "":
                break;
            default:
                Console.WriteLine("code [note] | preview <code> | redeem <code> | accept <id> | peers | check <id> | end <id> | server | " +
                    "rotate [key] | stolen [key|none] | confirm <id> | address <url> | uncallable on|off | offer <id> | want <id> | " +
                    "titles <id> | play <id> <key> | stop <playback> | rescan | quit");
                break;
        }
    }
    catch (MsfpException e)
    {
        Console.WriteLine($"error: {e.Code}: {e.Message}");
    }
}

await stop.CancelAsync();
await app.StopAsync();

MsfpRequest Request(HttpContext ctx, Stream body) => new()
{
    Method = ctx.Request.Method,
    Target = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget, // as sent: the signature covers it
    Headers = new AspNetHeaders(ctx.Request.Headers),
    Body = body,
    ClientAddress = ctx.Connection.RemoteIpAddress,
};

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Where to listen: "host:port", or ":port" for every interface.
static IPEndPoint ListenAt(string listen)
{
    var colon = listen.LastIndexOf(':');
    var host = listen[..colon].Trim('[', ']');
    var port = int.Parse(listen[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    return new IPEndPoint(host.Length == 0 ? IPAddress.IPv6Any : IPAddress.Parse(host), port);
}

// A certificate for the address's host, made at each start: its pin is
// named in this server's hellos, so peers accept it.
static X509Certificate2 SelfSigned(string host)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var req = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
    var san = new SubjectAlternativeNameBuilder();
    if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
    {
        san.AddIpAddress(ip);
    }
    else
    {
        san.AddDnsName(host);
    }

    req.CertificateExtensions.Add(san.Build());
    using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(365));
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

/// <summary>Prints the engine's events.</summary>
internal sealed class ConsoleNotifier : INotifier
{
    public void Notify(MsfpEvent e) => Console.WriteLine(e is PeerWaiting w
        ? $"\n{w.Name} used your code; its fingerprint is {w.Fingerprint}. Compare, then: accept {w.Peer}"
        : $"\n{e}");
}
