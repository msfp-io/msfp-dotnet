# Changelog

## Unreleased

### Added

- "No way in" (groups section 6). `GroupView.NoWayIn` says when this
  server has no pairing through a group that has other members. Nothing
  reaches those members from here, and nothing reaches here from them.
  `GroupsCutByAsync(peer)` lists the groups that ending a pairing would
  cut off, so a panel can say so before it ends.

### Changed

- A request mail is refused with `MsfpError.NoWayIn` when this server has
  no pairing through the group, where before it was kept but could never
  leave. `RequestMemberAsync` throws that error, and any other error from
  its request, instead of only logging it.

### Fixed

- A disposed engine's `RunAsync` kept running when its host didn't cancel
  the token, failing every hour on the disposed store. A Jellyfin restart
  that runs out of time stopping the plugin did that. `Dispose` now ends
  `RunAsync`, which returns without an error.

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
