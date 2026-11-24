<img src="https://raw.githubusercontent.com/msfp-io/msfp-dotnet/main/logo.png" alt="" width="128" align="right">

# msfp-dotnet

The MSFP engine for .NET. [MSFP](https://github.com/msfp-io/msfp-spec), the
Media Server Federation Protocol, lets media servers share their libraries
with each other: one server's people browse and play titles another server
holds, through their own server. Two operators pair their servers by hand,
and each server trusts only the servers it paired with. Groups make that
easier among friends: a server joins a group with one code, then pairs
with each member who accepts, without codes.

This library runs the protocol inside your media server, the **host**. The
engine signs and checks every call, keeps the pairings, keys, addresses and
limits, and answers peers. Your server implements storage (or uses one of
the two shipped stores) and hands the engine the requests peers send it;
it writes none of the protocol itself.

- The protocol: [msfp-io/msfp-spec](https://github.com/msfp-io/msfp-spec),
  version 0 as revised by proposals 16 and 17, still a draft until version 1
  ([spec/v0](https://github.com/msfp-io/msfp-spec/tree/main/spec/v0)).
  0.8.0-preview and later speak the revised draft; 0.7.1-preview and
  before speak the draft before it, and servers on the two don't pair. The [changelog](CHANGELOG.md) says what hosts change.
- The engine's API in words: [api/](https://github.com/msfp-io/msfp-spec/tree/main/api)
  in the spec's repository, mainly
  [engine.md](https://github.com/msfp-io/msfp-spec/blob/main/api/engine.md)
  and [host.md](https://github.com/msfp-io/msfp-spec/blob/main/api/host.md).
  This library follows the same design as the Go engine,
  [msfp-go](https://github.com/msfp-io/msfp-go), in .NET's own style.

**Status: 0.8.0-preview**, published on NuGet as previews (`Msfp.Engine`,
`Msfp.Protocol`, `Msfp.Describe`); before 1.0 the API may change. The protocol layer is complete
and passes every test vector of MSFP version 0. The engine pairs (keys,
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
sheets), for films, series and music. And groups (MSFP's groups part):
founding one, inviting with codes, members' requests and answers, pairing
through the group, roles, bans, leaving, dissolving, offers and pools per
group, and what this server shows from each group. And relations (MSFP
core section 7.7): a link lasts while the individual relation or a group
holds it.

## Packages

| Package | What it is |
|---------|------------|
| `Msfp.Engine` (namespace `Msfp`) | the engine: `MsfpEngine.OpenAsync`, its HTTP entry, pairing; the stores `MemoryStore` and `JsonFileStore` (namespace `Msfp.Stores`) |
| `Msfp.Describe` (namespace `Msfp.Describe`) | a media file as a copy, with ffprobe: the media type sniffed, ffprobe run safely, codecs strings from the codec records, the copy shaped (msfp-go's `describe`) |
| `Msfp.Protocol` (namespace `Msfp.Protocol`) | the protocol's pieces: keys, pairing codes, signatures, strict JSON, statements, title keys, the media checks |

All three have the same version. The only dependency beyond .NET is
[BouncyCastle.Cryptography](https://www.nuget.org/packages/BouncyCastle.Cryptography)
for Ed25519 and X25519, which .NET doesn't have, and the engine's use of
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
logs. `CodeTextAsync` gives a live code's text again, to send once more. Every method throws `MsfpException`, whose `Code` says why
(`MsfpError.CodeExpired`, `MsfpError.AlreadyPeer`…).

## Sharing and playing

A host that shares gives the engine its libraries and playback
(`MsfpConfig.Catalog`, `Playback`, `Media`: `ICatalog`, `IPlayback`,
`IMedia`): each film, episode and extra with its copies described as
facts: the file's media type, the MSFP registry's codec and subtitle
tokens, RFC 6381 codecs strings, H.273 colour, exact values, the layout
of its previews; its copies in its order of preference. A copy's ID in
the catalog is the host's handle of it: it names the copy alone, not
where its file is. Each call given a handle for a peer is given the
peer and the host's libraries offered to it (`PeerRef.Libraries`): the
host resolves the handle to a file in one of them, and throws
`MsfpError.NotFound` when it is in none. The engine
serves a copy's file by byte ranges itself, against the size and etag
the catalog gave, serves exactly the names a playback's answer opened,
and checks or rebuilds what leaves (playlists, served the same on every
fetch, their missing attributes filled from the copy's facts;
subtitles, fonts; preview sheets re-encoded). It maps no names: a media
server that names things otherwise maps them, and a value MSFP doesn't
know is sent and kept as it is. The host's server chooses nothing for a
peer: its play names the copy and the audio, else the first copy and
the file's default audio, and the engine decides what is served
(`StartRequest.Served`); `MsfpConfig.Encoders` says what its transcoder
makes (codecs, ranges, what each segment format carries). The host mounts the stream route of peers'
tickets beside the federation route:

```csharp
app.Map("/api/v1/stream/{**rest}", async (HttpContext ctx) =>
{
    var outcome = await engine.HandleStreamAsync(new MsfpRequest { /* as above, Body = Stream.Null */ },
        new AspNetSink(ctx.Response), ctx.RequestAborted);
    if (outcome == StreamOutcome.Fallback) { /* a ticket of the host's own viewers: its own route */ }
    else if (outcome == StreamOutcome.NotFound) { ctx.Response.StatusCode = 404; }
});
```

Give it every method: `HEAD` and the others get a 405 on every media URL
form, whatever the ticket (live, ended or never given), so `Fallback`
comes for `GET`s only there; a host that answers its own clients' `HEAD`
does so on a route of its own. The sink writes the
status and the headers the engine gives, and adds `Accept-Ranges: bytes`
to a 200 or a 206, the file's ETag it is given (`IETagSink`) and
`Allow: GET` to a 405 (the example's `AspNetSink` does).

The operator offers libraries with `SetOffersAsync` and sets limits
with `SetLimitsAsync`; the host calls `CatalogChanged()` when its titles
change. An episode keyed by its TMDB episode ID
(`series/tmdb/<id>/e<episode id>`) needs its place in TMDB's numbering,
`VideoTitle.Place` (`Season`, `Episode`; MSFP video section 4.1): without
one it isn't sent, and a new place is a change of the title. A peer's such
episodes come with theirs. To read a peer's titles: `TheirLibrariesAsync`, `SetWantsAsync`,
then `RunAsync` reads them on schedule (or `ReadNowAsync`) into the
store, where the host reads them; `PlayableAsync` says whether they can
be played now. A viewer plays with `PlayAsync` (to watch, to download, or
to let a media server scan the file: `PlayRequest.Purpose`). The host has
every copy's facts in the catalog and knows its device: it chooses the
copy (`VersionId`), its audio (`AudioTrack`) and its subtitles, and asks
a delivery (the file as it is, a remux to `video/mp2t` or `video/mp4`
segments, a transcode, a stream of which may be `copy`, to SDR or
keeping the range), or none for the baseline every server plays, H.264
and AAC in HLS; nothing of the device is sent, and every media URL is
built from the peer's ticket: none a peer wrote is fetched.
`Rfc6381.Read` reads a copy's codecs strings. The host serves the media
at its own route through `FetchAsync` (`MediaPath.TryParse` reads its
URL), and reports with `Progress` and `StopAsync` (a download's late
reports with `ReportOfflineAsync`); `PreviewSheetAsync` fetches a
copy's seek-bar previews when someone opens it, never ahead, and
`TrackLyricsAsync` a track's lyrics.

Two things are off until the operator turns them on: **local titles**
(those no public database knows: `SetLocalAsync` per pairing and
`SetGroupLocalAsync` per group; their keys made with `OpaqueKeyPart`,
their images given by `IImages`) and **downloads and scans** (`Limits`:
`Downloads`, `DownloadsPerDay`, `DownloadKbps`, `ScansPerDay`, and
`ScanBytes`, a pool's scan cap, stated in each scan's answer: 4 MiB
unless you say more, 32 MiB suggested). Theme songs and theme videos are extras like the others:
whether you offer them is your operator's choice. A download's late
reports count for 30 days, across restarts: the shipped
stores keep downloads (`IDownloadsTx`); a store of your own implements it
too, or they last until a restart. A download's pace follows
`MsfpConfig.TimeProvider` and waits with its timers.

Preview sheets are re-encoded, so that only their pixels pass: those a
peer sends before they reach a viewer (MSFP video section 7), and the
host's own before they leave (video section 6.2), each checked against
its layout first; a peer's JPEG image of a local title too. .NET has no
JPEG codec, so the engine has no default `ISheetEncoder` (msfp-go's is
`JPEGSheets`): give one (ImageSharp, SkiaSharp, ffmpeg as the example
does, the host's own). Without one, previews are left out both ways: the
host's copies leave without their `PreviewLayout` (so its plays open no
sheets, a `previews` call gets 404), peers' sheets aren't fetched
(`RemotePlayback.Sheets` is 0, `PreviewSheetAsync` is NotSupported), and only
a peer's PNG images can be fetched (the engine re-encodes PNGs itself).

The engine hands headers to the host through its sink only:
`IResponseSink.Start` gives the status, `Content-Type`, `Content-Length`
and `Content-Range`, and `IETagSink.SetETag` the file's ETag; the host's
sink writes them (and `Accept-Ranges`, `Allow`) on its own response type,
so the engine is the same under ASP.NET Core, a plugin's controller or a
test.

## When a peer's titles can't be read

A reading of a peer's titles that fails (`ReadNowAsync`, the scheduled
readings, `TheirLibrariesAsync`, `SetWantsAsync`) keeps what was read
before, and says why in `PeerView.Reading`: when it ended, and a code of
`ReadFailure`, a closed set, the same in msfp-go, for your pages to name
in your own words. It never holds an address, a URL, a file name or
anything the peer wrote; the exception has the detail, for the logs. It
is kept in memory: null until a reading ends after the start.
`CatalogReadEnded` carries the same code. A reading your own token
cancels isn't kept; a scheduled one past its 5 minutes is a `timeout`.

| Code | The peer's titles weren't read because |
|------|-----------------------------------------|
| `unreachable` | it didn't answer (no route, refused, a name that doesn't resolve, TLS failing) |
| `timeout` | it, or the reading, took too long |
| `certificate-refused` | its certificate is neither publicly trusted nor one it named |
| `refused-unsigned` | an answer not signed by its key: it doesn't hear this server's key (it ended the pairing, or lost its records), or something else answers at its address |
| `refused` | it refused, signed; `Refusal` is the problem type (`not-found`, `rate-limited`, `server-busy`…, MSFP core section 8.2) |
| `not-paired` | it isn't paired with this server, or must pair again |
| `suspended` | it revoked its key |
| `uncallable` | it can't be called |
| `no-common-version` | the two have no core version in common (`PeerView.Core` is -1) |
| `catalog-off` | the catalog part isn't on between the two |
| `older-draft` | it runs an engine of the draft before this revision of version 0 (0.7.1-preview and before): it must be updated |
| `answer-not-understood` | an answer that couldn't be read (coded, too large, not JSON, not of the call's form, or breaking the catalog's rules) |
| `internal` | this server's own failure (its store, say) |

Both drafts announce core 0 with the same parts, so the hello can't tell
them apart; the reading does. `older-draft` needs three marks at once:
every library of the libraries answer carries a `kind`, every title of a
page carries a `kind` matching its key (the old draft's required
members, gone from this one), and that page holds a film, an episode or
a track, none with `copies` (required now, unknown then). A title
without copies alone is an owner's mistake, skipped as before. While a
peer's libraries carry kinds, its readings are whole lists, whose titles
tell; this draft's libraries carry none, so it costs them nothing.

## Groups

A group is a list of signed events every member keeps; the engine checks
them, seals the members' requests to each other, and pairs through the
group (`IGroupsTx`, which both shipped stores implement, keeps them):

```csharp
var family = await engine.CreateGroupAsync("Family");                 // this server its owner
var (code, _) = await engine.MakeCodeWithAsync(new CodeOptions { Group = family.Id, Uses = 3 });
await engine.AcceptRequestAsync(family.Id, mail);                     // a member's request (GroupRequestWaiting)
await engine.SetGroupOffersAsync(family.Id, ["films"]);               // what every link through it is offered
```

A server redeems a group's code as any other (`RedeemCodeAsync`); one
already paired with the code's maker joins through the pairing, and the
maker's operator answers with `AcceptJoinAsync` or `RefuseJoinAsync`.
`GroupsAsync` and `GroupAsync` show members, roles, bans and requests;
`SetAdminAsync`, `BanAsync`, `GiveGroupAsync`, `LeaveGroupAsync`,
`DissolveGroupAsync` and `BlockAsync` act on them. A link's offers are
what it has by hand and what each of its groups gives it; a playback
counts against the first pool with room (`SetGroupPoolAsync`). What
this server shows from a group is one choice for the whole group:
`GroupShowsAsync` lists each member's libraries and `SetGroupShowsAsync`
hides some.

A link lasts while one relation holds it (MSFP core section 7.7): the
individual one, made by hand with a code, or a group both servers are
in. `AskIndividualAsync` asks a peer linked through a group for the
individual relation (or accepts its ask), `EndIndividualAsync` ends it
(the link stays while a group holds it), and `BlockPeerAsync` ends the
link and keeps the peer away from every group.

## The example

[examples/ShareFolder](examples/ShareFolder) is a small host, msfp-go's
`examples/share-folder` in C#: Kestrel with a self-signed certificate it
pins, its records in JSON files, typed commands in place of a panel, and
a folder of films and series shared with its peers
([Folder.cs](examples/ShareFolder/Folder.cs) is the engine's `ICatalog`,
`IImages`, `IPlayback` and `IMedia`). The folder holds films and series
named with their TMDB (or TheTVDB) IDs, `Title (Year) [tmdbid-949].mkv`
or a folder of their own, episodes `S01E02` (by their TMDB episode ID when
the name has one), extras in `Trailers/` and `Extras/`, local titles (no
ID, an image beside them), `.vtt` subtitles, HLS copies
(`<same name>.hls/`, H.264 and AAC: the baseline a play without a
delivery gets) and previews (`<same name>.trickplay/`) made beforehand;
nothing is transcoded. Copies are described from their files (the
media type from their first bytes, an etag from their content; with
`ffprobe` when it is installed, the Msfp.Describe package gives the rest:
codecs strings, H.273 colour, Dolby Vision, the audio and subtitle tracks
inside the file by stream index, chapters); with `ffmpeg`, it re-encodes preview
sheets and JPEG images (its `ISheetEncoder`). The comment at the top of
Folder.cs shows the layout. Run two of them on one machine:

```sh
cd examples/ShareFolder
dotnet run -- --address https://127.0.0.1:8441 --data a --folder ~/videos --loopback
dotnet run -- --address https://127.0.0.1:8442 --data b --loopback
```

`--loopback` lets peers on the same machine be called (the engine refuses
local addresses otherwise). Type `code` in the first and `redeem <code>` in
the second, compare the fingerprints both print, and `accept <id>` in the
first. Then `offer <id>` in the first shares the folder (`local <id> on`
its local titles too); `want <id>` in the second reads them, `titles
<id>` lists them, and `play <id> <key>` prints a URL that `curl -k` or
`ffplay -tls_verify 0` plays through the second
server; `pause <playback>`, `resume <playback>` and `stop <playback>`
follow, and until the stop the example reports the playback every
minute, as a host must (a peer ends a playback it hears nothing of for 2
minutes). `limits <id> 2 1 1 3 8000 2` in the first allows the second 2
streams, 1 transcode, 1 download at once, 3 a day at 8,000 kbit/s, and 2
scans a day; `download <id> <key>` in the second then prints the URL of
the file to save, `late <download> <ms>` sends a late report of it, and
`previews <id> <key>` fetches a film's previews. The comment at the top
of [Program.cs](examples/ShareFolder/Program.cs) lists every command.

Groups work the same way: `group-create <name>` in the first, `invite
<group>` for a code the others redeem, `groups` to see the members and the
requests waiting, and `pair <group> <mail>` to accept one. Relations:
`individual <id>`, `end-individual <id>` and `block <id>`.

## Versions

The library follows [semantic versioning](https://semver.org). Its
major.minor version is shared with the other MSFP engine: msfp-dotnet
0.8.x-preview follows the same API design as msfp-go 0.8.x (the spec's api/).
Patch versions are each library's own. The library's version is separate
from the protocol's: from 0.4.0-preview it speaks MSFP version 0, groups
included; 0.2.0-preview and before speak the same protocol numbered 1, and
the two don't talk. From 0.8.0-preview it speaks version 0 as revised
(proposals 16 and 17), which doesn't talk with 0.7.1-preview and before either.

## Testing

```sh
dotnet test
```

The engine's tests run whole servers against each other in one process,
and once over real TLS. The example host passes msfp-spec's
[conformance suite](https://github.com/msfp-io/msfp-spec/tree/main/conformance)
as msfp-go's does, run with `--code2`, `--load` and `--end`, and
`--group-code` for the groups checks; the checks a folder of films can't
answer (a title in several libraries, preview sheets, PGS subtitles) are
skipped. The protocol's test vectors under
[tests/Msfp.Protocol.Tests/testdata](tests/Msfp.Protocol.Tests/testdata)
come from the spec's
[vectors/](https://github.com/msfp-io/msfp-spec/tree/main/vectors), copied
as they are: `testdata/v0/` is the spec's `vectors/v0/`, and
`testdata/test-draft-strict.json` its `vectors/history/test-draft-strict.json`.
To refresh them, copy those files from a checkout of msfp-spec and run the
tests:

```sh
cp ../msfp-spec/vectors/v0/*.json tests/Msfp.Protocol.Tests/testdata/v0/
cp ../msfp-spec/vectors/history/test-draft-strict.json tests/Msfp.Protocol.Tests/testdata/
dotnet test
```

## Security

[SECURITY.md](SECURITY.md) says how to report a vulnerability, and what a
host should know of what crosses from peers.

## How it's made

msfp-dotnet is designed by its maintainer, who makes the architecture,
security and product decisions, and written by Claude (Anthropic's AI);
Claude's commits carry a `Co-Authored-By: Claude` trailer. The code is
checked by its tests; it hasn't had an independent security review yet.

## License

[MIT](LICENSE).
