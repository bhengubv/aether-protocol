// SPDX-License-Identifier: MIT
// AetherTag — human-readable identity address derived from an Ed25519 public key.
//
// Algorithm:
//   SHA-256(public_key) -> extract first 50 bits -> encode as 10 Crockford
//   base-32 characters -> format as "XXXXX-XXXXX"
//
// Crockford base-32 alphabet: "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
// (standard base-32 with I, L, O, U removed to avoid visual ambiguity)

#ifndef AETHER_TAG_H
#define AETHER_TAG_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/** Total buffer size: 5 data chars + '-' + 5 data chars + '\0' = 12 bytes. */
#define AETHER_TAG_LENGTH 12

/** An AetherTag value.  Always NUL-terminated; use value[] directly as a
 *  C string when the tag has been produced by aether_tag_from_public_key()
 *  or aether_tag_parse(). */
typedef struct {
    char value[AETHER_TAG_LENGTH];
} aether_tag_t;

/**
 * Derive an AetherTag from a 32-byte Ed25519 public key.
 *
 * Parameters:
 *   public_key — raw 32-byte Ed25519 public key (must not be NULL)
 *   key_len    — must equal 32
 *   out        — caller-allocated output (must not be NULL)
 *
 * Returns:  0 on success.
 *          -1 if public_key or out is NULL, or key_len != 32.
 */
int aether_tag_from_public_key(const uint8_t *public_key,
                               size_t key_len,
                               aether_tag_t *out);

/**
 * Parse a tag string into an aether_tag_t.
 *
 * Accepted formats (all normalised to upper-case with separator):
 *   "KXJB7-MN2P4"   — canonical form (with separator)
 *   "KXJB7MN2P4"    — no separator
 *   "kxjb7-mn2p4"   — lower-case
 *   "kxjb7mn2p4"    — lower-case, no separator
 *
 * Parameters:
 *   tag — input string (must not be NULL)
 *   out — caller-allocated output (must not be NULL)
 *
 * Returns:  0 on success.
 *          -1 if tag or out is NULL, length is wrong, or a character is not
 *             in the Crockford base-32 alphabet.
 */
int aether_tag_parse(const char *tag, aether_tag_t *out);

/**
 * Verify that a tag matches a given public key.
 *
 * Re-derives the expected tag from public_key and compares it to tag.
 *
 * Returns: 1 if the tag matches the public key.
 *          0 otherwise (mismatch, invalid tag format, or invalid key).
 */
int aether_tag_verify(const char *tag,
                      const uint8_t *public_key,
                      size_t key_len);

/**
 * Check whether an aether_tag_t contains a non-empty, well-formed tag.
 *
 * Returns: 1 if valid (non-empty and in "XXXXX-XXXXX" format with all
 *            characters drawn from the Crockford alphabet).
 *          0 otherwise.
 */
int aether_tag_is_valid(const aether_tag_t *tag);

#ifdef __cplusplus
}
#endif

#endif /* AETHER_TAG_H */
