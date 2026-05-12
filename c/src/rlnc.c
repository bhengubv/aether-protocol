// SPDX-License-Identifier: MIT
// RLNC Engine — Random Linear Network Coding over GF(2⁸).
//
// Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1 (0x11D — same as AES Rijndael).
//
// See: c/include/aether/rlnc.h for the public API.

/* _GNU_SOURCE exposes syscall() prototype on Linux (required with -std=c11). */
#if defined(__linux__) && !defined(_GNU_SOURCE)
#  define _GNU_SOURCE
#endif

#include <math.h>
#include <string.h>
#include <stdlib.h>
#include <stddef.h>
#include <stdio.h>    /* FILE, fopen, fread, fclose — /dev/urandom fallback */

/* Platform-specific CSPRNG */
#if defined(__linux__)
#  include <sys/syscall.h>
#  include <unistd.h>
   /* Use the getrandom(2) syscall directly via syscall() so we don't require
      glibc ≥ 2.25.  SYS_getrandom is available from Linux ≥ 3.17.        */
#  define _RLNC_HAVE_GETRANDOM 1
#elif defined(__APPLE__) || defined(__FreeBSD__) || defined(__OpenBSD__) || defined(__NetBSD__)
#  include <stdlib.h>   /* arc4random_buf */
#  define _RLNC_HAVE_ARC4RANDOM 1
#endif

#include "aether/rlnc.h"

// ── GF(2⁸) table storage ─────────────────────────────────────────────────────

/* 512-entry exp table (doubled to eliminate modular wrap in gf256_mul).
   256-entry log table.  Both are zero-initialised at program start;
   filled by aether_gf256_init(). */
static uint8_t _gf256_exp[512];
static uint8_t _gf256_log[256];

/* One-time-init spin-lock (same pattern as transport_metrics.c). */
static volatile int _gf256_lock  = 0;
static volatile int _gf256_ready = 0;

// ── Spin-lock helpers ─────────────────────────────────────────────────────────

static inline void _lock(volatile int *spin)
{
    while (__sync_lock_test_and_set(spin, 1)) { /* busy-wait */ }
}

static inline void _unlock(volatile int *spin)
{
    __sync_lock_release(spin);
}

// ── aether_gf256_init ─────────────────────────────────────────────────────────

void aether_gf256_init(void)
{
    if (_gf256_ready) return;   /* fast path: already done */

    _lock(&_gf256_lock);
    if (!_gf256_ready) {
        int x = 1;
        for (int i = 0; i < 255; i++) {
            _gf256_exp[i] = (uint8_t)x;
            _gf256_log[x] = (uint8_t)i;
            x <<= 1;
            if (x & 0x100) x ^= 0x11D;  /* reduce mod p(x) */
            x &= 0xFF;
        }
        for (int i = 255; i < 512; i++) {
            _gf256_exp[i] = _gf256_exp[i - 255];
        }
        _gf256_log[1] = 0;  /* log_α(1) = 0 */

        /* Release-store: ensure table writes visible before flag. */
        __sync_synchronize();
        _gf256_ready = 1;
    }
    _unlock(&_gf256_lock);
}

// ── aether_gf256_mul / inv ────────────────────────────────────────────────────

uint8_t aether_gf256_mul(uint8_t a, uint8_t b)
{
    if (a == 0 || b == 0) return 0;
    return _gf256_exp[(int)_gf256_log[a] + (int)_gf256_log[b]];
}

uint8_t aether_gf256_inv(uint8_t a)
{
    /* a == 0 is undefined — assert in debug builds only. */
    return _gf256_exp[255 - (int)_gf256_log[a]];
}

// ── CSPRNG helper ─────────────────────────────────────────────────────────────

static void _random_bytes(uint8_t *buf, size_t len)
{
#if defined(_RLNC_HAVE_GETRANDOM)
    /* getrandom(2) via raw syscall — no dependency on glibc version. */
    ssize_t n = syscall(SYS_getrandom, buf, len, 0);
    if (n == (ssize_t)len) return;
    /* Fall through to /dev/urandom on kernel < 3.17. */
#elif defined(_RLNC_HAVE_ARC4RANDOM)
    arc4random_buf(buf, len);
    return;
#endif
    /* Portable fallback: read /dev/urandom. */
    FILE *f = fopen("/dev/urandom", "rb");
    if (f) {
        size_t n = fread(buf, 1, len, f);
        fclose(f);
        if (n == len) return;
    }
    /* Last resort: fill with position-dependent non-zero bytes so we always
       produce a valid (non-zero) coefficient vector.  This is insecure and
       should never happen on a real target, but prevents a silent all-zeros
       coefficient vector which would corrupt encoding. */
    for (size_t i = 0; i < len; i++) {
        buf[i] = (uint8_t)((i + 1) & 0xFF);
    }
}

// ── _encode_symbol (inner loop) ───────────────────────────────────────────────

/* Compute: out[i] = XOR over j of GF_Mul(coeff[j], source[j][i]) */
static void _encode_symbol(
    const uint8_t * const *source,
    const uint8_t         *coeff,
    int                    k,
    int                    symbol_size,
    uint8_t               *out)
{
    memset(out, 0, (size_t)symbol_size);
    for (int j = 0; j < k; j++) {
        uint8_t c = coeff[j];
        if (c == 0) continue;
        const uint8_t *sym = source[j];
        for (int i = 0; i < symbol_size; i++) {
            out[i] = aether_gf256_add(out[i], aether_gf256_mul(c, sym[i]));
        }
    }
}

// ── aether_rlnc_encoder_new ───────────────────────────────────────────────────

aether_rlnc_encoder_t *aether_rlnc_encoder_new(
    const uint8_t * const *source,
    int                    k,
    int                    symbol_size,
    bool                   systematic)
{
    if (!source || k < 1 || k > 255 || symbol_size < 1) return NULL;
    aether_gf256_init();

    aether_rlnc_encoder_t *enc =
        (aether_rlnc_encoder_t *)calloc(1, sizeof(*enc));
    if (!enc) return NULL;

    enc->k           = k;
    enc->symbol_size = symbol_size;
    enc->next_index  = 0;
    enc->systematic  = systematic;

    /* Allocate pointer array. */
    enc->source = (uint8_t **)calloc((size_t)k, sizeof(uint8_t *));
    if (!enc->source) { free(enc); return NULL; }

    /* Deep-copy each source symbol. */
    for (int i = 0; i < k; i++) {
        enc->source[i] = (uint8_t *)malloc((size_t)symbol_size);
        if (!enc->source[i]) {
            /* Free already-allocated symbols. */
            for (int j = 0; j < i; j++) free(enc->source[j]);
            free(enc->source);
            free(enc);
            return NULL;
        }
        memcpy(enc->source[i], source[i], (size_t)symbol_size);
    }

    return enc;
}

// ── aether_rlnc_encoder_next_packet ──────────────────────────────────────────

void aether_rlnc_encoder_next_packet(
    aether_rlnc_encoder_t *enc,
    uint8_t               *out_coeff,
    uint8_t               *out_data)
{
    if (!enc || !out_coeff || !out_data) return;

    int k = enc->k;
    int s = enc->symbol_size;

    if (enc->systematic && enc->next_index < k) {
        /* Systematic: e_i identity coefficient vector. */
        memset(out_coeff, 0, (size_t)k);
        out_coeff[enc->next_index] = 1;
        memcpy(out_data, enc->source[enc->next_index], (size_t)s);
    } else {
        /* Repair: random GF(2⁸) coefficient vector. */
        _random_bytes(out_coeff, (size_t)k);
        /* Guard: ensure the vector is not all-zeros. */
        int any_nonzero = 0;
        for (int i = 0; i < k; i++) {
            if (out_coeff[i] != 0) { any_nonzero = 1; break; }
        }
        if (!any_nonzero) out_coeff[0] = 1;

        _encode_symbol(
            (const uint8_t * const *)enc->source,
            out_coeff, k, s, out_data);
    }

    enc->next_index++;
}

// ── aether_rlnc_encoder_free ──────────────────────────────────────────────────

void aether_rlnc_encoder_free(aether_rlnc_encoder_t *enc)
{
    if (!enc) return;
    if (enc->source) {
        for (int i = 0; i < enc->k; i++) free(enc->source[i]);
        free(enc->source);
    }
    free(enc);
}

// ── aether_rlnc_decoder_new ───────────────────────────────────────────────────

aether_rlnc_decoder_t *aether_rlnc_decoder_new(int k, int symbol_size)
{
    if (k < 1 || k > 255 || symbol_size < 1) return NULL;
    aether_gf256_init();

    aether_rlnc_decoder_t *dec =
        (aether_rlnc_decoder_t *)calloc(1, sizeof(*dec));
    if (!dec) return NULL;

    dec->k           = k;
    dec->symbol_size = symbol_size;
    dec->rank        = 0;

    /* calloc zeros all pointers → pivot_coeff[i] = NULL means "no row yet". */
    dec->pivot_coeff = (uint8_t **)calloc((size_t)k, sizeof(uint8_t *));
    dec->pivot_data  = (uint8_t **)calloc((size_t)k, sizeof(uint8_t *));

    if (!dec->pivot_coeff || !dec->pivot_data) {
        free(dec->pivot_coeff);
        free(dec->pivot_data);
        free(dec);
        return NULL;
    }

    return dec;
}

// ── aether_rlnc_decoder_add_packet ───────────────────────────────────────────

bool aether_rlnc_decoder_add_packet(
    aether_rlnc_decoder_t *dec,
    const uint8_t         *coeff,
    const uint8_t         *data)
{
    if (!dec || !coeff || !data) return false;

    int k = dec->k;
    int s = dec->symbol_size;

    /* Working copies of the incoming row. */
    uint8_t *row  = (uint8_t *)malloc((size_t)k);
    uint8_t *drow = (uint8_t *)malloc((size_t)s);
    if (!row || !drow) { free(row); free(drow); return false; }

    memcpy(row,  coeff, (size_t)k);
    memcpy(drow, data,  (size_t)s);

    /* ── Forward-elimination ──────────────────────────────────────────────── */
    for (int j = 0; j < k; j++) {
        if (row[j] == 0 || dec->pivot_coeff[j] == NULL) continue;

        uint8_t c  = row[j];
        uint8_t *pr = dec->pivot_coeff[j];
        uint8_t *pd = dec->pivot_data[j];

        for (int i = 0; i < k; i++) {
            row[i]  = aether_gf256_add(row[i],  aether_gf256_mul(c, pr[i]));
        }
        for (int i = 0; i < s; i++) {
            drow[i] = aether_gf256_add(drow[i], aether_gf256_mul(c, pd[i]));
        }
    }

    /* ── Find pivot column ────────────────────────────────────────────────── */
    int pivot_col = -1;
    for (int j = 0; j < k; j++) {
        if (row[j] != 0) { pivot_col = j; break; }
    }
    if (pivot_col < 0) {
        /* Linearly dependent — discard. */
        free(row); free(drow);
        return false;
    }

    /* ── Normalise: scale so pivot element = 1 ───────────────────────────── */
    uint8_t inv = aether_gf256_inv(row[pivot_col]);
    for (int i = 0; i < k; i++) row[i]  = aether_gf256_mul(inv, row[i]);
    for (int i = 0; i < s; i++) drow[i] = aether_gf256_mul(inv, drow[i]);

    /* ── Back-substitution: eliminate the pivot column from all other rows ── */
    for (int r = 0; r < k; r++) {
        uint8_t *pr = dec->pivot_coeff[r];
        if (!pr) continue;

        uint8_t c = pr[pivot_col];
        if (c == 0) continue;

        uint8_t *pd = dec->pivot_data[r];
        for (int i = 0; i < k; i++) {
            pr[i] = aether_gf256_add(pr[i], aether_gf256_mul(c, row[i]));
        }
        for (int i = 0; i < s; i++) {
            pd[i] = aether_gf256_add(pd[i], aether_gf256_mul(c, drow[i]));
        }
    }

    /* ── Install the new pivot row ────────────────────────────────────────── */
    dec->pivot_coeff[pivot_col] = row;
    dec->pivot_data[pivot_col]  = drow;
    dec->rank++;
    return true;
}

// ── aether_rlnc_decoder_rank / is_complete ───────────────────────────────────

int aether_rlnc_decoder_rank(const aether_rlnc_decoder_t *dec)
{
    if (!dec) return 0;
    return dec->rank;
}

bool aether_rlnc_decoder_is_complete(const aether_rlnc_decoder_t *dec)
{
    if (!dec) return false;
    return dec->rank == dec->k;
}

// ── aether_rlnc_decoder_try_decode ───────────────────────────────────────────

uint8_t *aether_rlnc_decoder_try_decode(
    const aether_rlnc_decoder_t *dec,
    size_t                      *out_len)
{
    if (!dec || !out_len) return NULL;
    if (!aether_rlnc_decoder_is_complete(dec)) return NULL;

    int k = dec->k;
    int s = dec->symbol_size;
    size_t total = (size_t)k * (size_t)s;

    uint8_t *result = (uint8_t *)malloc(total);
    if (!result) return NULL;

    /* The decoder maintains RREF, so pivot_data[j] == source symbol j. */
    for (int j = 0; j < k; j++) {
        memcpy(result + (size_t)j * (size_t)s,
               dec->pivot_data[j],
               (size_t)s);
    }

    *out_len = total;
    return result;
}

// ── aether_rlnc_decoder_free ─────────────────────────────────────────────────

void aether_rlnc_decoder_free(aether_rlnc_decoder_t *dec)
{
    if (!dec) return;
    if (dec->pivot_coeff) {
        for (int i = 0; i < dec->k; i++) free(dec->pivot_coeff[i]);
        free(dec->pivot_coeff);
    }
    if (dec->pivot_data) {
        for (int i = 0; i < dec->k; i++) free(dec->pivot_data[i]);
        free(dec->pivot_data);
    }
    free(dec);
}

// ── Helper: split source buffer into K zero-padded symbols ───────────────────

/*
 * Returns a heap-allocated array of [k] pointers, each pointing to a
 * heap-allocated [symbol_size]-byte block.  The caller must free every inner
 * block and the outer array separately.
 */
static uint8_t **_split_into_symbols(
    const uint8_t *source,
    size_t         source_len,
    int            k,
    int            symbol_size)
{
    uint8_t **syms = (uint8_t **)calloc((size_t)k, sizeof(uint8_t *));
    if (!syms) return NULL;

    for (int i = 0; i < k; i++) {
        syms[i] = (uint8_t *)calloc(1, (size_t)symbol_size); /* zero-pad */
        if (!syms[i]) {
            for (int j = 0; j < i; j++) free(syms[j]);
            free(syms);
            return NULL;
        }
        size_t offset = (size_t)i * (size_t)symbol_size;
        if (offset < source_len) {
            size_t copy_len = symbol_size;
            if (offset + copy_len > source_len) copy_len = source_len - offset;
            memcpy(syms[i], source + offset, copy_len);
        }
    }

    return syms;
}

static void _free_symbols(uint8_t **syms, int k)
{
    if (!syms) return;
    for (int i = 0; i < k; i++) free(syms[i]);
    free(syms);
}

// ── Vtable: _rlnc_encode ─────────────────────────────────────────────────────

static uint8_t *_rlnc_encode(
    const aether_fec_codec_t *codec,
    const uint8_t            *source,
    size_t                    source_len,
    int                       target_symbol_count,
    size_t                   *out_len)
{
    if (!codec || !source || source_len == 0
            || target_symbol_count <= 0 || !out_len) return NULL;

    const aether_rlnc_codec_t *self = (const aether_rlnc_codec_t *)codec;
    int    k           = self->k;
    int    symbol_size = (int)((source_len + (size_t)k - 1) / (size_t)k);
    int    packet_size = k + symbol_size;

    uint8_t **syms = _split_into_symbols(source, source_len, k, symbol_size);
    if (!syms) return NULL;

    aether_rlnc_encoder_t *enc = aether_rlnc_encoder_new(
        (const uint8_t * const *)syms, k, symbol_size, true);
    _free_symbols(syms, k);
    if (!enc) return NULL;

    size_t total = (size_t)target_symbol_count * (size_t)packet_size;
    uint8_t *output = (uint8_t *)malloc(total);
    if (!output) { aether_rlnc_encoder_free(enc); return NULL; }

    for (int i = 0; i < target_symbol_count; i++) {
        uint8_t *pkt    = output + (size_t)i * (size_t)packet_size;
        uint8_t *coeff  = pkt;
        uint8_t *data   = pkt + k;
        aether_rlnc_encoder_next_packet(enc, coeff, data);
    }

    aether_rlnc_encoder_free(enc);
    *out_len = total;
    return output;
}

// ── Vtable: _rlnc_try_decode ─────────────────────────────────────────────────

static uint8_t *_rlnc_try_decode(
    const aether_fec_codec_t *codec,
    const uint8_t           **received_symbols,
    const size_t             *symbol_lengths,
    int                       received_count,
    int                       source_symbol_count,
    size_t                   *out_len)
{
    (void)source_symbol_count; /* RLNC self-describes; not used */

    if (!codec || !received_symbols || !symbol_lengths
            || received_count <= 0 || !out_len) return NULL;

    const aether_rlnc_codec_t *self = (const aether_rlnc_codec_t *)codec;
    int k = self->k;

    /* Derive symbol_size from the first packet: packet_len = k + symbol_size. */
    if (symbol_lengths[0] <= (size_t)k) return NULL;
    int symbol_size = (int)(symbol_lengths[0] - (size_t)k);

    aether_rlnc_decoder_t *dec = aether_rlnc_decoder_new(k, symbol_size);
    if (!dec) return NULL;

    for (int i = 0; i < received_count; i++) {
        if (symbol_lengths[i] < (size_t)k + (size_t)symbol_size) continue;
        const uint8_t *pkt  = received_symbols[i];
        aether_rlnc_decoder_add_packet(dec, pkt, pkt + k);
        if (aether_rlnc_decoder_is_complete(dec)) break;
    }

    uint8_t *result = aether_rlnc_decoder_try_decode(dec, out_len);
    aether_rlnc_decoder_free(dec);
    return result;
}

// ── aether_rlnc_codec_new ────────────────────────────────────────────────────

aether_rlnc_codec_t *aether_rlnc_codec_new(int generation_size)
{
    if (generation_size < 1 || generation_size > 255) return NULL;
    aether_gf256_init();

    aether_rlnc_codec_t *c =
        (aether_rlnc_codec_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;

    c->k = generation_size;

    /* Populate the vtable fields in the embedded aether_fec_codec_t. */
    c->base.codec_name            = "RLNC-GF256";
    c->base.device_tier_required  = 0;
    c->base.overhead_fraction     = 0.05;
    c->base.fixed_symbol_size_bytes = 0;  /* variable-symbol codec */
    c->base.encode                = _rlnc_encode;
    c->base.try_decode            = _rlnc_try_decode;

    return c;
}

// ── aether_rlnc_codec_free ───────────────────────────────────────────────────

void aether_rlnc_codec_free(aether_rlnc_codec_t *codec)
{
    free(codec);
}
