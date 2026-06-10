// SPDX-License-Identifier: MIT
// AetherNet Bandwidth Measurement Framework (ABMF) — W18-5.
//
// C port of the C# reference implementation under
// src/AetherNet.Core/Bandwidth/ and src/AetherNet.Transport/Bandwidth/.
// Algorithm follows BBRv3 (draft-cardwell-iccrg-bbr-congestion-control-02)
// with AetherNet-specific extensions: PHY-layer capping, gossip warm-start,
// and confidence tiers.
//
// ─── Memory-management contract ─────────────────────────────────────────────
//
//   aethernet_bw_estimator_t  ->  aethernet_bw_estimator_new() allocates;
//                                 aethernet_bw_estimator_free() releases.
//                                 Ownership: the caller.
//
//   aethernet_node_monitor_t  ->  aethernet_node_monitor_new() allocates;
//                                 aethernet_node_monitor_free() releases.
//                                 The monitor BORROWS the estimator pointers
//                                 passed to aethernet_node_monitor_register();
//                                 it does NOT free them. The caller must keep
//                                 each estimator alive until after
//                                 aethernet_node_monitor_free() returns.
//
//   aethernet_bw_probe_ack_t  ->  value type; no heap allocation, no free.
//   aethernet_bw_sample_t     ->  value type; no heap allocation, no free.
//   aethernet_node_snapshot_t ->  value type; no heap allocation, no free.

#ifndef AETHERNET_BANDWIDTH_H
#define AETHERNET_BANDWIDTH_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── BandwidthConfidence ──────────────────────────────────────────────────── */

/**
 * How confident we are in the current bandwidth estimate.
 * Rises with probe rounds; resets on topology change or extended idle.
 *
 * Tiers (matches C# reference):
 *   None   — no probe rounds, not warmed from gossip.
 *   Low    — 1–4 rounds, or gossip-warmed with 0 own rounds.
 *   Medium — 5–19 rounds.
 *   High   — 20+ rounds.
 */
typedef enum {
    AETHERNET_BW_CONFIDENCE_NONE   = 0,
    AETHERNET_BW_CONFIDENCE_LOW    = 1,
    AETHERNET_BW_CONFIDENCE_MEDIUM = 2,
    AETHERNET_BW_CONFIDENCE_HIGH   = 3,
} aethernet_bw_confidence_t;

/* ── NodeActivityState ────────────────────────────────────────────────────── */

typedef enum {
    AETHERNET_NODE_OFFLINE  = 0,  /* No transports available. Node is isolated.          */
    AETHERNET_NODE_IDLE     = 1,  /* Transports available but no data in the last 5 s.   */
    AETHERNET_NODE_ACTIVE   = 2,  /* Data flowing; utilisation < 50 % of capacity.       */
    AETHERNET_NODE_BUSY     = 3,  /* Utilisation ≥ 50 %; approaching limits.             */
    AETHERNET_NODE_DEGRADED = 4,  /* Loss rate > 5 % or delivery rate declining.         */
} aethernet_node_state_t;

/* ── BandwidthSample ─────────────────────────────────────────────────────── */

/**
 * Point-in-time bandwidth measurement for a single transport link.
 *
 * All bandwidth values are in bits-per-second; all time values are in
 * microseconds. rto_us and effective_bps are derived (see below).
 */
typedef struct {
    char                      transport_name[64];

    /** BBRv3 BtlBw: max delivery rate over the 10×RTprop window (bps). */
    int64_t                   btlbw_bps;

    /** Available bandwidth: btlbw_bps × (1 − loss_rate) (bps). */
    int64_t                   available_bps;

    /** Bandwidth-Delay Product: btlbw_bps × rtprop_us / 8 (bytes). */
    int64_t                   bdp_bytes;

    /** RFC 6298 smoothed RTT (µs). */
    int64_t                   srtt_us;

    /** RFC 6298 RTT mean deviation — RTTVAR (µs). */
    int64_t                   rttvar_us;

    /** BBRv3 RTprop: minimum RTT observed in last 10 s (µs). */
    int64_t                   rtprop_us;

    /** EWMA fractional loss rate [0, 1]; α = 0.10. */
    double                    loss_rate;

    /** PHY-layer bandwidth cap from RSSI hints (bps). 0 = unknown. */
    int64_t                   phy_cap_bps;

    aethernet_bw_confidence_t confidence;

    /**
     * RFC 6298 §2.4 RTO: SRTT + max(G, 4×RTTVAR), G = 1 ms.
     * Clamped to [200 ms, 60 s]. Stored in microseconds.
     */
    int64_t                   rto_us;

    /** Effective bandwidth: min(btlbw_bps, phy_cap_bps) when phy_cap_bps > 0. */
    int64_t                   effective_bps;
} aethernet_bw_sample_t;

/* ── BandwidthProbeAck ───────────────────────────────────────────────────── */

/**
 * Four-timestamp probe ACK for two-way delay / RTT measurement (RFC 5136 §3).
 * All raw timestamps are microseconds since Unix epoch on each peer's local clock.
 * Clock synchronisation is NOT required — RTT is computed from sender-side
 * timestamps only.
 *
 * Call aethernet_bw_probe_ack_compute_derived() after populating the raw fields
 * to fill in rtt_us and forward_owd_us.
 */
typedef struct {
    uint32_t sequence;
    int64_t  sender_send_us;        /* µs since epoch, sender clock   */
    int64_t  receiver_receive_us;   /* µs since epoch, receiver clock */
    int64_t  receiver_send_us;      /* µs since epoch, receiver clock */
    int64_t  sender_receive_us;     /* µs since epoch, sender clock   */
    int32_t  probe_bytes;

    /* ── Derived fields — filled by aethernet_bw_probe_ack_compute_derived() ── */

    /**
     * Clock-sync-free RTT:
     *   rtt_us = (sender_receive_us − sender_send_us) −
     *            (receiver_send_us  − receiver_receive_us)
     */
    int64_t  rtt_us;

    /**
     * Forward one-way delay (sender → receiver). Requires loose clock sync;
     * treat as approximate unless NTP/PTP is available.
     *   forward_owd_us = receiver_receive_us − sender_send_us
     */
    int64_t  forward_owd_us;
} aethernet_bw_probe_ack_t;

/**
 * Populate rtt_us and forward_owd_us from the raw timestamp fields.
 * Safe to call with ack == NULL (no-op).
 */
void aethernet_bw_probe_ack_compute_derived(aethernet_bw_probe_ack_t *ack);

/* ── BandwidthEstimator ─────────────────────────────────────────────────── */

/**
 * Per-transport link bandwidth estimator.
 *
 * Implements a BBRv3-inspired algorithm:
 *   • BtlBw max-filter: circular buffer of 10 delivery-rate samples,
 *     window = 10×RTprop (evicts expired entries each tick).
 *   • RTprop min-filter: sliding window of samples from the last 10 seconds.
 *   • SRTT/RTTVAR: RFC 6298 Jacobson/Karels, α = 1/8, β = 1/4.
 *   • Loss rate: EWMA, α = 0.10.
 *   • PHY cap: RSSI-to-BtlBw table (same thresholds as C# reference).
 *   • Gossip warm-start: seeds the estimator from a peer's measurement.
 *   • Confidence tiers: None/Low/Medium/High, driven by probe_rounds.
 *
 * Thread-safe: all state protected by an internal pthread_mutex_t.
 */
typedef struct aethernet_bw_estimator aethernet_bw_estimator_t;

/**
 * Allocate a new estimator for the named transport.
 *
 * transport_name  — identifier, e.g. "BLE", "Wi-Fi Direct". Copied internally.
 *                   Must not be NULL; truncated at 63 chars.
 * max_bps         — theoretical maximum bitrate (initial optimistic seed).
 *
 * Returns newly-allocated estimator on success; NULL on allocation failure.
 * Caller frees with aethernet_bw_estimator_free().
 */
aethernet_bw_estimator_t *aethernet_bw_estimator_new(const char *transport_name,
                                                      int64_t     max_bps);

/** Free an estimator. Safe to call with NULL. */
void aethernet_bw_estimator_free(aethernet_bw_estimator_t *e);

/**
 * Record a successful delivery of `bytes` on the transport.
 * Both timestamps are microseconds since Unix epoch on the SAME clock.
 * No-op when bytes ≤ 0 or deliver_us ≤ send_us.
 */
void aethernet_bw_estimator_record_delivery(aethernet_bw_estimator_t *e,
                                             int32_t bytes,
                                             int64_t send_us,
                                             int64_t deliver_us);

/**
 * Record that `bytes` were lost (timeout or explicit NAK).
 * Increments the EWMA loss rate.
 */
void aethernet_bw_estimator_record_loss(aethernet_bw_estimator_t *e, int32_t bytes);

/**
 * Feed an active probe ACK into the estimator.
 * `local_receive_us` is the local-clock µs at the moment the ACK arrived.
 * The ack's derived fields (rtt_us) must already be computed via
 * aethernet_bw_probe_ack_compute_derived().
 */
void aethernet_bw_estimator_record_probe_result(aethernet_bw_estimator_t       *e,
                                                 const aethernet_bw_probe_ack_t *ack,
                                                 int64_t                         local_receive_us);

/**
 * Pre-warm from gossip. Only effective when confidence is NONE and the
 * estimator has not already been warmed — never downgrades an existing estimate.
 */
void aethernet_bw_estimator_warm_from_gossip(aethernet_bw_estimator_t  *e,
                                              int64_t                    btlbw_bps,
                                              int64_t                    rtprop_us,
                                              aethernet_bw_confidence_t  confidence);

/**
 * Apply a physical-layer RSSI hint. Constrains the estimate before probes
 * complete. Uses the same calibration table as the C# reference.
 * rssi_dbm — received signal strength in dBm (negative integer).
 */
void aethernet_bw_estimator_apply_phy_hint(aethernet_bw_estimator_t *e, int rssi_dbm);

/**
 * Snapshot the current estimate into `out`. Thread-safe (acquires lock).
 * `out` must not be NULL.
 */
void aethernet_bw_estimator_get_sample(const aethernet_bw_estimator_t *e,
                                        aethernet_bw_sample_t          *out);

/* ── NodeActivitySnapshot ────────────────────────────────────────────────── */

/**
 * Full node activity snapshot — the top-level model surfaced to UI.
 * Produced by the node monitor every sample_interval_ms milliseconds.
 */
typedef struct {
    aethernet_node_state_t state;

    /** Aggregate bits per second flowing INTO this node (all transports). */
    int64_t                ingress_bps;

    /** Aggregate bits per second flowing OUT of this node (all transports). */
    int64_t                egress_bps;

    /** Number of transports that have had data in the last sample interval. */
    int                    active_transports;

    /** Distinct peers with traffic in the last idle window (peer-aware record_* only). */
    int                    active_peers;

    /** Combined throughput: ingress_bps + egress_bps. */
    int64_t                total_bps;

    /** True when state is Active, Busy, or Degraded. */
    bool                   has_activity;
} aethernet_node_snapshot_t;

/* ── NodeActivityMonitor ─────────────────────────────────────────────────── */

/**
 * Observable node activity monitor.
 *
 * Runs a background pthread that sleeps sample_interval_ms between ticks.
 * Each tick reads volatile byte counters (reset atomically), derives bps
 * rates, queries each registered estimator for its loss_rate and btlbw_bps,
 * and publishes an aethernet_node_snapshot_t.
 *
 * Thread-safe: all mutable state protected by an internal pthread_mutex_t.
 * The snapshot callback is invoked from the background thread.
 *
 * Memory contract: the monitor BORROWS estimator pointers. It does NOT
 * free them. Keep each estimator alive until aethernet_node_monitor_free().
 */
typedef struct aethernet_node_monitor aethernet_node_monitor_t;

/**
 * Callback invoked on the background thread each sample interval.
 * snap       — borrowed snapshot (valid for the duration of the call).
 * user_data  — opaque pointer registered with the callback.
 */
typedef void (*aethernet_node_monitor_cb)(const aethernet_node_snapshot_t *snap,
                                          void *user_data);

/** Allocate a new node monitor. Returns NULL on allocation failure. */
aethernet_node_monitor_t *aethernet_node_monitor_new(void);

/** Stop the background thread (if running) and free all resources. */
void aethernet_node_monitor_free(aethernet_node_monitor_t *m);

/**
 * Register a transport with the monitor. `name` identifies the transport;
 * `e` is the estimator (BORROWED — do not free before the monitor).
 * Thread-safe; may be called before or after start().
 */
void aethernet_node_monitor_register(aethernet_node_monitor_t *m,
                                     const char               *name,
                                     aethernet_bw_estimator_t *e);

/**
 * Record inbound bytes on a transport. Call from the transport receive path.
 * Uses volatile atomic accumulation; safe to call from any thread.
 */
void aethernet_node_monitor_record_ingress(aethernet_node_monitor_t *m,
                                           const char               *transport,
                                           int32_t                   bytes);

/**
 * Record outbound bytes on a transport. Call from the transport send path.
 */
void aethernet_node_monitor_record_egress(aethernet_node_monitor_t *m,
                                          const char               *transport,
                                          int32_t                   bytes);

/**
 * Record inbound bytes on a transport FROM a specific peer. Calls the
 * transport-only record_ingress, then stamps peer_uhid → now in the
 * peer-last-seen table so the peer counts toward snapshot.active_peers.
 * The transport-only overload does NOT contribute to the peer count.
 * peer_uhid is copied; ignored when NULL or empty. Safe from any thread.
 */
void aethernet_node_monitor_record_ingress_peer(aethernet_node_monitor_t *m,
                                                const char               *transport,
                                                const char               *peer_uhid,
                                                int32_t                   bytes);

/**
 * Record outbound bytes on a transport TO a specific peer. Calls the
 * transport-only record_egress, then stamps peer_uhid → now in the
 * peer-last-seen table so the peer counts toward snapshot.active_peers.
 * peer_uhid is copied; ignored when NULL or empty. Safe from any thread.
 */
void aethernet_node_monitor_record_egress_peer(aethernet_node_monitor_t *m,
                                               const char               *transport,
                                               const char               *peer_uhid,
                                               int32_t                   bytes);

/**
 * Start the background sampling thread.
 * sample_interval_ms — interval between ticks (clamped to [10, 60000]).
 * No-op if already running.
 */
void aethernet_node_monitor_start(aethernet_node_monitor_t *m, int sample_interval_ms);

/** Stop the background thread. Blocks until the thread exits. No-op if stopped. */
void aethernet_node_monitor_stop(aethernet_node_monitor_t *m);

/**
 * Get a snapshot of the current node activity. Thread-safe.
 * `out` must not be NULL.
 */
void aethernet_node_monitor_get_snapshot(const aethernet_node_monitor_t *m,
                                          aethernet_node_snapshot_t      *out);

/**
 * Register a callback invoked each sample interval.
 * Pass cb = NULL to unregister. Replaces any existing callback.
 */
void aethernet_node_monitor_set_callback(aethernet_node_monitor_t *m,
                                          aethernet_node_monitor_cb cb,
                                          void                     *user_data);

/* ── BandwidthGossipPayload ──────────────────────────────────────────────── */

/**
 * Gossip payload broadcast to new peers during handshake so the receiving
 * node's estimator starts with a warm BtlBw estimate instead of probing from
 * zero. Value type — no heap allocation, no free.
 */
typedef struct {
    char                      peer_uhid[128];
    char                      transport_name[64];
    int64_t                   btlbw_bps;
    int64_t                   rtprop_us;
    aethernet_bw_confidence_t confidence;
    int64_t                   measured_at_unix_ms;
} aethernet_bw_gossip_t;

/* ── BandwidthDirector ───────────────────────────────────────────────────── */

/**
 * Cross-transport bandwidth synthesis and mesh gossip coordinator.
 *
 * Maintains a (peer_uhid × transport_name) → aethernet_bw_sample_t matrix and
 * recommends the best transport for a payload based on available bandwidth,
 * BDP, and per-transport power cost. Port of the C# BandwidthDirector.
 *
 * Matrix capacity: fixed at AETHERNET_BW_DIRECTOR_MAX_MATRIX (256) entries;
 * registered estimators are capped at AETHERNET_BW_DIRECTOR_MAX_ESTIMATORS
 * (32). Seeding silently drops when the matrix is full.
 *
 * ─── Memory-management contract ─────────────────────────────────────────────
 *   aethernet_bw_director_new()  allocates; aethernet_bw_director_free()
 *   releases (including the internal matrix). Ownership: the caller.
 *
 *   The director BORROWS the estimator pointers passed to
 *   aethernet_bw_director_register(); it does NOT free them. The caller must
 *   keep each estimator alive until after aethernet_bw_director_free() returns.
 *
 * Thread-safe: all state protected by an internal pthread_mutex_t.
 */
typedef struct aethernet_bw_director aethernet_bw_director_t;

/** Allocate a new director. Returns NULL on allocation failure. */
aethernet_bw_director_t *aethernet_bw_director_new(void);

/** Free a director (and its matrix). Borrowed estimators are NOT freed. Safe with NULL. */
void aethernet_bw_director_free(aethernet_bw_director_t *d);

/**
 * Register an estimator (BORROWED — the director does not free it). Keyed by the
 * estimator's transport name (case-insensitive). Re-registering the same
 * transport name replaces the previous estimator pointer.
 */
void aethernet_bw_director_register(aethernet_bw_director_t   *d,
                                    aethernet_bw_estimator_t  *e);

/**
 * Get the estimate for (peer_uhid, transport). Writes into *out and returns
 * true when a matrix entry exists; returns false otherwise (out untouched).
 */
bool aethernet_bw_director_get_estimate(const aethernet_bw_director_t *d,
                                        const char                    *peer_uhid,
                                        const char                    *transport,
                                        aethernet_bw_sample_t         *out);

/**
 * Recommend the best transport for a payload of payload_bytes to peer_uhid.
 * Writes the transport name into out_name (caller-allocated, out_cap bytes,
 * NUL-terminated). Returns true on success, false if no transports are
 * available (no matrix entries for the peer AND no registered estimators).
 *
 * Scoring (matches C# BandwidthDirector.RecommendTransport):
 *   score = (available_bps / power_cost) × bdp_bonus × confidence_factor
 *     bdp_bonus        = payload_bytes > bdp_bytes ? 0.0 : 1.5
 *     confidence_factor= confidence == None ? 0.5 : 1.0
 * With no matrix entries, falls back to the registered transport with the
 * lowest power cost.
 */
bool aethernet_bw_director_recommend_transport(const aethernet_bw_director_t *d,
                                               const char                    *peer_uhid,
                                               int64_t                        payload_bytes,
                                               char                          *out_name,
                                               size_t                         out_cap);

/**
 * Build a gossip payload for (peer_uhid, transport) from the matching
 * registered estimator's current sample. Returns false when no estimator is
 * registered for the transport or its confidence is None (out untouched);
 * true otherwise with *out populated.
 */
bool aethernet_bw_director_build_gossip(const aethernet_bw_director_t *d,
                                        const char                    *peer_uhid,
                                        const char                    *transport,
                                        aethernet_bw_gossip_t         *out);

/**
 * Apply a received gossip payload: warms the matching registered estimator via
 * aethernet_bw_estimator_warm_from_gossip(), then seeds the matrix entry for
 * (peer_uhid, transport) from that estimator's current sample. No-op when no
 * estimator is registered for the transport. Safe with NULL gossip.
 */
void aethernet_bw_director_apply_gossip(aethernet_bw_director_t      *d,
                                        const aethernet_bw_gossip_t  *gossip);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_BANDWIDTH_H */
