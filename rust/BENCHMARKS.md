# aether-protocol — Rust benchmark guide

Criterion-based bench harness covering the protocol's measurable hot
paths. Mirrors the per-language bench surface that landed in Wave 7:

- Go     — `go/BENCHMARKS.md` (commit `0aac997`)
- Python — `python/BENCHMARKS.md` (commit `0b01805`)
- C      — `c/BENCHMARKS.md` (commit `f3e38ed`)
- TS     — `typescript/BENCHMARKS.md` (commit `6d55f94`)

Same case names everywhere so per-language hot-path performance stays
directly comparable.

## How to run

```bash
cd rust
cargo bench --bench aether
```

Criterion writes results to `target/criterion/`. Each case builds a
distribution across many iterations and reports mean / median / IQR plus
a regression check against the previous run.

## What we measure

| Case | What |
|---|---|
| `x25519_agree` | One X25519 ECDH `priv * pub` operation |
| `hkdf_sha256_64bytes` | HKDF-SHA256 expand to 64 bytes (matches KDF_RK output size) |
| `x3dh_establish` | Service construction (cheap; full X3DH is wired in the integration tests) |
| `signal_encrypt` | Stub — populate after the Rust public-API surface stabilises |
| `signal_decrypt` | Stub |
| `packet_serialize` | Serialize a 64-byte-payload packet to wire bytes |
| `packet_serialize_large` | Same with a 10 KiB payload (catches per-byte hot loops) |
| `packet_deserialize` | Reverse of `packet_serialize` |
| `packet_round_trip` | `serialize -> deserialize` back-to-back |
| `route_store_lookup` | Stub |
| `route_store_save` | Stub |

The X3DH / Signal session / route-store stubs are placeholders so case
names line up with the rest of the language family. Real bench bodies for
those land once the `SignalProtocolService` builder API stabilises — the
core algorithm is exercised by the integration test suite (`rust/tests/`)
in the meantime.

## Regression gates

Criterion's default behaviour highlights any case that regresses ≥10%
vs the previous run. CI should fail on >25% to leave room for natural
noise; tighten as the harness matures.

## Verification on this host

The local Windows-MSVC toolchain hits a known `msvcrt.lib` linker
problem when building `cargo bench` (same issue documented in
`OPEN_ISSUES.md` for `cargo test --tests`). Bench numbers are collected
on Linux / macOS CI runners. The harness itself just uses the public
Rust API surface — no platform-specific code paths.
