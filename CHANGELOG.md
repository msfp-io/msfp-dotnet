# Changelog

## v0.9.0-preview (2026-11-27): MSFP Media (proposal 19)

MSFP's media language is now MSFP Media version 0, which MSFP carries
between servers. Doc comments cite the language's rules as MSFP Media's
(base, listing, playing, fetching, video, music) and the federation's as
MSFP's. The wire changes below don't talk with 0.8.0-preview on every
point (draft rules: no compatibility between drafts). They match msfp-go's
of the same proposal.

### Added

- `progressSeconds` in play's answer: the engine as owning server sends
  `MsfpConfig.ProgressSeconds` (45 to 120, 60 by default) and ends a
  playback idle for twice it; as viewer's server it reports at the
  interval each answer gave. `PlayAnswer.ProgressSeconds`,
  `Playback.ProgressInterval`, `Playback.IdleLimit`,
  `Playback.MinProgressSeconds`, `MaxProgressSeconds`,
  `DefaultProgressSeconds`.
- `paused` in progress: sent by the engine (`Progress`'s `paused`), read
  as a boolean (another type: 400), passed to the host as
  `Report.Paused`. `PlayReport.Paused`, `ReportOutcome.Paused`.
- `replaces` in play: `PlayRequest.Replaces` now names the old playback
  to the peer instead of stopping it first (a playback on another peer
  is still stopped first); on a 200 the peer ends it, and the engine
  forgets it. As owning server, the engine counts a play and the one it
  replaces as one (a play frees its stream and transcode, a download its
  download but not the day's count, a scan nothing), ends the old one
  with `EndReason.Replaced`, and tells the host `StartRequest.Replaces`.
  `RestartAsync` of a live playback replaces it.
  `Protocol.PlayRequest.Replaces` (malformed: 400), `LimitUse.Without`.
- `target-refused` (409): a target the source doesn't make (a remux's
  media type or a segment format that can't carry the copy, a transcode
  codec, a range, a copy no segment format carries, a subtitle to burn);
  `delivery-refused` is now for the delivery as such.
  `MsfpError.TargetRefused`, `Playback.ProblemTargetRefused`; a host's
  `StartAsync` may throw it.
- The language's other refusals: `Playback.ProblemGone`
  (`MsfpError.Gone` from `PlayAsync`; the peer's titles are read again),
  `Playback.ProblemUnreachable` (a host's `StartAsync` throwing
  `Unreachable` answers 503 unreachable), `Playback.ProblemTicketRefused`;
  `Protocol.Problems`: `TryGet`, `All`, `Temporary`, `LanguageProblem`,
  `Meaning` (a type read by its status when not known) and
  `ReadPlayRefusal` (what a caller tells its viewer, a
  `PlayRefusalReading`).
- `instead` on delivery-refused and target-refused:
  `MsfpException.Instead` gives what the peer would serve instead;
  `Protocol.Instead`, `Problems.ReadInstead`, `Problems.MaxInstead`.
- `titles`: `libraries` optional (absent: every library offered), the
  `kind` and `under` filters, `since` ignored with `under`, a changes
  answer filtered by kind (its `gone` too). `TitlesRequest` (`Read`,
  `ToJson`, `SinceAllowed`), `TitleFilter` (`Passes`, `UnderPrefix`,
  `Changes`, `None`, `KnownKind`), `FilterFacts`. The engine as viewer's
  server still asks for its wanted libraries, unfiltered.
- `msfpMedia` in the discovery document: `[0]` when a part that carries
  MSFP Media is listed. `Discovery.MsfpMedia`,
  `Discovery.MsfpMediaVersions`.
- The capability list's types and forms, for a host serving its own
  players with MSFP Media (MSFP refuses it; the engine never sends one,
  and matching a copy against it is the host's): `Capabilities` (`Read`),
  `VideoCapability`, `AudioCapability`, `PlayReader` (`Read`, with
  `Capabilities` and `Purposes`), `Protocol.PlayRequest.Capabilities`,
  `PlayAsked.Capabilities`.

### Changed

- `Playback.ReadPlay` refuses `capabilities` with 400 (it was an unknown
  member, ignored).
- `Playback.RefuseDelivery`, `Encoders`' codec checks and a music queue
  entry's remux to another media type refuse targets with
  `target-refused`; hosts matching `ProblemDeliveryRefused` match both.
- `Playback.IdleEnd` is the idle limit at the default interval (still 2
  minutes); a playback's own is `Playback.IdleLimit` of its answer's
  `progressSeconds`.
- The title of `delivery-refused` is "This delivery is refused".
- `MsfpError` gains `TargetRefused` and `Gone` after `DeliveryRefused`:
  the numbers of the codes after them move by two (match codes by name).
- The vectors are msfp-spec's of proposal 19 (with the `replaces`,
  `refusals`, `titleOffer` and `titleRequests` groups).

### Fixed

- A playback ended while the owning server was writing an answer of
  unknown length (a live transcode's segment) reset the HTTP/2 stream
  (`INTERNAL_ERROR`): the write was cancelled in flight, and the viewer's
  relay saw its stream cut. A write under way now gets 2 seconds to
  finish, no new write starts, and the answer ends cleanly, as msfp-go's
  does. The rare `KestrelTests.ShareAndPlayOverTls` failure was this.
- ShareFolder (the example): ffprobe is awaited, not waited on from a
  request thread; the probes of files no longer in the folder are
  forgotten.

## v0.8.0-preview (2026-11-24): the revision of version 0 (proposals 16 and 17)

MSFP version 0 is revised, and stays a draft until version 1: titles
carry their copies, plays ask a delivery and a purpose (downloads and
scans), extras and local titles are shared, and a copy's file is served
by byte ranges. Proposal 17 (neutrality), as frozen on 2026-11-22, then
makes the protocol carry facts in outside vocabularies (IANA media
types, RFC 6381, ITU-T H.273, the DASH roles, MusicBrainz's types) and
MSFP's registry, with open lists; drops every field that is only safe
through guidance, said twice, or that the receiver can work out; builds
every media URL from a ticket; and leaves the choices to the viewer's
server: no capabilities, a defined baseline. Servers on
the draft before (0.7.1-preview and before) and on the revised one don't
pair with each other: move every server at once. The migration guide at
the end of this section says what hosts change; its last part is
proposal 17's, and wins where the parts before name a member it removed.

### Added

- `Msfp.Describe` (namespace `Msfp.Describe`), msfp-go's `describe` in
  C#: a media file as a copy. `MediaTypes` (the media type from a file's
  first bytes, the registry's aliases and container names, the demuxer
  each sniffed type forces), `Prober` (ffprobe run safely: links
  resolved, a regular file only, the sniffed demuxer, the file protocol
  only, bounded probe size, time and output, two runs so codec records
  are read apart and attachments never, an optional frame read for
  HDR10+ and HLG's SEI; on Linux ffprobe reads the checked file by this
  process's descriptor), `Records` (avcC, hvcC, av1C, vpcC, Matroska's
  CodecPrivate, Annex B, AudioSpecificConfig), `CodecsStrings` (RFC 6381
  from records, else from ffprobe's fields), `Shaper.Shape` (the copy in
  MSFP's vocabularies) and `Probe.Raw` (ffprobe's output without paths
  or attachments). Its tests use msfp-go's `describe/testdata` unchanged:
  both engines shape the same copies.
- Why a peer's titles weren't read: `PeerView.Reading` (a `LastReading`:
  when the last reading ended, its `Failure`, and for a signed refusal
  its problem type), and `CatalogReadEnded.Failure` and `Refusal`.
  `ReadFailure` holds a closed set of codes (`ReadFailure.All`), the same
  in msfp-go, never holding an address or anything the peer wrote:
  `unreachable`, `timeout`, `certificate-refused`, `refused-unsigned`,
  `refused`, `not-paired`, `suspended`, `uncallable`,
  `no-common-version`, `catalog-off`, `older-draft`,
  `answer-not-understood`, `internal`. A peer still on the draft before
  this revision used to read as an empty catalog: its answers are now
  recognised (libraries and titles with a `kind`, films, episodes and
  tracks without `copies`), and the reading fails as `older-draft`
  (`Unreachable`), keeping what was kept before.

### Changed by proposal 17

- One rule for unknown values (`Names.IsToken`, `Names.Token`,
  `Names.IsMediaType`, MSFP core section 8.5): open fields take tokens,
  or media types, kept as they are when unknown; an optional malformed
  member is dropped alone, a required one drops its holder. No list of
  names in the packages: the registry (spec/registry.md) is the
  authority.
- Copies (`Protocol.Copy`): `MediaType` (the file's media type) replaces
  `Container`; codec and subtitle tokens are the registry's (ffprobe's
  `codec_name`); `Codecs` (RFC 6381) is required for h264, hevc, av1,
  vp9, aac and dts (`Rfc6381.Required`) and read by `Rfc6381.Read`;
  `Colour` is H.273 code points with `FullRange`; `Hdr` holds
  enhancement layers only (`hdr10plus`); new `RefFrames`, `Objects`
  (`dolby-atmos`, `dts-x`), subtitle `Role`, `EndMs`, `PreviewLayout`;
  `Parts` is a count; track IDs are the stream's index in the file.
  Gone: a copy's `Name`, `BitrateKbps` (the overall rate is `Size` × 8 /
  `DurationMs`), `Added`, `Previews`, `CopyPart`, a track's `Title`,
  `Profile`, `Layout`.
- Titles carry no kind on the wire (their key names it), libraries no
  kind (`Library.Kind`, `WantedLibrary.Kind`, `ShownLibrary.Kind`,
  `CatalogRules.LibraryKind` gone), extras no name; `ids` are sent only
  on episodes and on titles keyed under `tvdb`.
- Music: copies take `MediaType` (no `Name`, `Added`, `BitrateKbps`),
  up to 16; no `SortName`; `ids` only on `mb` titles; a track's artists,
  names, dates and genres leave only when they differ from its album's.
  Lyrics: no `synced`, `offset` or display names.
- play's request: `MediaType` (`video/mp2t`, `video/mp4`, `audio/…` for
  a copy with no video) replaces `Container`; transcode targets take
  `copy` (`Names.Copy`) and a `Range` (`sdr`, `keep`); no
  `AudioLanguage`, `SubtitleLanguage`, `SubtitleTrack`; queue entries
  are objects with their own copy and delivery (`QueuedTrack` on the
  viewer's side, `Protocol.QueueAsk`, `Playback.ReadQueueEntry`).
  Progress carries no `paused` (`Report.Paused` is gone).
- The owning side no longer chooses: the copy is the one `versionId`
  names, else the first; the audio the one `audioTrack` names, else the
  file's default (`Playback.ChooseCopy`, `ChooseAudio`). The engine
  decides what is served (`Served`: the defined baseline, H.264 High 4.1
  8-bit SDR BT.709 and AAC-LC stereo 48 kHz, each stream within it left
  unchanged, `Playback.Baseline`; remux segment formats; copied streams;
  ranges) and passes it to the host (`StartRequest.Served`,
  `QueuedCopy.Served`, with each entry's own `Delivery`, `MediaType` and
  `Transcode`). `StartRequest.MediaType` replaces `Container`;
  `StartRequest.SubtitleTrack` is gone.
- play's answer carries a `ticket`, `protocol`, `subtitles` (`id`,
  `form`, `ass`), `fontCount`, `scanBytes` (scans) and no URL, track,
  length or trickplay: the viewer's side builds every URL from the
  peer's stream root and the ticket (`MediaUrls`), never fetching an
  address a peer wrote. The owning side serves exactly those names
  (playlists' and segments' names only once a playlist served gave them)
  and `404`s past the counts. `previews` answers `{ticket, etag}`,
  `image` `{ticket}`.
- Previews: the layout is the catalog's (`Copy.PreviewLayout`);
  `CopyInfo.Trickplay` and `Msfp.Trickplay` are gone; sheets are
  fetched on demand (`PreviewSheetAsync`) under the playback's ticket or
  a previews ticket, cached by etag and layout; `PreviewsAsync` and
  `PreviewSheets`, which fetched every sheet ahead, are gone. Without a
  `SheetEncoder`, a copy leaves without its `PreviewLayout`.
- `RemotePlayback`: `Subtitles`, `Fonts`, `Sheets`, `ScanBytes`; its
  `DurationMs` and `MimeType` come from the catalog's copy. Gone:
  `AudioTracks`, `SubtitleTracks`, `SubtitleTrack`, `ForcedTracks`,
  `ForcedTrack`, `BurnedTrack`, `SubtitleStretchSeconds` (it is
  `WebVtt.StretchSeconds`), `Trickplay`, and the `Track` type.
- Limits: no `scanBytes`; a pool's `Limits.ScanBytes` is its own scan
  cap, stated in each scan's answer (`Playback.ScanCapOf`).
- Groups: no name in a mail nor in `group-pair`: a member's name is its
  profile's.
- The file is served and relayed with the copy's own `mediaType`
  (`Media.RelayFileType`); `Started.MimeType` and `StartedEntry`'s are
  gone.
- The master playlist: the engine fills `CODECS`, `RESOLUTION`,
  `VIDEO-RANGE`, `SUPPLEMENTAL-CODECS`, `BANDWIDTH` and `CHANNELS
  "16/JOC"` that the copy's facts tell when a stream is served
  unchanged, and logs the required ones still missing.
- The ShareFolder example: `mediaType` from the file's first bytes,
  codecs strings, H.273 colour, Dolby Vision, objects and track IDs from
  ffprobe, an etag from the file's content (SHA-256, kept by size and
  time), the previews' layout in the catalog, a remux by its segments'
  media type; at most 64 subtitle tracks a copy (the file's own, then
  the .vtt beside it).

### Removed by proposal 17

- `MsfpConfig.ChooseCopy`, `ChooseTracks`, `CopyName`, `TrackTitle` and
  `MsfpDefaults`; `Copies.MaxName`; `CatalogRules.LibraryKind`.

### Added by proposal 17

- `Served`, `Encoders.Ranges`, `Encoders.Segments`,
  `Encoders.DefaultSegments`; `CopyInfo.Forms` (the form of a track
  whose format video section 5.4 doesn't list); `QueuedTrack`;
  `RemotePlayback.Subtitles`, `Sheets`, `ScanBytes`;
  `PreviewSheetAsync`. In Msfp.Protocol: `Rfc6381`, `Names.IsMediaType`,
  `OwnerMakes`, `Playback.Baseline`, `TakesTranscode`, `PlayAsked`,
  `ServedSubtitle`, `Video.FormOf`, `ReadServed`, `ReadFontCount`,
  `PreviewsAnswer`, `MediaUrls`, `PlayAnswer.Urls`, `QueueAsk`,
  `Playback.ReadQueueEntry`, `Media.RelayFileType`, `Media.Serves`,
  `Playback.ScanCapOf`.

### Changed

- The host calls given a copy's handle for a peer are given the peer
  and the host's libraries offered to it (`PeerRef.Libraries`, sorted
  ordinally): `IPlayback.DescribeAsync(peer, copy, ct)`,
  `StartRequest.Peer`, `ILyrics.LyricsAsync(peer, copy, ct)` and
  `IMedia.ServeAsync(peer, handle, …)` (a playback's media: those
  offered when it started). A handle can then name the copy alone,
  wherever its files are, and the version ID a peer sees stays the same
  when a file moves to another library: the host resolves the handle to
  a file in one of those libraries, and throws `MsfpError.NotFound` when
  the copy is in none. Not a protocol change. The ShareFolder example
  serves its folder only to a peer offered it.
- Titles carry their copies: `VideoTitle.Copies` and `MusicTitle.Copies`
  are `Protocol.Copy` and `MusicCopy`, each copy's ID the host's handle
  (the engine sends the version ID `play` gives). `VideoTitle.Audio`,
  `Subtitles`, `Pictures`, `Video` and `Picture` are gone: `BestCopy`,
  `AudioLanguages` and `SubtitleLanguages` work them out from the copies.
  `MusicTitle` has music's new description (`SortName`, its type,
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
  `DownloadsPerDay`, `DownloadKbps`, `ScansPerDay`. The remote `Track` has no `Label` (and
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

### Fixed

- With the change log kept by the engine, a titles answer given while
  the refresh that `CatalogChanged()` starts in the background was still
  reading the host's titles came from the log as it was: a changes
  reading with nothing in it, the change seen only at the next reading.
  The answer now waits for that refresh.
- The ShareFolder example stops on SIGTERM and SIGINT: the command loop
  waited on the console, so the process never exited; requests still
  open get 2 seconds.

### Migration guide

**A host that shares (the owning side):**

1. `ICatalog.TitlesAsync` gives each film, episode and extra its copies
   (`VideoTitle.Copies`, `Protocol.Copy`; a music track's `MusicCopy`),
   in your order of preference: the copy's ID is your handle (it never leaves), its
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
   `IPlayback.DescribeAsync`, `ILyrics.LyricsAsync` and
   `IMedia.ServeAsync` take the `PeerRef` the call is made for, first;
   with `StartRequest.Peer`, its `Libraries` are the libraries offered to
   the peer: resolve a copy's handle to a file in one of them.
6. Extras: titles of kind `extra`, keyed `<film, series or season
   key>/x/<opaque>`, with `ExtraType` and an optional `Name` (theme
   songs and videos too, since proposal 17).
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

**Proposal 17, a host that shares:**

1. Describe each copy in outside vocabularies, as facts: `MediaType`
   read from the file's first bytes (the ISO BMFF brand table of the
   registry, the EBML DocType, the TS packet size; `audio/…` when there
   is no picture but cover art), the registry's codec and subtitle
   tokens (ffprobe's `codec_name`), `Codecs` for h264, hevc, av1, vp9,
   aac and dts at least (built from your media server's profile and
   level or the file's records: examples/ShareFolder/Probes.cs does it
   from ffprobe), `Colour` as H.273 numbers with `FullRange`, `Dv`
   whenever there is Dolby Vision, `Hdr` only `hdr10plus`, `Objects`,
   `RefFrames`, exact values; track IDs `a<n>`/`s<n>` with n the
   stream's index. Drop names, titles, bitrates, dates, profiles and
   layouts.
2. `ETag` changes when and only when the bytes change.
3. Put the previews' layout on the copy (`PreviewLayout`), not in
   `CopyInfo`; a change of it is a change of the title. Give a
   `SheetEncoder`, or it doesn't leave.
4. The first copy is the one played when a peer names none; the audio
   the file's default when it names none. There are no choice or name
   hooks.
5. `IPlayback.StartAsync`: read `MediaType` (a remux's segment format,
   `Served.Segments`), `Transcode` (`copy` streams left unchanged, a
   `Range` to make) and `Served` (what the engine decided, the
   baseline's streams left unchanged when within it); no
   `SubtitleTrack`. Queue entries carry their own delivery. Set
   `MsfpConfig.Encoders` (codecs, ranges, the codecs your segment
   formats carry). In fMP4 segments write HEVC `hvc1`, Dolby Vision
   `dvh1` (`dav1` on AV1), parameter sets out of band; give each master
   playlist variant `BANDWIDTH`, `CODECS` and, with video,
   `RESOLUTION`, plus `VIDEO-RANGE` and `SUPPLEMENTAL-CODECS` where
   they apply. Drop `Started.MimeType` and `StartedEntry`'s.
6. Serve every subtitle track of a format video section 5.4 lists in
   its form; list in `NotApart` those you can't, and don't list in the
   catalog an external track you can't serve apart; give `Forms` for
   other formats you serve apart.
7. A pool's scan cap is `Limits.ScanBytes` (32 MiB suggested,
   `Playback.ScanBytesRecommended`); it is no longer sent in limits.
   Up to 64 fonts a copy (`CopyInfo.Fonts`), 64 MiB in all.
8. Drop `Library.Kind` and extras' `Name`; music titles' `SortName`.

**Proposal 17, a host that plays peers' titles:**

1. Choose the copy (`VersionId`), the audio (`AudioTrack`) and the
   delivery from the copy's facts and your device: a copy whose media
   type, codec or 3D layout you don't know, or whose `Codecs` can't be
   read (`Rfc6381.Read`), is one you can't play as it is: ask a `remux`
   (`MediaType` `video/mp2t` or `video/mp4`), a `transcode` (registry
   tokens, `copy` for a stream your device plays, `Range` `keep` for
   HDR), or nothing for the baseline.
2. Choose the subtitles yourself, among the copy's tracks and
   `RemotePlayback.Subtitles` (served apart); name one only to burn it
   in.
3. Label copies and tracks from their facts (height, range, edition,
   codecs; role, flags, channels, objects); show "recently added" from
   the day you first saw a copy; type an empty library by its first
   titles; show an extra by its type and a number.
4. Fetch previews only for a copy someone opened, with
   `PreviewSheetAsync`; never for a whole library.
5. A scan's cap is `RemotePlayback.ScanBytes`.
6. Music queues are `QueuedTrack`s, each with its own copy and delivery
   if you like; a track's absent artists, dates and genres are its
   album's.

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
  (groups section 4.1). Requests already waiting when it is turned on
  are accepted too, by the engine's loop (`RunAsync`).
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
