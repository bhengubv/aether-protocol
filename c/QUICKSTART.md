<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — C Quickstart

A 5-minute wiring guide for adopting the C implementation.

> **What this subtree ships.** The C implementation provides only the
> low-level cryptographic primitives — X25519 / Ed25519 / AES-256-GCM /
> HKDF-SHA256 / HMAC-SHA256 / `signal_kdf_rk`. It deliberately does NOT
> provide the high-level Signal session, X3DH state machine, or
> Double-Ratchet implementation. C adopters layer their own session
> machinery on top of these primitives, calibrated for whatever
> embedded / RTOS / desktop target they run on. The reference high-level
> implementation is the C# `SignalProtocolService.cs` in `src/`. See
> §6 below for the cross-language interop fixtures that anchor wire
> compatibility.

## 1. Prerequisites

- libsodium ≥ 1.0.18 (development headers — `libsodium-dev` on Debian /
  Ubuntu, `libsodium` on Homebrew, `libsodium-devel` on RHEL / Fedora).
- CMake ≥ 3.16.
- A C11 compiler (GCC ≥ 4.9, Clang ≥ 3.6, MSVC 2019+).
- POSIX threads on Unix-like systems (linked automatically).

## 2. Build

From the repository root:

```bash
cd c
cmake -B build
cmake --build build
```

The default build produces:

- `build/libaether-protocol.a` — static library.
- `build/aether-demo` — small end-to-end demo binary.

The library has no link-time dependency on anything other than libsodium
and pthreads. It is `EXCLUDE_FROM_ALL`-clean — `make` / `cmake --build`
without an explicit target builds only the library and demo, not the
tests or bench.

## 3. Run the tests

```bash
cmake --build build --target test-protocol test-routing test-dtn \
                            test-sos test-fixtures test-signal-fixtures \
                            test-fuzz
ctest --test-dir build
```

Seven suites should pass: protocol, routing, DTN, SOS, fixture
verification, Signal-protocol fixture verification, and the fuzz
harness. The fixture suites confirm wire-compatibility with the C# /
Go / TypeScript / Rust / Python / Swift / Kotlin reference implementations.

## 4. The primitive surface

Everything is declared in `include/aether/security.h`. Each function
takes caller-allocated output buffers — no heap returned by the library.

```c
#include "aether/security.h"

// X25519 key agreement (RFC 7748 — raw 32-byte u-coordinate wire format).
bool aether_x25519_generate_keypair(uint8_t *out_priv, uint8_t *out_pub);
bool aether_x25519_derive_public(const uint8_t *priv, uint8_t *out_pub);
bool aether_x25519_agree(const uint8_t *local_priv,
                         const uint8_t *remote_pub,
                         uint8_t *out_shared);  // false on all-zero / low-order

// Ed25519 (32-byte seed private, 32-byte public, 64-byte signature).
bool aether_ed25519_generate_keypair(uint8_t *out_priv, uint8_t *out_pub);
bool aether_ed25519_sign(const uint8_t *priv, const uint8_t *data,
                         size_t data_len, uint8_t *out_sig);
bool aether_ed25519_verify(const uint8_t *pub, const uint8_t *data,
                          size_t data_len, const uint8_t *sig);

// AES-256-GCM (12-byte nonce, 16-byte tag).
bool aether_aes256_gcm_encrypt(/* … */);
bool aether_aes256_gcm_decrypt(/* … */);

// SHA-256, HMAC-SHA256, HKDF-SHA256 (RFC 5869 extract-and-expand).
bool aether_sha256(/* … */);
bool aether_hmac_sha256(/* … */);
bool aether_hkdf_sha256(/* … */);

// Signal §5.2 KDF_RK — the high-level wrapper everyone implements.
bool aether_signal_kdf_rk(const uint8_t *root_key,
                          const uint8_t *dh_output,
                          uint8_t *out_new_root_key,
                          uint8_t *out_new_chain_key);

void aether_zeroize(void *mem, size_t len);
bool aether_random_bytes(uint8_t *out, size_t len);
```

## 5. Worked example — Alice agrees a shared secret with Bob

```c
#include "aether/security.h"
#include <assert.h>

uint8_t alice_priv[32], alice_pub[32];
uint8_t bob_priv[32],   bob_pub[32];
assert(aether_x25519_generate_keypair(alice_priv, alice_pub));
assert(aether_x25519_generate_keypair(bob_priv,   bob_pub));

uint8_t alice_shared[32], bob_shared[32];
assert(aether_x25519_agree(alice_priv, bob_pub, alice_shared));
assert(aether_x25519_agree(bob_priv,   alice_pub, bob_shared));
// alice_shared == bob_shared.

// Derive a 64-byte (RK || CK) initial root from a 4×DH X3DH concatenation.
// (Show only the HKDF call — the X3DH X25519 chain is application-defined.)
uint8_t okm[64];
const uint8_t info[] = "aether-ratchet-rk-v1";
assert(aether_hkdf_sha256(NULL, 0, alice_shared, sizeof(alice_shared),
                          info, sizeof(info) - 1, sizeof(okm), okm));

aether_zeroize(alice_priv, sizeof(alice_priv));
aether_zeroize(bob_priv,   sizeof(bob_priv));
```

## 6. Cross-language interop

Wire compatibility across the eight implementation families is anchored
by JSON fixtures committed at the repository root. The C verifier is
`tests/test_signal_fixtures.c` — it loads `fixtures/signal/inputs.json`,
runs the same X25519 + HKDF chain on the C primitives, and asserts the
hex output matches `fixtures/signal/expected/*.json` byte-for-byte.

The fixture cases cover:

- `x3dh_basic` — 4×X25519 + HKDF root derivation.
- `kdf_rk_basic` — the `signal_kdf_rk` 64-byte (RK || CK) split.
- Symmetric-ratchet chain steps — HMAC-SHA256(CK, byte) → next CK / MK.

If `ctest --test-dir build -R Signal` passes, your C primitive layer can
talk on the wire to a C# / Go / Rust / TypeScript / Python / Swift /
Kotlin host.

## 7. Bench

```bash
cmake --build build --target bench
./build/bench/bench
```

See [BENCHMARKS.md](BENCHMARKS.md) for what we measure and how to pin a
baseline. The eleven cases match the Go / Python harnesses one-for-one
on the primitive surface.

## 8. Fuzz

The fuzz harness `tests/test_fuzz.c` runs 10k smoke iterations of
random input through the X25519 / HKDF / HMAC entry points (with
sentinel-byte output-buffer guards). Tunable via:

```bash
AETHER_FUZZ_ITERATIONS=1000000 AETHER_FUZZ_SEED=0x42 \
    ./build/tests/test-fuzz
```
