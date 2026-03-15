// SPDX-License-Identifier: MIT
// Aether Security - Ed25519, AES-GCM, HMAC-SHA256, HKDF

#ifndef AETHER_SECURITY_H
#define AETHER_SECURITY_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "constants.h"

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

#ifdef __cplusplus
}
#endif

#endif // AETHER_SECURITY_H
