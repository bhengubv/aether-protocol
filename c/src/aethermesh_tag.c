// SPDX-License-Identifier: MIT
// AetherMeshTag — human-readable identity address derived from an Ed25519 public key.

#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <ctype.h>

#include "aethermesh/aethermesh_tag.h"
#include "aethermesh/security.h"   /* aethermesh_sha256()                          */
#include "aethermesh/constants.h"  /* AETHERMESH_SHA256_SIZE, AETHERMESH_ED25519_PUBLIC_KEY_SIZE */

/* ─── Crockford base-32 alphabet ─────────────────────────────────────────── */

static const char CROCKFORD_ALPHABET[32] =
    "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/*
 * Map a single character to its 5-bit Crockford value, or -1 on error.
 * Accepts upper- and lower-case equivalents per Crockford spec.
 * Does NOT map 'I'/'i' -> 1, 'L'/'l' -> 1, 'O'/'o' -> 0 (those are
 * decode-only aliases; we never emit them and we reject them on parse
 * to keep the round-trip clean).
 */
static int crockford_decode_char(char c)
{
    /*
     * Crockford base-32 alphabet: "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
     * Removed: I (confused with 1), L (confused with 1), O (confused with 0), U (confused with V)
     *
     * Encoding table:
     *   0-9   → 0-9
     *   A     → 10
     *   B     → 11
     *   C     → 12
     *   D     → 13
     *   E     → 14
     *   F     → 15
     *   G     → 16
     *   H     → 17
     *   J     → 18   (I skipped)
     *   K     → 19
     *   M     → 20   (L skipped)
     *   N     → 21
     *   P     → 22   (O skipped)
     *   Q     → 23
     *   R     → 24
     *   S     → 25
     *   T     → 26
     *   V     → 27   (U skipped)
     *   W     → 28
     *   X     → 29
     *   Y     → 30
     *   Z     → 31
     */
    if (c >= '0' && c <= '9') return (int)(c - '0');       /* 0-9 → 0-9   */
    if (c >= 'A' && c <= 'H') return (int)(c - 'A' + 10);  /* A-H → 10-17 */
    if (c == 'J')              return 18;
    if (c == 'K')              return 19;
    if (c == 'M')              return 20;
    if (c == 'N')              return 21;
    if (c == 'P')              return 22;
    if (c == 'Q')              return 23;
    if (c == 'R')              return 24;
    if (c == 'S')              return 25;
    if (c == 'T')              return 26;
    if (c == 'V')              return 27;
    if (c == 'W')              return 28;
    if (c == 'X')              return 29;
    if (c == 'Y')              return 30;
    if (c == 'Z')              return 31;

    /* lower-case — fold to upper and re-decode */
    if (c >= 'a' && c <= 'z') return crockford_decode_char((char)(c - 'a' + 'A'));

    return -1; /* not in alphabet (includes I, L, O, U and all others) */
}

/* ─── Internal helpers ───────────────────────────────────────────────────── */

/*
 * Core encoding.  Accepts a 32-byte SHA-256 hash and writes 12 bytes
 * (including NUL) into out->value.
 */
static void encode_tag(const uint8_t hash[32], aethermesh_tag_t *out)
{
    /*
     * Extract the first 50 bits from the hash.
     *
     * Byte layout used in the spec:
     *   bits[49..42] = hash[0]        (8 bits)
     *   bits[41..34] = hash[1]        (8 bits)
     *   bits[33..26] = hash[2]        (8 bits)
     *   bits[25..18] = hash[3]        (8 bits)
     *   bits[17..10] = hash[4]        (8 bits)
     *   bits[ 9.. 2] = hash[5]        (8 bits)
     *   bits[ 1.. 0] = hash[6] >> 6   (top 2 bits of byte 6)
     *
     * Total: 50 bits packed into a uint64_t with the most-significant bit at
     * position 49 (i.e. the 50-bit value occupies bits 0..49 of the uint64_t).
     */
    uint64_t bits =
        ((uint64_t)hash[0] << 42) |
        ((uint64_t)hash[1] << 34) |
        ((uint64_t)hash[2] << 26) |
        ((uint64_t)hash[3] << 18) |
        ((uint64_t)hash[4] << 10) |
        ((uint64_t)hash[5] <<  2) |
        ((uint64_t)(hash[6] >> 6) & 0x3U);

    /* Decode 10 × 5-bit groups, most-significant group first. */
    char chars[10];
    for (int i = 9; i >= 0; i--) {
        chars[i] = CROCKFORD_ALPHABET[bits & 0x1FU];
        bits >>= 5;
    }

    /* Format as "XXXXX-XXXXX\0" */
    out->value[0]  = chars[0];
    out->value[1]  = chars[1];
    out->value[2]  = chars[2];
    out->value[3]  = chars[3];
    out->value[4]  = chars[4];
    out->value[5]  = '-';
    out->value[6]  = chars[5];
    out->value[7]  = chars[6];
    out->value[8]  = chars[7];
    out->value[9]  = chars[8];
    out->value[10] = chars[9];
    out->value[11] = '\0';
}

/* ─── Public API ─────────────────────────────────────────────────────────── */

int aethermesh_tag_from_public_key(const uint8_t *public_key,
                               size_t key_len,
                               aethermesh_tag_t *out)
{
    if (!public_key || !out || key_len != AETHERMESH_ED25519_PUBLIC_KEY_SIZE)
        return -1;

    uint8_t hash[AETHERMESH_SHA256_SIZE];
    if (!aethermesh_sha256(public_key, key_len, hash))
        return -1;

    encode_tag(hash, out);
    return 0;
}

int aethermesh_tag_parse(const char *tag, aethermesh_tag_t *out)
{
    if (!tag || !out)
        return -1;

    size_t len = strlen(tag);
    char   data[10]; /* 10 Crockford chars, sans separator */
    size_t di = 0;

    if (len == 11) {
        /* Canonical "XXXXX-XXXXX" */
        if (tag[5] != '-')
            return -1;
        /* Validate and collect the five chars before the separator */
        for (int i = 0; i < 5; i++) {
            int v = crockford_decode_char(tag[i]);
            if (v < 0) return -1;
            data[di++] = (char)v;
        }
        /* Validate and collect the five chars after the separator */
        for (int i = 6; i < 11; i++) {
            int v = crockford_decode_char(tag[i]);
            if (v < 0) return -1;
            data[di++] = (char)v;
        }
    } else if (len == 10) {
        /* No separator: "XXXXXXXXXX" */
        for (size_t i = 0; i < 10; i++) {
            int v = crockford_decode_char(tag[i]);
            if (v < 0) return -1;
            data[di++] = (char)v;
        }
    } else {
        return -1;
    }

    /* Reconstruct canonical form into out */
    out->value[0]  = CROCKFORD_ALPHABET[(uint8_t)data[0]];
    out->value[1]  = CROCKFORD_ALPHABET[(uint8_t)data[1]];
    out->value[2]  = CROCKFORD_ALPHABET[(uint8_t)data[2]];
    out->value[3]  = CROCKFORD_ALPHABET[(uint8_t)data[3]];
    out->value[4]  = CROCKFORD_ALPHABET[(uint8_t)data[4]];
    out->value[5]  = '-';
    out->value[6]  = CROCKFORD_ALPHABET[(uint8_t)data[5]];
    out->value[7]  = CROCKFORD_ALPHABET[(uint8_t)data[6]];
    out->value[8]  = CROCKFORD_ALPHABET[(uint8_t)data[7]];
    out->value[9]  = CROCKFORD_ALPHABET[(uint8_t)data[8]];
    out->value[10] = CROCKFORD_ALPHABET[(uint8_t)data[9]];
    out->value[11] = '\0';

    return 0;
}

int aethermesh_tag_verify(const char *tag,
                      const uint8_t *public_key,
                      size_t key_len)
{
    if (!tag || !public_key)
        return 0;

    aethermesh_tag_t expected;
    if (aethermesh_tag_from_public_key(public_key, key_len, &expected) != 0)
        return 0;

    aethermesh_tag_t parsed;
    if (aethermesh_tag_parse(tag, &parsed) != 0)
        return 0;

    return (memcmp(expected.value, parsed.value, AETHERMESH_TAG_LENGTH) == 0) ? 1 : 0;
}

int aethermesh_tag_is_valid(const aethermesh_tag_t *tag)
{
    if (!tag)
        return 0;
    if (tag->value[0] == '\0')
        return 0;

    /* Check length — must be exactly 11 printable chars + NUL at [11] */
    for (int i = 0; i < 10; i++) {
        int idx = (i < 5) ? i : (i + 1); /* skip separator position */
        if (crockford_decode_char(tag->value[idx]) < 0)
            return 0;
    }
    if (tag->value[5] != '-')
        return 0;
    if (tag->value[11] != '\0')
        return 0;

    return 1;
}
