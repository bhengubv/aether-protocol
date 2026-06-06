// SPDX-License-Identifier: MIT
// Aether NodeReputationService — per-UHID behavioural score aggregation.
//
// Scores are clamped to [0.0, 1.0].  Unknown peers default to 1.0
// (benefit of the doubt).  All storage is static; no heap allocation.
// NOTE: Not thread-safe by design — single-threaded embedded targets only.

#ifndef AETHERMESH_REPUTATION_H
#define AETHERMESH_REPUTATION_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Max number of tracked UHIDs before oldest entry is evicted.
#define AETHERMESH_REPUTATION_MAX_ENTRIES 1024
// UHID max length (including null terminator)
#define AETHERMESH_UHID_MAX_LEN 64

typedef struct AetherMeshReputationEntry {
    char uhid[AETHERMESH_UHID_MAX_LEN];
    double score;
} AetherMeshReputationEntry;

typedef struct AetherMeshNodeReputationService {
    AetherMeshReputationEntry entries[AETHERMESH_REPUTATION_MAX_ENTRIES];
    int count;
} AetherMeshNodeReputationService;

/**
 * Initialise (zero-fill) a reputation service.  Must be called before any
 * other function.
 */
void aethermesh_reputation_init(AetherMeshNodeReputationService *svc);

/**
 * Record a detected RREQ flood attempt from `uhid`.  Score delta: -0.05.
 */
void aethermesh_reputation_record_rreq_flood(AetherMeshNodeReputationService *svc, const char *uhid);

/**
 * Record a packet replay attempt from `uhid`.  Score delta: -0.15.
 */
void aethermesh_reputation_record_replay(AetherMeshNodeReputationService *svc, const char *uhid);

/**
 * Record a signature verification failure from `uhid`.  Score delta: -0.20.
 */
void aethermesh_reputation_record_sig_failure(AetherMeshNodeReputationService *svc, const char *uhid);

/**
 * Record a custody-transfer refusal by `uhid`.  Score delta: -0.05.
 */
void aethermesh_reputation_record_custody_refusal(AetherMeshNodeReputationService *svc, const char *uhid);

/**
 * Record a successful delivery by `uhid`.  `round_trip_ms` is reserved for
 * future latency weighting and is currently unused.  Score delta: +0.01.
 */
void aethermesh_reputation_record_delivery_success(AetherMeshNodeReputationService *svc,
                                               const char *uhid,
                                               int round_trip_ms);

/**
 * Record a delivery failure by `uhid`.  Score delta: -0.02.
 */
void aethermesh_reputation_record_delivery_failure(AetherMeshNodeReputationService *svc, const char *uhid);

/**
 * Return the current score for `uhid`.  Returns 1.0 if `uhid` is unknown
 * (benefit of the doubt).  Result is always in [0.0, 1.0].
 */
double aethermesh_reputation_get_score(const AetherMeshNodeReputationService *svc, const char *uhid);

/**
 * Apply a pre-weighted score delta (for gossip propagation).
 * weighted_delta is clamped to [-1.0, 1.0] before application.
 */
void aethermesh_reputation_apply_weighted_delta(
    AetherMeshNodeReputationService *svc,
    const char *uhid,
    double weighted_delta
);

#ifdef __cplusplus
}
#endif

#endif // AETHERMESH_REPUTATION_H
