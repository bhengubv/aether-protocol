## Cross-Language Signal Protocol Fixtures

Canonical X3DH + Double-Ratchet test vectors. The same inputs must yield the
same outputs in every language implementation; these fixtures pin the
expected outputs in committed JSON so any drift between languages becomes a
failing test in CI.

## Why

`MeshPacket` wire-format fixtures (one directory up) catch byte-order /
length-prefix / encoding drift. They cannot catch crypto drift — two
implementations can both be self-consistent with the same `RatchetService`
unit tests yet derive different chain keys from the same root key, because
each test against itself passes silently.

These vectors fix that: every language ships a `SignalFixtureTests` harness
that loads `inputs.json`, runs its own X3DH + ratchet, and byte-compares
against `expected/<case>.json`. The first divergence between implementations
shows up as a hex mismatch in CI.

## Layout

```
fixtures/signal/
├── inputs.json                  # canonical inputs (raw private keys, chain keys)
├── expected/
│   ├── x3dh_basic.json          # full X3DH outputs (DHs, root key, chain keys)
│   ├── ratchet_step_basic.json  # one HMAC ratchet step
│   └── ratchet_step_three_iterations.json  # three sequential steps
```

## Cases

### `x3dh_basic`

Full X3DH session establishment from pinned 32-byte X25519 raw private keys
for: alice IK, alice EK (ephemeral), bob IK, bob SPK (signed pre-key), bob
OPK (one-time pre-key).

Verifies cross-language agreement on:
1. **X25519 public-key derivation** (priv -> pub via base-point scalar mult).
   RFC 7748 clamping is library-internal; the inputs are raw bytes that all
   libraries must clamp identically.
2. **X25519 ECDH** for each of the 4 DHs in initiator-side X3DH (Signal §3.3):
   * DH1 = DH(IK_A, SPK_B)
   * DH2 = DH(EK_A, IK_B)
   * DH3 = DH(EK_A, SPK_B)
   * DH4 = DH(EK_A, OPK_B)
3. **HKDF-SHA256** root-key derivation over `concat(DH1||DH2||DH3||DH4)` with
   info string `aether-x3dh-root-v1`.
4. **HKDF-SHA256** chain-key derivation from the root key with info strings
   `aether-chain-initiator-send-v1` / `aether-chain-initiator-recv-v1`.

### `ratchet_step_basic`

A single Double-Ratchet step: input chain key, output (message key, next
chain key). Validates HMAC-SHA256 with single-byte domain separation
(`0x01` for message key, `0x02` for next chain key) per Signal
Double-Ratchet §5.1.

### `ratchet_step_three_iterations`

Three sequential ratchet steps from a fixed initial chain key. Each step's
output chain key feeds the next. Catches drift in the ratchet sequence
(off-by-one, wrong domain-separation byte order, wrong hash function).

## Per-language contract

Each language implements a fixture-loader test that, for every case in
`inputs.json`:

1. Parses inputs (hex strings -> raw bytes).
2. Runs the language's own X3DH and/or HMAC ratchet.
3. Asserts every output field equals the corresponding `expected/<case>.json`
   field, byte-for-byte.

Implementations should NOT special-case the fixture inputs — the same code
path that handles a real session must produce the fixture outputs.

## Regenerating fixtures

```bash
# Un-skip Generate_ExpectedFixtures in
# tests/AetherMesh.Core.Tests/SignalFixtureGenerator.cs and run:
dotnet test tests/AetherMesh.Core.Tests/AetherMesh.Core.Tests.csproj \
  --filter "FullyQualifiedName~SignalFixtureGenerator" \
  --logger "console;verbosity=detailed"
```

The test prints expected outputs to stdout; copy them into the appropriate
`expected/<case>.json`. **Re-verify every language afterwards** — any
intentional protocol change is a wire-break event.
