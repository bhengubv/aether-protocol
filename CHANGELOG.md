# Changelog

All notable changes to `aether-protocol` are documented in this file.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning: [Semantic Versioning](https://semver.org/spec/v2.0.0.html) —
see [VERSIONING.md](VERSIONING.md) for wire-break promotion rules.

---

## [1.6.2] — 2026-06-10

ABMF cross-language **numeric parity proof**. Adds an executable conformance
oracle and fixes six silent cross-language divergences it caught — bugs that
would have made the 8 SDKs report different bandwidth numbers or pick different
transports for the same inputs, with no error anywhere.

### Added — `tests/cross-language/bandwidth-fixtures.json`

A deterministic conformance corpus (3 probe-ack + 4 RTO + 5 PHY-cap + 7
estimator + 3 director cases) with expected outputs generated from the C#
reference. Every SDK drives this same corpus and must produce identical
results — integer/enum fields exact, floats within 0.01. This is the bandwidth
analogue of `uri-fixtures.json`. Drivers added in all 8 languages
(`BandwidthFixtureTests` / `fixture_test.go` / `test_bandwidth_fixtures.*` / etc.).

### Changed — BDP derives from the effective (PHY-capped) rate

`BandwidthSample.BdpBytes` is now computed from the **effective** rate
(`min(BtlBw, PhyCap)`), not the raw BtlBw. The BDP must size the in-flight
window to the rate the link can actually carry. A discriminating fixture
(`phy_hint_caps_estimate`, `bdpBytes: 5`) locks this. Applied to all 8 SDKs.

### Fixed — six divergences surfaced by the corpus

1. **TS** — `Math.round` where C# casts `(long)` (truncates). 937.5 → 938 vs 937.
   Changed all rate/BDP/available derivations to `Math.trunc`.
2. **Kotlin** — `srtt`/`rttVar` floored to whole milliseconds via `.toLong()`
   (2.8125 → 2.0). Now built from nanoseconds, preserving fractional ms.
3. **C** — constructor seeded `max_bps` into the BtlBw max-filter window, so the
   first ~10 samples reported `max(seed, real_rate)`. Now leaves the window empty
   (initial display snapshot still shows max_bps, matching C#).
4. **Rust** — `get_estimates` seeded the matrix from live estimators on every
   query, making the unknown-peer fallback dead code (returned Wi-Fi Direct
   instead of the lowest-power BLE). Now a pure matrix read, matching C#.
5. **Go / TS / Kotlin / Swift / Rust / C** — BDP computed from raw BtlBw instead
   of the effective rate (see "Changed" above).
6. **Swift** — `BandwidthDirector` mutated an estimator actor's
   `onSampleImproved` across the actor boundary (Swift-6 compile error on macOS,
   missed by the Windows typecheck). Added `addSampleImprovedHandler` so the
   append happens inside the actor.

(The earlier 1.6.1 audit also fixed the `bdpBonus` 0.0-vs-1.0 split and a Kotlin
`Double.MIN_VALUE` sentinel bug — see that entry.)

### Verification

- C# Core suite: **844/844**. Fixture oracle: 22/22.
- Go 22, Python 22, TypeScript 22, Kotlin 22, Rust 22, C 22 fixtures — all pass.
- Swift typechecks clean (strict concurrency, Swift 6); macOS CI runs its 22.
- macOS: C `ctest` BandwidthTests pass; Swift `BandwidthFixtureTests` run on CI.

### Why
Most protocols never prove cross-language numeric identity — they assume it and
ship drift. AetherNet now proves it with an oracle that fails loudly on any
divergence. Six real bugs in one pass is the evidence that "identical by
construction" was never enough.

---

## [1.6.1] — 2026-06-10

ABMF cross-language completion + consistency pass. Brings the Bandwidth
Measurement Framework to all 8 SDKs and fixes three defects surfaced by a
cross-language audit.

### Added — ABMF ports to all 7 non-C# SDKs

Go, Python, TypeScript, Kotlin, Swift, Rust, and C now implement the full
ABMF surface introduced in 1.6.0 (C#):
- `BandwidthEstimator` (BBRv3 BtlBw + RTprop + RFC 6298 + PHY RSSI cap + confidence tiers)
- `BandwidthDirector` (cross-transport matrix + gossip warm-start)
- `NodeActivityMonitor` (activity state + per-transport stats + subscribe)
- Packet types `BandwidthProbe (53)`, `BandwidthAck (54)`, `BandwidthGossip (55)`

Test counts: Go 27, Python 55, TypeScript 32, Kotlin 24 (bandwidth), Rust 24,
C 39, Swift 36 (Mac CI). C# 49.

### Fixed

1. **`ActivePeers` always reported 0.** The C# `NodeActivityMonitor` declared an
   `activePeers` counter but the tick never incremented it — so a node moving
   real traffic showed "0 peers" on any UI bound to the snapshot. Added
   peer-aware `RecordIngress(transport, peerUhid, bytes)` /
   `RecordEgress(transport, peerUhid, bytes)` overloads + a peer-last-seen
   table; the tick now counts distinct peers active within the idle window and
   prunes stale entries. Fixed identically across all 8 SDKs.

2. **Transport-selection scoring diverged across languages.** The BDP-fit bonus
   for oversize payloads was `0.0` in C#/TypeScript/Kotlin/Swift/C but `1.0` in
   Go/Python/Rust (those agents read the spec prose, not the reference). `0.0`
   collapses every oversize-payload candidate's score to zero, degrading
   selection to a tie-break instead of ranking by bandwidth. Unified to the
   **neutral `1.0`** everywhere so `RecommendTransport` ranks identically in all
   8 languages.

3. **Kotlin `RecommendTransport` could return null for a valid peer.** Used
   `Double.MIN_VALUE` (the smallest *positive* double) as the best-score
   sentinel; a first candidate scoring 0 failed `score > bestScore` and was
   skipped. Changed to `Double.NEGATIVE_INFINITY` to match C#'s `double.MinValue`
   semantics.

### Why
"All core AetherNet functions should be on AetherNet" — and consistently. A
bandwidth framework that selects different transports in Kotlin vs Go is worse
than none. The audit that caught items 2 and 3 is exactly why every port drives
toward a single reference.

---

## [1.6.0] — 2026-06-10

### Added — AetherNet Bandwidth Measurement Framework (ABMF)

First release of a complete, standards-exceeding bandwidth measurement layer.
No existing protocol (TCP, QUIC, BBRv3, GCC/RMCAT) addresses multi-transport
mesh bandwidth with UI-surfaceable activity state.

**Core interfaces (`src/AetherNet.Core/Bandwidth/`):**
- `IBandwidthEstimator` — per-transport BBRv3-inspired state machine: BtlBw
  max-filter, RTprop min-filter, RFC 6298 SRTT/RTTVAR, confidence tiers.
- `IBandwidthProbeService` — active probing via `BandwidthProbe`/`BandwidthAck` packets.
- `IBandwidthDirector` — cross-transport synthesis + gossip warm-start.
- `INodeActivityMonitor` — observable activity state + per-transport stats for UI.
- `BandwidthSample`, `BandwidthProbeAck`, `BandwidthGossipPayload`,
  `NodeActivitySnapshot`, `TransportActivitySnapshot`, `NodeActivityState` — wire models.

**Reference implementations (`src/AetherNet.Transport/Bandwidth/`):**
- `BandwidthEstimator` — BBRv3 BtlBw max-filter + RTprop + RFC 6298 + PHY RSSI capping.
- `BandwidthDirector` — BDP-matrix transport selector + gossip coordinator.
- `NodeActivityMonitor` — 500 ms sampling loop; zero-dep `MeshSubject<T>` observable.

**New packet types:**
- `BandwidthProbe (53)` — active probe with send timestamp.
- `BandwidthAck (54)` — four-timestamp ack for clock-sync-free RTT.
- `BandwidthGossip (55)` — gossip warm-start on handshake.

**What exceeds existing standards (RFC 6298, RFC 9002/BBRv3, RFC 8836/GCC):**
1. **Cross-transport BDP matrix** — simultaneous BLE/Wi-Fi Direct/NearLink measurement.
2. **Gossip warm-start** — new sessions start with a measured estimate, not zero.
3. **PHY-layer RSSI capping** — BLE RSSI constrains estimates before probes complete.
4. **UI-surfaceable activity monitor** — `NodeActivityState` (Offline/Idle/Active/Busy/Degraded),
   per-transport utilization fraction, observable stream for dashboards and status bars.
5. **Confidence tiers** — explicit Low/Medium/High quality signal for ABR consumers.
6. **Formal convergence proof** — `formal/bandwidth-convergence.pnml`.

**Documentation:** `docs/bandwidth-estimation.md` — full algorithm specification,
RSSI calibration tables, transport selection scoring, gossip protocol.

**Tests:** 46/46 bandwidth tests pass (BandwidthEstimatorTests, NodeActivityMonitorTests,
BandwidthDirectorTests). Full Core suite: **819/819 pass**.

---

## [1.5.0] — 2026-06-09

Second move-upstream wave: two more protocol-level classes promoted out of
`AetherMedia.LocalLibrary` into the AetherNet packages that own their domain.
Neither class ever had any media-specific code; they were just living in the
wrong repo.

### Added — `AetherNet.Security.AesGcmEnvelope`

Canonical AES-256-GCM envelope for self-to-self payloads. Wire layout:
`[nonce(12)][tag(16)][cipher(N)]`. The same envelope is used by
scrobbles, bookmark + play-history sync, message-draft sync, vault-shard
metadata, and any other sender→self payload on the mesh — so a single
shared encryption pipeline keeps the key-management story honest (one
user key, one envelope, one decrypt path).

- `Encrypt(byte[] key, ReadOnlySpan<byte> plaintext) → byte[]` (random nonce per call)
- `Decrypt(byte[] key, byte[] envelope) → byte[]`
- `KeySize = 32` (AES-256)

Promoted from `AetherMedia.LocalLibrary.Audio.Mesh.AesGcmEnvelope`.

### Added — `AetherNet.Forge.MeshPackageDistributor`

Generic mesh-distribution layer that composes
`IForgeService` + `IContentService` + `IAetherNetIncentiveProvider` into a
single publish/fetch/relay-credit pipeline. Originally built for media
plugin packages (skins, Milkdrop presets, AVS effects) but the mechanism
is fully generic — any AetherNet consumer wanting to distribute any kind
of package by stable identifier (plugins, themes, code-cache artifacts,
shader presets) now has a one-line API.

- `PublishAsync(packageId, payload, contentType, ct)` → `ForgeEntry`
- `TryFetchAsync(packageId, ct)` → `byte[]?` (cache-hit → direct path,
  cache-miss → `IDirectoryService.ResolveAsync` → chunk request →
  assemble → re-cache)
- `RecordChunkRelayAsync(packet, ct)` → drives `IAetherNetIncentiveProvider`
- `SkinPackageId(name)` / `PresetPackageId(family, name)` / `PluginPackageId(id, version)` — generic stable-identifier conventions
- `IntegrityHash(payload)` — SHA-256 over the bytes

Maps to formal models `forge-integrity`, `content-bitmap`, `forge-eviction`.

Promoted from `AetherMedia.LocalLibrary.Audio.Mesh.MeshPackageDistributor`.

### Changed

- `AetherNet.Forge` now `ProjectReference`s `AetherNet.Content` (was just `Core`).
  Required because `MeshPackageDistributor` calls `IContentService`.

### Verification

- New tests: `tests/AetherNet.Core.Tests/Security/AesGcmEnvelopeTests.cs`
  (13 cases), `tests/AetherNet.Core.Tests/Forge/MeshPackageDistributorTests.cs`
  (21 cases). **34/34 pass** on net10.0.
- Full Core test suite: **green**.
- Downstream `AetherMedia.LocalLibrary.Tests`: **192/192 pass** on net9.0
  and net10.0.
- Downstream `AetherMedia.Social.Tests`: **68/68 pass** on net9.0 and net10.0.

### Why
"All core AetherNet functions should be on AetherNet." Following the
1.3.0 `MeshInvariants` move and the 1.4.0 `aether://` URI scheme, this
wave brings the symmetric-encryption envelope and the generic
mesh-package distributor where they belong — the protocol repo — so
every C# consumer (Bruh!, SDPKT, txtMe!, third-party) can use them
without taking a transitive dependency on `AetherMedia`.

---

## [1.4.0] — 2026-06-09

### Added — `aether://` URI scheme

New first-class URI scheme for addressing resources on the Aether mesh. A URI
like `aether://KXJB7-MN2P4/content/sha256-abc?codec=opus#t=1m30s` parses
identically across all 8 SDKs and dispatches to a per-app handler manifest.

**C# reference implementation (`AetherNet.Core/Uri/`)**

- `AetherUri` — readonly struct, immutable value. `Parse(s)` / `TryParse(s, out u, out err)`.
- `AetherUriBuilder` — fluent builder.
- `AetherUriException` — parse/build/dispatch failure.
- `AetherUriHandlerDescriptor` — `(handlerName, pathTemplate, expectedQueryKeys, description)`
  with `{param}` route capture.
- `AetherUriHandlerManifest` — per-app registry of handlers with `Resolve(uri)`.
- `IAetherUriRouter` + `AetherUriRouter` — thread-safe in-process dispatcher.

**Documentation**

- `docs/aether-uri-scheme.md` — RFC-style ABNF grammar, design principles,
  reserved-handler list, security notes, cross-language conformance contract.

**Cross-language conformance corpus**

- `tests/cross-language/uri-fixtures.json` — 14 valid + 11 invalid + 6 manifest
  cases. Every AetherNet SDK MUST drive this corpus through its parser; the
  fixture is the source of truth for byte-equal canonical output.

**Tests**

- `tests/AetherNet.Core.Tests/Uri/` — 58 hand-written + 32 fixture-driven =
  90/90 pass.
- Full Core test suite: **713/713 pass** on net10.0.

### Why
Apps need a stable, OS-routable address format. Without it, deep-links,
QR codes, and cross-app navigation (AetherMedia opening a watch session shared
via AetherTxTMe) cannot work. `aether://` is that contract.

### Language ports — full 8-SDK coverage

| SDK | Location | Tests | Verification |
|-----|----------|-------|--------------|
| C# (reference)   | `src/AetherNet.Core/Uri/`                       | 90/90 pass         | `dotnet test` net10.0 |
| Go               | `go/uri/`                                       | 90 cases pass      | `go test` + `go vet` + `-race` clean |
| Python           | `python/aethernet/uri/`                         | 87 pass            | `pytest` + `pyflakes` + `ruff` clean |
| TypeScript       | `typescript/src/uri/`                           | 62 pass            | `vitest`, `tsc --noEmit` clean (scope) |
| Kotlin           | `kotlin/src/main/kotlin/aethernet/uri/`         | 123 pass           | `./gradlew test` BUILD SUCCESSFUL |
| Rust             | `rust/src/uri/`                                 | 60 pass            | `cargo test` + `cargo build --release` clean |
| Swift            | `swift/Sources/AetherNetProtocol/URI/`          | type-check clean   | `swiftc -typecheck -strict-concurrency=complete -swift-version 6`; Mac/Linux CI runs full XCTest |
| C                | `c/src/aethernet_uri.c` + `c/include/aethernet/aethernet_uri.h` | 63 pass | gcc `-Wall -Wextra -Werror` clean; Mac cmake+ctest verified post-push |

Every SDK drives the same `tests/cross-language/uri-fixtures.json` corpus
(14 valid + 11 invalid + 6 manifest cases) — byte-equal canonical output is
the conformance contract.

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
