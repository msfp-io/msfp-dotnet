# msfp-dotnet

The MSFP engine for .NET. [MSFP](https://github.com/msfp-io/msfp-spec), the
Media Server Federation Protocol, lets media servers share their libraries
with each other: one server's people browse and play titles another server
holds, through their own server. Two operators pair their servers by hand,
and each server trusts only the servers it paired with.

This library runs the protocol inside your media server, the **host**. The
engine signs and checks every call, keeps the pairings, keys, addresses and
limits, and answers peers. Your server implements storage (or uses one of
the two shipped stores) and hands the engine the requests peers send it;
it writes none of the protocol itself.

- The protocol: [msfp-io/msfp-spec](https://github.com/msfp-io/msfp-spec),
  version 1 ([spec/v1](https://github.com/msfp-io/msfp-spec/tree/main/spec/v1)).
- The engine's API in words: [api/](https://github.com/msfp-io/msfp-spec/tree/main/api)
  in the spec's repository, mainly
  [engine.md](https://github.com/msfp-io/msfp-spec/blob/main/api/engine.md)
  and [host.md](https://github.com/msfp-io/msfp-spec/blob/main/api/host.md).
  This library follows the same design as the Go engine,
  [msfp-go](https://github.com/msfp-io/msfp-go), in .NET's own style.

**Status: 0.2.0-preview, not released.** The protocol layer is complete
and passes every test vector of MSFP version 1. The engine pairs (keys,
codes, `pair`, `hello`, accepting and ending, signed answers, limits),
rotates and revokes its key (with its next key, or replaced without it),
follows its peers' rotations and revocations, moves to a new address
(`SetAddressAsync`) and follows its peers' moves, runs as a server that
can't be called (`SetUncallableAsync`) or pairs with one, backs off from
peers that don't answer, and serves its discovery document. It shares
and plays too: the catalog both ways (libraries offered and wanted, titles
read whole or as changes, its own change log or the host's), playback both
ways (play, progress, stop, the pairing's limits), the media of peers'
tickets and the relay of a peer's media (HLS, WebVTT, PGS, preview
sheets), for films, series and music.

## Packages

| Package | What it is |
|---------|------------|
| `Msfp.Engine` (namespace `Msfp`) | the engine: `MsfpEngine.OpenAsync`, its HTTP entry, pairing; the stores `MemoryStore` and `JsonFileStore` (namespace `Msfp.Stores`) |
| `Msfp.Protocol` (namespace `Msfp.Protocol`) | the protocol's pieces: keys, pairing codes, signatures, strict JSON, statements, title keys, the media checks |

Both have the same version. The only dependency beyond .NET is
[BouncyCastle.Cryptography](https://www.nuget.org/packages/BouncyCastle.Cryptography)
for Ed25519, which .NET doesn't have, and the engine's use of
`Microsoft.Extensions.Logging.Abstractions`. It needs .NET 10.

## The minimum

A server that only pairs needs two things: its **address** (where peers
call it, `https://` only) and a **store** for the engine's records. Then
it gives the engine the requests that come to its federation routes:

```csharp
using Microsoft.AspNetCore.Http.Features;
using Msfp;
using Msfp.Stores;

var app = WebApplication.CreateSlimBuilder(args).Build();

using var store = JsonFileStore.Open("data/msfp"); // its records, sealed with a key file
using var engine = await MsfpEngine.OpenAsync(new MsfpConfig
{
    Address = "https://media.example.org", // where peers call it
    Store = store,
});

app.MapPost("/api/v1/federation/{call}", async (HttpContext ctx) =>
{
    var res = await engine.HandleFederationAsync(new MsfpRequest
    {
        Method = ctx.Request.Method,
        Target = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget, // as sent: the signature covers it
        Headers = new AspNetHeaders(ctx.Request.Headers),
        Body = ctx.Request.Body,
        ClientAddress = ctx.Connection.RemoteIpAddress,
    }, ctx.RequestAborted);
    ctx.Response.StatusCode = res.Status;
    foreach (var (name, value) in res.Headers)
    {
        ctx.Response.Headers.Append(name, value);
    }
    await ctx.Response.Body.WriteAsync(res.Body, ctx.RequestAborted);
});

app.Run();
```

`AspNetHeaders` is a three-line adapter from ASP.NET Core's headers to
`IHeaderReader` (see the example). Mount the route on the raw path, with no
authentication in front and no compression of its answers.

The entry is neutral on purpose: `MsfpRequest` and `MsfpResponse` are plain
objects, so an MVC controller (as in a Jellyfin plugin), plain Kestrel,
`HttpListener` or a test can each carry them. An answer whose
`PlainNotFound` is true is the unsigned 404 a stranger gets: the host may
send its own not-found page in its place.

Pairing is then three calls, from your server's panel:

```csharp
var (code, _) = await engine.MakeCodeAsync("for Alice"); // send it to the other operator
var peer = await engine.RedeemCodeAsync(pasted);         // a code the other operator sent
await engine.AcceptAsync(id);                            // once both compared fingerprints
```

`MsfpConfig.Notifier` tells your server what happened, for its panel and
logs. Every method throws `MsfpException`, whose `Code` says why
(`MsfpError.CodeExpired`, `MsfpError.AlreadyPeer`…).

## Sharing and playing

A host that shares gives the engine its libraries and playback
(`MsfpConfig.Catalog`, `Playback`, `Media`: `ICatalog`, `IPlayback`,
`IMedia`), and mounts the stream route of peers' tickets beside the
federation route:

```csharp
app.Map("/api/v1/stream/{**rest}", async (HttpContext ctx) =>
{
    var outcome = await engine.HandleStreamAsync(new MsfpRequest { /* as above, Body = Stream.Null */ },
        new AspNetSink(ctx.Response), ctx.RequestAborted);
    if (outcome == StreamOutcome.Fallback) { /* a ticket of the host's own viewers: its own route */ }
    else if (outcome == StreamOutcome.NotFound) { ctx.Response.StatusCode = 404; }
});
```

The operator offers libraries with `SetOffersAsync` and sets limits
with `SetLimitsAsync`; the host calls `CatalogChanged()` when its titles
change. An episode keyed by its TMDB episode ID
(`series/tmdb/<id>/e<episode id>`) needs its place in TMDB's numbering,
`VideoTitle.Place` (`Season`, `Episode`; MSFP video section 4.1): without
one it isn't sent, and a new place is a change of the title. A peer's such
episodes come with theirs. To read a peer's titles: `TheirLibrariesAsync`, `SetWantsAsync`,
then `RunAsync` reads them on schedule (or `ReadNowAsync`) into the
store, where the host reads them; `PlayableAsync` says whether they can
be played now. A viewer plays with `PlayAsync`, the host serves the media
at its own route through `FetchAsync` (`MediaPath.TryParse` reads its
URL), and reports with `Progress` and `StopAsync`.

Preview sheets a peer sends are re-encoded before they reach a viewer
(MSFP video section 7). .NET has no JPEG codec, so the engine has no
default `ISheetEncoder`: give one (ImageSharp, SkiaSharp, the host's
own), or previews of peers' titles are left out.

## The example

[examples/ShareFolder](examples/ShareFolder) is a small host, msfp-go's
`examples/share-folder` in C#: Kestrel with a self-signed certificate it
pins, its records in JSON files, typed commands in place of a panel, and
a folder of films shared with its peers ([Folder.cs](examples/ShareFolder/Folder.cs)
is the engine's `ICatalog`, `IPlayback` and `IMedia`). A film is a file
named with its TMDB ID, `Title (Year) [tmdbid-949].mp4`, optionally with
an HLS copy made beforehand beside it (`<same name>.hls/master.m3u8`) and
WebVTT subtitles (`<same name>.<language>.vtt`); nothing is transcoded.
Run two of them on one machine:

```sh
cd examples/ShareFolder
dotnet run -- --address https://127.0.0.1:8441 --data a --films ~/films --loopback
dotnet run -- --address https://127.0.0.1:8442 --data b --loopback
```

`--loopback` lets peers on the same machine be called (the engine refuses
local addresses otherwise). Type `code` in the first and `redeem <code>` in
the second, compare the fingerprints both print, and `accept <id>` in the
first. Then `offer <id>` in the first shares the films; `want <id>` in the
second reads them, `titles <id>` lists them, and `play <id> <key>` prints a
URL that `curl -k` or `ffplay -tls_verify 0` plays through the second
server. The comment at the top of
[Program.cs](examples/ShareFolder/Program.cs) lists every command.

## Versions

The library follows [semantic versioning](https://semver.org). Its
major.minor version is shared with the other MSFP engines: msfp-dotnet
0.2.x will follow the same API design as msfp-go 0.2.x (the spec's api/).
Patch versions are each library's own. The library's version is separate
from the protocol's: it speaks MSFP version 1.

## Testing

```sh
dotnet test
```

The engine's tests run whole servers against each other in one process,
and once over real TLS. The example host passes msfp-spec's
[conformance suite](https://github.com/msfp-io/msfp-spec/tree/main/conformance)
as msfp-go's does: 118 checks passed, 0 failed, 3 skipped (a title in
several libraries, preview sheets and PGS subtitles, which a folder of
films doesn't have), with `--code2`, `--load` and `--end`. The protocol's test vectors under
[tests/Msfp.Protocol.Tests/testdata](tests/Msfp.Protocol.Tests/testdata)
come from the spec's
[vectors/](https://github.com/msfp-io/msfp-spec/tree/main/vectors), copied
as they are: `testdata/v1/` is the spec's `vectors/v1/`, and
`testdata/v0-strict.json` its `vectors/v0-strict.json`. To refresh them,
copy those files from a checkout of msfp-spec and run the tests:

```sh
cp ../msfp-spec/vectors/v1/*.json tests/Msfp.Protocol.Tests/testdata/v1/
cp ../msfp-spec/vectors/v0-strict.json tests/Msfp.Protocol.Tests/testdata/
dotnet test
```

## How it's made

msfp-dotnet is designed by its maintainer, who makes the architecture,
security and product decisions, and written by Claude (Anthropic's AI);
Claude's commits carry a `Co-Authored-By: Claude` trailer. The code is
checked by its tests; it hasn't had an independent security review yet.

## License

[MIT](LICENSE).
