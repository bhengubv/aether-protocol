// SPDX-License-Identifier: MIT
// Aether BehavioralAnomalyDetector — detects volume spikes, destination
// scatter, geohash spoofing, and SPK signature failures.
// All storage is static; no heap allocation after the initial malloc.
// NOTE: Not thread-safe by design — single-threaded embedded targets only.

#ifndef AETHERNET_ANOMALY_H
#define AETHERNET_ANOMALY_H

#include "aethernet_reputation.h"
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ─── Tunable options ──────────────────────────────────────────────────────── */

typedef struct {
    int64_t volume_window_ms;        /* default 30000 */
    double  volume_spike_multiplier; /* default 5.0   */
    double  ewma_alpha;              /* default 0.20  */
    int64_t scatter_window_ms;       /* default 60000 */
    int     scatter_threshold;       /* default 50    */
    int     geohash_prefix_length;   /* default 4     */
    int64_t geohash_rate_limit_ms;   /* default 60000 */
} AetherNetAnomalyOptions;

/**
 * Fill *opts with safe defaults.
 */
void aethernet_anomaly_options_default(AetherNetAnomalyOptions *opts);

/* ─── Opaque detector handle ───────────────────────────────────────────────── */

typedef struct AetherNetBehavioralAnomalyDetector AetherNetBehavioralAnomalyDetector;

/**
 * Create a new detector backed by the given reputation service.
 * If opts is NULL, defaults are used.
 * Returns NULL only on allocation failure.
 */
AetherNetBehavioralAnomalyDetector *aethernet_anomaly_create(
    AetherNetNodeReputationService *reputation,
    const AetherNetAnomalyOptions  *opts   /* NULL = defaults */
);

/**
 * Free all resources associated with the detector.
 */
void aethernet_anomaly_destroy(AetherNetBehavioralAnomalyDetector *det);

/**
 * Called for every packet forwarded through or received by this node.
 * Runs the volume-spike and destination-scatter detectors for source_uhid.
 */
void aethernet_anomaly_observe_packet(
    AetherNetBehavioralAnomalyDetector *det,
    const char *source_uhid,
    const char *destination_uhid,
    int64_t     timestamp_ms
);

/**
 * Called when a node announces a geohash prefix that differs from the prefix
 * inferred from routing topology.  Fires sig_failure when prefix mismatch is
 * detected and the per-UHID rate-limit has expired.
 */
void aethernet_anomaly_observe_geohash_claim(
    AetherNetBehavioralAnomalyDetector *det,
    const char *uhid,
    const char *claimed_geohash,
    const char *observed_routing_geohash
);

/**
 * Called when a SPK/Ed25519 signature verification fails.
 * Direct passthrough to aethernet_reputation_record_sig_failure.
 */
void aethernet_anomaly_observe_spk_sig_failure(
    AetherNetBehavioralAnomalyDetector *det,
    const char *uhid
);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_ANOMALY_H */
