// SPDX-License-Identifier: MIT
//
// Bench harness for the C primitive crypto hot paths.
//
// Mirrors the cases pinned by the Go bench (`go/bench/bench_test.go`,
// commit f873543) and the C# AetherMesh.Benchmarks suite — same hot paths so
// a regression in any language shows up as a delta against the committed
// baseline. The C subtree is primitives-only, so we skip the high-level
// Signal Encrypt/Decrypt and packet serialize/deserialize cases that
// require session machinery; those live with the C# / Go / Python
// reference impls.
//
// Output: a markdown table of `op | ns/op | ops/sec` to stdout. Pipe to
// `tee bench/baseline.md` to pin a baseline. No CTest entry — bench is a
// separate `cmake --build . --target bench && ./bench/bench` invocation.
//
// Default iterations: 1000 / case (sub-second on a laptop). Bump via
// AETHERMESH_BENCH_ITERATIONS for tighter signal.

// Ensure POSIX clock_gettime / CLOCK_MONOTONIC are visible under strict
// C11 builds where C_EXTENSIONS=OFF strips the GNU defaults.
#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#define _POSIX_C_SOURCE 200809L
#endif

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethermesh/security.h"

#ifdef _WIN32
#include <windows.h>
#endif

// ─── Monotonic wall clock ────────────────────────────────────────────────

// Returns nanoseconds since some unspecified epoch. We only ever take
// differences, so the epoch doesn't matter — what matters is monotonicity
// (no jumps backward, no leap-second adjustments) and high resolution.
static uint64_t now_ns(void) {
#ifdef _WIN32
    static LARGE_INTEGER freq = {0};
    if (freq.QuadPart == 0) QueryPerformanceFrequency(&freq);
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    // (counter * 1e9) / freq, but done as 128-bit-safe split to avoid overflow.
    return (uint64_t)((double)c.QuadPart * 1.0e9 / (double)freq.QuadPart);
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec;
#endif
}

// ─── Bench scaffolding ───────────────────────────────────────────────────
//
// Each case is a function returning the per-iteration nanosecond cost,
// computed as (total_elapsed_ns / iterations). Setup cost is paid before
// the timer starts; cleanup is paid after.

typedef double (*bench_case_fn)(size_t iterations);

typedef struct {
    const char  *name;
    bench_case_fn fn;
} bench_case_t;

static double run_case(bench_case_fn fn, size_t iterations) {
    return fn(iterations);
}

// Defeat dead-code elimination — accumulate output bytes into this sink
// so the compiler can't optimise the call away.
static volatile uint64_t bench_sink;

static void touch(const uint8_t *buf, size_t len) {
    uint64_t s = 0;
    for (size_t i = 0; i < len; i++) s += buf[i];
    bench_sink ^= s;
}

// ─── Cases ───────────────────────────────────────────────────────────────

// X25519 ECDH agreement — inner loop of X3DH (4x per session) and the
// DH-ratchet (2x per ratchet step).
static double bench_x25519_agree(size_t iterations) {
    uint8_t alice_priv[32], alice_pub[32];
    uint8_t bob_priv[32], bob_pub[32];
    aethermesh_x25519_generate_keypair(alice_priv, alice_pub);
    aethermesh_x25519_generate_keypair(bob_priv, bob_pub);

    uint8_t shared[32];
    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_x25519_agree(alice_priv, bob_pub, shared);
    }
    uint64_t end = now_ns();
    touch(shared, sizeof(shared));
    return (double)(end - start) / (double)iterations;
}

// HKDF-SHA256 producing 64 bytes — exactly the KDF_RK shape per Signal
// §5.2 (32-byte new RK + 32-byte new CK), called once per DH-ratchet step.
static double bench_hkdf_sha256_64b(size_t iterations) {
    uint8_t salt[32], ikm[32];
    aethermesh_random_bytes(salt, sizeof(salt));
    aethermesh_random_bytes(ikm,  sizeof(ikm));
    const uint8_t info[] = "aether-ratchet-rk-v1";
    uint8_t out[64];

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_hkdf_sha256(salt, sizeof(salt), ikm, sizeof(ikm),
                          info, sizeof(info) - 1, sizeof(out), out);
    }
    uint64_t end = now_ns();
    touch(out, sizeof(out));
    return (double)(end - start) / (double)iterations;
}

// signal_kdf_rk — the high-level wrapper around HKDF that the C public
// header exports. Slightly more allocation overhead than raw HKDF.
static double bench_signal_kdf_rk(size_t iterations) {
    uint8_t rk[32], dh[32];
    aethermesh_random_bytes(rk, sizeof(rk));
    aethermesh_random_bytes(dh, sizeof(dh));
    uint8_t new_rk[32], new_ck[32];

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_signal_kdf_rk(rk, dh, new_rk, new_ck);
    }
    uint64_t end = now_ns();
    touch(new_rk, sizeof(new_rk));
    touch(new_ck, sizeof(new_ck));
    return (double)(end - start) / (double)iterations;
}

// HMAC-SHA256 with a 32-byte key + 32-byte message — the symmetric
// ratchet's chain-step shape (HMAC(CK, byte) -> next CK / message key).
static double bench_hmac_sha256(size_t iterations) {
    uint8_t key[32], msg[32];
    aethermesh_random_bytes(key, sizeof(key));
    aethermesh_random_bytes(msg, sizeof(msg));
    uint8_t out[32];

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_hmac_sha256(key, sizeof(key), msg, sizeof(msg), out);
    }
    uint64_t end = now_ns();
    touch(out, sizeof(out));
    return (double)(end - start) / (double)iterations;
}

// AES-256-GCM encrypt of a 256-byte payload — typical mesh message size
// after Signal ciphertext expansion.
static double bench_aes256_gcm_encrypt(size_t iterations) {
    uint8_t key[32];
    aethermesh_random_bytes(key, sizeof(key));
    uint8_t plaintext[256];
    aethermesh_random_bytes(plaintext, sizeof(plaintext));
    uint8_t ciphertext[256];
    uint8_t tag[16];
    uint8_t nonce[12];

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_aes256_gcm_encrypt(plaintext, sizeof(plaintext), key, NULL, NULL, 0,
                                  ciphertext, tag, nonce);
    }
    uint64_t end = now_ns();
    touch(ciphertext, sizeof(ciphertext));
    return (double)(end - start) / (double)iterations;
}

// AES-256-GCM decrypt — pre-encrypts a fresh payload so the decrypt loop
// sees a constant ciphertext (no setup leak into the timed region).
static double bench_aes256_gcm_decrypt(size_t iterations) {
    uint8_t key[32];
    aethermesh_random_bytes(key, sizeof(key));
    uint8_t plaintext[256];
    aethermesh_random_bytes(plaintext, sizeof(plaintext));
    uint8_t ciphertext[256];
    uint8_t tag[16];
    uint8_t nonce[12];
    aethermesh_aes256_gcm_encrypt(plaintext, sizeof(plaintext), key, NULL, NULL, 0,
                              ciphertext, tag, nonce);

    uint8_t recovered[256];
    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_aes256_gcm_decrypt(ciphertext, sizeof(ciphertext), key,
                                  nonce, tag, NULL, 0, recovered);
    }
    uint64_t end = now_ns();
    touch(recovered, sizeof(recovered));
    return (double)(end - start) / (double)iterations;
}

// Ed25519 sign over a 256-byte message — pre-key-bundle signing path.
static double bench_ed25519_sign(size_t iterations) {
    uint8_t priv[32], pub[32];
    aethermesh_ed25519_generate_keypair(priv, pub);
    uint8_t msg[256];
    aethermesh_random_bytes(msg, sizeof(msg));
    uint8_t sig[64];

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_ed25519_sign(priv, msg, sizeof(msg), sig);
    }
    uint64_t end = now_ns();
    touch(sig, sizeof(sig));
    return (double)(end - start) / (double)iterations;
}

// Ed25519 verify — pre-key-bundle verification path.
static double bench_ed25519_verify(size_t iterations) {
    uint8_t priv[32], pub[32];
    aethermesh_ed25519_generate_keypair(priv, pub);
    uint8_t msg[256];
    aethermesh_random_bytes(msg, sizeof(msg));
    uint8_t sig[64];
    aethermesh_ed25519_sign(priv, msg, sizeof(msg), sig);

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        (void)aethermesh_ed25519_verify(pub, msg, sizeof(msg), sig);
    }
    uint64_t end = now_ns();
    return (double)(end - start) / (double)iterations;
}

// X25519 derive_public — alone, separate from agree, since adopters call
// it on every key-rotation (DH-ratchet send-step).
static double bench_x25519_derive_public(size_t iterations) {
    uint8_t priv[32];
    aethermesh_random_bytes(priv, sizeof(priv));
    uint8_t pub[32];

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_x25519_derive_public(priv, pub);
    }
    uint64_t end = now_ns();
    touch(pub, sizeof(pub));
    return (double)(end - start) / (double)iterations;
}

// X25519 keypair generation — paid on every fresh ephemeral key (X3DH
// initiator side, DH-ratchet send-step).
static double bench_x25519_generate_keypair(size_t iterations) {
    uint8_t priv[32], pub[32];

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_x25519_generate_keypair(priv, pub);
    }
    uint64_t end = now_ns();
    touch(pub, sizeof(pub));
    return (double)(end - start) / (double)iterations;
}

// SHA-256 of a 1 KB input — common for fingerprinting / packet hashing.
static double bench_sha256_1kb(size_t iterations) {
    uint8_t input[1024];
    aethermesh_random_bytes(input, sizeof(input));
    uint8_t out[32];

    uint64_t start = now_ns();
    for (size_t i = 0; i < iterations; i++) {
        aethermesh_sha256(input, sizeof(input), out);
    }
    uint64_t end = now_ns();
    touch(out, sizeof(out));
    return (double)(end - start) / (double)iterations;
}

// ─── Driver ──────────────────────────────────────────────────────────────

static const bench_case_t cases[] = {
    { "x25519_generate_keypair",  bench_x25519_generate_keypair },
    { "x25519_derive_public",     bench_x25519_derive_public    },
    { "x25519_agree",             bench_x25519_agree            },
    { "ed25519_sign",             bench_ed25519_sign            },
    { "ed25519_verify",           bench_ed25519_verify          },
    { "aes256_gcm_encrypt_256B",  bench_aes256_gcm_encrypt      },
    { "aes256_gcm_decrypt_256B",  bench_aes256_gcm_decrypt      },
    { "hmac_sha256",              bench_hmac_sha256             },
    { "hkdf_sha256_64B",          bench_hkdf_sha256_64b         },
    { "signal_kdf_rk",            bench_signal_kdf_rk           },
    { "sha256_1KB",               bench_sha256_1kb              },
};

int main(void) {
    size_t iterations = 1000;
    const char *iter_env = getenv("AETHERMESH_BENCH_ITERATIONS");
    if (iter_env && *iter_env) {
        long parsed = strtol(iter_env, NULL, 10);
        if (parsed > 0 && parsed < (long)(1ULL << 28)) {
            iterations = (size_t)parsed;
        }
    }

    fprintf(stderr, "aethermesh-protocol C bench: %zu iterations / case\n", iterations);

    // Markdown header — pipe stdout to tee bench/baseline.md to pin.
    printf("| op | ns/op | ops/sec |\n");
    printf("| --- | ---:| ---:|\n");

    size_t n = sizeof(cases) / sizeof(cases[0]);
    for (size_t i = 0; i < n; i++) {
        double ns_per_op = run_case(cases[i].fn, iterations);
        double ops_per_sec = ns_per_op > 0.0 ? 1.0e9 / ns_per_op : 0.0;
        printf("| %s | %.0f | %.0f |\n", cases[i].name, ns_per_op, ops_per_sec);
        fflush(stdout);
    }

    // Reference the sink so the optimiser keeps the touched-output cost.
    if (bench_sink == 0xDEADBEEFCAFEBABEULL) fputs("", stderr);
    return 0;
}
