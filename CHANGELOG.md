# Changelog

## Unreleased: v0.2.0-preview

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

## v0.1.0-preview (not released)

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
