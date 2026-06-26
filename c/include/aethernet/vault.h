// SPDX-License-Identifier: MIT
// aether-vault: in-memory erasure-coded distributed backup service (Phase-2
// extension). Port of the C# reference (AetherNet.Vault.InMemoryVaultService) —
// K=10 / M=4 over the systematic Cauchy-Reed-Solomon codec in reed_solomon.h, so
// a shard set produced here is decodable by any other node.

#ifndef AETHERNET_VAULT_H
#define AETHERNET_VAULT_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define AETHERNET_VAULT_K 10 /**< Default data shards.   */
#define AETHERNET_VAULT_M 4  /**< Default parity shards. */

/**
 * The only thing the owner must retain to reconstruct a vaulted file. Filled by
 * aethernet_vault_store() into a caller-provided struct whose owned heap fields
 * must be released with aethernet_vault_manifest_free().
 */
typedef struct {
    char   *content_hash;  /**< owned; SHA-256 hex (64 chars + NUL) of the plaintext. */
    char  **shard_hashes;  /**< owned; total_shards SHA-256 hex strings, in shard order. */
    int     total_shards;  /**< K + M. */
    int     k;             /**< data shards. */
    int     m;             /**< parity shards. */
    int64_t size_bytes;    /**< original plaintext size. */
    char   *label;         /**< owned; caller-supplied label. */
    int64_t created_at_ms; /**< creation time, ms since the Unix epoch. */
} aethernet_vault_manifest_t;

/// Release the owned heap fields of a manifest and zero it. NULL is a no-op.
void aethernet_vault_manifest_free(aethernet_vault_manifest_t *manifest);

/// A current reachability report for a vaulted file.
typedef struct {
    int    total_shards;     /**< K + M. */
    int    reachable_shards; /**< how many of the manifest's shards are reachable. */
    bool   is_recoverable;   /**< reachable_shards >= K. */
    double redundancy_score; /**< reachable / total in [0, 1]. */
} aethernet_vault_health_t;

/// Opaque in-memory vault service handle.
typedef struct aethernet_vault_service aethernet_vault_service_t;

aethernet_vault_service_t *aethernet_vault_service_new(void);
void aethernet_vault_service_free(aethernet_vault_service_t *service);

/**
 * Erasure-code `data` (data_len bytes) under K=10 / M=4, persist the K+M shards,
 * and fill *out_manifest (owned by the caller — free with
 * aethernet_vault_manifest_free()). data_len 0 is valid (stores K 1-byte zero
 * data shards). Returns true on success, false on bad args / OOM.
 */
bool aethernet_vault_store(aethernet_vault_service_t *service,
                           const uint8_t *data, size_t data_len,
                           const char *label,
                           aethernet_vault_manifest_t *out_manifest);

/**
 * Reconstruct the original blob from any K available shards. On success allocates
 * *out_data via malloc (size_bytes bytes — caller must free()) and writes
 * size_bytes to *out_len. Returns false when fewer than K shards remain
 * (unrecoverable) or on bad args / OOM.
 */
bool aethernet_vault_recover(aethernet_vault_service_t *service,
                             const aethernet_vault_manifest_t *manifest,
                             uint8_t **out_data, size_t *out_len);

/// Fill a reachability report for `manifest`.
void aethernet_vault_check_health(aethernet_vault_service_t *service,
                                  const aethernet_vault_manifest_t *manifest,
                                  aethernet_vault_health_t *out_health);

/**
 * Re-replicate shards toward `target_redundancy`. No-op in the in-memory
 * implementation; returns true.
 */
bool aethernet_vault_replicate(aethernet_vault_service_t *service,
                               const aethernet_vault_manifest_t *manifest,
                               int target_redundancy);

/**
 * Evict a single stored shard by its SHA-256 hex hash (a shard is lost / pruned).
 * Returns true if a shard was removed. Lets callers model shard loss that
 * aethernet_vault_check_health() / aethernet_vault_recover() then reflect.
 */
bool aethernet_vault_forget_shard(aethernet_vault_service_t *service, const char *shard_hash);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_VAULT_H
