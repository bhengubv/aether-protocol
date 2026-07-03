// SPDX-License-Identifier: MIT
// BIP-39 recovery-phrase backup for an AetherNet identity.

#ifndef AETHERNET_BIP39_H
#define AETHERNET_BIP39_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * BIP-39 mnemonic codec over the official English wordlist. Converts between
 * entropy, the human-writable recovery phrase, and the derived seed.
 *
 * This is the real, standard BIP-39 algorithm, verified against the official
 * Trezor test vectors (fixtures/bip39/vectors.json) — a phrase produced here
 * restores on any conformant BIP-39 wallet, and every AetherNet language SDK
 * reproduces the same words and seed byte-for-byte. The embedded wordlist is
 * the 2048-word official English list (fixtures/bip39/english.txt,
 * SHA-256 2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda).
 *
 *   entropy (16..32 bytes, multiple of 4) --entropy_to_mnemonic--> phrase
 *   phrase  --mnemonic_to_entropy--> entropy   (SHA-256 checksum enforced)
 *   phrase  --mnemonic_to_seed--> 64-byte seed  (PBKDF2-HMAC-SHA512, 2048 rounds)
 *
 * NFKD note: BIP-39 specifies NFKD normalization of the mnemonic and passphrase
 * before PBKDF2. The official English vectors are pure ASCII, for which NFKD is
 * the identity, so this C port hashes the UTF-8 bytes as-is. Full NFKD of an
 * arbitrary non-ASCII passphrase is out of scope here, matching typical C
 * BIP-39 libraries; ASCII passphrases (including the "TREZOR" vector) are exact.
 */

/** Number of words in the official BIP-39 English wordlist. */
#define AETHERNET_BIP39_WORDLIST_SIZE 2048U

/** Length of the derived BIP-39 seed in bytes (PBKDF2-HMAC-SHA512 output). */
#define AETHERNET_BIP39_SEED_SIZE 64U

/** PBKDF2 iteration count mandated by BIP-39. */
#define AETHERNET_BIP39_PBKDF2_ITERATIONS 2048U

/** Byte length of an AetherNet identity seed (Ed25519 private seed → 24 words). */
#define AETHERNET_BIP39_IDENTITY_SEED_SIZE 32U

/** Ed25519 public-key length (bytes). */
#define AETHERNET_BIP39_ED25519_PUBLIC_KEY_SIZE 32U

/**
 * The embedded official BIP-39 English wordlist, indexable 0..2047.
 * Each entry is a NUL-terminated lowercase ASCII word.
 */
extern const char *const aethernet_bip39_wordlist[AETHERNET_BIP39_WORDLIST_SIZE];

/**
 * Encode entropy as a BIP-39 mnemonic phrase (single-space-separated words).
 *
 * Parameters:
 *   entropy      — 16, 20, 24, 28, or 32 bytes (128..256 bits).
 *   entropy_len  — length of entropy in bytes.
 *   out          — caller-allocated buffer receiving a NUL-terminated phrase.
 *   out_cap      — capacity of `out` in bytes (including the NUL terminator).
 *                  A 24-word phrase needs at most 24*9 = 216 bytes incl. NUL;
 *                  AETHERNET_BIP39_MAX_PHRASE_LEN is a safe upper bound.
 *
 * Returns: true on success. false if entropy_len is not a supported length,
 *          any pointer is NULL, or `out_cap` is too small.
 */
bool aethernet_bip39_entropy_to_mnemonic(const uint8_t *entropy,
                                          size_t entropy_len,
                                          char *out,
                                          size_t out_cap);

/**
 * Decode a BIP-39 mnemonic back to its entropy, enforcing the SHA-256 checksum.
 *
 * Rejects (returns false) an unknown word, a word count not in
 * {12,15,18,21,24}, or a checksum mismatch — so a mistyped phrase is refused
 * rather than silently yielding the wrong secret. Words are separated by any
 * run of ASCII spaces; leading/trailing spaces are ignored.
 *
 * Parameters:
 *   mnemonic     — NUL-terminated phrase.
 *   out_entropy  — caller-allocated buffer of at least `out_cap` bytes.
 *   out_cap      — capacity of out_entropy (>= 32 covers every valid phrase).
 *   out_len      — receives the number of entropy bytes written (16..32).
 *
 * Returns: true on success, false on any malformed / invalid input.
 */
bool aethernet_bip39_mnemonic_to_entropy(const char *mnemonic,
                                          uint8_t *out_entropy,
                                          size_t out_cap,
                                          size_t *out_len);

/**
 * Derive the 64-byte BIP-39 seed from a mnemonic and optional passphrase, using
 * PBKDF2-HMAC-SHA512 with 2048 iterations and salt "mnemonic" + passphrase.
 *
 * The mnemonic is canonicalized to single-space-separated words before use
 * (matching the C# reference). Inputs are hashed as UTF-8 bytes (see the NFKD
 * note above). Does NOT verify the checksum — callers wanting rejection of a
 * mistyped phrase should first call aethernet_bip39_mnemonic_to_entropy().
 *
 * Parameters:
 *   mnemonic   — NUL-terminated phrase.
 *   passphrase — NUL-terminated passphrase, or NULL for none ("").
 *   out_seed   — caller-allocated AETHERNET_BIP39_SEED_SIZE (64) bytes.
 *
 * Returns: true on success, false on NULL mnemonic/out_seed or internal error.
 */
bool aethernet_bip39_mnemonic_to_seed(const char *mnemonic,
                                      const char *passphrase,
                                      uint8_t *out_seed);

/**
 * Returns true if `mnemonic` is a well-formed BIP-39 phrase with a valid
 * checksum (i.e. aethernet_bip39_mnemonic_to_entropy would succeed).
 */
bool aethernet_bip39_is_valid(const char *mnemonic);

/**
 * A safe upper bound on the buffer size (incl. NUL) for any BIP-39 phrase this
 * codec emits. Longest English word is 8 chars; 24 words * (8+1) = 216, +1 NUL.
 */
#define AETHERNET_BIP39_MAX_PHRASE_LEN 217U

/* ─────────────────────────────────────────────────────────────────────────
 * Identity recovery-phrase backup
 *
 * An AetherNet identity is an Ed25519 key pair whose private key is a 32-byte
 * seed — exactly 256 bits, mapping onto a 24-word BIP-39 phrase. From the 24
 * words alone the identity is fully reconstructed on any device; no server, no
 * account, no custodian holds anything — the phrase *is* the identity.
 * ──────────────────────────────────────────────────────────────────────── */

/**
 * Produce the 24-word recovery phrase for an identity's 32-byte Ed25519 seed.
 *
 * Parameters:
 *   ed25519_private_key — the 32-byte Ed25519 private seed.
 *   out                 — caller-allocated buffer for the NUL-terminated phrase.
 *   out_cap             — capacity of `out` (AETHERNET_BIP39_MAX_PHRASE_LEN
 *                         is always sufficient).
 *
 * Returns: true on success, false if the key is not 32 bytes, a pointer is
 *          NULL, or `out_cap` is too small.
 */
bool aethernet_identity_to_recovery_phrase(const uint8_t *ed25519_private_key,
                                            char *out,
                                            size_t out_cap);

/**
 * Restore a full identity key pair from a 24-word recovery phrase. The BIP-39
 * checksum is enforced, so a mistyped word is rejected rather than silently
 * reconstructing a different identity. Only a 24-word (256-bit) phrase is
 * accepted as an identity seed.
 *
 * Parameters:
 *   recovery_phrase — NUL-terminated 24-word phrase.
 *   out_private_key — caller-allocated 32 bytes (the recovered Ed25519 seed).
 *   out_public_key  — caller-allocated 32 bytes (Ed25519 public key derived
 *                     from the seed).
 *
 * Returns: true on success. false if the phrase is malformed, fails its
 *          checksum, is not 24 words, or public-key derivation fails.
 */
bool aethernet_identity_from_recovery_phrase(const char *recovery_phrase,
                                             uint8_t *out_private_key,
                                             uint8_t *out_public_key);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_BIP39_H
