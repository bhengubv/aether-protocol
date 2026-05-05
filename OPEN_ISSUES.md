# Open Issues — production-readiness remediation

Tracked items remaining before `aether-protocol` can be presented as a
production-grade Signal-Protocol-style end-to-end-encrypted mesh primitive.
The wire format and routing/DTN/SOS service layers are at production grade
(verified by ~280 service tests + 122 cross-language byte-equality
assertions). Everything below is the cryptographic-protocol layer plus
documentation honesty.

Last reviewed: 2026-05-05 (closed wire-format alignment + service tests).

---

## Critical — security correctness (blocking 1.0)

### 1. Real X3DH ephemeral key — all 8 languages

**State.** Every language exposes `generatePreKeyBundle` / `processPreKeyBundle` /
`encrypt` / `decrypt` on its `SignalProtocolService`. The internal `KEY_EXCHANGE`
implementation uses the local node's identity key for *both* DH operations, so
the resulting shared secret is derived from `DH(idA, idB)` only. The Signal
spec requires three DH operations: `DH(idA, signedPreKeyB) || DH(ephA, idB)
|| DH(ephA, signedPreKeyB)`, with an optional fourth `DH(ephA, oneTimePreKeyB)`.

**Why this matters.** Without an ephemeral on the initiator side, every session
between Alice and Bob produces the same root key for the lifetime of their
identity keys. If either identity key is ever compromised, every past session
becomes decryptable. The README claim "forward secrecy" is materially false.

**What needs to change.** Each language's `generatePreKeyBundle` must publish
*both* a signed pre-key AND a refreshable ephemeral pre-key set. Each language's
`processPreKeyBundle` must generate a fresh ephemeral keypair for the initiator,
perform 3 (or 4 with one-time pre-key) DH operations, derive the root key via
HKDF over the concatenation, then derive the chain key.

**Test anchor.** Add `fixtures/signal/` with hand-computed test vectors per
the Signal spec (or generated from libsignal as the reference). Each language
proves byte-identical session-establishment output against the vectors.

### 2. Double-Ratchet alignment — pick ONE construction family-wide

**State.**
- C# uses HKDF for the root chain step (non-canonical).
- Python and Go use HMAC-SHA256 (matches the Signal spec).
- Rust uses HKDF with a different salt than C#.
- Kotlin, Swift, TypeScript, C — not line-by-line audited.

**Why this matters.** Once a session is in motion, a C# node and a Python node
diverge on the second message. They cannot decrypt each other's traffic.

**What needs to change.** Pick HMAC-SHA256 (Signal spec). Align all 8
implementations to the same KDF function and the same salts/info constants.

**Test anchor.** Same `fixtures/signal/` corpus from item 1 — extend with
multi-message ratchet vectors (5-message chain).

### 3. Rust pre-key bundles: X25519 → P-256 (or family-wide pivot)

**State.** `rust/src/security/signal_protocol.rs` ships X25519 32-byte raw
public keys in pre-key bundles. Every other language ships P-256 65-byte
uncompressed.

**Why this matters.** Rust pre-key bundles can't be processed by any other
implementation. A Rust initiator can never establish a session with a non-Rust
responder, or vice versa.

**Architectural decision needed.** Two options:

| Option | Pro | Con |
|---|---|---|
| Family adopts P-256 (current README claim) | 7 langs unchanged; .NET native ECDH | non-Signal-canonical; no ChaCha20-Poly1305 |
| Family adopts X25519/Ed25519 (Signal-canonical) | matches published spec; battle-tested cross-lang libs | 7 langs add a new curve dep |

Current recommendation: **X25519 + Ed25519 (Signal-canonical)**. The repo
self-identifies as "Signal Protocol"; aligning means the name is honest. The
extra dep is small (every language has a maintained X25519 lib).

---

## High — documentation honesty (blocking public-facing 1.0)

### 4. `docs/PROTOCOL_SPEC.md` reconciliation

The spec describes a wire layout that no implementation uses. Constants drift
between spec, C# `ProtocolConstants.cs`, and the other languages — e.g., the
spec's "min packet = 50 bytes" arithmetic in §2.2 is internally inconsistent.

**What needs to change.** Rewrite the spec line-by-line against the actual
serializer output (run `go run go/cmd/fixturegen` and pin the byte layout in
the spec). Where the spec disagrees with implementation, decide which to
change before publishing.

### 5. Demo program signing fix

`samples/Aether.Demo.Console` and the per-language demos sign the entire
serialized wire bytes for visualization, but `PacketSigningService.Build
SignableData` actually constructs a different (canonical, fixed-layout) buffer.
Readers of the demo source come away with an incorrect mental model of the
signing scheme.

**What needs to change.** Update the demo to sign via the canonical
`BuildSignableData` path and add a comment block calling out the difference
between "what's signed" and "what's on the wire."

### 6. `docs/adaptive-secure-streaming-spec.md`

625-line forward-design doc dated 2026-05-01. Zero corresponding code in any
language. Either implement at least a skeleton, or add a header banner
labelling it `Status: PROPOSAL — not implemented`.

---

## Medium — polish

### 7. Fixture corpus expansion

**Resolved 2026-05-05:** the parallel `tests/cross-language/` scaffold was
deleted; `fixtures/` is now the canonical cross-language corpus.

**Still open** — the deleted scaffold had 4 input cases not in `fixtures/`
that would round out coverage. Worth porting to `fixtures/inputs.json`:

- `utf8-chinese` — Chinese characters in UHIDs (3-byte UTF-8 without ASCII
  gaps; my `unicode_uhids` covers Korean + Russian which have different
  byte patterns).
- `utf8-emoji` — 4-byte UTF-8 sequences. Catches BMP / supplementary-plane
  handling bugs.
- `high-priority` — Priority=255 (SOS).
- `large-payload` — payload > 1 MB.

To add: extend `fixtures/inputs.json`, regenerate `fixtures/expected/*.bin`
via `cd go && go run ./cmd/fixturegen`, commit. Each language's
`FixtureTests` will pick the new cases up automatically.

### 8. End-to-end two-node interop on real hardware

Needs at minimum: 2 phones (or 1 phone + 1 Pi with BLE), one running each of
two language implementations, exchanging a packet over BLE. The `fixtures/`
corpus proves byte-identity at the serializer level; this test would prove
the full transport+routing+session stack works end-to-end on physical RF.

Out of scope for code-only sessions. Track for a hardware bring-up.

---

## How to use this file

When a Phase 3 session lands work that closes one of these items:

1. Add a `**RESOLVED <date>:**` block under the item describing what shipped.
2. Strike through the original "What needs to change" line.
3. Update README's Roadmap → "Open" section accordingly.
4. Move closed items to a `## Resolved` section at the bottom of this file.

The README's status table is derived from this file — keep them in sync.
