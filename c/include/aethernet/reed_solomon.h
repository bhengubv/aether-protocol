// SPDX-License-Identifier: MIT
// AetherNet Vault — systematic Cauchy-Reed-Solomon (K, N) erasure codec over GF(2⁸).
//
// C port of AetherNet.Vault.ReedSolomonCodec, byte-identical to the C# reference
// and every other language implementation, proven against
// fixtures/vault/reed_solomon_basic.json.
//
// FIELD: arithmetic is over GF(2⁸) with primitive polynomial x⁸+x⁴+x³+x²+1 (0x11D,
// the AES/Rijndael polynomial), α = 2 — the SAME field as the RLNC engine
// (aethernet_gf256_*). This codec reuses those exported field tables, so the parity
// bytes are identical to every other node's, which is what makes a parity shard
// scattered by one node decodable by any other.
//
// SHARD LAYOUT: the K DATA shards are SYSTEMATIC — shard i (0…K-1) is exactly
//   plaintext[i*shardSize .. i*shardSize+shardSize], zero-padded if short,
//   shardSize = ceil(size/K)
// The M parity shards are real Cauchy-Reed-Solomon (MDS), so ANY K of the N shards
// reconstruct the original; K-1 or fewer is unrecoverable.

#ifndef AETHERNET_REED_SOLOMON_H
#define AETHERNET_REED_SOLOMON_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Systematic Reed-Solomon (K data + M parity) erasure codec over GF(2⁸). Allocate
 * with aethernet_reed_solomon_new(); free with aethernet_reed_solomon_free().
 *
 * parity[i] (i = 0…M-1) is the K-byte Cauchy coefficient vector for parity shard
 * K+i; together with the implicit K×K systematic identity for the data shards these
 * form the full N×K MDS generator matrix.
 */
typedef struct {
    int       k;          /**< Number of data shards (>= 1).               */
    int       m;          /**< Number of parity shards (>= 0).             */
    int       n;          /**< Total shards K + M (<= 256).                */
    uint8_t **parity;     /**< M rows of K Cauchy coefficient bytes.       */
} aethernet_reed_solomon_t;

/**
 * Create a codec with k data shards and m parity shards. k must be >= 1, m >= 0,
 * and k + m <= 256. Initialises the GF(2⁸) tables if needed.
 *
 * Returns an allocated codec, or NULL on invalid parameters / allocation failure.
 */
aethernet_reed_solomon_t *aethernet_reed_solomon_new(int k, int m);

/**
 * Free a codec previously created by aethernet_reed_solomon_new(). NULL is a no-op.
 */
void aethernet_reed_solomon_free(aethernet_reed_solomon_t *codec);

/**
 * Encode k data shards (each shard_size bytes) into the full set of N shards.
 * Shards 0…K-1 are the data shards unchanged (systematic, fresh copies); shards
 * K…N-1 are the M Reed-Solomon parity shards.
 *
 * data_shards must be an array of K non-NULL pointers, each shard_size bytes.
 * out_shards is a caller-allocated array of N uint8_t* slots; on success each slot
 * receives a freshly malloc'd shard_size-byte buffer that the caller must free().
 *
 * Returns true on success, false on bad arguments / allocation failure (in which
 * case any partially-written out_shards slots are freed and zeroed).
 */
bool aethernet_reed_solomon_encode(const aethernet_reed_solomon_t *codec,
                                   const uint8_t *const *data_shards,
                                   size_t shard_size,
                                   uint8_t **out_shards);

/**
 * Split data into K equal zero-padded systematic data shards of length
 * shardSize = ceil(len/K) and return the full set of N = K+M shards. This is the
 * file-level entry point matching the vault data layout.
 *
 * out_shards is a caller-allocated array of N uint8_t* slots; each receives a fresh
 * malloc'd shardSize-byte buffer the caller must free(). *out_shard_size receives
 * shardSize.
 *
 * Returns true on success, false on empty input / bad args / OOM.
 */
bool aethernet_reed_solomon_encode_data(const aethernet_reed_solomon_t *codec,
                                        const uint8_t *data,
                                        size_t data_len,
                                        uint8_t **out_shards,
                                        size_t *out_shard_size);

/**
 * Reconstruct the K data shards (indices 0…K-1, in order) from any K available
 * shards.
 *
 * available_indices / available_shards are parallel arrays of length
 * available_count; available_indices[j] is the shard index (0…N-1) of
 * available_shards[j]. At least K distinct valid entries are required; the K
 * lowest-indexed are used (any K suffice for an MDS code). All supplied shards must
 * be shard_size bytes.
 *
 * out_data_shards is a caller-allocated array of K uint8_t* slots; each receives a
 * fresh malloc'd shard_size-byte buffer the caller must free().
 *
 * Returns true on success, false when fewer than K shards are available
 * (unrecoverable) or on bad args / OOM.
 */
bool aethernet_reed_solomon_decode_data_shards(const aethernet_reed_solomon_t *codec,
                                               const int *available_indices,
                                               const uint8_t *const *available_shards,
                                               size_t available_count,
                                               size_t shard_size,
                                               uint8_t **out_data_shards);

/**
 * Reconstruct the original blob of original_size bytes from any K surviving shards.
 * Concatenates the K recovered data shards in index order then trims to
 * original_size.
 *
 * On success allocates *out_data via malloc (original_size bytes) — caller must
 * free() — and writes original_size to *out_len.
 *
 * Returns true on success, false when fewer than K shards are available, when
 * original_size exceeds the reconstructed length, or on bad args / OOM.
 */
bool aethernet_reed_solomon_reconstruct_data(const aethernet_reed_solomon_t *codec,
                                             const int *available_indices,
                                             const uint8_t *const *available_shards,
                                             size_t available_count,
                                             size_t shard_size,
                                             size_t original_size,
                                             uint8_t **out_data,
                                             size_t *out_len);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_REED_SOLOMON_H */
