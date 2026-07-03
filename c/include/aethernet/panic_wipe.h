// SPDX-License-Identifier: MIT
// Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.

#ifndef AETHERNET_PANIC_WIPE_H
#define AETHERNET_PANIC_WIPE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.
 * A duress PIN (or panic button) irreversibly destroys the node's key material,
 * so a seized device reveals nothing and looks like a fresh install.
 *
 * This is the protocol-level core — deterministic and portable across every
 * AetherNet SDK. Faithful mirror of src/AetherNet.Security/Privacy/PanicWipe.cs:
 *
 *   - aethernet_duress_pin_hash / aethernet_verify_duress_pin — recognise the
 *     duress PIN (SHA-256, constant-time compare); the PIN itself is never
 *     stored.
 *   - aethernet_secure_erase — best-effort in-memory erase of key material
 *     (overwrite with random, then zero).
 *   - AETHERNET_IDENTITY_KEY_NAMES + aethernet_prekey_name /
 *     aethernet_signed_prekey_name — the canonical set of key-store entries a
 *     wipe must destroy.
 *
 * Destroying the hosting app's local database, platform keychain entries and any
 * decoy store is the app's job — it owns that storage. This gives the app the
 * crypto trigger, the secure-erase primitive, and the manifest of what to
 * remove, so every app wipes the same identity material the same way.
 *
 * The deterministic parts (PIN hash, key-name manifest) reproduce the C#
 * reference and the shared fixture (fixtures/panicwipe/vectors.json)
 * byte-for-byte. Crypto reuses the SDK's existing libsodium backend
 * (crypto_hash_sha256, sodium_memcmp, randombytes_buf).
 */

/** Length of a duress-PIN hash (SHA-256 output) in bytes. */
#define AETHERNET_DURESS_PIN_HASH_SIZE 32U

/** Number of one-time / signed pre-key slots a wipe sweeps (0..N-1). */
#define AETHERNET_MAX_PREKEYS 200

/** Number of entries in AETHERNET_IDENTITY_KEY_NAMES. */
#define AETHERNET_IDENTITY_KEY_NAME_COUNT 7U

/**
 * The key-store entry names that together constitute an AetherNet identity —
 * everything a panic-wipe must destroy, besides the numbered pre-keys. Same
 * names, same order as the C# reference PanicWipe.IdentityKeyNames.
 *
 * Array of AETHERNET_IDENTITY_KEY_NAME_COUNT NUL-terminated strings.
 */
extern const char *const AETHERNET_IDENTITY_KEY_NAMES[AETHERNET_IDENTITY_KEY_NAME_COUNT];

/**
 * The duress-PIN hash: SHA-256 of the UTF-8 PIN. Stored at setup and compared
 * on unlock — the PIN is only ever kept as this hash.
 *
 * Parameters:
 *   pin   — NUL-terminated UTF-8 PIN (the empty string "" is valid; NULL is
 *           treated as the empty string, matching hashing zero bytes).
 *   out32 — caller-allocated AETHERNET_DURESS_PIN_HASH_SIZE (32) bytes.
 *
 * Returns: true on success, false only on a NULL out32 or internal error.
 */
bool aethernet_duress_pin_hash(const char *pin, uint8_t *out32);

/**
 * Constant-time check of whether `pin` matches a stored duress-PIN hash — i.e.
 * whether unlocking should trigger a wipe.
 *
 * Parameters:
 *   pin        — NUL-terminated UTF-8 PIN (NULL treated as "").
 *   stored_hash — the stored hash bytes.
 *   stored_len — length of stored_hash in bytes.
 *
 * Returns: false if stored_len != AETHERNET_DURESS_PIN_HASH_SIZE (32) or
 *          stored_hash is NULL; otherwise the constant-time equality of
 *          SHA-256(pin) and stored_hash.
 */
bool aethernet_verify_duress_pin(const char *pin,
                                 const uint8_t *stored_hash,
                                 size_t stored_len);

/**
 * Best-effort secure erase of in-memory key material: overwrite with random
 * bytes, then zero. Call on every buffer holding a secret before releasing it.
 * Defence in depth — the runtime or OS may still hold copies, but this removes
 * the obvious one and leaves no plaintext secret in the buffer.
 *
 * A NULL buffer or len == 0 is a no-op.
 */
void aethernet_secure_erase(uint8_t *buf, size_t len);

/**
 * Key-store name of the i-th one-time pre-key: "prekey_{index}".
 *
 * Parameters:
 *   index — pre-key slot (typically 0..AETHERNET_MAX_PREKEYS-1; any int is
 *           formatted).
 *   out   — caller-allocated buffer receiving the NUL-terminated name.
 *   out_cap — capacity of `out` in bytes (32 is always sufficient).
 *
 * Returns: true on success, false on NULL out or insufficient out_cap.
 */
bool aethernet_prekey_name(int index, char *out, size_t out_cap);

/**
 * Key-store name of the i-th signed pre-key: "signed_prekey_{index}".
 *
 * Parameters:
 *   index — pre-key slot (typically 0..AETHERNET_MAX_PREKEYS-1; any int is
 *           formatted).
 *   out   — caller-allocated buffer receiving the NUL-terminated name.
 *   out_cap — capacity of `out` in bytes (40 is always sufficient).
 *
 * Returns: true on success, false on NULL out or insufficient out_cap.
 */
bool aethernet_signed_prekey_name(int index, char *out, size_t out_cap);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_PANIC_WIPE_H
