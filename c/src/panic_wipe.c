// SPDX-License-Identifier: MIT
// Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.
//
// Faithful mirror of src/AetherNet.Security/Privacy/PanicWipe.cs. The
// deterministic parts — the duress-PIN hash (SHA-256 of the UTF-8 PIN) and the
// identity key-name manifest — reproduce the C# reference and the shared
// fixture (fixtures/panicwipe/vectors.json) byte-for-byte.
//
// Crypto reuses the SDK's existing libsodium backend (see src/security.c): no
// new vendored deps. crypto_hash_sha256 for the PIN hash, sodium_memcmp for the
// constant-time compare, randombytes_buf for the secure-erase overwrite.

#include <stdio.h>
#include <string.h>

#include "aethernet/panic_wipe.h"

#include <sodium.h>

/**
 * Initialize libsodium (idempotent). Mirrors the guard in src/security.c so this
 * translation unit is self-sufficient — randombytes_buf and crypto_hash_sha256
 * both require sodium_init() to have run.
 */
static void ensure_libsodium_initialized(void) {
    static volatile int initialized = 0;
    if (!initialized) {
        if (sodium_init() >= 0) {
            initialized = 1;
        }
    }
}

/*
 * The key-store entry names that together constitute an AetherNet identity.
 * Same names, same order as C# PanicWipe.IdentityKeyNames.
 */
const char *const AETHERNET_IDENTITY_KEY_NAMES[AETHERNET_IDENTITY_KEY_NAME_COUNT] = {
    "aether_identity_pub",
    "aether_identity_priv",
    "aether_identity_generated",
    "aether_device_salt",
    "aether_drk",
    "aether_ble_rotation_key",
    "aether_ble_irk",
};

bool aethernet_duress_pin_hash(const char *pin, uint8_t *out32) {
    if (!out32) return false;

    ensure_libsodium_initialized();

    // C# hashes Encoding.UTF8.GetBytes(pin); the C SDK treats the PIN as its raw
    // UTF-8 bytes (the fixture PINs are ASCII/UTF-8 literals). A NULL pin is
    // treated as the empty string — SHA-256 over zero bytes — matching the empty
    // "" vector.
    const char *p = pin ? pin : "";
    size_t len = strlen(p);

    unsigned char hash[crypto_hash_sha256_BYTES]; /* 32 */
    if (crypto_hash_sha256(hash, (const unsigned char *)p, len) != 0) {
        return false;
    }

    memcpy(out32, hash, crypto_hash_sha256_BYTES);
    sodium_memzero(hash, sizeof(hash));
    return true;
}

bool aethernet_verify_duress_pin(const char *pin,
                                 const uint8_t *stored_hash,
                                 size_t stored_len) {
    // Mirror C#: a stored hash that is not exactly 32 bytes can never match.
    if (!stored_hash || stored_len != AETHERNET_DURESS_PIN_HASH_SIZE) {
        return false;
    }

    uint8_t computed[AETHERNET_DURESS_PIN_HASH_SIZE];
    if (!aethernet_duress_pin_hash(pin, computed)) {
        return false;
    }

    // Constant-time compare (libsodium sodium_memcmp returns 0 on equal).
    bool equal = sodium_memcmp(computed, stored_hash,
                               AETHERNET_DURESS_PIN_HASH_SIZE) == 0;
    sodium_memzero(computed, sizeof(computed));
    return equal;
}

void aethernet_secure_erase(uint8_t *buf, size_t len) {
    if (!buf || len == 0) return;

    ensure_libsodium_initialized();

    // Overwrite with random, then zero — same two-step as C# SecureErase
    // (RandomNumberGenerator.Fill then CryptographicOperations.ZeroMemory).
    randombytes_buf(buf, len);
    sodium_memzero(buf, len);
}

bool aethernet_prekey_name(int index, char *out, size_t out_cap) {
    if (!out) return false;
    int n = snprintf(out, out_cap, "prekey_%d", index);
    return n > 0 && (size_t)n < out_cap;
}

bool aethernet_signed_prekey_name(int index, char *out, size_t out_cap) {
    if (!out) return false;
    int n = snprintf(out, out_cap, "signed_prekey_%d", index);
    return n > 0 && (size_t)n < out_cap;
}
