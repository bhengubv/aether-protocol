// SPDX-License-Identifier: MIT
// Aether Security - Ed25519, AES-GCM, HMAC-SHA256, HKDF

#ifndef AETHER_SECURITY_H
#define AETHER_SECURITY_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "constants.h"
#include "aether/protocol.h"         /* aether_mesh_packet_t / aether_packet_t */
#include "aether_reputation.h"       /* AetherNodeReputationService            */

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Generate an Ed25519 key pair.
 *
 * Returns: true on success.
 * Outputs:
 *   out_private: AETHER_ED25519_PRIVATE_KEY_SIZE bytes
 *   out_public:  AETHER_ED25519_PUBLIC_KEY_SIZE bytes
 *
 * Caller must allocate output buffers.
 */
bool aether_ed25519_generate_keypair(uint8_t *out_private,
                                     uint8_t *out_public);

/**
 * Sign data with an Ed25519 private key.
 *
 * Returns: true on success.
 * Output:
 *   out_signature: AETHER_ED25519_SIGNATURE_SIZE bytes (caller allocates)
 */
bool aether_ed25519_sign(const uint8_t *private_key,
                        const uint8_t *data,
                        size_t data_len,
                        uint8_t *out_signature);

/**
 * Verify an Ed25519 signature.
 *
 * Returns: true if signature is valid, false otherwise.
 */
bool aether_ed25519_verify(const uint8_t *public_key,
                          const uint8_t *data,
                          size_t data_len,
                          const uint8_t *signature);

/**
 * AES-256-GCM encryption.
 *
 * Returns: true on success.
 * Output:
 *   out_ciphertext: variable length (same as plaintext_len)
 *   out_tag:        AETHER_AES_GCM_TAG_SIZE bytes
 *   out_nonce:      AETHER_AES_GCM_NONCE_SIZE bytes (if nonce is NULL, randomly generated)
 *
 * Caller must allocate output buffers.
 * If nonce is NULL, a random nonce is generated and stored in out_nonce.
 * If nonce is not NULL, that nonce is used and copied to out_nonce.
 */
bool aether_aes256_gcm_encrypt(const uint8_t *plaintext,
                               size_t plaintext_len,
                               const uint8_t *key,  // 32 bytes for AES-256
                               const uint8_t *nonce, // NULL to generate, or AETHER_AES_GCM_NONCE_SIZE bytes
                               const uint8_t *aad,   // Additional authenticated data (can be NULL)
                               size_t aad_len,
                               uint8_t *out_ciphertext,
                               uint8_t *out_tag,
                               uint8_t *out_nonce);

/**
 * AES-256-GCM decryption.
 *
 * Returns: true on success, false if authentication fails.
 * Output:
 *   out_plaintext: variable length (same as ciphertext_len)
 *
 * Caller must allocate output buffer.
 */
bool aether_aes256_gcm_decrypt(const uint8_t *ciphertext,
                               size_t ciphertext_len,
                               const uint8_t *key,     // 32 bytes for AES-256
                               const uint8_t *nonce,   // AETHER_AES_GCM_NONCE_SIZE bytes
                               const uint8_t *tag,     // AETHER_AES_GCM_TAG_SIZE bytes
                               const uint8_t *aad,     // Additional authenticated data (can be NULL)
                               size_t aad_len,
                               uint8_t *out_plaintext);

/**
 * HMAC-SHA256 (used in symmetric ratchet).
 *
 * Returns: true on success.
 * Output:
 *   out_hash: AETHER_HMAC_SHA256_SIZE bytes (caller allocates)
 */
bool aether_hmac_sha256(const uint8_t *key,
                       size_t key_len,
                       const uint8_t *data,
                       size_t data_len,
                       uint8_t *out_hash);

/**
 * SHA-256 hash.
 *
 * Returns: true on success.
 * Output:
 *   out_hash: AETHER_SHA256_SIZE bytes (caller allocates)
 */
bool aether_sha256(const uint8_t *data,
                  size_t data_len,
                  uint8_t *out_hash);

/**
 * HKDF-SHA256 (HMAC-based Key Derivation Function).
 * Implements RFC 5869 with extract-and-expand.
 *
 * Returns: true on success.
 * Output:
 *   out_okm: output_len bytes (caller allocates)
 *
 * Parameters:
 *   salt:     can be NULL; if NULL, uses zeros
 *   salt_len: length of salt
 *   ikm:      input key material
 *   ikm_len:  length of ikm
 *   info:     context/application-specific info
 *   info_len: length of info
 *   output_len: desired output length
 */
bool aether_hkdf_sha256(const uint8_t *salt,
                       size_t salt_len,
                       const uint8_t *ikm,
                       size_t ikm_len,
                       const uint8_t *info,
                       size_t info_len,
                       size_t output_len,
                       uint8_t *out_okm);

/**
 * Signal Double-Ratchet KDF_RK (Signal §5.2).
 *
 * Derives a new root key and a new chain key from the current root key and
 * a fresh DH output. Implementation: HKDF-SHA256 with
 *   salt = root_key (32 bytes)
 *   ikm  = dh_output (32 bytes)
 *   info = UTF-8 "aether-ratchet-rk-v1"
 *   L    = 64
 * The 64-byte output is split: first 32 bytes -> new root key, second 32
 * bytes -> new chain key. Wire-format compatible with every other Aether
 * Signal-Protocol implementation; verified by
 * fixtures/signal/expected/kdf_rk_basic.json.
 *
 * Caller-allocated outputs (each 32 bytes):
 *   out_new_root_key:  new root key (RK')
 *   out_new_chain_key: new chain key (CK)
 *
 * Returns: true on success.
 */
bool aether_signal_kdf_rk(const uint8_t *root_key,
                          const uint8_t *dh_output,
                          uint8_t *out_new_root_key,
                          uint8_t *out_new_chain_key);

/**
 * X25519 key agreement primitives (RFC 7748).
 *
 * Used for the X3DH key exchange in the Signal Protocol layer. Public-key
 * wire format: raw 32-byte little-endian Montgomery u-coordinate per RFC
 * 7748 §6.1. No SEC1 prefix, no compressed/uncompressed flag — same encoding
 * every Signal-Protocol-style implementation uses across the cross-language
 * family.
 */

/** X25519 public key size in bytes. */
#define AETHER_X25519_PUBLIC_KEY_SIZE 32U
/** X25519 private key size in bytes. */
#define AETHER_X25519_PRIVATE_KEY_SIZE 32U
/** X25519 shared-secret size in bytes (output of one DH op). */
#define AETHER_X25519_SHARED_SECRET_SIZE 32U

/**
 * Generate a fresh X25519 keypair.
 *
 * Output buffers (caller-allocated):
 *   out_private: AETHER_X25519_PRIVATE_KEY_SIZE bytes
 *   out_public:  AETHER_X25519_PUBLIC_KEY_SIZE bytes
 *
 * Returns: true on success.
 */
bool aether_x25519_generate_keypair(uint8_t *out_private,
                                    uint8_t *out_public);

/**
 * Compute the X25519 ECDH shared secret. Returns 32 raw shared-secret bytes
 * suitable for direct concatenation into an HKDF input.
 *
 * RFC 7748 §6.1 mandates that implementations check the result is not the
 * all-zero point — that's a small-subgroup attack indicator via a low-order
 * remote public key. This function returns false in that case.
 *
 * Output buffer (caller-allocated):
 *   out_shared: AETHER_X25519_SHARED_SECRET_SIZE bytes
 *
 * Returns: true on success, false on the all-zero case or invalid pointers.
 */
bool aether_x25519_agree(const uint8_t *local_private,
                         const uint8_t *remote_public,
                         uint8_t *out_shared);

/**
 * Derive the X25519 public key from a raw private key (priv * Basepoint).
 *
 * Output buffer (caller-allocated):
 *   out_public: AETHER_X25519_PUBLIC_KEY_SIZE bytes
 *
 * Returns: true on success.
 */
bool aether_x25519_derive_public(const uint8_t *private_key,
                                 uint8_t *out_public);

/**
 * Zero sensitive memory (constant-time, not optimized away).
 * Used for key material cleanup.
 */
void aether_zeroize(void *mem, size_t len);

/**
 * Generate cryptographically random bytes.
 * Uses libsodium's randombytes.
 *
 * Returns: true on success.
 */
bool aether_random_bytes(uint8_t *out, size_t len);

/* ─────────────────────────────────────────────────────────────────────────
 * Nonce deduplication store
 *
 * Tracks (source_uhid, nonce) pairs for replay-attack prevention.
 * Entries are recorded with a TTL (seconds); expired entries are pruned
 * lazily on each subsequent call for the same source.
 *
 * Thread-safety: NOT thread-safe — single-threaded embedded targets only,
 * matching the rest of the C library.
 * ──────────────────────────────────────────────────────────────────────── */

/** Opaque nonce-store handle.  Create with aether_nonce_store_new(). */
typedef struct aether_nonce_store aether_nonce_store_t;

/**
 * Allocate and initialise a new nonce store.
 * Caller must free with aether_nonce_store_free().
 *
 * Returns: non-NULL on success, NULL on allocation failure.
 */
aether_nonce_store_t *aether_nonce_store_new(void);

/**
 * Free a nonce store previously created by aether_nonce_store_new().
 * Passing NULL is safe (no-op).
 */
void aether_nonce_store_free(aether_nonce_store_t *store);

/**
 * Check whether (source_uhid, nonce[nonce_len]) has been seen before,
 * and if not, record it with a TTL of `ttl_seconds` seconds.
 *
 * Parameters:
 *   store       — nonce store (must not be NULL)
 *   source_uhid — null-terminated UHID string identifying the sender
 *   nonce       — raw nonce bytes
 *   nonce_len   — number of nonce bytes (must be > 0)
 *   ttl_seconds — how long to remember this (source, nonce) pair
 *
 * Returns: true  — nonce is fresh; pair has been recorded.
 *          false — nonce is a replay (already seen within TTL), or any
 *                  parameter is NULL / nonce_len == 0.
 */
bool aether_nonce_store_check_and_record(aether_nonce_store_t *store,
                                         const char *source_uhid,
                                         const uint8_t *nonce,
                                         size_t nonce_len,
                                         int ttl_seconds);

/* ─────────────────────────────────────────────────────────────────────────
 * PacketSigning service
 *
 * Thin service layer over the nonce store + Ed25519 verify, with optional
 * NodeReputationService injection.  When a reputation service is attached:
 *
 *   • Nonce replay  → aether_reputation_record_replay(rep, source_uhid)
 *   • Sig failure   → aether_reputation_record_sig_failure(rep, source_uhid)
 *
 * The service owns NO heap allocation — embed AetherPacketSigningService
 * by value or as a static.  Call aether_packet_signing_init() before use.
 * ──────────────────────────────────────────────────────────────────────── */

/** Packet signing / verification service. */
typedef struct {
    /** Nonce deduplication store.  May be NULL; if so, replay checking is
     *  skipped (useful for unit tests that only test signature logic). */
    aether_nonce_store_t *nonce_store;

    /** Optional reputation service.  NULL = no reputation signals fired. */
    AetherNodeReputationService *reputation;
} AetherPacketSigningService;

/**
 * Initialise an AetherPacketSigningService.
 *
 * Parameters:
 *   svc        — service to initialise (must not be NULL)
 *   nonce_store — nonce store to use (may be NULL to skip replay detection)
 *
 * The reputation pointer defaults to NULL; use
 * aether_packet_signing_set_reputation() to attach one.
 */
void aether_packet_signing_init(AetherPacketSigningService *svc,
                                aether_nonce_store_t *nonce_store);

/**
 * Attach (or detach) a reputation service.
 *
 * Parameters:
 *   svc — service (must not be NULL)
 *   rep — reputation service to attach, or NULL to detach
 *
 * The caller retains ownership of `rep`; the signing service holds only a
 * non-owning pointer.
 */
void aether_packet_signing_set_reputation(AetherPacketSigningService *svc,
                                          AetherNodeReputationService *rep);

/**
 * Verify the Ed25519 signature on a packet, checking for nonce replay first.
 *
 * Workflow:
 *   1. If svc->nonce_store != NULL: call aether_nonce_store_check_and_record().
 *      On replay (returns false): fire reputation hook + return false.
 *   2. Rebuild signable data via aether_packet_get_signable_data().
 *   3. aether_ed25519_verify() against sender_public_key.
 *      On failure: fire reputation hook + return false.
 *   4. Return true.
 *
 * Parameters:
 *   svc              — signing service (must not be NULL)
 *   packet           — mesh packet to verify (must not be NULL)
 *   sender_public_key — 32-byte Ed25519 public key of the claimed sender
 *   ttl_seconds      — TTL passed to the nonce store (e.g. 300 for 5 min)
 *
 * Returns: true if valid, false otherwise.
 *
 * Note: aether_packet_get_signable_data() is declared in aether/protocol.h.
 *       Callers who use this function must also include aether/protocol.h.
 */
bool aether_packet_signing_verify(AetherPacketSigningService *svc,
                                  const aether_mesh_packet_t *packet,
                                  const uint8_t *sender_public_key,
                                  int ttl_seconds);

#ifdef __cplusplus
}
#endif

#endif // AETHER_SECURITY_H
