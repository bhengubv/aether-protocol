// SPDX-License-Identifier: MIT
// Unit tests for rlnc.c — GF(2⁸) arithmetic, encoder, decoder, codec.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>

#include "aether/rlnc.h"
#include "aether/transport.h"

// ── Helpers ───────────────────────────────────────────────────────────────────

/** Allocate a k×symbol_size source symbol array filled with predictable bytes. */
static uint8_t **make_source(int k, int sym_size) {
    uint8_t **src = (uint8_t **)malloc((size_t)k * sizeof(uint8_t *));
    assert(src != NULL);
    for (int i = 0; i < k; i++) {
        src[i] = (uint8_t *)malloc((size_t)sym_size);
        assert(src[i] != NULL);
        for (int j = 0; j < sym_size; j++) {
            src[i][j] = (uint8_t)((i * sym_size + j) & 0xFF);
        }
    }
    return src;
}

static void free_source(uint8_t **src, int k) {
    for (int i = 0; i < k; i++) free(src[i]);
    free(src);
}

// ── GF(2⁸) arithmetic ────────────────────────────────────────────────────────

static void test_gf256_add_is_xor(void) {
    aether_gf256_init();
    assert(aether_gf256_add(0xAB, 0xCD) == (uint8_t)(0xAB ^ 0xCD));
    assert(aether_gf256_add(0x00, 0xFF) == 0xFF);
    assert(aether_gf256_add(0xFF, 0xFF) == 0x00);
    printf("  PASS test_gf256_add_is_xor\n");
}

static void test_gf256_mul_by_zero(void) {
    aether_gf256_init();
    for (int a = 0; a < 256; a++) {
        assert(aether_gf256_mul((uint8_t)a, 0) == 0);
        assert(aether_gf256_mul(0, (uint8_t)a) == 0);
    }
    printf("  PASS test_gf256_mul_by_zero\n");
}

static void test_gf256_mul_by_one(void) {
    aether_gf256_init();
    for (int a = 0; a < 256; a++) {
        assert(aether_gf256_mul((uint8_t)a, 1) == (uint8_t)a);
        assert(aether_gf256_mul(1, (uint8_t)a) == (uint8_t)a);
    }
    printf("  PASS test_gf256_mul_by_one\n");
}

static void test_gf256_mul_inv_round_trip(void) {
    aether_gf256_init();
    for (int a = 1; a < 256; a++) {
        uint8_t inv = aether_gf256_inv((uint8_t)a);
        uint8_t one = aether_gf256_mul((uint8_t)a, inv);
        assert(one == 1);
    }
    printf("  PASS test_gf256_mul_inv_round_trip\n");
}

static void test_gf256_mul_commutativity(void) {
    aether_gf256_init();
    for (int a = 0; a < 256; a++) {
        for (int b = 0; b < 256; b++) {
            assert(aether_gf256_mul((uint8_t)a, (uint8_t)b) ==
                   aether_gf256_mul((uint8_t)b, (uint8_t)a));
        }
    }
    printf("  PASS test_gf256_mul_commutativity\n");
}

static void test_gf256_mul_distributivity(void) {
    // a*(b+c) == a*b + a*c
    aether_gf256_init();
    uint8_t a = 0x53, b = 0xCA, c = 0x71;
    uint8_t lhs = aether_gf256_mul(a, aether_gf256_add(b, c));
    uint8_t rhs = aether_gf256_add(aether_gf256_mul(a, b), aether_gf256_mul(a, c));
    assert(lhs == rhs);
    printf("  PASS test_gf256_mul_distributivity\n");
}

// ── RlncEncoder ───────────────────────────────────────────────────────────────

static void test_encoder_systematic_first_k_packets(void) {
    int k = 4, sym = 8;
    uint8_t **src = make_source(k, sym);
    aether_rlnc_encoder_t *enc = aether_rlnc_encoder_new(
        (const uint8_t * const *)src, k, sym, true);
    assert(enc != NULL);

    uint8_t *coeff = (uint8_t *)malloc((size_t)k);
    uint8_t *data  = (uint8_t *)malloc((size_t)sym);
    for (int i = 0; i < k; i++) {
        aether_rlnc_encoder_next_packet(enc, coeff, data);
        // Coefficient vector must be e_i.
        assert(coeff[i] == 1);
        for (int j = 0; j < k; j++) {
            if (j != i) assert(coeff[j] == 0);
        }
        // Data must equal source[i].
        assert(memcmp(data, src[i], (size_t)sym) == 0);
    }
    free(coeff);
    free(data);
    aether_rlnc_encoder_free(enc);
    free_source(src, k);
    printf("  PASS test_encoder_systematic_first_k_packets\n");
}

static void test_encoder_repair_packets_not_all_zero(void) {
    int k = 3, sym = 4;
    uint8_t **src = make_source(k, sym);
    // Non-systematic encoder — every packet is a repair packet.
    aether_rlnc_encoder_t *enc = aether_rlnc_encoder_new(
        (const uint8_t * const *)src, k, sym, false);
    assert(enc != NULL);

    uint8_t *coeff = (uint8_t *)malloc((size_t)k);
    uint8_t *data  = (uint8_t *)malloc((size_t)sym);
    for (int i = 0; i < 20; i++) {
        aether_rlnc_encoder_next_packet(enc, coeff, data);
        bool any_nonzero = false;
        for (int j = 0; j < k; j++) {
            if (coeff[j] != 0) { any_nonzero = true; break; }
        }
        assert(any_nonzero && "repair packet has all-zero coefficient vector");
    }
    free(coeff);
    free(data);
    aether_rlnc_encoder_free(enc);
    free_source(src, k);
    printf("  PASS test_encoder_repair_packets_not_all_zero\n");
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

static void test_decoder_round_trip_k4(void) {
    int k = 4, sym = 8;
    uint8_t **src = make_source(k, sym);
    aether_rlnc_encoder_t *enc = aether_rlnc_encoder_new(
        (const uint8_t * const *)src, k, sym, true);
    aether_rlnc_decoder_t *dec = aether_rlnc_decoder_new(k, sym);
    assert(enc && dec);

    uint8_t *coeff = (uint8_t *)malloc((size_t)k);
    uint8_t *data  = (uint8_t *)malloc((size_t)sym);
    while (!aether_rlnc_decoder_is_complete(dec)) {
        aether_rlnc_encoder_next_packet(enc, coeff, data);
        aether_rlnc_decoder_add_packet(dec, coeff, data);
    }
    free(coeff);
    free(data);

    size_t decoded_len = 0;
    uint8_t *decoded = aether_rlnc_decoder_try_decode(dec, &decoded_len);
    assert(decoded != NULL);
    assert(decoded_len == k * sym);

    for (int i = 0; i < k; i++) {
        assert(memcmp(decoded + i * sym, src[i], (size_t)sym) == 0);
    }
    free(decoded);
    aether_rlnc_decoder_free(dec);
    aether_rlnc_encoder_free(enc);
    free_source(src, k);
    printf("  PASS test_decoder_round_trip_k4\n");
}

static void test_decoder_exactly_k_systematic_complete(void) {
    int k = 3, sym = 4;
    uint8_t **src = make_source(k, sym);
    aether_rlnc_encoder_t *enc = aether_rlnc_encoder_new(
        (const uint8_t * const *)src, k, sym, true);
    aether_rlnc_decoder_t *dec = aether_rlnc_decoder_new(k, sym);

    uint8_t *coeff = (uint8_t *)malloc((size_t)k);
    uint8_t *data  = (uint8_t *)malloc((size_t)sym);
    for (int i = 0; i < k; i++) {
        aether_rlnc_encoder_next_packet(enc, coeff, data);
        aether_rlnc_decoder_add_packet(dec, coeff, data);
    }
    free(coeff);
    free(data);

    assert(aether_rlnc_decoder_is_complete(dec));
    assert(aether_rlnc_decoder_rank(dec) == k);

    aether_rlnc_decoder_free(dec);
    aether_rlnc_encoder_free(enc);
    free_source(src, k);
    printf("  PASS test_decoder_exactly_k_systematic_complete\n");
}

static void test_decoder_linearly_dependent_packet_ignored(void) {
    int k = 2, sym = 4;
    uint8_t **src = make_source(k, sym);
    aether_rlnc_encoder_t *enc = aether_rlnc_encoder_new(
        (const uint8_t * const *)src, k, sym, true);
    aether_rlnc_decoder_t *dec = aether_rlnc_decoder_new(k, sym);

    uint8_t coeff0[2], data0[4];
    aether_rlnc_encoder_next_packet(enc, coeff0, data0);
    bool added1 = aether_rlnc_decoder_add_packet(dec, coeff0, data0);
    int rank_before = aether_rlnc_decoder_rank(dec);
    bool added2 = aether_rlnc_decoder_add_packet(dec, coeff0, data0); // duplicate
    (void)added1; (void)added2;
    assert(aether_rlnc_decoder_rank(dec) == rank_before &&
           "duplicate packet should not increase rank");

    aether_rlnc_decoder_free(dec);
    aether_rlnc_encoder_free(enc);
    free_source(src, k);
    printf("  PASS test_decoder_linearly_dependent_packet_ignored\n");
}

static void test_decoder_is_complete_at_rank_k(void) {
    int k = 2, sym = 3;
    uint8_t **src = make_source(k, sym);
    aether_rlnc_encoder_t *enc = aether_rlnc_encoder_new(
        (const uint8_t * const *)src, k, sym, true);
    aether_rlnc_decoder_t *dec = aether_rlnc_decoder_new(k, sym);

    uint8_t *c = (uint8_t *)malloc((size_t)k);
    uint8_t *d = (uint8_t *)malloc((size_t)sym);

    assert(!aether_rlnc_decoder_is_complete(dec));
    aether_rlnc_encoder_next_packet(enc, c, d); aether_rlnc_decoder_add_packet(dec, c, d);
    assert(!aether_rlnc_decoder_is_complete(dec));
    aether_rlnc_encoder_next_packet(enc, c, d); aether_rlnc_decoder_add_packet(dec, c, d);
    assert(aether_rlnc_decoder_is_complete(dec));

    free(c); free(d);
    aether_rlnc_decoder_free(dec);
    aether_rlnc_encoder_free(enc);
    free_source(src, k);
    printf("  PASS test_decoder_is_complete_at_rank_k\n");
}

static void test_decoder_repair_only_round_trip(void) {
    int k = 4, sym = 8;
    uint8_t **src = make_source(k, sym);
    // Non-systematic encoder — all repair packets.
    aether_rlnc_encoder_t *enc = aether_rlnc_encoder_new(
        (const uint8_t * const *)src, k, sym, false);
    aether_rlnc_decoder_t *dec = aether_rlnc_decoder_new(k, sym);

    uint8_t *c = (uint8_t *)malloc((size_t)k);
    uint8_t *d = (uint8_t *)malloc((size_t)sym);
    int attempts = 0;
    while (!aether_rlnc_decoder_is_complete(dec)) {
        aether_rlnc_encoder_next_packet(enc, c, d);
        aether_rlnc_decoder_add_packet(dec, c, d);
        assert(++attempts < 200 && "repair-only decoder stalled");
    }
    free(c); free(d);

    size_t decoded_len = 0;
    uint8_t *decoded = aether_rlnc_decoder_try_decode(dec, &decoded_len);
    assert(decoded != NULL);
    assert(decoded_len == k * sym);
    for (int i = 0; i < k; i++) {
        assert(memcmp(decoded + i * sym, src[i], (size_t)sym) == 0);
    }
    free(decoded);
    aether_rlnc_decoder_free(dec);
    aether_rlnc_encoder_free(enc);
    free_source(src, k);
    printf("  PASS test_decoder_repair_only_round_trip\n");
}

// ── RlncCodec (vtable) ────────────────────────────────────────────────────────

static void test_codec_k1_single_symbol(void) {
    aether_rlnc_codec_t *codec = aether_rlnc_codec_new(1);
    assert(codec != NULL);
    aether_fec_codec_t *base = (aether_fec_codec_t *)codec;

    const uint8_t source[] = {0xDE, 0xAD, 0xBE, 0xEF};
    size_t source_len = sizeof(source);
    int target_count = 2;
    size_t encoded_len = 0;

    uint8_t *encoded = base->encode(base, source, source_len, target_count, &encoded_len);
    assert(encoded != NULL);

    // Build packet-pointer and symbol-length arrays.
    size_t pkt_size = encoded_len / (size_t)target_count;
    const uint8_t **pkts    = (const uint8_t **)malloc((size_t)target_count * sizeof(uint8_t *));
    size_t         *lengths = (size_t *)malloc((size_t)target_count * sizeof(size_t));
    for (int i = 0; i < target_count; i++) {
        pkts[i]    = encoded + i * pkt_size;
        lengths[i] = pkt_size;
    }

    size_t decoded_len = 0;
    uint8_t *decoded = base->try_decode(base, pkts, lengths, target_count, 1, &decoded_len);
    assert(decoded != NULL);
    assert(decoded_len >= source_len);
    assert(memcmp(decoded, source, source_len) == 0);

    free(decoded);
    free(lengths);
    free(pkts);
    free(encoded);
    aether_rlnc_codec_free(codec);
    printf("  PASS test_codec_k1_single_symbol\n");
}

static void test_codec_large_payload_round_trip(void) {
    int k = 16;
    aether_rlnc_codec_t *codec = aether_rlnc_codec_new(k);
    assert(codec != NULL);
    aether_fec_codec_t *base = (aether_fec_codec_t *)codec;

    // 1024-byte source.
    size_t source_len = 1024;
    uint8_t *source = (uint8_t *)malloc(source_len);
    for (size_t i = 0; i < source_len; i++) source[i] = (uint8_t)(i & 0xFF);

    int    target_count = 20; // 16 systematic + 4 repair
    size_t encoded_len  = 0;
    uint8_t *encoded = base->encode(base, source, source_len, target_count, &encoded_len);
    assert(encoded != NULL);

    size_t          pkt_size = encoded_len / (size_t)target_count;
    const uint8_t **pkts    = (const uint8_t **)malloc((size_t)target_count * sizeof(uint8_t *));
    size_t         *lengths = (size_t *)malloc((size_t)target_count * sizeof(size_t));
    for (int i = 0; i < target_count; i++) {
        pkts[i]    = encoded + i * pkt_size;
        lengths[i] = pkt_size;
    }

    size_t decoded_len = 0;
    uint8_t *decoded = base->try_decode(base, pkts, lengths, target_count, k, &decoded_len);
    assert(decoded != NULL);
    assert(decoded_len >= source_len);
    assert(memcmp(decoded, source, source_len) == 0);

    free(decoded);
    free(lengths);
    free(pkts);
    free(encoded);
    free(source);
    aether_rlnc_codec_free(codec);
    printf("  PASS test_codec_large_payload_round_trip\n");
}

// ── main ──────────────────────────────────────────────────────────────────────

int main(void) {
    printf("=== RLNC Tests ===\n");

    // GF(256) arithmetic
    test_gf256_add_is_xor();
    test_gf256_mul_by_zero();
    test_gf256_mul_by_one();
    test_gf256_mul_inv_round_trip();
    test_gf256_mul_commutativity();
    test_gf256_mul_distributivity();

    // Encoder
    test_encoder_systematic_first_k_packets();
    test_encoder_repair_packets_not_all_zero();

    // Decoder
    test_decoder_round_trip_k4();
    test_decoder_exactly_k_systematic_complete();
    test_decoder_linearly_dependent_packet_ignored();
    test_decoder_is_complete_at_rank_k();
    test_decoder_repair_only_round_trip();

    // Codec vtable
    test_codec_k1_single_symbol();
    test_codec_large_payload_round_trip();

    printf("=== All RLNC tests passed ===\n");
    return 0;
}
