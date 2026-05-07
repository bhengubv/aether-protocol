// SPDX-License-Identifier: MIT
//
// Internal fuzz harness for primitive crypto entry points.
//
// The C subtree is primitives-only — high-level Signal session machinery
// lives in the C# reference. So the fuzz surface here is the same surface
// any C adopter would call from their own session layer:
//
//   - aether_x25519_derive_public  / aether_x25519_agree
//   - aether_hkdf_sha256
//   - aether_hmac_sha256
//
// Each iteration draws a 4096-byte random buffer from a deterministic PRNG
// and slices it across the three primitive families. The contract every
// call must satisfy:
//
//   - Never write past its caller-allocated output buffer.
//   - Return false (not crash) on the documented failure cases — for
//     X25519 that's the all-zero shared-secret case, which RFC 7748 §6.1
//     mandates be rejected as a low-order-public-key attack indicator.
//   - Return success-with-correct-length-output on legitimate inputs.
//
// We do NOT use libFuzzer here — keeping the harness portable across the
// systems our C adopters target (embedded toolchains, MSVC, older GCC)
// matters more than libFuzzer's coverage-guided mutation. Iterations are
// drawn from a seedable PCG-style mixer so failures reproduce.
//
// Iteration count defaults to 10k (smoke, <2s on a modern laptop), tunable
// via env AETHER_FUZZ_ITERATIONS for adversarial local runs (100k+ takes
// ~15s and is the default we use locally before pushing).

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aether/security.h"

// ─── Deterministic PRNG ──────────────────────────────────────────────────
//
// SplitMix64 — one of the fastest passable mixers. Same source seed →
// same byte stream, so a CI failure can be reproduced by setting
// AETHER_FUZZ_SEED. We do NOT need cryptographic strength here; we need
// reproducibility and decent statistical coverage of the input space.

static uint64_t splitmix_state;

static uint64_t splitmix_next(void) {
    uint64_t z = (splitmix_state += 0x9E3779B97F4A7C15ULL);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31);
}

static void fill_random(uint8_t *buf, size_t len) {
    size_t i = 0;
    while (i + 8 <= len) {
        uint64_t x = splitmix_next();
        memcpy(buf + i, &x, 8);
        i += 8;
    }
    if (i < len) {
        uint64_t x = splitmix_next();
        memcpy(buf + i, &x, len - i);
    }
}

// ─── Sentinel guard: detect output-buffer overruns ──────────────────────
//
// Each output buffer is wrapped in a fixed-pattern guard region. After
// the call, we check the guards are unchanged. A cheap, portable
// alternative to ASan / Valgrind that runs in plain `cmake --build`.

#define GUARD_BYTE 0xA5
#define GUARD_LEN  16

static int check_guards(const uint8_t *p, size_t n) {
    for (size_t i = 0; i < n; i++) {
        if (p[i] != GUARD_BYTE) return 0;
    }
    return 1;
}

// ─── X25519 fuzz slice ──────────────────────────────────────────────────

// Take 32 bytes for a private key. Derive public, then agree against
// a peer derived from the next 32 bytes. Verify:
//   - derive_public always succeeds (libsodium clamps internally).
//   - agree returns true and the shared secret is non-zero, OR returns
//     false (the all-zero / low-order rejection case).
// Return non-zero on contract violation.
static int fuzz_x25519(const uint8_t *buf, size_t len) {
    if (len < 64) return 0;
    uint8_t alice_priv[32], bob_priv[32];
    memcpy(alice_priv, buf, 32);
    memcpy(bob_priv,  buf + 32, 32);

    uint8_t guard_before[GUARD_LEN];
    uint8_t alice_pub[32];
    uint8_t guard_after[GUARD_LEN];
    memset(guard_before, GUARD_BYTE, GUARD_LEN);
    memset(guard_after,  GUARD_BYTE, GUARD_LEN);

    if (!aether_x25519_derive_public(alice_priv, alice_pub)) {
        fprintf(stderr, "fuzz: derive_public failed unexpectedly\n");
        return 1;
    }
    if (!check_guards(guard_before, GUARD_LEN) ||
        !check_guards(guard_after,  GUARD_LEN)) {
        fprintf(stderr, "fuzz: derive_public corrupted output guards\n");
        return 1;
    }

    uint8_t bob_pub[32];
    if (!aether_x25519_derive_public(bob_priv, bob_pub)) return 1;

    uint8_t shared[32];
    bool ok = aether_x25519_agree(alice_priv, bob_pub, shared);
    if (ok) {
        // Successful agree must yield a non-zero shared secret (RFC 7748
        // §6.1 — the all-zero case must have been rejected).
        int all_zero = 1;
        for (int i = 0; i < 32; i++) if (shared[i] != 0) { all_zero = 0; break; }
        if (all_zero) {
            fprintf(stderr, "fuzz: agree returned ok with all-zero secret\n");
            return 1;
        }
    }
    // ok == false is fine — that's the low-order-public rejection.

    // Self-agree (using own pub) must also be well-defined.
    (void)aether_x25519_agree(alice_priv, alice_pub, shared);

    return 0;
}

// ─── HKDF fuzz slice ────────────────────────────────────────────────────

// Carve up to 256 bytes for salt + ikm + info. Pick a random output length
// in [1, 96] (HKDF-SHA256 ceiling per RFC 5869 is 8160 bytes; we keep it
// small to stay in the fast path). Verify:
//   - hkdf returns true on every reachable input.
//   - the output buffer's exact-length region is filled (guard bytes
//     immediately after must remain GUARD_BYTE).
static int fuzz_hkdf(const uint8_t *buf, size_t len) {
    if (len < 8) return 0;
    size_t budget = len > 256 ? 256 : len;

    // Carve salt/ikm/info from the buffer. Pick lengths from the first
    // 4 bytes of input.
    size_t salt_len = (buf[0] % 33);              // 0..32
    size_t ikm_len  = ((buf[1] % 32) + 1);        // 1..32
    size_t info_len = (buf[2] % 33);              // 0..32
    size_t out_len  = ((buf[3] % 96) + 1);        // 1..96

    size_t off = 4;
    if (off + salt_len + ikm_len + info_len > budget) return 0;
    const uint8_t *salt = buf + off; off += salt_len;
    const uint8_t *ikm  = buf + off; off += ikm_len;
    const uint8_t *info = buf + off; off += info_len;

    uint8_t out_with_guard[96 + GUARD_LEN];
    memset(out_with_guard, GUARD_BYTE, sizeof(out_with_guard));

    bool ok = aether_hkdf_sha256(salt_len ? salt : NULL, salt_len,
                                 ikm, ikm_len,
                                 info_len ? info : NULL, info_len,
                                 out_len, out_with_guard);
    if (!ok) {
        fprintf(stderr, "fuzz: hkdf failed (salt=%zu ikm=%zu info=%zu out=%zu)\n",
                salt_len, ikm_len, info_len, out_len);
        return 1;
    }
    if (!check_guards(out_with_guard + out_len, GUARD_LEN)) {
        fprintf(stderr, "fuzz: hkdf wrote past requested output_len=%zu\n", out_len);
        return 1;
    }
    return 0;
}

// ─── HMAC fuzz slice ────────────────────────────────────────────────────

// Use the remainder of the buffer for an HMAC key + data. Verify the call
// succeeds and writes exactly 32 bytes.
static int fuzz_hmac(const uint8_t *buf, size_t len) {
    if (len < 2) return 0;
    size_t key_len = (buf[0] % 64) + 1;     // 1..64
    if (1 + key_len >= len) return 0;
    const uint8_t *key  = buf + 1;
    const uint8_t *data = buf + 1 + key_len;
    size_t data_len = len - 1 - key_len;

    uint8_t out_with_guard[32 + GUARD_LEN];
    memset(out_with_guard, GUARD_BYTE, sizeof(out_with_guard));

    bool ok = aether_hmac_sha256(key, key_len, data, data_len, out_with_guard);
    if (!ok) {
        fprintf(stderr, "fuzz: hmac failed (key=%zu data=%zu)\n", key_len, data_len);
        return 1;
    }
    if (!check_guards(out_with_guard + 32, GUARD_LEN)) {
        fprintf(stderr, "fuzz: hmac wrote past 32 bytes\n");
        return 1;
    }
    return 0;
}

// ─── Driver ─────────────────────────────────────────────────────────────

int main(void) {
    // Defaults: 10k iterations (~1s on a laptop, smoke-test profile). Bump
    // via AETHER_FUZZ_ITERATIONS for local adversarial runs.
    size_t iterations = 10000;
    splitmix_state = 0xA37E1B23DEADBEEFULL;

    const char *iter_env = getenv("AETHER_FUZZ_ITERATIONS");
    if (iter_env && *iter_env) {
        long parsed = strtol(iter_env, NULL, 10);
        if (parsed > 0 && parsed < (long)(1ULL << 30)) {
            iterations = (size_t)parsed;
        }
    }
    const char *seed_env = getenv("AETHER_FUZZ_SEED");
    if (seed_env && *seed_env) {
        splitmix_state = (uint64_t)strtoull(seed_env, NULL, 0);
    }

    printf("Fuzz harness: %zu iterations, seed=0x%016llx\n",
           iterations, (unsigned long long)splitmix_state);

    uint8_t buf[4096];
    size_t failures = 0;

    for (size_t iter = 0; iter < iterations; iter++) {
        fill_random(buf, sizeof(buf));

        // Slice the buffer across three primitive families.
        // X25519 takes the first 64 bytes; HKDF the next 256; HMAC the
        // rest.
        if (fuzz_x25519(buf, 64) != 0) failures++;
        if (fuzz_hkdf(buf + 64, 256) != 0) failures++;
        if (fuzz_hmac(buf + 64 + 256, sizeof(buf) - 64 - 256) != 0) failures++;

        if (failures > 0) {
            fprintf(stderr, "fuzz: aborting on first failure (iter %zu)\n", iter);
            return 1;
        }
    }

    printf("Fuzz: %zu iterations across X25519/HKDF/HMAC primitives — all green\n",
           iterations);
    return 0;
}
