// SPDX-License-Identifier: MIT
// RLNC Engine — Random Linear Network Coding over GF(2⁸).
//
// Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1 (0x11D — same as AES Rijndael).
//
// Components
// ──────────
//   aethernet_gf256_*          — GF(2⁸) log/exp tables and arithmetic helpers.
//   aethernet_rlnc_encoder_t   — systematic + random-repair packet generation.
//   aethernet_rlnc_decoder_t   — incremental Gauss-Jordan elimination.
//   aethernet_rlnc_codec_t     — aethernet_fec_codec_t vtable adapter.
//
// Wire format per packet:
//   [ K coefficient bytes ][ symbolSize data bytes ]
//
// Thread safety:
//   aethernet_gf256_init() is idempotent and thread-safe via a spin-lock once-flag.
//   Encoder and decoder instances are NOT thread-safe; external synchronisation
//   is required when sharing an instance across threads.
//   aethernet_rlnc_codec_t (encode/try_decode) IS thread-safe: the codec itself
//   is stateless; all mutable state lives in per-call encoder/decoder locals.

#ifndef AETHERNET_RLNC_H
#define AETHERNET_RLNC_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#include "transport.h"  /* aethernet_fec_codec_t */

#ifdef __cplusplus
extern "C" {
#endif

// ── GF(2⁸) arithmetic ─────────────────────────────────────────────────────────

/**
 * Initialise the GF(2⁸) log/exp lookup tables.
 *
 * Idempotent and thread-safe (spin-lock once-flag).  Called automatically by
 * aethernet_rlnc_encoder_new(), aethernet_rlnc_decoder_new(), and
 * aethernet_rlnc_codec_new().  Only call explicitly if you need the arithmetic
 * functions (gf256_mul / gf256_inv) before creating an encoder/decoder.
 */
void aethernet_gf256_init(void);

/**
 * Multiply two GF(2⁸) elements.
 *
 * @pre aethernet_gf256_init() must have been called.
 */
uint8_t aethernet_gf256_mul(uint8_t a, uint8_t b);

/**
 * Multiplicative inverse in GF(2⁸): Inv(a) = α^(255 − log_α(a)).
 *
 * @pre a != 0; aethernet_gf256_init() must have been called.
 * @pre a must not be 0; behaviour is undefined for a == 0.
 */
uint8_t aethernet_gf256_inv(uint8_t a);

/**
 * Addition in GF(2⁸) = XOR.  Implemented as a static inline for performance.
 */
static inline uint8_t aethernet_gf256_add(uint8_t a, uint8_t b) { return (uint8_t)(a ^ b); }

// ── RlncEncoder ───────────────────────────────────────────────────────────────

/**
 * Encodes K source symbols as systematic + random-repair RLNC packets.
 *
 * The first K packets are systematic (identity coefficient vectors;
 * byte-identical to the source symbols).  Subsequent packets use random
 * GF(2⁸) coefficients from the OS CSPRNG.
 *
 * Each call to aethernet_rlnc_encoder_next_packet() advances the internal
 * counter by one.
 *
 * Ownership: caller owns the struct; free with aethernet_rlnc_encoder_free().
 * The encoder makes its own internal copies of the source symbols.
 */
typedef struct {
    int       k;            /**< Generation size — number of source symbols. */
    int       symbol_size;  /**< Byte length of each symbol.                 */
    int       next_index;   /**< Index of next packet to generate.           */
    bool      systematic;   /**< true → first K packets are systematic.      */
    uint8_t **source;       /**< k heap-allocated symbol_size-byte arrays.   */
} aethernet_rlnc_encoder_t;

/**
 * Allocate and initialise an RLNC encoder.
 *
 * @param source      Array of [k] pointers to [symbol_size]-byte source
 *                    symbols.  The encoder copies the symbol data internally.
 * @param k           Generation size (number of source symbols).  Must be
 *                    in [1, 255].
 * @param symbol_size Byte length of each source symbol.
 * @param systematic  When true, the first K packets carry identity
 *                    coefficients and are byte-identical to the source.
 * @return            Allocated encoder, or NULL on allocation failure.
 */
aethernet_rlnc_encoder_t *aethernet_rlnc_encoder_new(
    const uint8_t * const *source,
    int                    k,
    int                    symbol_size,
    bool                   systematic);

/**
 * Produce the next encoded packet.
 *
 * Writes [k] coefficient bytes to out_coeff and [symbol_size] encoded bytes
 * to out_data.  Both buffers must be allocated by the caller with sufficient
 * capacity.
 *
 * @param enc       Encoder (non-NULL).
 * @param out_coeff Caller-allocated buffer of at least [k] bytes.
 * @param out_data  Caller-allocated buffer of at least [symbol_size] bytes.
 */
void aethernet_rlnc_encoder_next_packet(
    aethernet_rlnc_encoder_t *enc,
    uint8_t               *out_coeff,
    uint8_t               *out_data);

/**
 * Free all resources owned by the encoder.
 *
 * @param enc  Encoder to free (may be NULL — no-op).
 */
void aethernet_rlnc_encoder_free(aethernet_rlnc_encoder_t *enc);

// ── RlncDecoder ───────────────────────────────────────────────────────────────

/**
 * Incremental Gauss-Jordan decoder over GF(2⁸).
 *
 * Maintains the accumulated coefficient matrix in Reduced Row Echelon Form
 * (RREF) as packets arrive.  Decoding is immediate when rank == k.
 *
 * Ownership: caller owns the struct; free with aethernet_rlnc_decoder_free().
 */
typedef struct {
    int       k;             /**< Generation size K.                         */
    int       symbol_size;   /**< Byte length of each symbol.                */
    int       rank;          /**< Number of linearly independent packets.    */
    uint8_t **pivot_coeff;   /**< k nullable pointers to k-byte coefficient rows. */
    uint8_t **pivot_data;    /**< k nullable pointers to symbol_size-byte data rows. */
} aethernet_rlnc_decoder_t;

/**
 * Allocate and initialise an RLNC decoder.
 *
 * @param k           Generation size K.  Must be in [1, 255].
 * @param symbol_size Byte length of each encoded symbol.
 * @return            Allocated decoder, or NULL on allocation failure.
 */
aethernet_rlnc_decoder_t *aethernet_rlnc_decoder_new(int k, int symbol_size);

/**
 * Submit an encoded packet and advance the RREF state.
 *
 * @param dec   Decoder (non-NULL).
 * @param coeff K-byte GF(2⁸) coefficient vector.
 * @param data  [symbol_size]-byte encoded data.
 * @return      true if rank increased (packet was linearly independent).
 */
bool aethernet_rlnc_decoder_add_packet(
    aethernet_rlnc_decoder_t *dec,
    const uint8_t         *coeff,
    const uint8_t         *data);

/**
 * Number of linearly independent packets received.
 */
int aethernet_rlnc_decoder_rank(const aethernet_rlnc_decoder_t *dec);

/**
 * true when all K source symbols can be reconstructed.
 */
bool aethernet_rlnc_decoder_is_complete(const aethernet_rlnc_decoder_t *dec);

/**
 * Reconstruct the decoded source when is_complete.
 *
 * @param dec     Decoder (non-NULL, must be complete).
 * @param out_len Populated with the size of the returned buffer (k * symbol_size).
 * @return        Heap-allocated decoded bytes, or NULL if not complete or OOM.
 *                Caller must free().
 */
uint8_t *aethernet_rlnc_decoder_try_decode(
    const aethernet_rlnc_decoder_t *dec,
    size_t                      *out_len);

/**
 * Free all resources owned by the decoder.
 *
 * @param dec  Decoder to free (may be NULL — no-op).
 */
void aethernet_rlnc_decoder_free(aethernet_rlnc_decoder_t *dec);

// ── RlncCodec : aethernet_fec_codec_t ───────────────────────────────────────────

/**
 * RLNC FEC codec struct.
 *
 * The embedded aethernet_fec_codec_t is the first member so aethernet_rlnc_codec_t *
 * can be freely cast to aethernet_fec_codec_t * and passed to the generic
 * transport layer.
 *
 * Each encoded packet = [ K coefficient bytes ][ symbolSize data bytes ].
 */
typedef struct {
    aethernet_fec_codec_t base;  /**< Must be first — vtable + metadata.      */
    int                k;     /**< Generation size K.  Range: [1, 255].    */
} aethernet_rlnc_codec_t;

/**
 * Allocate and initialise an RLNC codec.
 *
 * @param generation_size  K — source symbols per generation.  Range: [1, 255].
 * @return                 Allocated codec, or NULL if generation_size is out
 *                         of range or on allocation failure.
 */
aethernet_rlnc_codec_t *aethernet_rlnc_codec_new(int generation_size);

/**
 * Free the codec.
 *
 * @param codec  Codec to free (may be NULL — no-op).
 */
void aethernet_rlnc_codec_free(aethernet_rlnc_codec_t *codec);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_RLNC_H */
