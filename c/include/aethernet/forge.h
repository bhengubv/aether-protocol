// SPDX-License-Identifier: MIT
// aether-forge: mesh-native package cache proxy (Phase-2 extension).
//
// The first internet pull of a package is cached as Aether content; subsequent
// pulls by anyone in the mesh are served locally at mesh speeds. Port of the C#
// reference (AetherNet.Forge). Ecosystems: npm, pip, cargo, go, nuget, git.

#ifndef AETHERNET_FORGE_H
#define AETHERNET_FORGE_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Metadata record for one cached package artifact. The owned string fields are
 * managed by the service; entries returned to callers are borrowed and valid
 * until the next mutating call.
 */
typedef struct {
    char   *content_hash;  // owned
    char   *package_id;    // owned; "ecosystem:name@version", e.g. "npm:react@18.2.0"
    int64_t fetched_at_ms;
    int64_t size_bytes;
    int32_t download_count;
} aethernet_forge_entry_t;

/// Aggregate scalar statistics for the local Forge cache (top packages via
/// aethernet_forge_top_packages).
typedef struct {
    int64_t total_bytes_saved;
    int32_t total_peers_served;
    int32_t catalogue_size;
} aethernet_forge_stats_t;

/// Opaque in-memory forge service handle.
typedef struct aethernet_forge_service aethernet_forge_service_t;

aethernet_forge_service_t *aethernet_forge_service_new(void);
void aethernet_forge_service_free(aethernet_forge_service_t *service);

/// Fired when a new artifact is added via aethernet_forge_cache. The pointer is
/// valid only for the duration of the callback.
typedef void (*aethernet_forge_new_entry_cb)(const aethernet_forge_entry_t *entry, void *user_data);
void aethernet_forge_set_new_entry_callback(aethernet_forge_service_t *service, aethernet_forge_new_entry_cb cb, void *user_data);

/// Look up a cached entry by package id; returns a borrowed pointer, or NULL if not cached.
const aethernet_forge_entry_t *aethernet_forge_query(aethernet_forge_service_t *service, const char *package_id);

/// Store a new artifact (idempotent — an existing package_id is returned unchanged).
/// Returns a borrowed pointer to the stored entry.
const aethernet_forge_entry_t *aethernet_forge_cache(
    aethernet_forge_service_t *service, const char *package_id, const char *content_hash, int64_t size_bytes);

/// Increment the download counter and return the (borrowed) entry, or NULL if not cached.
const aethernet_forge_entry_t *aethernet_forge_fetch(aethernet_forge_service_t *service, const char *package_id);

/// Fill scalar cache statistics.
void aethernet_forge_get_stats(aethernet_forge_service_t *service, aethernet_forge_stats_t *out_stats);

/// Write up to `max` borrowed top-package pointers (most-downloaded first) into
/// out_top; returns the count written.
int aethernet_forge_top_packages(aethernet_forge_service_t *service, const aethernet_forge_entry_t **out_top, int max);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_FORGE_H
