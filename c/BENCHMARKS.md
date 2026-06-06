<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — C Benchmarks

Wall-clock benchmark harness for the C primitive surface. Mirrors the
cases pinned by `go/bench/bench_test.go` (commit `f873543`) and the
Python `python/benchmarks/` suite — same hot paths so a regression in
any language shows up as a delta against the saved baseline.

## How to run

```bash
cd c
cmake -B build
cmake --build build --target bench
./build/bench/bench
```

The bench is **not** part of `ctest` — it is a separate optional target
under `EXCLUDE_FROM_ALL`, so the standard `cmake --build build` does
not pull it in unless explicitly requested.

## How to pin a baseline

The bench writes a markdown table to stdout. Pipe it to `tee`:

```bash
./build/bench/bench | tee bench/baseline.md
```

Subsequent runs can be diffed against `bench/baseline.md` to spot
regressions.

## Tuning

```bash
# 100k iterations / case for tighter signal.
AETHERNET_BENCH_ITERATIONS=100000 ./build/bench/bench
```

Default is 1000 iterations / case — sub-second total wall clock on a
laptop.

## What we measure

Eleven primitive entry points, in the order called by a typical
Signal-protocol session:

| Op | Where called |
| --- | --- |
| `x25519_generate_keypair`   | Each fresh ephemeral key (X3DH, DH-ratchet send-step). |
| `x25519_derive_public`      | Public-key recovery from a stored private key. |
| `x25519_agree`              | X3DH (4×) and DH-ratchet (2×). |
| `ed25519_sign`              | Pre-key bundle signing. |
| `ed25519_verify`            | Pre-key bundle verification. |
| `aes256_gcm_encrypt_256B`   | Steady-state Encrypt (256-byte payload). |
| `aes256_gcm_decrypt_256B`   | Steady-state Decrypt (256-byte payload). |
| `hmac_sha256`               | Symmetric ratchet chain step. |
| `hkdf_sha256_64B`           | KDF_RK output shape (32-byte RK + 32-byte CK). |
| `signal_kdf_rk`             | High-level KDF_RK wrapper. |
| `sha256_1KB`                | Fingerprint / packet hashing. |

## Wall-clock source

`clock_gettime(CLOCK_MONOTONIC)` on POSIX, `QueryPerformanceCounter` on
Windows. Both are monotonic and high-resolution — no leap-second jumps,
no NTP corrections leaking into the timing.

Dead-code elimination is defeated by accumulating output bytes into a
`volatile` sink — the compiler can't optimise the calls away.

## Output format

```
| op | ns/op | ops/sec |
| --- | ---:| ---:|
| x25519_agree | 35404 | 28246 |
| ...
```

`ns/op` is total wall-clock time / iteration count. `ops/sec` is
`1e9 / ns_per_op`. Both are unrounded floats — keep raw numbers, not
ratios, when comparing baselines.

## Why no high-level Encrypt / Decrypt cases

The C subtree ships primitives only. Adopters layer their own
high-level Signal session implementation on top — so the
`signal_encrypt` / `signal_decrypt` / `packet_serialize` /
`packet_deserialize` cases the Go / Python benches pin do not exist
here. The reference high-level implementation is the C#
`SignalProtocolService` in `src/AetherNet.Protocol/Security/`.
