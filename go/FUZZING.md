<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Go Fuzzing

Native Go 1.18+ `testing.F` fuzz harnesses covering the untrusted-input
boundaries — the wire-format deserializer and the JSON DTOs used by
the persistent state stores. Every entry point that ingests bytes from
an attacker-controlled source is fuzzed.

## Run

Short adversarial smoke (one target):

```bash
go test -fuzz "FuzzDeserialize$" -fuzztime 30s ./protocol/...
```

Continuous-integration baseline (5-minute sweep per target — the
recommended local burn-in before signing off on a serializer change):

```bash
go test -fuzz "FuzzDeserialize$" -fuzztime 5m ./protocol/...
go test -fuzz "FuzzDeserializeFixtureSeeded$" -fuzztime 5m ./protocol/...
go test -fuzz "FuzzDeserializeSignalSession$" -fuzztime 5m ./security/...
go test -fuzz "FuzzPreKeyBundleJSON$" -fuzztime 5m ./security/...
```

Fuzz corpora discovered during a run are written to
`testdata/fuzz/<TargetName>/` and re-played as deterministic seeds on
every subsequent `go test` run — so once a counter-example lands, it
stays in the regression set forever without any extra wiring.

## Targets

| Target | Package | What it pins |
| ------ | ------- | ------------ |
| `FuzzDeserialize` | `protocol` | Random + mutated bytes through `PacketSerializer.Deserialize`; contract is "valid `*MeshPacket` OR non-nil error, never panic, never hang". |
| `FuzzDeserializeFixtureSeeded` | `protocol` | Same contract, but seeded with every cross-language fixture `.bin` so the runtime mutator starts from known-good packets and walks outward — finds edge cases that pure-random bytes never reach. |
| `FuzzDeserializeSignalSession` | `security` | Adversarial JSON through the persistent-store session decoder; must never panic on arbitrary bytes. |
| `FuzzPreKeyBundleJSON` | `security` | `encoding/json` round-trip on the `PreKeyBundle` DTO over arbitrary bytes — pins decoder robustness. |

## Smoke result

From the fuzz-landing commit (`5ddfb22`): 42,984 execs in 3 s, 0
crashes, 3 new interesting inputs discovered (now pinned in
`testdata/fuzz/`). Inputs are capped at 1 KB per the design constraint
to keep mutator memory bounded — the cap is well above every observed
fixture (typical packet ~100 bytes).

## Why these specific targets

The Go fuzz engine only mutates `[]byte` (and a handful of primitive
types) through the corpus. Every fuzzable entry point in the protocol
ultimately funnels through one of the four targets above:

- The wire-format `Deserialize` is the only path attacker-controlled
  bytes traverse to reach a typed `*MeshPacket`.
- The two security-package decoders are the only paths
  attacker-controlled JSON traverses to reach a typed
  `*SignalSession` / `PreKeyBundle`.

Crypto primitives (X25519 / Ed25519 / AES-GCM / HKDF / HMAC) are
covered by the Go stdlib's own fuzz suite — re-fuzzing them here
would just re-test stdlib code.
