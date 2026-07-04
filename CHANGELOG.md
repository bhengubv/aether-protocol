# Changelog

All notable changes to `aether-protocol` are documented in this file.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning: [Semantic Versioning](https://semver.org/spec/v2.0.0.html) —
see [VERSIONING.md](VERSIONING.md) for wire-break promotion rules.

---

## [Unreleased]

**Serverless-integration gap closure.** Five cross-language gaps closed at full function
with byte-identical wire parity preserved (no mesh wire-serialization or fixture changed).
Verified per language; Swift and C on the macOS build server.

### Added

- **Transport-backed WebRTC signalling carrier** (`RelayWebRtcSignaling`) — carries the WebRTC
  SDP/ICE offer/answer handshake **out-of-band over any transport** (the circuit relay, the mesh,
  or an in-process channel), so two nodes negotiate a direct data channel with **no central
  signalling server**. Framed with a 4-byte `AWS1` magic + JSON body — out-of-band, so it changes
  no mesh wire-serialization and no fixture. Shipped in all 8 WebRTC-capable SDKs; full headless
  offer→answer→direct-data handshake verified in Go, TypeScript, and Rust; carrier round-trip +
  byte-parity elsewhere.
- **Circuit-relay-v2 auto-selected as a serverless fallback transport** — the native
  `CircuitRelayControl` (PacketType **57**) relay engine is now wrapped as a transport and
  registered with the `TransportManager` at last-resort power cost, so a node with no direct path
  to a peer transparently routes through a third node that can reach both — decentralised, no
  libp2p, no server. Shipped in all 8 SDKs; wire messages are the byte-identical `RelayFrame`
  corpus under `fixtures/circuit-relay/`.

### Security

- **Fail-closed route-reply (RREP) verification** (all 8 SDKs) — AODV-style routing no longer
  installs a forward route from an unverified RREP. The default verifier now **REJECTS** every
  route reply (an absent or partial verifier is fail-closed), and a real `Ed25519RouteReplyVerifier`
  accepts only an RREP carrying a valid Ed25519 signature from the node it claims to originate from
  (checked over the shared canonical signable bytes). Closes the RREP-hijack / blackhole hole. **C**
  previously performed *no* RREP verification at all and now ships a fail-closed verify hook. No
  wire-format change — the packet signature field already existed and the signable-data layout is
  reused unchanged.

### Fixed

- **TypeScript packet-signing determinism** — the canonical signable-bytes builder used
  `Buffer.allocUnsafe` + a `DataView` over Node's shared buffer pool, writing to the pool offset
  instead of the buffer and producing non-deterministic signable bytes (Ed25519 verification failed
  intermittently under load). Fixed to `Buffer.alloc` + offset-correct writes. **Byte layout
  unchanged** (same fields, widths, little-endian order); the cross-language fixture corpus is
  unaffected.
- **C `aether-tests` aggregate** — `test-circuit-relay-fixture` (the circuit-relay byte-parity test)
  was registered but omitted from the aggregate build target, so a fresh/CI build never compiled its
  binary. Added to the aggregate `DEPENDS`; verified on the macOS build server.
- **Signalling frames are now byte-identical across all 8 languages.** The transport-backed WebRTC
  carrier's `AWS1`+JSON control body is now escaped exactly like C#'s `System.Text.Json`
  `JavaScriptEncoder.Default` in every SDK — the seven bytes `+`, `<`, `>`, `&`, `'`, `"` and
  backtick, plus every non-ASCII scalar as an uppercase `\uXXXX` per UTF-16 code unit (astral
  scalars as a surrogate pair). Go, Rust, C, Kotlin, and Swift previously used their stdlib JSON
  encoder and produced a divergent frame whenever an SDP/ICE value carried one of those bytes
  (e.g. a base64 `+` in an ice-pwd, or a non-ASCII `a=ice-ufrag`); C#, Python, and TypeScript were
  already exact. The **C** carrier additionally gained an `sdp_mline_index` field so a candidate
  frame carries the real m-line index rather than a hardcoded `0`. These are out-of-band control
  frames — no mesh wire-serialization and no fixture changed.

### Documentation

- Documented the circuit-relay-v2 engine (PacketType 57) and its `fixtures/circuit-relay/` corpus.
- Corrected the WebRTC-transport comments across all 8 SDKs (and both WebRTC READMEs) to the
  **serverless-by-default** design — host-candidate-only ICE, STUN/TURN optional for NAT traversal.
- Marked the one-time-prekey-pool and C Signal-Protocol notes **resolved** in `docs/THREAT_MODEL.md`,
  `docs/index.md`, and `docs/PROTOCOL_SPEC.md` after verifying a default-100 OPK pool and a full
  X3DH + Double Ratchet C implementation in all 8 languages.
- Corrected the README cross-language fixture counts (**17** wire-format fixtures, **6** Signal test
  vectors); fixed the `CONTRIBUTING.md` repo URL and aligned its contribution posture with the README.
  (Translated docs under `docs/i18n/**` carry the same stale ICE/OPK/C-Signal wording and are a
  tracked follow-up.)

---

## [2.3.0] — 2026-07-03

**Security & privacy layer.** A recovery-phrase backup, Bluetooth
tracking-protection, panic-wipe, and decentralised multi-device sync — each implemented in all
eight languages and pinned to a shared cross-language fixture. Additive; **no wire-format change to
existing packets**. These sit *alongside* the 18-service wire suite, not inside it: three are
**local** (no new packet type), and multi-device sync carries its own envelopes inside the existing
DTN/mesh path. Swift and C additionally verified on the macOS build server.

### Added

- **Recovery-phrase backup** (`AetherNet.Security.Backup`) — standard **BIP-39** encoding of the
  32-byte Ed25519 identity seed as a **24-word** phrase, SHA-256-checksummed (a mistyped word is
  rejected, never silently wrong), verified against the official Trezor test vectors. Restore a node
  from the words alone — no server, no custodian. `fixtures/bip39/`.
- **Bluetooth tracking-protection** (`AetherNet.Security.Privacy.BlePrivacy`) — rotating, key-derived
  BLE Service UUID (HMAC-SHA256, 15-minute window) + IRK-based resolvable private addresses (the RFC
  `ah` function, AES-128) to defeat passive BLE device-tracking. `fixtures/bleprivacy/`.
- **Panic-wipe** (`AetherNet.Security.Privacy.PanicWipe`) — duress-PIN (SHA-256, constant-time
  compared) triggered secure-erase of all identity key material (overwrite-with-random, then zero),
  over a fixed manifest of identity key names. `fixtures/panicwipe/`.
- **Multi-device sync** (`AetherNet.Security.Sync`) — decentralised, server-less device-linking: an
  Ed25519-signed `DeviceLink` pairs a user's own devices, and last-write-wins `SyncRecord` binary
  envelopes reconcile state, carried E2E-encrypted over DTN/mesh — no cloud account, no sync server.
  `fixtures/sync/`.

### Notes

- The `DeviceLink` Ed25519 signature is **byte-identical across 7 of the 8 languages**. Apple's
  CryptoKit deliberately randomises Ed25519 signatures, so Swift reaches **verification** parity (a
  valid, byte-differing signature) — the signed body stays byte-identical and every link cross-verifies
  on all 8 SDKs. It is the only place in this layer where "byte-identical" carries an asterisk.
- New wire formats (`SyncRecord`, `DeviceLink`, the BLE rotating-UUID + RPA scheme) are specified in
  `docs/PROTOCOL_SPEC.md` §12; threat model in `docs/THREAT_MODEL.md`.

---

## [2.2.0] — 2026-07-02

**The full wire-service suite — every reserved packet type is now a real, byte-identical
service in all eight languages.** Additive; no wire-format change to existing packets. Closes the
"reserved-but-unserviced PacketType" gap surfaced by a two-pass protocol audit: 18 packet types
that the wire format had reserved but no SDK actually produced or handled now have thin,
fixture-locked services (produce + handle + event) across C#, Go, Python, TypeScript, Kotlin,
Rust, Swift, and C. Swift and C additionally verified on the macOS build server.

### Added

- **Presence** — beacon (PacketType 21) + query (22) over a rotating, key-derived ephemeral
  routing ID plus a coarse geohash (never the stable identity). `fixtures/presence/`.
- **Heartbeat** — lightweight peer-liveness keep-alive (10). `fixtures/heartbeat/`.
- **Profile sync** — signed profile-card exchange over the mesh (23). `fixtures/profiles/`.
- **Ephemeral-routing-ID announce** — directed transport of an encrypted ERID announcement so a
  friend can still reach you after your routing ID rotates (56). `fixtures/erid/`.
- **Pre-key exchange** — request (25) + response (26) transport of a Signal pre-key bundle over
  the mesh, to bootstrap an end-to-end session with an unmet peer. `fixtures/prekey/`.
- **Channels** — signed messages to a private, members-only group channel (7). `fixtures/channels/`.
- **Push-to-talk** (15) + **screen share** (32) — binary media frames sharing the 29-byte
  VoiceCall/VideoFrame header (call_id big-endian, sequence/timestamp little-endian, flag byte).
  `fixtures/media/`.
- **Call control** — ring / accept / decline / hang-up signalling for voice and video (27).
  `fixtures/videocall/`.
- **SOS acknowledgement** — delivery confirmation back to the sender of an emergency broadcast
  (6). `fixtures/sos/`.
- **Space breadcrumbs** (40), **Forge announce** (41), **Vault shard request** (42) — the wire
  bindings that let the existing Space / Forge / Vault modules produce and handle their mesh
  packets. `fixtures/space/`, `fixtures/forge/`, `fixtures/vaultshard/`.
- **Bandwidth measurement (ABMF)** — probe (53) / ack (54) / gossip (55) binary link-throughput
  wire binding, little-endian, `expected_hex`-pinned. `fixtures/bandwidth/`.
- **Documentation & discoverability.** README rewritten around a "What you get — every service,
  in every language" capability table (all 18 services with packet numbers, fixture paths, and
  8/8 language coverage), a byte-identity headline, a definitional lede, and an 8-question FAQ.
  Human-language translations expanded from 11 to **21** — added isiZulu, Afrikaans, Sesotho,
  Kiswahili, Hausa, Amharic, Hindi, Indonesian, Bengali, and Urdu under `docs/i18n/`, and fixed
  the per-file language-selector bar so cross-language navigation resolves from every page. Added
  `llms.txt`, a `robots.txt` AI-crawler allowlist, and `CITATION.cff` for search/LLM discoverability.

### Notes

- Each service is a thin binding — it produces and handles its wire packet and raises events; the
  host application wires it to its Signal session, routing table, and local state. This is the
  protocol layer, on the same honest RF footing as the rest of the SDK (radio paths remain
  field-unverified until the hardware bring-up tracked in `OPEN_ISSUES.md`).

---

## [2.1.0] — 2026-07-01

**libp2p PeerID identity bridge + decentralised relay layer (spikes).** Non-breaking; no
wire-format change. Adds the identity handoff that lets an AetherNet node compute its own and
any peer's libp2p PeerID from the Ed25519 public key alone — the bridge to the global libp2p
relay / DHT — at full eight-language parity.

### Added

- **`Ed25519 public key → libp2p PeerID` derivation at full eight-language parity** (C#, Go,
  Python, TypeScript, Kotlin, C, Rust, Swift). Pure and deterministic —
  `identity-multihash(protobuf(Ed25519, pubkey))` → base58btc, no multibase prefix — byte-identical
  across languages and verified against real `js-libp2p` output. New `fixtures/peerid/` cross-language
  corpus + Go oracle (`go/cmd/peeridfixturegen`). The C# surface is `AetherNet.Identity.PeerId`
  (in `AetherNet.Core`).
- **Decentralised relay layer** under `relay/` — feasibility-verified libp2p spikes with green
  tests: circuit-relay-v2 reservation + relayed connect, DCUtR hole-punch, SFrame-over-WebRTC
  blind forward (byte-exact), a .NET↔js-libp2p host, and an in-browser (WebView) libp2p boot.
  In-repo substrate research for riding existing global libp2p networks before AetherNet has its
  own node fabric; additive, and not shipped in the SDK packages.

---

## [2.0.0] — 2026-06-26

**Breaking wire-format release.** The delay-tolerant networking (DTN) bundle
envelope moves from JSON to a canonical **binary** wire format, byte-identical
across all eight language implementations. A 2.0.0 node and a 1.x node can no
longer exchange DTN bundles — hence the major bump, per the wire-break rule in
[VERSIONING.md](VERSIONING.md). The cross-language fixture corpus
(`fixtures/dtn/`) pins the exact bytes and `fixture-interop.yml` gates them.

### Changed — BREAKING

- **DTN bundle wire format: JSON → binary envelope.** One canonical little-endian
  envelope for `DtnBundle` (PacketType 18), `CustodyAck` (19) and
  `DeliveryReceipt` (20), with a `format_version` byte for future migration.
  Replaces the JSON serialisation that had silently diverged across languages
  (base64-vs-int-array payloads, ISO-vs-millisecond timestamps,
  dashed-uuid-vs-hex ids). Pinned by `fixtures/dtn/expected/*.bin` generated from
  the Go oracle. Clean break — there is no dual JSON/binary read path.
- **C SDK: real DTN store-and-forward + epidemic replication.** The C node now
  accepts custody of third-party bundles — holds, acks and relays them (it
  previously dropped them) — reaching custody parity with the other SDKs.

### Added

- **Phase-2 application modules at full eight-language parity**, each with
  cross-language behavioural tests: **Vault** (Reed-Solomon erasure-coded
  backup), **Forge** (mesh package cache), **Space** (geo-pinned breadcrumb
  noticeboards), **Market** (P2P marketplace + Proof-of-Vicinity escrow) and
  **FMHY** (markdown content catalogue + parser).
- **Real P-256 ECDSA signature-verify fallback** across all eight SDKs,
  fixture-verified (PROTOCOL_SPEC §7.5). C vendors micro-ecc; the others use
  their platform crypto.
- **WebRTC P2P internet transport** (`AetherNet.Transport.WebRtc`) across all
  eight languages — a serverless data channel (DTLS-SRTP) with SDP/ICE carried
  over an injected signalling channel, loopback-verified. This is the first real
  (non-simulated) transport the cross-language ports can carry; it does not
  change the mesh wire format. (Swift builds; its runtime test is gated in CI by
  the `libdatachannel` system dependency.)
- **Real LoRa serial transport** driver across all eight languages (previously a
  stub everywhere).
- **Real NFC and NearLink transports** on Windows (BLE-GATT central with an RSSI
  proximity gate) and Android (HCE / SSAP-over-BLE-GATT service).

### Fixed

- Replaced a cluster of build-and-drop / fire-once stubs with real behaviour:
  the C voice, group-voice and streaming paths now serialise and transmit on
  send; Rust surfaces inbound voice, group-voice, media segments and video
  frames; the C# `NullGroupKeyProvider` emits the documented warning instead of
  silently sending plaintext; the C and Python anomaly detectors use a real
  windowed geohash rate-limit (was fire-once-per-node-forever); the Wi-Fi Direct
  Group-Owner reply path is now bidirectional; Rust bandwidth surfaces
  locally-probed transports before the first gossip.
- A systemic **stub-guard** test now fails the build on any new stub marker.
- Test-suite hardening: the Python `_run` helpers use a persistent per-module
  event loop (kills cross-file pollution — 130 bulk failures → 0; suite now
  745 pass / 1 skip), and the Rust DTN integration tests were migrated to the
  binary envelope.
- **WebRTC native transport bring-up.** The opt-in native loopbacks (Swift via
  `libdatachannel`, Kotlin via `webrtc-java`) are now proven end-to-end, which
  surfaced and fixed two real defects: a Swift use-after-free in `WebRtcPeerLink`
  teardown (a callback could fire into a freed peer — user-pointers are now
  retained and teardown is deferred and idempotent) and a Kotlin crash on
  headless mesh nodes (`webrtc-java` aborted bringing up audio hardware; it now
  uses a dummy `AudioDeviceModule`).
- De-flaked two timing-sensitive tests (Rust pre-key-rotation persistence, C#
  bandwidth confidence-advance), made the Swift test suite strict-concurrency
  clean, and raised the Rust MSRV to 1.88.

### Security

- **C SDK HMAC-SHA256 key-length fix.** `aethernet_hmac_sha256` now uses the
  streaming libsodium HMAC API (`crypto_auth_hmacsha256_init/update/final`). The
  one-shot `crypto_auth_hmacsha256` it previously called reads a *fixed* 32-byte
  key and ignores the supplied key length — an out-of-bounds read for keys
  shorter than 32 bytes. No in-tree AetherNet call site passed a short key, but
  the helper is public API.

### Tests

- Cross-language test coverage was re-verified by reading every SDK's tests directly
  (an earlier automated audit had wrongly flagged many thorough suites as shallow).
  The three real gaps it surfaced are now filled with behavioural tests: Go's
  in-memory + filesystem key-value stores (round-trip, durability across instances,
  namespacing, input validation), Rust's single-node `InMemoryPoVService`
  (issue/verify/accept/score, tampered-token + self-vouch rejection, defection
  penalty), and Kotlin's `MeshTipService` send/receive (broadcast vs routed unicast,
  settlement, relay-onward, drop paths). All other reported gaps were already covered.

### Dependencies

- Dependabot bumps across Rust, .NET, npm, Go and Kotlin.

---

## [1.8.0] — 2026-06-14

**Money layer + real Vault/PoV + 9-language parity.** A generic mesh-level
incentive surface so any application can settle a tip on top of an
established AetherNet relationship without each app inventing its own
billing protocol — plus the cryptography we promised the threat model.

### Added — `AetherNet.Tipping`

New package shipping the generic `TipPacket(24)` send/receive primitives.
A node now exposes a `SettleMeshTipAsync` provider hook; routers settle a
tip across any path the sender can already reach. Tipping is wire-level —
it does not depend on any single currency layer, including `Sdpkt`.

### Added — `PoVTokenExchange(43)` packet

Pairs with the existing Proof-of-Vault / Proof-of-Velocity stack so the
network can verify a tip is backed by actual stake without a centralised
oracle. Default interface method on the relationship provider — existing
implementations compile unchanged.

### Changed — Vault: stub → real Cauchy–Reed–Solomon

`AetherNet.Security.Vault` replaces the previous placeholder with a
production Cauchy-Reed-Solomon implementation. Wire format unchanged;
shard sizes and parity ratios fixed at the values exposed in 1.6.x. The
codec is implemented once and shared across all 9 SDKs.

### Changed — PoV: stub → real Ed25519

`AetherNet.Security.Pov` replaces the placeholder verifier with real
Ed25519. Mirrors the canonical RustCrypto reference; conformance fixtures
in `tests/cross-language/` lock the byte-for-byte outputs across every SDK.

### Added — 9-language parity (ArkTS / HarmonyOS joins as the 9th SDK)

Cross-language parity proven byte-identical on the new tipping +
Vault + PoV surface across **C#**, **Go**, **Python**, **TypeScript**,
**Kotlin**, **Rust**, **C**, **Swift**, and **ArkTS (HarmonyOS)**. The
Swift build is Mac-gated as before; the ArkTS port ships from a
Windows-side `npx tsx` test harness with the same fixture corpus.

### Compatibility

Non-breaking. `SettleMeshTipAsync` and `PoVTokenExchange` are default
interface methods — existing `IAetherNetRelationship` implementations
compile and run unchanged.

---

## [1.7.0] — 2026-06-13

**ERID — the Ephemeral Routing Id privacy primitive.** Closes the
single largest metadata-leak surface the 1.6 threat-model audit flagged:
a node's wire address no longer doubles as a long-term identifier. Pairs
with eight cross-cutting privacy + anti-spoofing fixes that landed
alongside.

### Added — `EphemeralRoutingId` (the T2 primitive)

A rotating, key-derived wire address. The address itself is a function
of `(identity, epoch)`; everyone who already knows the identity can
derive the current ERID, anyone who doesn't sees an opaque rotating
string. Implemented in `AetherNet.Core` and `AetherNet.Security`.

### Added — `EridDirectory`

Resolves the rotating wire address inside an established relationship.
Replaces the prior long-term routing table for in-relationship lookups.
The directory salts entries so the on-the-wire reputation gossip never
exposes the underlying UHID.

### Added — `EridAnnounce` packet type `(56)`

The handshake frame for advertising the current ERID to an already-
authenticated peer. Capability-gated (see below) so unupgraded nodes
silently fall back to the legacy routing.

### Added — `EridAnnouncementCodec`

The canonical wire codec for the ERID handshake. Identical byte output
across all 8 SDKs, proven via `tests/cross-language/erid-fixtures.json`.

### Added — `EridExchangeService`

Stage-1d in-session ERID exchange. Two peers that already share a
Signal-derived identity rotate their wire addresses without
re-handshaking the application layer.

### Added — `erid-routing` capability gate

The capability is declared but **not yet advertised by default** in
1.7.0; this is the upgrade-window window for downstream apps to adopt
the new primitive before nodes start filtering on it.

### Changed — Privacy hardening across the wire

- Salt directory names on the wire (private DNS-style resolution).
- Drop the free-text `Reason` field from reputation gossip so trust
  reports stop leaking application-layer text.
- Uniform `aether/2` handshake banner across all 7 language ports —
  removes the technology fingerprint that allowed network observers to
  identify CircleAether deployments.

### Fixed — Anti-spoofing / DDoS resistance

- Rate-limit + score the **authenticated** neighbour, not the spoofable
  `SourceUhid`. Closes the spoof-and-overload pattern the 1.6 audit
  caught.
- Sybil-proof reputation gossip — reports are weighted by **earned**
  trust, not raw standing. A flood of new identities can no longer
  manipulate the aggregate.
- Relay rate cap on discovery (`RequestRateLimiter`) — one flooder can
  no longer drain the shared aggregate for everyone else.
- Fix rate-limiter ordering so the per-neighbour bucket evaluates before
  the global aggregate.

### Added — `IRoutingService.HandleRouteRequestAsync(...)` link-layer sender

Anti-spoofing fix surfaced a new parameter on the routing service. Test
doubles must implement it; the new signature is the 1.7.0 wire contract.

### Added — 8-language parity

ERID + the privacy hardening above proven byte-identical across **C#**,
**TypeScript**, **Go**, **Python**, **Rust**, **Kotlin**, **Swift**
(Mac-gated), and **C**. Conformance corpus in `tests/cross-language/`.

### Compatibility

Non-breaking for callers. Test doubles of `IRoutingService` must
implement the new `linkLayerSenderUhid` parameter; the change is
required for any custom fake or replay in downstream test suites.

### Tooling / housekeeping

- Coordinated dep-major migration: **Kotlin 2.2 + Gradle 9 + kotest 6**,
  **RustCrypto v2** (criterion → divan), **Microsoft.NET.Test.Sdk 18.6**,
  **coverlet 10.0**, **TS dev deps**, **golang.org/x/crypto 0.51**, and
  five GitHub Actions to current majors.
- Kotlin AOSP-Soong compatibility: hand-rolled JSON in 4 wire types.
- Swift fix-ups: actor-isolated monitor config setters; off-by-one in
  the confidence-progression unit test.

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
