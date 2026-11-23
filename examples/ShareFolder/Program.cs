// A small MSFP host: one server, its records in JSON files, sharing a
// folder of films and series (Folder.cs) with the servers it pairs with by
// hand, and playing theirs. msfp-go's examples/share-folder, in C#.
//
// Two of them on one machine, in two terminals:
//
//   dotnet run -- --address https://127.0.0.1:8441 --listen :8441 --data a --folder ~/videos --loopback
//   dotnet run -- --address https://127.0.0.1:8442 --listen :8442 --data b --loopback
//
// --loopback lets peers on the same machine be called (the engine refuses
// local addresses otherwise); --listen defaults to the address's port, on
// every interface. Then type "code" in the first, "redeem <code>" in the
// second, compare the fingerprints both print, and "accept <id>" in the
// first. With ffprobe installed, copies are described from their files
// (with the Msfp.Describe package: length, picture, audio and subtitle
// tracks, chapters); with ffmpeg, preview sheets and
// JPEG images are re-encoded (.NET has no JPEG codec: without it, no
// previews are offered or relayed). The commands:
//
//   code [note]        make a pairing code, shown once
//   preview <code>     what a code says, before redeeming it
//   redeem <code>      redeem another server's code
//   accept <id>        accept a peer that redeemed our code
//   peers              list the pairings (and why a peer's titles weren't read, when they weren't)
//   check <id>         send a peer a hello now
//   end <id>           end a pairing
//   server             this server's fingerprint and next key
//   rotate [key]       move this server to its next key (or to this exported key)
//   stolen [key|none]  revoke this server's key and rotate ("none": without the next key, every pairing ends)
//   confirm <id>       accept a peer's new key, after comparing fingerprints
//   address <url>      move this server (listen there too, so it reaches itself)
//   uncallable on|off  this server can't be called (behind a router), or can again
//   offer <id>         share the folder with a peer
//   local <id> on|off  share its local titles with the peer too (off by default)
//   limits <id> <streams> <transcodes> [<downloads> <downloads a day> <download kbps> <scans a day>]
//                      the peer's streams here; downloads and scans allowed (none by default)
//   want <id>          read a peer's titles, all its libraries
//   titles <id>        list a peer's titles, as read
//   play <id> <key>    play a peer's title: prints a URL that curl -k or ffplay -tls_verify 0 plays
//                      through this server (anyone who has it may, for as long as the playback
//                      lasts: an example, not a viewer's page)
//   pause <playback>   say the viewer paused (resume <playback>: resumed); a playback is
//                      reported to its peer every minute until stopped, since a peer ends
//                      one it hears nothing of for 2 minutes
//   download <id> <key>  download a peer's title: prints the URL of the file to save (a download
//                      lives 24 hours, its late reports 30 days)
//   late <download> <position ms>  send a download's late report (watched offline)
//   previews <id> <key>  fetch a peer's film's previews, sheet by sheet
//   stop <playback>    end a playback
//   rescan             tell the engine the folder changed
//   group-create <name>          found a group
//   groups                       the groups, their members, requests and joins waiting
//   invite <group>               a code for the group (redeemed with redeem, accepted with accept)
//   pair <group> <mail>          accept a member's request; refuse <group> <mail> refuses it
//   join-accept <group> <peer>   accept a peer already paired that used our group code
//   admin <group> <member>       name a member admin (the owner only)
//   ban <group> <member>         ban a member
//   dissolve <group>             end the group and the pairings only it made
//   group-offer <group>          share the folder with the group
//   group-local <group> on|off   share its local titles with the group too
//   auto <group> on|off          accept the group's members by itself
//   leave <group>                leave the group
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
var shared = Arg("--folder");
var loopback = args.Contains("--loopback");

var cert = SelfSigned(new Uri(address).Host);
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.Listen(ListenAt(listen), l => l.UseHttps(cert)));
var app = builder.Build();

using var store = JsonFileStore.Open(data);
var folder = shared is null ? null : new Folder(shared);
var notifier = new ConsoleNotifier();
using var engine = await MsfpEngine.OpenAsync(new MsfpConfig
{
    Address = address,
    Store = store,
    AllowLoopback = loopback,
    Pins = [Pins.Pin(cert)],
    Notifier = notifier,
    Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("msfp"),
    Parts = ["catalog", "playback", "media", "video", "groups"],
    Catalog = folder,
    Playback = folder,
    Media = folder,
    SheetEncoder = FfmpegSheets.IfInstalled(),
});
if (folder is not null)
{
    folder.Opaque = engine.OpaqueKeyPart;
}

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
var watches = new Watches(engine);
notifier.RemotePlaybackEnded = watches.Stop;
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
            case "individual":
                var rel = await engine.AskIndividualAsync(arg);
                Console.WriteLine($"individual: {rel.Individual}, asked: {rel.IndividualAsked}");
                break;
            case "end-individual":
                await engine.EndIndividualAsync(arg);
                Console.WriteLine("individual relation ended");
                break;
            case "block":
                await engine.BlockPeerAsync(arg);
                Console.WriteLine("blocked");
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
                await engine.SetOffersAsync(arg, ["folder"]);
                Console.WriteLine("offered");
                break;
            case "local":
            case "group-local":
                var idAndOn = arg.Split(' ', 2);
                var on = idAndOn.Length > 1 && idAndOn[1] == "on";
                await (cmd == "local" ? engine.SetLocalAsync(idAndOn[0], on) : engine.SetGroupLocalAsync(idAndOn[0], on));
                Console.WriteLine("local titles shared: " + (on ? "on" : "off"));
                break;
            case "limits":
                var lw = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int N(int i) => i < lw.Length ? int.Parse(lw[i], System.Globalization.CultureInfo.InvariantCulture) : 0;
                var limits = new Limits
                {
                    Streams = N(1), Transcodes = N(2), Downloads = N(3), DownloadsPerDay = N(4), DownloadKbps = N(5), ScansPerDay = N(6),
                };
                if (limits.ScansPerDay > 0)
                {
                    limits.ScanBytes = Playback.ScanBytesRecommended; // a scan reads at most 32 MiB: the scan's answer says so
                }

                await engine.SetLimitsAsync(lw.Length > 0 ? lw[0] : "", limits);
                Console.WriteLine("limits: " + limits);
                break;
            case "download":
                var dl = arg.Split(' ', 2);
                var download = await engine.PlayAsync(dl[0], new Msfp.PlayRequest
                {
                    Title = dl.Length > 1 ? dl[1] : "", Purpose = Playback.PurposeDownload, Delivery = Playback.DeliveryFile,
                });
                Console.WriteLine($"{address}/watch/{download.Id}/file  ({download.Size} bytes; stop {download.Id})");
                break;
            case "late":
                var late = arg.Split(' ', 2);
                await engine.ReportOfflineAsync(late[0], new OfflineReport(
                    late.Length > 1 && long.TryParse(late[1], System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 0, DateTimeOffset.UtcNow));
                Console.WriteLine("sent");
                break;
            case "previews":
                var pk = arg.Split(' ', 2);
                Console.WriteLine($"{await PreviewsOfAsync(pk[0], pk.Length > 1 ? pk[1] : "")} sheets fetched");
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
                // No delivery: the baseline, H.264 and AAC in HLS, which a
                // browser plays (MSFP playback section 2.7). A viewer's server
                // that knows its device would choose from the copy's facts.
                var pb = await engine.PlayAsync(peerAndKey[0], new Msfp.PlayRequest { Title = peerAndKey.Length > 1 ? peerAndKey[1] : "" });
                watches.Start(pb.Id);
                var name = pb.Protocol == "hls" ? "master.m3u8" : "file";
                Console.WriteLine($"{address}/watch/{pb.Id}/{name}  ({pb.Method}, {pb.Protocol}; stop {pb.Id})");
                break;
            case "pause":
            case "resume":
                Console.WriteLine(watches.Pause(arg, cmd == "pause") ? cmd + "d" : "no such playback here");
                break;
            case "stop":
                watches.Stop(arg);
                await engine.StopAsync(arg, -1);
                Console.WriteLine("stopped");
                break;
            case "rescan":
                engine.CatalogChanged();
                break;
            case "group-create":
                Console.WriteLine("group " + (await engine.CreateGroupAsync(arg)).Id);
                break;
            case "groups":
                foreach (var g in await engine.GroupsAsync())
                {
                    Console.WriteLine($"group {g.Id} \"{g.Name}\" {g.Role}");
                    if (g.NoWayIn)
                    {
                        Console.WriteLine("  no way in: no pairing through this group");
                    }

                    foreach (var m in g.Members)
                    {
                        Console.WriteLine($"  member {m.Id} \"{m.Name}\" {m.Role} peer={m.Peer}");
                    }

                    foreach (var r in g.Requests)
                    {
                        Console.WriteLine($"  request {r.Mail} from \"{r.Name}\"");
                    }

                    foreach (var j in g.Joins)
                    {
                        Console.WriteLine($"  join {j.Peer} \"{j.Name}\"");
                    }
                }

                break;
            case "invite":
                var (inviteText, _) = await engine.MakeCodeWithAsync(new CodeOptions { Group = arg, Uses = 10 });
                Console.WriteLine(inviteText);
                break;
            case "pair":
            case "refuse":
            case "join-accept":
            case "admin":
            case "ban":
            case "auto":
                var groupAndRest = arg.Split(' ', 2);
                var rest = groupAndRest.Length > 1 ? groupAndRest[1] : "";
                await (cmd switch
                {
                    "pair" => engine.AcceptRequestAsync(groupAndRest[0], rest),
                    "refuse" => engine.RefuseRequestAsync(groupAndRest[0], rest),
                    "join-accept" => engine.AcceptJoinAsync(groupAndRest[0], rest),
                    "admin" => engine.SetAdminAsync(groupAndRest[0], rest, true),
                    "ban" => engine.BanAsync(groupAndRest[0], rest),
                    _ => engine.SetGroupAnswerAsync(groupAndRest[0], rest == "on"),
                });
                Console.WriteLine("done");
                break;
            case "group-offer":
                await engine.SetGroupOffersAsync(arg, ["folder"]);
                Console.WriteLine("offered");
                break;
            case "leave":
                await engine.LeaveGroupAsync(arg);
                Console.WriteLine("left");
                break;
            case "dissolve":
                await engine.DissolveGroupAsync(arg, keepPairings: false);
                Console.WriteLine("dissolved");
                break;
            case "peers":
                engine.PanelOpen();
                foreach (var p in await engine.PeersAsync())
                {
                    var pending = p.Pending is null ? "" : "  new key to confirm: " + p.Pending.Fingerprint;
                    var relation = p.Individual ? "  individual" : p.IndividualAsked ? "  asked" : p.IndividualAsks ? "  asks-you" : "";
                    var groups = p.Groups.Count > 0 ? $"  groups={p.Groups.Count}" : "";
                    var reading = p.Reading?.Failure is { } why ? $"  titles not read: {why} {p.Reading.Refusal}" : "";
                    Console.WriteLine($"{p.Id}  {p.State.Value,-7} {p.Fingerprint}  {p.Name}  {p.Address}{relation}{groups}{pending}{reading}");
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
                    "rotate [key] | stolen [key|none] | confirm <id> | address <url> | uncallable on|off | offer <id> | local <id> on|off | " +
                    "limits <id> <n>... | want <id> | titles <id> | play <id> <key> | download <id> <key> | late <download> <ms> | previews <id> <key> | " +
                    "stop <playback> | rescan | group-create <name> | groups | invite <group> | pair <group> <mail> | " +
                    "refuse <group> <mail> | join-accept <group> <peer> | admin <group> <member> | ban <group> <member> | dissolve <group> | group-offer <group> | group-local <group> on|off | auto <group> on|off | leave <group> | quit");
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

// Fetches the seek-bar previews of a peer's title's first copy, sheet by
// sheet: someone opened it (MSFP video section 5.5).
async Task<int> PreviewsOfAsync(string peer, string key)
{
    Msfp.Title? t = null;
    await store.ReadAsync(async tx => t = await tx.PeerTitleAsync(peer, key), default);
    if (t is not Msfp.VideoTitle v || v.BestCopy() is not { PreviewLayout: { } layout } c)
    {
        throw new MsfpException(MsfpError.NotFound);
    }

    for (var n = 0; n < layout.Sheets; n++)
    {
        await engine.PreviewSheetAsync(peer, key, c.Id, n);
    }

    return layout.Sheets;
}

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

/// <summary>Prints the engine's events.</summary>
internal sealed class ConsoleNotifier : INotifier
{
    /// <summary>Called with a remote playback's ID when it ends, on the peer's side or here: no more reports.</summary>
    public Action<string>? RemotePlaybackEnded { get; set; }

    public void Notify(MsfpEvent e)
    {
        if (e is RemotePlaybackEnded r)
        {
            RemotePlaybackEnded?.Invoke(r.Playback);
        }

        Print(e);
    }

    private static void Print(MsfpEvent e) => Console.WriteLine(e is PeerWaiting w
        ? $"\n{w.Name} used your code; its fingerprint is {w.Fingerprint}. Compare, then: accept {w.Peer}"
        : $"\n{e}");
}

/// <summary>
/// The playbacks of peers' titles open here, each reported to its peer until
/// stopped (MSFP playback section 3): at once, on a pause or a resume, and
/// every minute (the engine sends one report per 45 seconds at most). A real
/// host reports what its player says; this example has none, so the time
/// played stands for the position.
/// </summary>
internal sealed class Watches(MsfpEngine engine)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Watch> _open = new(StringComparer.Ordinal);

    public void Start(string id)
    {
        var w = new Watch();
        lock (_lock)
        {
            _open[id] = w;
        }

        engine.Progress(id, 0, false);
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                while (await timer.WaitForNextTickAsync(w.Stop.Token))
                {
                    if (Where(id) is not { } at)
                    {
                        return;
                    }

                    engine.Progress(id, at.PositionMs, at.Paused);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Says the viewer paused or resumed: told to the peer at once.</summary>
    public bool Pause(string id, bool paused)
    {
        if (Where(id) is not { } at)
        {
            return false;
        }

        lock (_lock)
        {
            if (_open.TryGetValue(id, out var w) && w.Paused != paused)
            {
                (w.PositionMs, w.Paused, w.Since) = (at.PositionMs, paused, DateTimeOffset.UtcNow);
            }
        }

        engine.Progress(id, at.PositionMs, paused);
        return true;
    }

    public void Stop(string id)
    {
        lock (_lock)
        {
            if (_open.Remove(id, out var w))
            {
                w.Stop.Cancel();
            }
        }
    }

    private (long PositionMs, bool Paused)? Where(string id)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(id, out var w))
            {
                return null;
            }

            return w.Paused ? (w.PositionMs, true)
                : (w.PositionMs + (long)(DateTimeOffset.UtcNow - w.Since).TotalMilliseconds, false);
        }
    }

    private sealed class Watch
    {
        public CancellationTokenSource Stop { get; } = new();

        public long PositionMs { get; set; }

        public bool Paused { get; set; }

        public DateTimeOffset Since { get; set; } = DateTimeOffset.UtcNow;
    }
}
