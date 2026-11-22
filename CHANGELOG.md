# Changelog

## Unreleased: the revision of version 0 (proposal 16)

MSFP version 0 is revised, and stays a draft until version 1: titles
carry their copies, plays ask a delivery and a purpose (downloads and
scans), extras and local titles are shared, and a copy's file is served
by byte ranges. Servers on the draft before (0.7.1-preview and before)
and on the revised one don't pair with each other: move every server at
once. The migration guide at the end of this section says what hosts
change.

### Changed

- Titles carry their copies: `VideoTitle.Copies` and `MusicTitle.Copies`
  are `Protocol.Copy` and `MusicCopy`, each copy's ID the host's handle
  (the engine sends the version ID `play` gives). `VideoTitle.Audio`,
  `Subtitles`, `Pictures`, `Video` and `Picture` are gone: `BestCopy`,
  `AudioLanguages` and `SubtitleLanguages` work them out from the copies.
  `MusicTitle` has music's new description (`SortName`, `ReleaseType`,
  `Compilation`, `Date`, `OriginalDate`, `Genres`, `Image`), no `Year` or
  `DurationMs`. The stores write copies with the specification's names,
  as the Go engine does.
- Playback and media (step B2): `play` takes the title's copies as the
  catalog sends them; `ICatalog.CopiesAsync` and the engine's `Copy` are
  gone, and `IPlayback.DescribeAsync` gives only what the catalog doesn't
  say (`CopyInfo`: previews' layout, fonts, tracks not served apart).
  `StartRequest` carries `Purpose`, `Delivery`, `Container`, `Transcode`,
  `AudioRenditions` and a music `Queue`; `Started` its `Queue` (no
  `Segments`: copies carry them); `IPlayback.ProgressAsync` takes a
  `Report` (with `At`, `Title`, `Stop`, `Late`). `MediaPath` has
  `MediaKind.Ass`, `MediaKind.Font`, `Whole`, `Font`, `Entry` (a queue
  entry's stream) and `OfCopy`; for `MediaKind.File` the engine resolves
  the range against the copy's size and writes the status itself, the
  ETag through `IETagSink`. `Limits` gains `Downloads`,
  `DownloadsPerDay`, `DownloadKbps`, `ScansPerDay`. `Capabilities` is
  `Protocol.PlayCapabilities`. The remote `Track` has no `Label` (and
  `Format`, `External`, `Text`, `Ass`); `RemotePlayback` has no `Segments`
  and gains `Purpose`, `Delivery`, `Etag`, `Size`, `CopyChanged`,
  `BurnedTrack`, `Fonts`, `Queue`. `PlaybackView` and `PlaybackStarted`
  carry the purpose.
- Media answers `HEAD` and other methods with `405` (a host's sink adds
  `Allow: GET`); the file by byte ranges with its exact size and `ETag`;
  playlists only complete, their renditions the copy's audio tracks.
- `titles` pages are bounded by size too (5,000 titles, 3 MiB), `next`
  set whenever titles remain; copies are cleaned before they leave (names
  and track titles only when they are labels).
- The owning side re-encodes the host's preview sheets with the
  `ISheetEncoder` before they leave, checked against the layout (one
  outside it isn't sent); without a `SheetEncoder`, it serves no sheets
  (plays answer without them, `previews` gets 404). It serves each
  playlist the same on every fetch under a ticket (the first kept, at
  most 16 of 4 MiB), and answers `HEAD` and other methods with `405` on
  every media URL form, whatever the ticket (live, ended or never given:
  `StreamOutcome.Fallback` comes for `GET` only there).
- A scan's cap counts the bytes sent: a range is held against it while
  it is served, and what wasn't sent is given back. A relayed file that
  ends short of its length fails on the viewer's side.
- A download's pace follows `MsfpConfig.TimeProvider`, and waits with
  its timers: a test's clock paces without waiting.
- `play` reads a music queue from the request as read once:
  `Playback.ReadPlay` has an overload that gives the request's object.
- Example: one library, `folder`, of films and series (`--films` is now
  `--folder`); extras, local titles with images, previews, copies
  described with ffprobe, ffmpeg as its `ISheetEncoder`; commands
  `local`, `group-local`, `limits`, `download`, `late`, `previews`.

### Added

- Extras (`VideoTitle.ExtraType`, `Name`), sent only with their title,
  and gone with it; the `tvdb` and `local` keys; a local title's
  description (`Name`, `Year`, `Image`).
- `SetLocalAsync` and `SetGroupLocalAsync` (`PeerView.Local`,
  `GroupView.Local`): local titles shared, per pairing and per group, off
  by default.
- Downloads and scans (`Playback.PurposeDownload`, `PurposeScan`): a
  download's ticket of 24 hours without the idle rule, its reads paced at
  `downloadKbps`; a scan's of 10 minutes, its reads within 32 MiB.
  `ReportOfflineAsync` sends a download's late reports; `RestartAsync`
  reads a file on under a new playback of the same etag
  (`MsfpError.CopyChanged` otherwise); `PlayingTrack` names the queued
  track playing. `MsfpError.DeliveryRefused`.
- The `previews` call (`PreviewsAsync`, sheets fetched before a first
  play) and the `lyrics` call (`TrackLyricsAsync`; a host's `ILyrics`
  gives them). `IETagSink`: a sink given the file's ETag.
- The `image` call: a host's `IImages` gives a local title's image,
  served under a ticket of its own; `TitleImageAsync` asks a peer for
  one, checked and re-encoded. `OpaqueKeyPart` makes opaque parts of
  keys.
- `IDownloadsTx` and `DownloadRecord`: downloads kept in the store for
  their late reports, both sides, so a restart keeps them; the shipped
  stores implement it (a `state.json` from before reads as none kept),
  and `DeletePeerAsync` removes a peer's.
- `SECURITY.md`: how to report a vulnerability, and what a host should
  know of fonts and ASS from peers, images and sheets (and the
  `ISheetEncoder` they need), local titles' images, downloads, scan
  access, `HEAD` on media URLs.

### Migration guide

**A host that shares (the owning side):**

1. `ICatalog.TitlesAsync` gives each film, episode and extra its copies
   (`VideoTitle.Copies`, `Protocol.Copy`; a music track's `MusicCopy`),
   best first: the copy's ID is your handle (it never leaves), its
   `DurationMs`, `Container`, exact `Size`, an `ETag` that changes when
   and only when its bytes change, its `AudioTracks` (may be empty), and
   what else your media server knows. Series and seasons carry none.
   Remove `ICatalog.CopiesAsync`.
2. `IPlayback.DescribeAsync` gives only `CopyInfo`: the previews' layout,
   the fonts of ASS tracks, the tracks you can't serve apart.
3. `IPlayback.StartAsync` reads `Purpose`, `Delivery`, `Container`,
   `Transcode`, `AudioRenditions` and a music `Queue` from the
   `StartRequest`; it throws `MsfpError.DeliveryRefused` for a delivery
   it won't make, and fills `Started.Queue` for a queue.
4. `IPlayback.ProgressAsync` takes a `Report`: `At`, `Title`, `Stop`, and
   `Late` for a download's late report after its playback ended.
5. `IMedia.ServeAsync`: for `MediaKind.File` the engine gives the exact
   range and writes the status and headers itself (the ETag through your
   sink's `IETagSink`); serve the bytes. New paths: `MediaKind.Ass`,
   `MediaKind.Font`, `Whole` (a whole text track), `Entry` (a queue
   entry's stream), and `OfCopy` (a sheet under a previews ticket: the
   handle is the copy's). Playlists are complete (`VOD`,
   `#EXT-X-ENDLIST`), their audio renditions named by the copy's track
   IDs. Sheets are JPEGs within the layout; the engine re-encodes them
   with `MsfpConfig.SheetEncoder`, which you give (.NET has no JPEG
   codec), or no sheets are served.
6. Extras: titles of kind `extra`, keyed `<film, series or season
   key>/x/<opaque>`, with `ExtraType` and an optional `Name`. Leave theme
   songs and theme videos out.
7. Local titles: `movie/local/<opaque>`, `series/local/<opaque>`, with
   `Name`, `Year` and `Image`; make `<opaque>` with `OpaqueKeyPart`.
   Implement `IImages` to give their images. The operator shares them per
   pairing (`SetLocalAsync`) and per group (`SetGroupLocalAsync`); show
   the switches in your panel, off by default.
8. Downloads and scans: `Limits` gains `Downloads`, `DownloadsPerDay`,
   `DownloadKbps` and `ScansPerDay`, all 0 (none) by default: show them
   in your panel.
9. A store of your own implements `IDownloadsTx`, or downloads' late
   reports don't survive a restart.
10. Your own clients' `HEAD` on media: answer it on a route of your own;
    the engine answers `405` on its stream route, your tickets there
    included.

**A host that plays peers' titles (the viewer's side):**

1. Read copies from the titles kept (`BestCopy`, `AudioLanguages`,
   `SubtitleLanguages` replace `Audio`, `Subtitles`, `Pictures`,
   `Video`, `Picture`); build your library from them, never by reading a
   peer's file.
2. `PlayRequest` gains `Purpose`, `Delivery`, `Container`, `Transcode`,
   `AudioRenditions` and `Queue`; on `MsfpError.DeliveryRefused`, ask
   again with another delivery, or none.
3. `RemotePlayback` gains `Purpose`, `Delivery`, `Etag`, `Size`,
   `CopyChanged`, `BurnedTrack`, `Fonts` and `Queue`; `Segments` is gone
   (the copy carries them), and so is a `Track`'s `Label`.
4. To present a copy as a file of your own: answer your clients' `HEAD`
   from the catalog (its size and etag); when a read fails,
   `RestartAsync` reads on under a new playback of the same etag
   (`MsfpError.CopyChanged` otherwise).
5. Downloads: send their late reports with `ReportOfflineAsync`, for 30
   days. Previews before a first play: `PreviewsAsync`; lyrics:
   `TrackLyricsAsync`; local titles' images: `TitleImageAsync`, shown as
   the owner's. Music: `PlayingTrack` names the queued track playing.
   Give a `SheetEncoder` for previews and JPEG images to cross.

## v0.7.1-preview (2026-11-21)

msfp-go v0.7.1 in .NET.

### Added

- `CodeTextAsync`: a live code's text again, to copy or send once more,
  built with this server's address and key now (as msfp-go v0.7.1).

## v0.7.0-preview (2026-11-21)

msfp-go v0.7.0 in .NET.

### Added

- What this server shows from a group is one choice for the whole group:
  everything its members offer it, but the libraries hidden with
  `SetGroupShowsAsync`; `GroupShowsAsync` lists each member's libraries
  and whether they're shown. What is read of a server is what it is shown
  individually (`SetWantsAsync`, while the individual relation holds) plus
  what its groups show of it.

- `PeerView.JoiningGroup`: a server waiting with one of our group's
  invites is a request to join that group, for the panel to show there.

### Changed

- Automatically accepting new members (`SetGroupAnswerAsync` true) accepts
  a request as soon as it arrives: the group's `waitDays` changes nothing
  (groups section 4.1). Requests that waited before it was turned on keep
  waiting for the operator.
- `SetWantsAsync` no longer counts for a server known only through a
  group: what its groups show does.


## v0.6.0-preview (2026-11-21)

msfp-go v0.6.0 in .NET.

Relations (MSFP core section 7.7, proposal 14): the operator manages why
two servers are linked, not the link. A link lasts while one relation
holds it: the individual relation, which both sides want, or a group. As
msfp-go.

### Added

- `individual` in `hello`: whether this side wants the individual
  relation. `AskIndividualAsync` asks a peer linked through a group for
  it, accepts its ask, or keeps a link a dissolution left to the
  operator; `EndIndividualAsync` ends it or withdraws the ask;
  `RefuseIndividualAsync` refuses an ask, privately. `PeerView` says
  `Individual`, `IndividualAsked`, `IndividualAsks` and `KeepChoice`;
  `RelationsChanged` tells the panel.
- `BlockPeerAsync`: ends a link whatever holds it, and keeps the peer away
  from every group.

### Changed

- A plain code gives the individual relation; a group's code gives the
  group relation only, so the inviter is no longer an individual
  pairing of the newcomer's.
- `LeaveGroupAsync(id, ask)`: the links only the group held end, but
  those in `ask`, asked for the individual relation first, which stay
  until their operators answer. They no longer become pairings by hand.
  Another member's `left` ends such a link on this side too.
- `keepPairings` on a dissolution leaves each link only the group held
  to each side's operator (`KeepChoice`), instead of making it a pairing
  by hand.
- Only a block keeps a server away from groups. `EndAsync` still records
  the keys for the signed 404, but a server ended so can be linked again
  through a group. Keys forgotten before this version aren't marked
  blocked: a server blocked before is no longer kept away from groups.
- Offers and limits of the individual relation count only while both
  sides want it.
- `ByHand` in `PeerView` is now `Individual`, under its former name.

### Example

- `individual`, `end-individual` and `block`.


## v0.5.0-preview (2026-11-21)

msfp-go v0.5.0 in .NET.

### Added

- "No way in" (groups section 6). `GroupView.NoWayIn` says when this
  server has no pairing through a group that has other members. Nothing
  reaches those members from here, and nothing reaches here from them.
  `GroupsCutByAsync(peer)` lists the groups that ending a pairing would
  cut off, so a panel can say so before it ends.
- A member back in (groups section 2). A member with no pairing through a
  group to a code's maker can use a new code for the group: by `pair`, or
  by `group-join` when the two are paired some other way. Nobody signs
  `joined`; once the maker accepts, the pairing gains the group, and
  events and mail flow again.

### Changed

- A request mail is refused with `MsfpError.NoWayIn` when this server has
  no pairing through the group, where before it was kept but could never
  leave. `RequestMemberAsync` throws that error, and any other error from
  its request, instead of only logging it.

### Fixed

- An owner event replayed without counting didn't take its `ownerSeq`, so
  every later owner event waited forever and the owner could no longer
  act in the group. It happened when, for example, the owner named an
  admin who was leaving at the same moment, with the `left` replayed
  first. It now takes its `ownerSeq` as any replayed event takes its `seq`
  (groups section 3.5).
- A disposed engine's `RunAsync` kept running when its host didn't cancel
  the token, failing every hour on the disposed store. A Jellyfin restart
  that runs out of time stopping the plugin did that. `Dispose` now ends
  `RunAsync`, which returns without an error.

### Example

- `groups` says when a group has no way in.

## v0.4.1-preview (2026-11-21)

### Fixed

- After a stolen key, a member's group events could stay missing on some
  members for up to 10 minutes (until the next `group-sync`), and every
  event after them with them: the `revoked` and `rotated` events were
  pushed while the peers' `revoke` and `rotate` calls were on their way,
  and a peer that still held this server suspended refused them. Now, as
  soon as a peer moves to this server's new key, the engine runs
  `group-sync` with it for each group they share, which pushes what it
  lacks. The same covers a plain rotation.

### Example

- `admin <group> <member>`, `ban <group> <member>` and `dissolve <group>`.

## v0.4.0-preview (2026-11-20)

The major.minor version is msfp-go's: this is msfp-go v0.4.0 in .NET
(v0.3.0 had no .NET counterpart of its own: 0.2.0-preview already
carried its `place`).

MSFP is renumbered version 0, a draft until version 1 (msfp-spec proposal
13): the engine speaks core 0 and every part at 0, signs with the labels
`msfp-0 …`, and makes codes `msfp-pair-0.…`. It doesn't talk to servers
of the former version 1. The vectors are the spec's `vectors/v0/`, and
`vectors/history/test-draft-strict.json` (in place of `v0-strict.json`).

- `Msfp.Protocol`: pairing codes have the layout of core section 5.1 of
  version 0, a flag byte after the expiry, and for a group's code the
  group's ID and name (`PairingCode.Group`, `PairingCode.GroupName`,
  `PairingCode.MaxGroupName`, `PairingCode.GroupIdSize`);
  `PairingCode.MaxLifetime` (30 days). `Versions.PartGroups`;
  `Agreement.On` is any version, 0 included.
- `Msfp.Protocol`, groups: `GroupEvents` (the event types, `Sign`,
  `Read`, `SealMail`, `ReadMail`, `PeekMail`, `OpenMail`, `MailKeyPair`,
  `MailId`, `JoinProof`, `CheckJoinProof`), `GroupEnvelope`,
  `GroupEvent`, `GroupMail`, `SealedContent`, `GroupSettings`,
  `GroupState` (`Successor`, `Seq`, `Highest`, `Have`, `IsAdmin`,
  `WasBanned`), `GroupMember`. Mail is sealed with HPKE (RFC 9180 base
  mode, DHKEM(X25519, HKDF-SHA256), HKDF-SHA256, AES-128-GCM).
- `Msfp.Engine`, groups (MSFP groups part): `CreateGroupAsync`,
  `GroupsAsync`, `GroupAsync`, `MakeCodeWithAsync` with `CodeOptions`
  (uses, lifetime, a group; `MaxCodeUses`), `AcceptRequestAsync`,
  `RefuseRequestAsync`, `LiftRefusalAsync`, `RequestMemberAsync`,
  `SetGroupAnswerAsync`, `SetGroupOffersAsync`, `GroupOffersAsync`,
  `SetGroupPoolAsync`, `SetAdminAsync`, `SetHeirAsync`, `GiveGroupAsync`,
  `SetGroupSettingsAsync`, `BanAsync`, `UnbanAsync`, `LeaveGroupAsync`
  (giving up a join too), `DissolveGroupAsync`, `BlockAsync`,
  `AcceptJoinAsync`, `RefuseJoinAsync`. The calls `group-sync`,
  `group-push`, `group-pair` and `group-join`; the part `groups`, on by
  default. A store keeps groups by implementing `IGroupsTx` (the shipped
  stores do). A link's offers and limits are what it has by hand and what
  each of its groups gives it; a playback counts against the first pool
  with room, and `libraries` tells the most generous.
- Records: `GroupRecord`, `GroupJoin`, `GroupPool`, `GroupRequest`,
  `StoredEvent`, `StoredMail`, `SentMail`; `PeerRecord` gains `Groups`,
  `NotByHand`, `GroupOffers`, `Pools` and `JoiningGroup`; `CodeRecord`,
  `Uses` and `Group`.
- Views: `GroupView` (with `Members`, `Banned`, `Requests`, `Joins`,
  `Joining`), `GroupMemberView` (`Refused`, `Asked`), `GroupBanView`,
  `GroupRequestView`, `GroupJoinView`; `PeerView` gains `Groups` and
  `ByHand`; `CodeView`, `Uses` and `Group`; `CodePreview`, `Group` and
  `GroupName`. `PeerView.Core` is -1 when there is no core in common (0
  is a version now).
- Events `GroupRequestWaiting`, `GroupJoinWaiting`, `GroupJoined`,
  `GroupChanged`, `GroupLeft`. Errors `CodeOptions`, `NoGroups`,
  `NoRight`, `NotMember`, `AlreadyMember`.
- A server already paired with a group code's maker joins through the
  pairing (MSFP groups section 8.4): `RedeemCodeAsync` calls `group-join`
  instead of `pair`, `ReadCodeAsync` no longer refuses such a code.
  Unanswered, a join is dropped after 30 days on both sides.
- A group's state is the same for every copy that kept the same events,
  whatever order they arrived in (MSFP groups sections 3.2 and 3.5, as
  msfp-go): `GroupState.Compute(group, envelopes)` replays them all and
  gives the state with a `GroupEventResult` per event ID, whose
  `GroupVerdict` is Counted, Refused, Pending or Forged;
  `GroupEvents.Check` is what a copy checks before keeping an event (a
  `GroupCheck`). `GroupState.Apply`, `GroupApplied` and the verdicts
  Kept, Held and Had are gone; `GroupState.Highest` gives the seq a
  member signs next. Events that don't count are kept and passed on too,
  and Forged ones, and Pending ones after 30 days, dropped;
  `GroupRecord.Held` is no longer written (its events are read as kept
  ones). Signed by someone else than the member, an event can no longer
  exclude it.
- A stranger is heard on `pair` (a code live) and `group-pair` (a member
  of one of this server's groups) only: on any other call it gets the
  plain 404, as in msfp-go.

## v0.2.0-preview (2026-11-19)

- An episode keyed by its TMDB episode ID carries `place`, its season and
  episode in TMDB's numbering (MSFP video sections 3, 3.1 and 4.1). The
  host sets `VideoTitle.Place` (a `Place` with `Season` and `Episode`)
  for its own such episodes: one without a valid place isn't sent, and a
  new place is a change of the title. A peer's such episode without a
  valid place is skipped; `place` on other titles is ignored. Peers'
  titles keep it in the stores (`place`, as msfp-go's file store).
  `Msfp.Protocol`: `Place`, `VideoTitle.Place` (a new positional member,
  after `Video`), `VideoKey.ByEpisodeId`, `Video.MaxSeason` and
  `Video.MaxEpisode`. The vectors are the spec's of 2026-11-19.

## v0.1.0-preview (2026-11-19)

The MSFP v1 engine for .NET, the same API design as msfp-go v0.2.0
(msfp-spec's api/), in .NET's own style. Not released.

- `Msfp.Protocol`: the protocol's pieces (keys, pairing codes,
  signatures, strict JSON, statements, title keys, languages as MSFP
  sends them, the media checks: HLS, WebVTT, PGS, preview sheets). Passes
  every case of the spec's `vectors/v1/` and `vectors/v0-strict.json`.
- `Msfp.Engine`: `MsfpEngine.OpenAsync`, its HTTP entries
  (`HandleFederationAsync`, `HandleStreamAsync`, `HandleDiscovery`), and
  everything msfp-go v0.2.0 does: pairing, `hello`, limits, rotation and
  revocation of its own key and its peers', address changes, servers
  that can't be called, backoff, the discovery document, the catalog both
  ways, playback both ways, the media of peers' tickets and the relay of
  a peer's media, for films, series and music; multi-part copies
  (`CopyInfo.Parts`, `StartRequest.Parts`) as v0.2.0.
- The stores `MemoryStore` and `JsonFileStore` (namespace `Msfp.Stores`).
- No default `ISheetEncoder` (.NET has no JPEG codec): without one,
  previews of peers' titles are left out.
- `examples/ShareFolder`: msfp-go's `examples/share-folder` in C#. It
  passes msfp-spec's conformance suite as msfp-go's example does (118
  passed, 0 failed, 3 skipped).
