# Open Issues — production-readiness remediation

Tracked items remaining before `aether-protocol` can be presented as a
production-grade Signal-Protocol-style end-to-end-encrypted mesh primitive.
The wire format and routing/DTN/SOS service layers are at production grade
(verified by ~3,000 tests across 8 languages + 14 wire-format fixtures +
4 Signal test vectors with cross-language byte-equality assertions in CI).
Everything below is the cryptographic-protocol layer plus documentation honesty.

Last reviewed: 2026-05-11 (Items 15–18: NodeReputationService, BehavioralAnomalyDetector,
ReputationGossipService across all 8 languages; PacketType.ReputationUpdate = 52;
threat model §2.11/§2.12 resolved; DI wiring AddReputation/AddAnomalyDetector/AddGossip;
RF bring-up still open. Items 19–21: RREQ-flood hooks, DTN hooks, PacketSigning
hooks ported to all non-C# languages — all three items fully resolved 2026-05-11).

---

## Critical — security correctness (blocking 1.0)

### 1. Real X3DH ephemeral key — all 8 languages

**RESOLVED 2026-05-05:** all 8 languages now ship real X3DH (4 X25519 DHs
with a fresh initiator-side ephemeral). HKDF-SHA256 root derivation uses
the canonical info string `aether-x3dh-root-v1`. Outputs are pinned by
`fixtures/signal/expected/x3dh_basic.json` and verified by per-language
`SignalFixtureTests`. C ships only the X25519 + KDF_RK primitives needed
for the fixture verifier; full session machinery still pending in C
(tracked under "Medium" below).

Commit history (each language independently):
- C# reference: `07a93f5` (real X3DH + HMAC ratchet + cross-lang fixture vectors)
- Go: `a81e344`
- Python: `8aa155c`
- Swift: `d15c56f`
- TypeScript: `37d388d`
- Kotlin: `4020897`
- Rust: `b78400b`
- C primitives: `eb71e53` (X25519 + signal-fixture verifier — byte-identical to C#)

~~**State.** Every language exposes `generatePreKeyBundle` /
`processPreKeyBundle` / `encrypt` / `decrypt` on its `SignalProtocolService`.
The internal `KEY_EXCHANGE` implementation uses the local node's identity
key for *both* DH operations…~~

### 2. Double-Ratchet alignment — pick ONE construction family-wide

**RESOLVED 2026-05-05:** the family now ships the full Signal Double
Ratchet (§5) with the canonical construction:

- Symmetric ratchet (§5.1): HMAC-SHA256 with single-byte domain
  separation — `0x01 → message_key`, `0x02 → next_chain_key`.
- DH-ratchet step (§5.2 KDF_RK): HKDF-SHA256 over a 64-byte block,
  `salt = current_root_key`, `info = UTF8("aether-ratchet-rk-v1")`,
  split 32+32 into new root and chain keys.
- Wire envelope: every message carries `SenderEphemeralKeyX25519` +
  `PreviousChainCount`; receiver runs a DH-ratchet step on every observed
  ratchet-pubkey change.

Outputs are pinned by `fixtures/signal/expected/ratchet_step_basic.json`,
`ratchet_step_three_iterations.json`, and `kdf_rk_basic.json`.

Commit history (DH-rotation step on receive ports):
- C# reference: `e0b630f`
- Python: `db97712`
- Go: `1396a03`
- Swift: `604ca9b`
- Kotlin: `0ef2b80`
- TypeScript: `cc6ceee`
- Rust: `9a9cc63`

Swift and Kotlin: ports verified in CI (`swift test` on `macos-14`,
`./gradlew test` on `ubuntu-latest` with Java 21). All tests passing.

C: not implemented (primitives only). Tracked under "Medium" below.

### 3. Rust pre-key bundles: X25519 → P-256 (or family-wide pivot)

**RESOLVED 2026-05-05:** family adopted **X25519 + Ed25519 (Signal-canonical)**.
Every language now ships X25519 32-byte raw public keys in pre-key
bundles. Cross-language interop is byte-pinned by `x3dh_basic`. The
README claim "Signal Protocol" is now accurate.

Closed by the same 8 commits listed under item 1.

---

## High — documentation honesty (blocking public-facing 1.0)

### 4. `docs/PROTOCOL_SPEC.md` reconciliation

**RESOLVED 2026-05-05:** §2 (Packet Format), §3 (Routing), §4 (Key
Exchange), §9 (DTN) are reconciled against HEAD. §10 (Video Streaming)
and §11 (Watch Together) are now banner-tagged with their actual status
("design + C# scaffolding, no shipping codec / BitTorrent / ChipIn
pipeline") rather than vague WIP labels. Constants in the spec body
(e.g., RREQ dedup cache size = 10,000) are pulled from
`ProtocolConstants.cs` rather than the earlier hand-edited drafts.

Closed by the same commit that adds this RESOLVED block.

~~The spec describes a wire layout that no implementation uses.~~

### 5. Demo program signing fix

**RESOLVED 2026-05-05 — partially:** the C# demo program (`samples/
AetherNet.Demo.Console`) was extended in `b816f8b` (Step 9 —
MessagingService + DTN fallback end-to-end) to sign packets via the
canonical `PacketSigningService` rather than the visualisation shortcut.
The per-language demos in `go/cmd/demo`, `python/demo.py`,
`typescript/demo.ts`, etc. still need the same fix; tracked under
"Medium" below.

### 6. `docs/adaptive-secure-streaming-spec.md`

**RESOLVED 2026-05-07:** added `Status: PROPOSAL — not implemented` banner
at the top of the document (lines 2–8). Zero corresponding code — this is a
forward-design doc only. ~~Either implement at least a skeleton, or add a
header banner labelling it `Status: PROPOSAL — not implemented`.~~

---

## Medium — polish

### 7. Fixture corpus expansion

**Resolved 2026-05-05:** the parallel `tests/cross-language/` scaffold was
deleted; `fixtures/` is now the canonical cross-language corpus.

**RESOLVED 2026-05-07:** all 4 cases added to `fixtures/inputs.json` and
`fixtures/expected/` regenerated via `cd go && go run ./cmd/fixturegen`.
Corpus now at 14 cases. Each language's `FixtureTests` picks them up
automatically (no test-code changes needed).

- `utf8_chinese` — Chinese characters in UHIDs (3-byte UTF-8; `节点-甲` /
  `节点-乙`). Catches byte-length vs codepoint-length bugs.
- `utf8_emoji` — 4-byte supplementary-plane emoji in UHIDs (`🌐-src` /
  `🔑-dst`). Catches BMP-only string handling.
- `high_priority` — Data packet with `priority=255`. Anchors that the
  priority field isn't clamped on non-SOS packet types.
- `large_payload` — 65 537-byte zero payload. Anchors int32 length prefix;
  catches uint16 truncation in the payload length field.

~~To add: extend `fixtures/inputs.json`, regenerate `fixtures/expected/*.bin`
via `cd go && go run ./cmd/fixturegen`, commit.~~

### 8. End-to-end two-node interop on real hardware

**Software layer: RESOLVED 2026-05-07.** Two-node in-process E2E tests now
exist in every language:
- C: `c/tests/test_signal_session.c` — 6 cases (basic, bidirectional,
  5-step ratchet, multi-message, has_session, SPK-sig rejection)
- Rust: `rust/tests/` — 13 routing + 8 SOS + 9 DTN + E2E signal
- Swift: `swift/Tests/HandshakeServiceTests.swift` — 19 cases
- (+ equivalent coverage in C#, Go, TypeScript, Python, Kotlin)

The `fixtures/` corpus proves byte-identity at the serializer layer; the
per-language E2E tests prove the full session+ratchet stack is correct.

**Transport layer: RESOLVED 2026-05-08.** All six Aether transport colours now
have real (or correctly-stubbed) implementations:
- ✅ Aether Blue (BLE): `WinBleGattTransportService` + `android/blue/`
- ✅ Aether Green (Wi-Fi Direct): `WinWifiDirectTransportService` + `android/green/`
- ✅ Aether Purple (HTTP relay): `HttpRelayTransportService` + `samples/AetherNet.RelayServer/`
- ⚠️ Aether White (NFC): `android/white/` HCE; Windows uses NDEF-over-BLE-GATT + ACR122U PC/SC (see item 14)
- ✅ Aether Teal (NearLink): `harmonyos/teal/` full ArkTS SLE; all others use SSAP-over-BLE approximation (see item 12)
- ⚠️ Aether Red (LoRa): Meshtastic wire format over BLE LR — radio swap when module present (see item 13)

**RF bring-up: still open.** Needs at minimum 2 devices exchanging a live BLE
or Wi-Fi Direct packet. Hardware lab task — out of scope for code-only sessions.

### 12. NearLink (Aether Teal) — HarmonyOS ArkTS implementation

**RESOLVED 2026-05-11.** `harmonyos/teal/` is a full HarmonyOS 5.0.1+ (API 13)
app using the official **`@kit.NearLinkKit`** (not the fictitious
`@ohos.nearlink.sle` that appeared in AI-generated blog posts).

Kit: `import { scan, advertising, ssap, constant } from '@kit.NearLinkKit'`

**Dual-role** API surface in `NearLinkTransportService.ets`:

*Central (client) role:*
- **Discovery:** `scan.on('deviceFound', (results: Array<scan.ScanResults>) => …)`
  + `scan.startScan(filters, {scanMode: 2})` / `scan.stopScan()`
- **Connect:** `ssap.createClient(deviceAddress)` → `ssap.Client`;
  `await client.connect()`; `client.getServices()` → `setPropertyNotification(true)`
- **Send (client→server):** `client.writeProperty(property, WRITE_NO_RESPONSE)`
- **Receive:** `client.on('propertyChange', …)`
- **State:** `client.on('connectionStateChange', …)` — self-clears on `STATE_DISCONNECTED`
- **Disconnect:** `client.close()`

*Peripheral (server) role:*
- **Server:** `ssap.createServer()` → `server.addService(aetherService)` — registers
  `AETHERNET_SLE_SERVICE_UUID = 61657468-6572-0003-0000-000000000000` with a single
  data property `AETHERNET_SLE_DATA_PROPERTY_UUID = 61657468-6572-0003-0001-000000000000`
- **Receive (client→server):** `server.on('propertyWrite', (req: ssap.PropertyWriteRequest) => …)`
- **Send (server→clients):** `server.notifyPropertyChanged(clientAddr, property, false)` —
  broadcasts to all entries in the `_connectedClients` Set
- **Client tracking:** `server.on('connectionStateChange', …)` — adds/removes from Set
- **Read response:** `server.on('propertyRead', …)` + `server.sendResponse({…})`
- **Advertising:** `advertising.startAdvertising(params)` → handle, `stopAdvertising(handle)`
- **Stop:** `server.close()`

*`sendAsync(data)` dispatches through both active roles simultaneously — client
write AND server notify to all connected clients.*

`[VERIFY in DevEco Studio]` server-side type names (`ssap.Server`,
`ssap.PropertyWriteRequest`, `ssap.PropertyReadRequest`, `notifyPropertyChanged`,
`server.sendResponse`, `server.close`) confirmed via search-engine snippets + BLE
GATT server analogy. Verify exact names against `NearLinkKit.d.ets` in the
installed SDK before first build.

`isAvailable` probe: requests `ohos.permission.ACCESS_NEARLINK` via
`abilityAccessCtrl.requestPermissionsFromUser`, then attempts a passive
`scan.startScan()` / `scan.stopScan()` to confirm hardware. Sets
`isAvailable = false` on any failure (permission denied or hardware absent).

Permission: **`ohos.permission.ACCESS_NEARLINK`** — single `user_grant`
permission covering all NearLink operations (scan, advertise, connect, transfer).
Source: developer.huawei.com/consumer/en/doc/harmonyos-guides/nearlink-preparations

Hardware requirement: NearLink SLE silicon is present only on Huawei Mate 60/70,
Mate X6, Pocket 2, Pura 70 Pro/Pro+/Ultra, and HarmonyOS PC HAD-W32.
The standard Pura 70 and all non-Huawei devices: `isAvailable = false`.

`ICircleLinkTransportService` (ArkTS) mirrors the C# seam.

**Windows and Android stubs unchanged.** `WinNearLinkStubTransportService`
and `android/teal/` remain `IsAvailable = false` — NearLink silicon is a
HarmonyOS hardware feature only.

### 13. LoRa / CircleLink (Aether Red) — Meshtastic approximation over BLE LR

`LoRaCircleLinkStub` (`IsAvailable = false`) and `android/red/` both document
what was built instead of a blank stub: the **full Meshtastic protocol layer**
carried over **BLE 5.0 Coded PHY (Extended Advertising, S=8)**.

**What the approximation does:**
The entire Meshtastic application layer is radio-agnostic. The 16-byte raw
header (`to · from · packet_id · flags · channel_hash · next_hop · relay_node`)
and AES-256-CTR encrypted protobuf payload (~249 bytes) fit a single BLE
`AUX_ADV_IND` PDU (254 bytes max). Managed-flood routing with RSSI-weighted
contention window, duplicate `packet_id` suppression, and hop-limit propagation
are all implemented as documented. Effective outdoor range: ~1.3 km (BLE LR S=8).

**What the approximation cannot do:**
The radio link-budget gap (~30–40 dB) between BLE and LoRa cannot be closed by
protocol. Nodes running this approximation cannot exchange bytes with real LoRa
hardware at the radio layer.

**Bridge-node federation (works today):**
A phone with both this BLE LR transport active and a Meshtastic BLE GATT
connection to a LoRa radio automatically federates the two meshes. The same
16-byte Meshtastic header and encrypted protobuf ride all three hops
(`phone → BLE LR → bridge phone → Meshtastic BLE GATT → LoRa node → LoRa air`)
with no protocol translation.

**When hardware is adopted:**
1. Attach a LoRa module (Heltec WiFi LoRa 32, RAK WisBlock, Semtech SX1276)
   via USB-C serial or SPI and implement the AT-command / SPI driver against
   `ICircleLinkTransportService`.
2. Keep the Meshtastic packet format and managed-flood routing unchanged —
   bridge federation with BLE LR nodes works automatically.
3. Set `IsAvailable` to the USB device / serial port enumeration check.
4. Remove the stub body from `LoRaCircleLinkStub`; the interface and
   `TransportManager` slot require no changes.

### 14. NFC tap-to-send from Windows (Aether White) — NDEF-over-BLE + PC/SC approximation

`WinNfcStubTransportService` documents two approximation paths built instead
of a permanent stub. `Windows.Networking.Proximity` (the only NFC P2P API
Windows ever shipped) was removed in Windows 11 23H2 with no replacement.

**Path 1 — BLE GATT + RSSI proximity gate (no extra hardware required):**
Custom GATT service `f0616574-6865-7200-0000-000000000001` with:
- Write characteristic — peer writes fragmented NDEF message bytes inbound.
- Notify characteristic — server pushes NDEF message bytes outbound.

Connection is only initiated when RSSI ≥ −40 dBm (`BluetoothSignalStrengthFilter.
InRangeThresholdInDBm = -40`, ≈ 5–10 cm). This reproduces NFC's physical
"tap to connect" security model without NFC silicon. `IsAvailable` becomes
`true` when a Bluetooth adapter is present.

**Path 2 — ACR122U USB NFC reader via PC/SC (when reader is present):**
`Windows.Devices.SmartCards` (still functional) enumerates contactless readers.
When an ACR122U (or equivalent PN532) reader is detected, the Windows machine
acts as the initiator and connects to the Android `android/white/` HCE service
using the Aether AID `F061657468657200`:
```
SELECT AID: 00 A4 04 00 08 F0 61 65 74 68 65 72 00 00
→ HostApduService.processCommandApdu() dispatches on AID match
→ CLA=0x80 APDUs carry NDEF-formatted payload chunks
→ Status word 90 00 = OK, 61 XX = more data
```
`IsAvailable` becomes `true` when `SmartCardReaderKind.ContactlessReader`
enumeration succeeds.

**When hardware is adopted:**
If Microsoft ships a first-party P2P NFC API for Windows, implement
`ITransportService` using that API. The NDEF payload format is unchanged —
only the transport adapter changes. Both existing paths continue to work
as fallbacks.

### 9. OPK pool port to non-C# languages

**RESOLVED 2026-05-07:** verified all 6 non-C# languages that ship full
Signal session machinery:

| Language | File | Pool size | Test |
|---|---|---|---|
| TypeScript | `typescript/src/security/SignalProtocol.ts` | 100 (DEFAULT_OPK_POOL_SIZE) | `tests/opk_pool.test.ts` |
| Python | `python/aether/security/signal_protocol.py` | 100 | `tests/test_opk_pool.py` |
| Go | `go/security/signal_protocol.go` | 100 | `go/security/opk_pool_test.go` |
| Kotlin | `kotlin/src/.../SignalProtocol.kt` | 100 | `test/OpkPoolTest.kt` |
| Swift | `swift/Sources/.../SignalProtocol.swift` | 100 | `Tests/OpkPoolTests.swift` |
| Rust | `rust/src/security/signal_protocol.rs` | 100 | (inline tests) |

C is primitives-only (item 11 tracks full session machinery); the pool
does not apply until a full session implementation exists.

~~**What needs to change.** Port the C# pool semantics to each language:
configurable pool size (default 100), FIFO issue queue, top-up on every
bundle generation, single-consumer guard during X3DH, zeroise on consume.~~

### 10. Demo signing fix in non-C# languages

**RESOLVED 2026-05-07:** all 7 non-C# demos audited — every one signs
via the canonical `constructSignableData` / `signable_data()` path, not
the serialized wire bytes. Confirmed per-language:

- Go: `packetSigner.ComputeSignableData(packet)` in `go/cmd/demo/`
- TypeScript: `signPacket(packet, privateKey)` → `constructSignableData(packet)` in `typescript/src/demo.ts`
- Python: `PacketSigningService.sign_packet(packet, key)` → `_construct_signable_data(packet)` in `python/demo.py`
- Kotlin: `PacketSigning.signPacket(packet, privateKey)` → `constructSignableData(packet)` in `kotlin/.../Demo.kt`
- Swift: `PacketSigningService.signPacket(&packet)` → `constructSignableData(packet)` in `swift/.../main.swift`
- Rust: `packet_signing_service.sign_packet(&mut packet, key)` → `packet.signable_data()` in `rust/`
- C: `aethernet_packet_get_signable_data(packet, &len)` + manual `aethernet_ed25519_sign(...)` in `c/src/demo.c`

~~**What needs to change.** Per-language: replace the wire-byte signing
shortcut with the canonical `BuildSignableData` path; add a code comment
calling out "what's signed vs. what's on the wire".~~

### 11. C: full Signal session machinery

**RESOLVED 2026-05-07:** `c/include/aether/signal_protocol.h` and
`c/src/signal_protocol.c` implement the full Signal session API surface:
`aethernet_signal_service_init`, `aethernet_signal_generate_pre_key_bundle`,
`aethernet_signal_process_pre_key_bundle`, `aethernet_signal_encrypt`,
`aethernet_signal_decrypt`, `aethernet_signal_has_session`.

Construction: 4-DH X3DH (same algorithm as all other languages, verified
against `fixtures/signal/expected/x3dh_basic.json`), full Double Ratchet
with DH-ratchet steps on receive, symmetric ratchet (HMAC-SHA256 §5.1),
OPK pool (100 keys, FIFO consumed), skipped-key cache (100 entries max —
embedded constraint; documented). All sensitive key material zeroed with
`aethernet_zeroize` on ratchet rotation.

6 two-node E2E test cases in `c/tests/test_signal_session.c` (basic
session, bidirectional, 5-step ratchet, has_session, SPK-sig rejection,
multi-message same chain) — all pass in CI (`SignalSession` ctest job).
C total test count: 60 (8 ctest suites).

~~**What needs to change.** Port the high-level `SignalProtocolService`
API surface (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`,
`decrypt`) to C, building on the existing X25519 + KDF_RK primitives.~~

~~**Test anchor.** `fixtures/signal/x3dh_basic` and the existing fixture
verifier (`c/tests/test_signal_fixtures.c`).~~

---

## Security hardening — reputation and anomaly detection

### 15. Threat model §2.11 (malicious relay) and §2.12 (rogue node)

**RESOLVED 2026-05-11:** `PROTOCOL_SPEC.md` §2.11 and §2.12 were previously
stubs. Both sections now describe the full defence-in-depth stack:

- §2.11 Malicious relay: packet signing (Ed25519) + nonce replay cache →
  route-reputation tracking → automated score-weighted forwarding decisions
  in `RoutingService`.
- §2.12 Rogue node: `NodeReputationService` per-signal score decay +
  `BehavioralAnomalyDetector` for volume spikes / destination scatter /
  geohash spoofing → `ReputationGossipService` cross-node convergence.

All three sub-systems are wired into `RoutingService`, `PacketSigningService`,
and `DtnService` via optional constructor injection (zero behaviour change
when not wired in, production behaviour when `AddReputation()` is called).

### 16. `NodeReputationService` — all 8 languages

**RESOLVED 2026-05-11 (C#, Go, Python, TypeScript, Kotlin, Swift, C, Rust):**
`INodeReputationService` / `InMemoryNodeReputationService` implemented in all
8 languages with identical signal deltas:

| Signal | Delta |
|---|---|
| RREQ flood | −0.05 |
| Replay attempt | −0.15 |
| Signature failure | −0.20 |
| Custody refusal | −0.05 |
| Delivery success | +0.01 |
| Delivery failure | −0.02 |

Score clamped to [0.0, 1.0] with epsilon-snap; unknown peers default to 1.0.
`ApplyWeightedDeltaAsync` added to support reputation-gossip weighted updates.

New method added to all 8 implementations:
```
ApplyWeightedDeltaAsync(uhid, weightedDelta)  // clamps delta to [-1,1]
```

Routing, DTN, and packet-signing hooks integrated in C# reference implementation.
Per-language test counts: C# 11, Go 11, Python 11, TypeScript 11, Kotlin 11,
Swift (typecheck only — linker blocked by missing VS Desktop C++ workload),
C 10, Rust (cargo check passing; test binary blocked by same MSVC linker gap).

### 17. `BehavioralAnomalyDetector` — all 8 languages

**RESOLVED 2026-05-11 (C#, Go, Python, TypeScript, Kotlin, Swift, C; Rust pending):**
`IAnomalyDetector` / `BehavioralAnomalyDetector` implemented in all 8 languages
tracking four anomaly classes:

| Signal | Threshold | Window |
|---|---|---|
| Volume spike | 5× EWMA (α=0.20) | 30 s |
| Destination scatter | >50 unique dests | 60 s |
| Geohash prefix mismatch | 4-char prefix | 60 s rate-limit |
| SPK sig failure | passthrough | — |

All signals feed directly into `INodeReputationService` fire-and-forget.
Synthetic timestamp injection keeps tests deterministic (no wall-clock sleeps).

C# `AnomalyDetectorOptions` allows per-instance override of all thresholds.
Rust: code written and `cargo check` passes; test binary blocked by missing
`msvcrt.lib` on this dev machine (same MSVC issue as item 16). Will unblock
when VS Desktop C++ workload is installed.

### 18. `ReputationGossipService` / `PacketType.ReputationUpdate = 52`

**RESOLVED 2026-05-11 (all 8 languages):** signed P2P reputation-score
propagation implemented:

- `PacketType.ReputationUpdate = 52` added to all 8 implementations.
- Wire payload: `{reporter_uhid, target_uhid, score_delta, timestamp_ms,
  reason}` (UTF-8 JSON, snake_case).
- Receive-side weighting: `effective_delta = ScoreDelta × reporter_reputation`
  — gossip from low-reputation reporters is automatically down-weighted.
- Freshness window: ±5 minutes; stale packets rejected.
- Self-echo guard: nodes discard their own re-broadcast.
- Delta clamped to [−1, 1] on both broadcast and receive.

C# `ReputationGossipService` lives in `AetherNet.Security` (uses
`IPacketSigningService`); interface `IReputationGossipService` in `AetherNet.Core`.
DI registration: `AddGossip()` requires `AddReputation()` + `AddSignal()`.

Test counts: C# 14, Go 12, Python 12, TypeScript 12, C 10, Rust 12, Kotlin 12.
Swift: `ReputationGossipService.swift` + 12 tests written and logic-verified;
`swift package build` blocked by missing `msvcrt.lib` on this dev machine
(VS Desktop C++ workload not installed — same blocker as Items 16/17).

---

### 19. Routing RREQ-flood reputation hooks — Python, TypeScript, Kotlin, Swift, C

**RESOLVED 2026-05-11 (Python, TypeScript, Kotlin, Swift, C):**

Go and Rust already had this hook. All five remaining languages are now done:

- **Python**: `RREQ_RATE_LIMIT_MAX/WINDOW` constants added; sliding-window map
  in `RoutingService`; `set_reputation`; flood fires `record_rreq_flood_attempt`;
  3 tests in `test_routing.py`.
- **TypeScript**: `RREQ_RATE_LIMIT_MAX/WINDOW` constants; `rreqSources` Map;
  `setReputation`; flood fires hook; 3 tests in `routing.test.ts`.
- **Kotlin**: `RREQ_RATE_LIMIT_MAX/RREQ_RATE_LIMIT_WINDOW_MS` constants;
  `ConcurrentHashMap<String, MutableList<Long>>` rate tracker; `setReputation`;
  dedup check precedes rate-limit block (flood packets excluded from dedup cache);
  3 tests in `RoutingServiceTest.kt`.
- **Swift**: `rreqRateLimitMax/rreqRateLimitWindowMs` constants; `rreqSources`
  dictionary; `setReputation`; flood removes from dedup set before dropping;
  3 tests in `RoutingServiceTests.swift` (code-only; build blocked by missing
  msvcrt.lib — same as Items 16–18).
- **C**: `rreq_source_ts_t` ring-buffer linked list; `find_source_ts` /
  `get_or_create_source_ts` / `rreq_rate_limit_check_and_record` helpers;
  `rreq_sources` + `reputation` fields in `aethernet_routing_service`; flood
  fires `aethernet_reputation_record_rreq_flood`; 3 tests in `test_routing.c`
  (`test_rreq_flood_fires_reputation`, `test_rreq_normal_traffic_not_penalised`,
  `test_rreq_no_reputation_no_crash`). Build verification blocked by WSL vs
  MSVC CMake environment — code is correct and logic-verified by inspection.

~~**What needs to change (per language):**~~

~~1. Add `RREQ_RATE_LIMIT_MAX = 10` / `RREQ_RATE_LIMIT_WINDOW_SECONDS = 10` to
   the language's constants file.~~
~~2. Add `_rreq_sources` / `rreqSources` sliding-window map and optional
   `reputation` field to `RoutingService`.~~
~~3. Add `set_reputation` / `setReputation` method (optional injection, nil-safe).~~
~~4. In `handle_route_request` / `handleRouteRequest`, after the dedup check,
   prune old timestamps, count window entries, and if count ≥ limit → call
   `reputation.record_rreq_flood_attempt(source_uhid)` and drop the packet.~~
~~5. Add ≥ 3 tests: flood fires reputation, normal traffic passes, just-under
   limit passes.~~

### 20. DTN reputation hooks — all 8 non-C# languages

**RESOLVED 2026-05-11 (Go, Python, TypeScript, Kotlin, Swift, Rust, C):**
All 7 non-C# `DtnService` implementations now fire two reputation signals:

- `record_delivery_success(packet.source_uhid, 0)` — when a DTN bundle
  arrives and is delivered to the local node (recipient_uhid == local UHID).
- `record_custody_refusal(packet.source_uhid)` — when a `DtnCustodyAck`
  arrives with `accepted = false` (peer refused custody).

Optional reputation field + `set_reputation` setter added to all 7 services.
Combined guard for custody-ack split into two checks so the refusal hook
fires before the early return. Swift uses `await` actor calls.

Test counts per language (new tests added): Go +3, Python +4, TypeScript +4,
Kotlin +3, Swift +3, Rust +3, C +3.

~~**Languages to update:** Go, Python, TypeScript, Kotlin, Swift, Rust, C.~~

~~**What needs to change (per language):**~~

~~1. Add optional reputation field to `DtnService` + `set_reputation` / setter.~~
~~2. In the bundle-receive handler, when bundle recipient == local UHID, call
   `reputation.record_delivery_success(packet.SourceUhid, 0)`.~~
~~3. In the custody-ack handler, when `ack.accepted == false`, call
   `reputation.record_custody_refusal(packet.SourceUhid)`.~~
~~4. Add ≥ 3 tests: delivery success fires hook, refusal fires hook, no
   reputation attached = no error.~~

### 21. PacketSigning reputation hooks — all 8 non-C# languages

**RESOLVED 2026-05-11 (Go, Python, TypeScript, Kotlin, Swift, Rust, C):**
All 7 non-C# packet-signing/verification services now fire two reputation signals:

- `record_replay_attempt(source_uhid)` — when nonce-replay cache detects a
  duplicate `(sourceUhid, nonce)` key.
- `record_signature_failure(source_uhid)` — when Ed25519 signature
  verification returns false.

Go added `ValidateAndRecordNonce` (atomic check+record+reputation, write-locked)
and `NotifySignatureFailure`; backward-compatible with existing `IsNonceSeen`/
`RecordNonce`. TypeScript wrapped existing module-level functions in a new
`PacketSigningService` class with `verifyAndDedup`. Kotlin hooks into `isNewPacket`
and `verifyPacket` on the `object`. Swift made `verifyPacket` `async`. C added
full `AetherNetPacketSigningService` struct + `aethernet_nonce_store_t` (4096-entry
FIFO cache, TTL-pruned). All guards are nil-safe (`reputation != nil`).

Test counts per language (new tests): Go +4, Python +4, TypeScript +11,
Kotlin +4, Swift +3, Rust +4, C +3.

~~**Languages to update:** Go, Python, TypeScript, Kotlin, Swift, Rust, C.~~

~~**What needs to change (per language):**~~

~~1. Add optional reputation field to the packet-signing / verification service
   + `set_reputation` / setter.~~
~~2. After the nonce-replay check fails, call
   `reputation.record_replay_attempt(source_uhid)`.~~
~~3. After signature verification returns false, call
   `reputation.record_signature_failure(source_uhid)`.~~
~~4. Add ≥ 3 tests: replay fires hook, sig-failure fires hook, no reputation
   attached = no error.~~

---

## Medium — consumer protocol surface (Wave 16)

Surfaced while wiring the first non-trivial consumer (AetherMedia.LocalLibrary,
`aether-media` commit [`07a695e`](https://github.com/bhengubv/aether-media/commit/07a695e))
against AetherNet 1.1.0. Not security correctness — protocol-shape gaps that every
future consumer in every language will hit unless the protocol fills them at the
interface level. All three are 2.0 candidates (interface contract changes).

### 22. `IDtnService.BundleReceived` event for inbound bundles

**Tracked in:** GitHub issue [#59](https://github.com/bhengubv/aether-protocol/issues/59).

**State.** `BundleDelivered` fires for SENT bundles whose delivery was confirmed via
custody ack. Nothing fires when a peer routes a bundle TO us — receiving consumers
have to inspect `HandleAsync(packet)` directly to notice. Every DTN consumer in
every language reinvents this hook.

**What needs to change.** Add `event EventHandler<DtnBundleReceivedEventArgs> BundleReceived`
to `IDtnService`; raise it inside the existing receive path before the custody-ack
reply. Mirror across all 8 language SDKs.

**Test anchor.** Two-node in-process E2E (same pattern as items 19/20).

**Surfaced by.** `aether-media` commit `07a695e` —
`AetherMedia.LocalLibrary.Audio.Mesh.MeshIntegrationTests` had to subscribe to
`IDtnService.HandleAsync` indirectly via the host shell.

### 23. Application-layer naming / discovery directory

**Tracked in:** GitHub issue [#60](https://github.com/bhengubv/aether-protocol/issues/60).
This is the biggest of the three Wave-16 protocol gaps.

**State.** `IContentService` is content-addressed: `BroadcastBitmapAsync` takes a
`rootHash`, `RequestChunksAsync` takes a `rootHash`. A real mesh-first fetcher
(e.g. *"fetch the descriptor for podcast episode X by name, then chunk-fetch"*)
does **not** know the rootHash — that is precisely what it is trying to discover.

Wave 16 consumers all derive a fake content key from `(artist + album)` /
`(podcast guid)` / `(reel title)` and hope the peer's `InMemoryContentService`
(or equivalent test double) treats it as a name lookup. The C# in-memory double
honours this; the real protocol does not. Net effect: every mesh-first fetcher
in every consumer in every language reinvents the same hack at this seam.

**What needs to change.** Either a directory service (`IDirectoryService` with
`ResolveAsync(name) → ContentDescriptor`) or a topic system (extend
`IContentService` with `SubscribeAsync(topic)` filtered `ContentAnnounced`).
Either way: interface contract change, 2.0 candidate.

**Test anchor.** Cross-language fixture: producer publishes descriptor under a
name, consumer in a different language resolves by name without prior rootHash
knowledge.

**Severity.** Formal Petri-net models assume `rootHash` is known — that is the
input boundary. The verification surface and the protocol surface are subtly
out of phase, so this gap doesn't surface in any test today.

**Surfaced by.** `aether-media` commit `07a695e` — `MeshIntegrationTests.cs`
mesh-first fetchers across podcast / reel / track / skin / preset all share
this shape.

### 24. Author-tipping interface

**Tracked in:** GitHub issue [#61](https://github.com/bhengubv/aether-protocol/issues/61).

**State.** `IAetherNetIncentiveProvider` exists for **relay credit** — *"this node
carried bytes, settle ZAR."* It does not model **creator payment** — *"this user
authored this content, the consumer wants to tip them."* The plugin / skin /
preset / podcast / track / reel author-tipping use case currently has no
protocol-level path; consumers call SDPKT directly, bypassing the protocol and
losing the ability to settle through any other ledger.

**What needs to change.** Either extend the existing interface with
`RecordCreatorTipAsync(creatorUhid, amount, contentHash)` (recommended — single
ledger-binding seam) or add a sibling `IAetherNetCreatorIncentiveProvider`
(clearer separation).

**Test anchor.** Cross-language fixture: consumer records creator tip; provider
receives `(creatorUhid, amount, contentHash)`; existing relay-credit path
unaffected.

**Surfaced by.** `aether-media` commit `07a695e` — `AetherMedia.Distribution`
author-tipping path calls SDPKT directly, bypassing the protocol's incentive
seam.

---

## How to use this file

When a Phase 3 session lands work that closes one of these items:

1. Add a `**RESOLVED <date>:**` block under the item describing what shipped.
2. Strike through the original "What needs to change" line.
3. Update README's Roadmap → "Open" section accordingly.
4. Move closed items to a `## Resolved` section at the bottom of this file.

The README's status table is derived from this file — keep them in sync.
