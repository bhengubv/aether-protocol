# Open Issues — production-readiness remediation

Tracked items remaining before `aether-protocol` can be presented as a
production-grade Signal-Protocol-style end-to-end-encrypted mesh primitive.
The wire format and routing/DTN/SOS service layers are at production grade
(verified by 1,315 tests across 8 languages + 14 wire-format fixtures +
4 Signal test vectors with cross-language byte-equality assertions in CI).
Everything below is the cryptographic-protocol layer plus documentation honesty.

Last reviewed: 2026-05-07 (closed real X3DH, full Double Ratchet, OPK pool,
PROTOCOL_SPEC §4/§10/§11 reconciliation, fixture corpus 14 cases, demo
signing audit, adaptive-streaming-spec banner; updated to 1,315 verified tests).

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
Aether.Demo.Console`) was extended in `b816f8b` (Step 9 —
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

Needs at minimum: 2 phones (or 1 phone + 1 Pi with BLE), one running each of
two language implementations, exchanging a packet over BLE. The `fixtures/`
corpus proves byte-identity at the serializer level; this test would prove
the full transport+routing+session stack works end-to-end on physical RF.

Out of scope for code-only sessions. Track for a hardware bring-up.

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
- C: `aether_packet_get_signable_data(packet, &len)` + manual `aether_ed25519_sign(...)` in `c/src/demo.c`

~~**What needs to change.** Per-language: replace the wire-byte signing
shortcut with the canonical `BuildSignableData` path; add a code comment
calling out "what's signed vs. what's on the wire".~~

### 11. C: full Signal session machinery

**State.** C ships only X25519 + KDF_RK primitives + symmetric ratchet
fixture verification (commits `eb71e53`, `6416e06`). It does NOT implement
the full X3DH session establishment, OPK / SPK lifecycle, or the
DH-ratchet integration. Hosts that want full E2EE on C-based microcontrollers
cannot use the current C surface for end-to-end traffic.

**What needs to change.** Port the high-level `SignalProtocolService`
API surface (`generate_pre_key_bundle`, `process_pre_key_bundle`, `encrypt`,
`decrypt`) to C, building on the existing X25519 + KDF_RK primitives.

**Test anchor.** `fixtures/signal/x3dh_basic` and the existing fixture
verifier (`c/tests/test_signal_fixtures.c`).

---

## How to use this file

When a Phase 3 session lands work that closes one of these items:

1. Add a `**RESOLVED <date>:**` block under the item describing what shipped.
2. Strike through the original "What needs to change" line.
3. Update README's Roadmap → "Open" section accordingly.
4. Move closed items to a `## Resolved` section at the bottom of this file.

The README's status table is derived from this file — keep them in sync.
