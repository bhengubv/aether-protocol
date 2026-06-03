// SPDX-License-Identifier: MIT
//
// Unit tests for the ChunkBitmap wire-format codec (chunk_bitmap.c).
//
// Exercises the same 4 canonical vectors used by every other language runner
// in fixtures/content/chunk_bitmap_vectors.json:
//   chunk_bitmap_sparse — 8 chunks, indices [0,2,5]
//   chunk_bitmap_empty  — 8 chunks, no indices
//   chunk_bitmap_full   — 8 chunks, all indices
//   chunk_bitmap_16bit  — 16 chunks, selected indices

#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aether/chunk_bitmap.h"

// ── Vector table ──────────────────────────────────────────────────────────────

typedef struct {
    const char *name;
    int         chunk_count;
    int         have_indices[16];
    int         index_count;
    const char *have_bitset_hex;   // lowercase
    const char *have_bitset_base64;
    uint32_t    generation;
    const char *expected_json;
    const char *root_hash;
} vector_t;

static const vector_t VECTORS[] = {
    {
        .name             = "chunk_bitmap_sparse",
        .chunk_count      = 8,
        .have_indices     = {0, 2, 5},
        .index_count      = 3,
        .have_bitset_hex  = "25",
        .have_bitset_base64 = "JQ==",
        .generation       = 1,
        .root_hash        = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        .expected_json    =
            "{\"root_hash\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\","
            "\"chunk_count\":8,\"have_bitset\":\"JQ==\",\"generation\":1}",
    },
    {
        .name             = "chunk_bitmap_empty",
        .chunk_count      = 8,
        .have_indices     = {0},
        .index_count      = 0,   // no indices present
        .have_bitset_hex  = "00",
        .have_bitset_base64 = "AA==",
        .generation       = 1,
        .root_hash        = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        .expected_json    =
            "{\"root_hash\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\","
            "\"chunk_count\":8,\"have_bitset\":\"AA==\",\"generation\":1}",
    },
    {
        .name             = "chunk_bitmap_full",
        .chunk_count      = 8,
        .have_indices     = {0, 1, 2, 3, 4, 5, 6, 7},
        .index_count      = 8,
        .have_bitset_hex  = "ff",
        .have_bitset_base64 = "/w==",
        .generation       = 2,
        .root_hash        = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        .expected_json    =
            "{\"root_hash\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\","
            "\"chunk_count\":8,\"have_bitset\":\"/w==\",\"generation\":2}",
    },
    {
        // 16-chunk content, chunks 0 and 8 present.
        // Byte 0 = 0x01 (bit0), byte 1 = 0x01 (bit8). Tests multi-byte bitset.
        .name             = "chunk_bitmap_16chunks_partial",
        .chunk_count      = 16,
        .have_indices     = {0, 8},
        .index_count      = 2,
        .have_bitset_hex  = "0101",
        .have_bitset_base64 = "AQE=",
        .generation       = 5,
        .root_hash        = "ba7816bf8f01cfea414140de5dae2ec73b00361a396177a9cb410ff61f20015a",
        .expected_json    =
            "{\"root_hash\":\"ba7816bf8f01cfea414140de5dae2ec73b00361a396177a9cb410ff61f20015a\","
            "\"chunk_count\":16,\"have_bitset\":\"AQE=\",\"generation\":5}",
    },
};
#define VECTOR_COUNT ((int)(sizeof(VECTORS) / sizeof(VECTORS[0])))

// ── Helper: hex-encode a byte buffer ─────────────────────────────────────────

static void to_hex(const uint8_t *data, size_t len, char *out)
{
    for (size_t i = 0; i < len; i++)
        snprintf(out + i * 2, 3, "%02x", (unsigned)data[i]);
    out[len * 2] = '\0';
}

// ── Helper: simple base64 decoder (no libsodium dependency) ──────────────────

// Build the decode table at first call (avoids GCC-extension range initialiser).
static uint8_t s_b64_dec[256];
static int     s_b64_dec_ready = 0;

static void b64_dec_init(void)
{
    if (s_b64_dec_ready) return;
    memset(s_b64_dec, 0xFF, sizeof(s_b64_dec));
    const char *alpha =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    for (int i = 0; i < 64; i++)
        s_b64_dec[(unsigned char)alpha[i]] = (uint8_t)i;
    s_b64_dec_ready = 1;
}

// Decode base64 string to byte buffer. Returns allocated bytes + sets *out_len.
static uint8_t *b64_decode(const char *s, size_t *out_len)
{
    b64_dec_init();
    size_t slen = strlen(s);
    // Remove padding from count
    size_t pad = 0;
    while (slen > 0 && s[slen - 1 - pad] == '=') pad++;

    size_t olen = (slen / 4) * 3 - pad;
    uint8_t *out = (uint8_t *)malloc(olen + 1);
    if (!out) { *out_len = 0; return NULL; }

    size_t j = 0;
    for (size_t i = 0; i < slen; i += 4) {
        uint8_t a = s_b64_dec[(uint8_t)s[i]];
        uint8_t b = s_b64_dec[(uint8_t)s[i+1]];
        uint8_t c = (i+2 < slen && s[i+2] != '=') ? s_b64_dec[(uint8_t)s[i+2]] : 0;
        uint8_t d = (i+3 < slen && s[i+3] != '=') ? s_b64_dec[(uint8_t)s[i+3]] : 0;
        if (j < olen)                     out[j++] = (uint8_t)((a << 2) | (b >> 4));
        if (j < olen && s[i+2] != '=')   out[j++] = (uint8_t)((b << 4) | (c >> 2));
        if (j < olen && s[i+3] != '=')   out[j++] = (uint8_t)((c << 6) | d);
    }
    *out_len = olen;
    return out;
}

// ── Test: encode produces correct bitset ─────────────────────────────────────

static void test_encode_produces_correct_bitset(void)
{
    printf("TEST: encode produces correct bitset...");
    for (int v = 0; v < VECTOR_COUNT; v++) {
        const vector_t *vec = &VECTORS[v];
        aether_bitset_t bs = aether_bitset_encode(
            vec->chunk_count, vec->have_indices, vec->index_count);

        // Verify length = ceil(chunk_count / 8).
        size_t expected_len = (size_t)((vec->chunk_count + 7) / 8);
        assert(bs.len == expected_len);

        // Verify hex.
        char hex_buf[128];
        to_hex(bs.bytes, bs.len, hex_buf);
        assert(strcmp(hex_buf, vec->have_bitset_hex) == 0);

        // Verify base64.
        char *b64 = aether_chunk_bitmap_marshal_json(
            vec->root_hash, vec->chunk_count, bs.bytes, bs.len, 0);
        // We can't easily get just the base64 out of marshal, so build it
        // by encoding separately via the static helper.
        // Instead do a decode + re-encode check:
        size_t decoded_len;
        uint8_t *decoded = b64_decode(vec->have_bitset_base64, &decoded_len);
        assert(decoded_len == bs.len);
        assert(memcmp(decoded, bs.bytes, bs.len) == 0);
        free(decoded);
        free(b64);

        aether_bitset_free(bs);
    }
    printf(" OK\n");
}

// ── Test: decode recovers correct indices ─────────────────────────────────────

static void test_decode_recovers_correct_indices(void)
{
    printf("TEST: decode recovers correct indices...");
    for (int v = 0; v < VECTOR_COUNT; v++) {
        const vector_t *vec = &VECTORS[v];

        // Decode from base64 fixture value.
        size_t blen;
        uint8_t *bitset = b64_decode(vec->have_bitset_base64, &blen);

        int out_count = 0;
        int *indices = aether_bitset_decode(bitset, blen, vec->chunk_count, &out_count);
        free(bitset);

        // Compare sorted results against expected.
        assert(out_count == vec->index_count);
        // aether_bitset_decode returns indices in ascending order already.
        for (int k = 0; k < vec->index_count; k++)
            assert(indices[k] == vec->have_indices[k]);

        free(indices);
    }
    printf(" OK\n");
}

// ── Test: JSON serialization matches expected ─────────────────────────────────

static void test_json_serialization_matches_expected(void)
{
    printf("TEST: JSON serialization matches expected...");
    for (int v = 0; v < VECTOR_COUNT; v++) {
        const vector_t *vec = &VECTORS[v];

        aether_bitset_t bs = aether_bitset_encode(
            vec->chunk_count, vec->have_indices, vec->index_count);

        char *json = aether_chunk_bitmap_marshal_json(
            vec->root_hash, vec->chunk_count, bs.bytes, bs.len, vec->generation);

        assert(json != NULL);
        if (strcmp(json, vec->expected_json) != 0) {
            printf("\n  FAIL [%s]\n  got:      %s\n  expected: %s\n",
                   vec->name, json, vec->expected_json);
            assert(0 && "JSON mismatch");
        }

        free(json);
        aether_bitset_free(bs);
    }
    printf(" OK\n");
}

// ── Test: bitset length is ceil(chunk_count / 8) ─────────────────────────────

static void test_bitset_length_is_ceil_div_8(void)
{
    printf("TEST: bitset length is ceil(chunk_count/8)...");
    for (int v = 0; v < VECTOR_COUNT; v++) {
        const vector_t *vec = &VECTORS[v];
        aether_bitset_t bs = aether_bitset_encode(
            vec->chunk_count, vec->have_indices, vec->index_count);
        size_t expected = (size_t)((vec->chunk_count + 7) / 8);
        assert(bs.len == expected);
        aether_bitset_free(bs);
    }
    printf(" OK\n");
}

// ── Test: trailing bits are zero ──────────────────────────────────────────────

static void test_trailing_bits_are_zero(void)
{
    printf("TEST: trailing bits are zero...");
    for (int v = 0; v < VECTOR_COUNT; v++) {
        const vector_t *vec = &VECTORS[v];
        if (vec->chunk_count == 0) continue;

        aether_bitset_t bs = aether_bitset_encode(
            vec->chunk_count, vec->have_indices, vec->index_count);

        int trailing = vec->chunk_count % 8; // bits used in the last byte (0 = full)
        if (trailing != 0) {
            uint8_t last      = bs.bytes[bs.len - 1];
            uint8_t valid_mask = (uint8_t)((1u << trailing) - 1u);
            uint8_t bad        = last & (uint8_t)(~valid_mask);
            if (bad != 0) {
                printf("\n  FAIL [%s] last byte=0x%02x, trailing=%d, bad bits=0x%02x\n",
                       vec->name, (unsigned)last, trailing, (unsigned)bad);
                assert(0 && "trailing bits not zero");
            }
        }

        aether_bitset_free(bs);
    }
    printf(" OK\n");
}

// ── main ─────────────────────────────────────────────────────────────────────

int main(void)
{
    printf("=== ChunkBitmap tests ===\n");
    test_encode_produces_correct_bitset();
    test_decode_recovers_correct_indices();
    test_json_serialization_matches_expected();
    test_bitset_length_is_ceil_div_8();
    test_trailing_bits_are_zero();
    printf("All ChunkBitmap tests passed.\n");
    return 0;
}
