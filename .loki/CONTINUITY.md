# Loki Mode Working Memory — aether-protocol production hardening
Last Updated: 2026-05-05
Current Phase: development (Tier 1-6 fan-out)
Current Iteration: 1

## Active Goal

Close all 6 outstanding tiers of work in the open-source `aether-protocol`
mesh networking + Signal-protocol library:

- **Tier 1**: Port full Double Ratchet (Signal §5) from C# reference to the
  other 7 language implementations (Go, Python, TS, Swift, Kotlin, Rust, C)
  so cross-language interop works post-X3DH, not just at the X3DH layer.
- **Tier 2**: Unit tests for the 6 untested C# modules (Streaming, Voice,
  Transport, Messaging service, Content, Storage — ~3,500 LOC zero coverage).
- **Tier 3**: Commit `src/Aether.Streaming/` (untracked, builds clean,
  pre-existing in-progress work from a prior session).
- **Tier 4**: Demo expansions for higher-level modules — there's no console
  demo of MessagingService, DTN custody, Voice call, Stream subscribe.
- **Tier 5**: Maturity items — fuzz tests on `PacketSerializer.Deserialize`
  + `EncryptedPayloadCodec.Deserialize`, benchmark suite, one-time pre-key
  pool (currently single-OPK per bundle, breaks under concurrent initiators),
  doc reconciliation (PROTOCOL_SPEC.md §4 / §10 / §11 still WIP).
- **Tier 6**: Cleanup — review `VerifyWithFallback` for removal,
  `InitiatorEphemeralKeyX25519` deprecation marker, `ProtocolConstants` drift
  check vs spec.

## Current Reference Point

C# implementation at commit **e0b630f** (full Double Ratchet shipped) +
**0a0df16** (SignalMessageEnvelopeCipher bridge). Pushed to
`bhengubv/aether-protocol main`. 109/109 C# tests pass.

Wire envelope additions in C#:
- `EncryptedPayload.SenderEphemeralKeyX25519` (32 bytes, every msg)
- `EncryptedPayload.PreviousChainCount` (int, every msg)
- `EncryptedPayload.InitiatorEphemeralKeyX25519` retained for backward-
  compat — equals SenderEphemeralKeyX25519 on PreKey msgs

HKDF info string added: `aether-ratchet-rk-v1` (KDF_RK for DH-ratchet step).
Existing strings unchanged: `aether-x3dh-root-v1`,
`aether-chain-initiator-send-v1`, `aether-chain-initiator-recv-v1`.

Cross-language fixtures at `fixtures/signal/expected/*.json` MUST continue
to pass — they pin X3DH math and HMAC ratchet, both of which the Double
Ratchet does NOT change.

## Session Cadence

- Wave 1 (parallel, ~10 agents): Tier 1 ports for Go/Python/TS/Rust/Swift/
  Kotlin/C + Tier 5 OPK pool + Tier 5 fuzz + Tier 4 demos
- Wave 2 (parallel, after Wave 1): Tier 2 tests for 6 modules
- Wave 3 (parallel, after Wave 2): Tier 5 doc reconciliation + Tier 5
  benchmarks + Tier 6 cleanup

## Just Completed (this session)

- 5bd52a9 csharp: PacketSigningService — dedup keyed by (source, nonce)
- 7a56f72 csharp: Ed25519SigningService — pin P-256 deadline to fixed UTC
- e0b630f csharp: full Double Ratchet — DH-rotation step on receive
- 0a0df16 csharp: bridge Aether.Messaging ↔ Aether.Security via Signal cipher

## Next Actions (Priority Order)

1. Direct: commit untracked `src/Aether.Streaming/` (Tier 3)
2. Parallel dispatch Wave 1 (~10 agents)
3. Aggregate Wave 1 results, dispatch Wave 2

## Working Context

- Repo root: `C:/Dev/Solutions/com.bhengubv/aether-protocol`
- Reference C# Double Ratchet: `src/Aether.Security/Services/SignalProtocolService.cs`
- Cross-language fixtures: `fixtures/signal/`
- Each language has its own subtree (`go/`, `python/`, `typescript/`, `swift/`,
  `kotlin/`, `rust/`, `c/`) with its own `signal_protocol` source + tests
