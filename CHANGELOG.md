# Changelog

All notable changes to `aether-protocol` are documented in this file.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning: [Semantic Versioning](https://semver.org/spec/v2.0.0.html) —
see [VERSIONING.md](VERSIONING.md) for wire-break promotion rules.

---

## [1.3.0] — 2026-06-09

### Added — AetherNet.Content.Diagnostics.MeshInvariants

Promoted the formal-property runtime predicates from
`AetherMedia.LocalLibrary.Audio.Mesh.MeshInvariants` to
`AetherNet.Content.Diagnostics.MeshInvariants`. Core AetherNet functions
belong on AetherNet — every C# consumer (Bruh!, SDPKT, txtMe!, third-party)
can now use these without depending on the AetherMedia package.

Five existing predicates moved (unchanged behaviour, dependency on
`MeshPackageDistributor.IntegrityHash` inlined to `SHA256.HashData`):

- `DtnCustodyEventuallyTerminates` ← `formal/dtn-custody`
- `MultiDeviceSyncConverges` ← `formal/multi-device-sync`
- `ContentBitmapEventuallyComplete` ← `formal/content-bitmap`
- `ForgeIntegrity` ← `formal/forge-integrity` (now self-contained)
- `StreamSequenceMonotonic` ← `formal/stream-abr`

Three new predicates added (closes `02_REMAINING_WORK.md` §10):

- `WatchTogetherBoundedLatency(hostMs, followerMs[], toleranceMs)` ←
  `formal/watch-together-timed`. Follower drift after RTT compensation must
  stay within tolerance (default 100 ms).
- `OutboxBounded(currentDepth, maxDepth)` ← `formal/outbox-backpressure`.
  Outbox queue never exceeds its cap; new work is rejected at the limit.
- `ByzantineQuorumReached<T>(votes, out agreed, faultTolerance = N/3)` ←
  `formal/byzantine-routing`. Agreement requires (N − f) peers reporting
  the same value; gates cover-art / lyric / news-source / route-reply trust.

### Internal

- All 6 C library repo-health fixes (commits `54c6431` → `ecbecb7`) shipped
  along with 1.3.0: `aethernet_transport_metrics_t` + `_rank_entry_t` + macro
  + vtable fields; `aethernet_fec_codec_t`; `aethernet_blake3` + macro + impl;
  `max_range_meters` vtable field; `aethernet_gossip.h` `size_t` include;
  `test_signal_session.c` line-continuation repair; in-process transport
  global-callback + metrics implementation. Mac cmake build now passes
  end-to-end with 24/24 ctest tests green (was build-blocked under #62).

### Tests

- AetherNet.Core.Tests: 655/655 pass (was 628 in 1.2.0 — +27 new tests for the
  3 new predicates and surrounding hardening)
- C library: 24/24 ctest pass on Mac (rustc/cmake/libsodium/cjson/blake3 toolchain)

### Migration from 1.2.0

Consumers of `AetherMedia.LocalLibrary.Audio.Mesh.MeshInvariants` should
update the `using` to `AetherNet.Content.Diagnostics` and the
`PackageReference` to `AetherNet.Content` 1.3.0. The five existing predicates
keep identical signatures and behaviour.

---

## [1.2.0] — 2026-06-07

### Added — consumer-protocol-surface (Wave 16 / 17)

Three additive interface extensions surfaced while wiring
AetherMedia.LocalLibrary against AetherNet 1.1.0. All non-breaking
(default interface methods + new event handlers + new packet types in
previously-reserved enum slots). Existing consumers compile and run
unchanged.

- **`IDtnService.BundleReceived` event** — fires when a DTN bundle
  addressed to the local node arrives. Distinct from `BundleDelivered`
  (which fires on the SENDER side once a delivery receipt flows back).
  Subscribers that want to know "a bundle arrived for me" now have a
  clean signal — previously they had to inspect `HandleAsync(packet)`
  indirectly via the host shell. Closes [#59](https://github.com/bhengubv/aether-protocol/issues/59).
- **`IDirectoryService` — application-layer name resolution** — new
  interface in `AetherNet.Content` that maps human/application names
  (e.g. `"podcast:abc123"`, `"reel:hash"`, `"album:artist/title"`) to
  `ContentDescriptor`. Closes the gap where mesh-first fetchers had to
  derive a fake content key from `(artist + album)` etc. because
  `IContentService.BroadcastBitmapAsync` is `rootHash`-keyed.
  - Two new packet types: `NamePublish = 38`, `NameQuery = 39`
    (snake_case JSON wire format for cross-language interop)
  - Methods: `PublishAsync(name, descriptor)`, `ResolveAsync(name, timeout?)`,
    `ListNamesAsync()`, `HandleAsync(packet)`
  - Event: `EntryAnnounced`
  - DI registration: `AddDirectory()` (requires `AddRouting()`)
  - Closes [#60](https://github.com/bhengubv/aether-protocol/issues/60).
- **`IAetherNetIncentiveProvider.RecordCreatorTipAsync`** — new
  default-method on the existing extensibility interface. Distinct from
  `RecordRelayAsync` (which records relay credit for nodes forwarding
  bytes); this records direct creator → consumer settlement (the user
  who AUTHORED the content). Hosts (SDPKT, BhenguPay) wire their
  settlement logic; default no-op preserves backward compatibility.
  Closes [#61](https://github.com/bhengubv/aether-protocol/issues/61).

### Cross-language

The 7 non-C# implementations ship the same three additions with
language-idiomatic patterns (callbacks/channels for events, suspend/
async functions for futures). Wire format is byte-equal across all 8
languages via snake_case JSON. Per-language status as of 1.2.0 release:
Go / Python / TypeScript / Kotlin fully build + test green on dev
machines. Swift / Rust / C verified syntax-clean (`swiftc -parse` /
`cargo check` / `gcc -fsyntax-only`); full build verification awaits
Linux CI on this tag.

### Internal — 8-language surface

628/628 C# Core tests pass (was 615, +13). 11/11 Soak tests pass.
Tests added per language: Go +14, Python +16, TypeScript +12,
Kotlin +13, Swift +13 (syntax-verified), Rust +14 (syntax-verified),
C +11 (syntax-verified). No regressions across any language.

---

## [1.0.1] — 2026-05-21

### Fixed
- `ITransportService.ts` now exports `PerTransportMetrics` and `rankTransports`
  (were missing, causing all `transport_rank.test.ts` tests to fail at import)
- `InProcessTransport` now implements `metrics: PerTransportMetrics` and records
  a sample on each successful delivery (fixes `transport_inprocess.test.ts`
  metrics assertions)
- `PROTOCOL_SPEC.md` `SosPriority` corrected from `999` to `255` (byte overflow)
- `PROTOCOL_SPEC.md` `DefaultChunkSizeBytes` corrected from `262144` to `8192`
  (mesh-correct value matching all runtime implementations)

---

## [1.0.0] — 2026-05-21

First stable release. All eight language implementations (C#, Kotlin, Swift,
Rust, TypeScript, Go, Python, C) are wire-compatible and publish to their
respective package registries from a single CI/CD pipeline.

### Added

**Core protocol**
- AODV-based mesh routing with Kalman-filtered transport selection
  (`PredictiveTransportSelector`)
- RREQ deduplication with TTL-based eviction
  (`DeduplicationWindowSeconds = 300`) — closes replay-DoS vector
- Signal Protocol Double Ratchet end-to-end encryption for all peer messaging
- Minimum packet length enforced at 43 bytes in both C# and TypeScript
  deserialisers (was incorrectly 31)

**Services**
- `IStreamingService` — live broadcast, segment publishing, ABR bandwidth
  estimation, active-stream enumeration
- `IWatchTogetherService` — watch-party hosting/joining, play/pause/seek/speed
  sync, live emoji reactions, BitTorrent broadcast, ChipIn crowdfunding
- `IVideoCallService` / `IGroupVideoService` — 1-to-1 and multi-party video
  with automatic FullMesh → SFU topology switch
- `IVoiceCallService` / `IGroupVoiceCallService` — audio-only calls and
  group voice rooms
- `IContentService` — chunked P2P file distribution with hash verification
  and chunk reassembly
- `IDtnService` — store-and-forward bundle delivery for offline peers
- `IMessagingService` — Signal-encrypted direct messaging with outbox retry;
  zero-plaintext-persistence security guarantee
- `IReputationGossipService` — peer reputation scoring gossiped across mesh
- `IAnomalyDetector` — traffic-pattern anomaly detection
- `ISosBroadcastService` — priority-255 emergency broadcast interrupting all
  lower-priority traffic
- `IHandshakeService` — peer capability negotiation on connect
- `IRoutingService` — AODV route discovery and maintenance
- `ITransportManager` — pluggable transport layer (BLE, Wi-Fi Direct,
  NearLink, NFC, LoRa, HTTP relay)

**Extensibility**
- `IAetherNetAiProvider` — AI integration point (route suggestion, transport
  biasing, threat assessment); null-provider contract guarantees safe
  unconditional call on all methods
- `NullAetherNetAiProvider` — canonical reference implementation; allocation-free
  on hot path
- `IAetherNetIncentiveProvider` — node-tipping / relay-incentive hook
- `IAetherNetBackendClient` — optional cloud-relay fallback

**Identity**
- `AetherNetTag` — human-readable Ed25519-derived identifiers (e.g. `KXJB7-MN2P4`)
- UHID-based routing; all packets signed to source UHID

**Android companions** (Blue / Green / White)
- `AetherNetGattServer` — BLE GATT server with companion-object packet parsing
  and TTL decrement; 23 JVM unit tests
- `AetherNetWifiDirectService` — Wi-Fi Direct transport with companion-object
  framing; 19 JVM unit tests
- `AetherNetHceService` — NFC HCE with companion-object AID recognition; 18
  JVM unit tests

**Infrastructure**
- Full CI across all 8 languages on every push to `main` / `develop`
- Coverlet coverage gate (80%) with exclusions for generated code and
  `[ExcludeFromCodeCoverage]` attributes
- Rust MSRV job (`1.75`) in CI
- Go module verification (`go mod verify`) in CI
- Benchmark regression gate — C# and Rust baselines enforced; failures are
  hard errors (exit 1)
- Source Link + deterministic builds; symbol packages (`.snupkg`)
- Single-command publish to all 8 registries on semver tag

**Documentation**
- Architecture overview, transport layer deep-dive, getting-started guide
- `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `VERSIONING.md`

### Fixed

- RREQ replay-DoS: dedup cache previously evicted on size; now uses TTL
- Messaging outbox retry incorrectly incremented retry counter for
  zero-length (awaiting-session) messages, causing silent `Failed` state
- `catch {}` swallowing `OutOfMemoryException` in `PacketSerializer` — now
  `catch (Exception ex) when (ex is not OutOfMemoryException)`
- Minimum packet length constant corrected to 43 bytes in C# and TypeScript

### Changed

- `DefaultChunkSizeBytes`: spec corrected to `8192` (8 KB) — matches runtime;
  previous spec value of `262144` was copied from a TCP context and wrong for
  BLE/mesh transports
- `SosPriority`: spec corrected to `255` — matches runtime; previous spec
  value of `999` overflows a single byte

---

*Older entries will appear here as the project's history is backfilled.*
