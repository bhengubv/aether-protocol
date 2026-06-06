// SPDX-License-Identifier: MIT
//
// ChunkBitmapPayload wire-format codec — C11 implementation.
//
// No external dependencies beyond the C standard library.  Base64 encoding
// is implemented inline (RFC 4648 §4 standard alphabet, with padding).
// JSON marshalling uses snprintf for correct field ordering without a JSON
// library.
//
// Suppress MSVC safe-string warnings — all string operations here are
// length-bounded and correct for C11.
#ifdef _MSC_VER
#  define _CRT_SECURE_NO_WARNINGS
#endif

#include "aethernet/chunk_bitmap.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

// ── Internal: RFC 4648 §4 Base64 encoder (standard alphabet, padded) ─────────

static const char s_b64_chars[] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// Returns a freshly malloc'd NUL-terminated Base64 string for data[0..len).
/// Returns NULL on allocation failure. Caller frees.
static char *b64_encode(const uint8_t *data, size_t len)
{
    // RFC 4648: output length = ceil(len/3)*4 bytes, plus NUL.
    size_t olen = ((len + 2) / 3) * 4;
    char  *out  = (char *)malloc(olen + 1);
    if (!out) return NULL;

    size_t i = 0, j = 0;
    while (i < len) {
        uint32_t a = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t b = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t c = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t triple = (a << 16) | (b << 8) | c;
        out[j++] = s_b64_chars[(triple >> 18) & 0x3F];
        out[j++] = s_b64_chars[(triple >> 12) & 0x3F];
        out[j++] = s_b64_chars[(triple >>  6) & 0x3F];
        out[j++] = s_b64_chars[ triple        & 0x3F];
    }

    // Overwrite with '=' padding for the incomplete trailing group.
    size_t pad = (3 - (len % 3)) % 3;
    for (size_t p = 0; p < pad; p++)
        out[olen - 1 - p] = '=';

    out[olen] = '\0';
    return out;
}

// ── aethernet_bitset_encode ──────────────────────────────────────────────────────

aethernet_bitset_t aethernet_bitset_encode(int        chunk_count,
                                     const int *have_indices,
                                     int        index_count)
{
    aethernet_bitset_t result = {NULL, 0};
    if (chunk_count <= 0)
        return result;

    size_t blen = (size_t)((chunk_count + 7) / 8);
    uint8_t *bytes = (uint8_t *)calloc(blen, 1);
    if (!bytes)
        return result;

    for (int k = 0; k < index_count; k++) {
        int i = have_indices[k];
        if (i < 0 || i >= chunk_count)
            continue; // silently skip out-of-range indices
        bytes[i >> 3] = (uint8_t)(bytes[i >> 3] | (uint8_t)(1u << (i & 7)));
    }

    result.bytes = bytes;
    result.len   = blen;
    return result;
}

// ── aethernet_bitset_decode ──────────────────────────────────────────────────────

int *aethernet_bitset_decode(const uint8_t *bitset,
                          size_t         bitset_len,
                          int            chunk_count,
                          int           *out_count)
{
    *out_count = 0;
    if (!bitset || bitset_len == 0 || chunk_count <= 0)
        return NULL;

    // Count set bits first so we allocate exactly the right size.
    int limit = chunk_count;
    if ((size_t)(limit / 8) > bitset_len)
        limit = (int)(bitset_len * 8);

    int n = 0;
    for (int i = 0; i < limit; i++)
        if ((bitset[i >> 3] & (uint8_t)(1u << (i & 7))) != 0)
            n++;

    if (n == 0)
        return NULL;

    int *result = (int *)malloc((size_t)n * sizeof(int));
    if (!result)
        return NULL;

    int j = 0;
    for (int i = 0; i < limit; i++)
        if ((bitset[i >> 3] & (uint8_t)(1u << (i & 7))) != 0)
            result[j++] = i;

    *out_count = n;
    return result;
}

// ── aethernet_bitset_free ────────────────────────────────────────────────────────

void aethernet_bitset_free(aethernet_bitset_t bs)
{
    free(bs.bytes);
}

// ── aethernet_chunk_bitmap_marshal_json ─────────────────────────────────────────

char *aethernet_chunk_bitmap_marshal_json(const char    *root_hash,
                                       int            chunk_count,
                                       const uint8_t *have_bitset,
                                       size_t         have_bitset_len,
                                       uint32_t       generation)
{
    // Base64-encode the bitset first.
    char *b64 = NULL;
    if (have_bitset && have_bitset_len > 0) {
        b64 = b64_encode(have_bitset, have_bitset_len);
        if (!b64) return NULL;
    } else {
        // Empty bitset → empty string (chunk_count == 0 case).
        b64 = (char *)malloc(1);
        if (!b64) return NULL;
        b64[0] = '\0';
    }

    // Measure required buffer size.
    int needed = snprintf(NULL, 0,
        "{\"root_hash\":\"%s\",\"chunk_count\":%d,"
        "\"have_bitset\":\"%s\",\"generation\":%u}",
        root_hash, chunk_count, b64, (unsigned)generation);

    char *json = (char *)malloc((size_t)(needed + 1));
    if (!json) {
        free(b64);
        return NULL;
    }
    snprintf(json, (size_t)(needed + 1),
        "{\"root_hash\":\"%s\",\"chunk_count\":%d,"
        "\"have_bitset\":\"%s\",\"generation\":%u}",
        root_hash, chunk_count, b64, (unsigned)generation);

    free(b64);
    return json;
}
