// SPDX-License-Identifier: MIT
// Predictive transport selector — 2-state Kalman RTT filter over PerTransportMetrics.
//
// Extends aethernet_transport_rank() by replacing the EWMA RTT term with a
// Kalman-estimated RTT that predicts degrading links before they exceed a threshold.
// The posterior variance also penalises uncertain links even when their point estimate
// looks good.
//
// Score formula:
//   (effective_bps / power_cost) × (1 − loss_rate) / max(kalman_rtt, 1)
//       × (1 / (1 + sqrt(variance) / 100))
//
// Thread safety: spin-lock inside aethernet_predictive_selector_t.

#ifndef AETHERNET_PREDICTIVE_SELECTOR_H
#define AETHERNET_PREDICTIVE_SELECTOR_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#include "transport.h"

#ifdef __cplusplus
extern "C" {
#endif

// ── Kalman RTT filter ─────────────────────────────────────────────────────────

/**
 * 2-state Kalman filter estimating RTT and its drift for one transport link.
 *
 *   State:   x = [rtt; drift]
 *   Model:   F = [[1,1],[0,1]]   (constant velocity)
 *   Observe: H = [1, 0]          (RTT only)
 *
 * Tuning constants (defaults calibrated for mesh links at 50–1000 ms RTT):
 *   q_rtt   = 25.0  — process noise variance for RTT (ms²)
 *   q_drift =  5.0  — process noise variance for drift (ms²)
 *   r       = 100.0 — observation noise variance (ms²)
 */
typedef struct {
    double q_rtt;    /**< Process noise for RTT Q[0,0] (ms²).  Default 25.0  */
    double q_drift;  /**< Process noise for drift Q[1,1] (ms²). Default 5.0  */
    double r;        /**< Observation noise variance R (ms²).  Default 100.0 */

    double rtt;      /**< Current RTT estimate (ms).                          */
    double drift;    /**< Current drift estimate (ms/sample). Positive = rising RTT. */

    /* Covariance P (2×2 symmetric, stored as upper triangle). */
    double p00;      /**< RTT variance (ms²).                                 */
    double p01;      /**< RTT-drift covariance.                               */
    double p11;      /**< Drift variance.                                     */
} aethernet_kalman_rtt_filter_t;

/**
 * Initialise a Kalman filter with an initial RTT prior.
 *
 * @param f             Filter to initialise (must be non-NULL).
 * @param initial_rtt_ms  Starting RTT estimate in ms. Typical value: 200.0.
 */
void aethernet_kalman_filter_init(aethernet_kalman_rtt_filter_t *f, double initial_rtt_ms);

/**
 * Incorporate a new RTT measurement and update the filter state.
 *
 * Performs the full Kalman predict→update cycle.  Returns the updated RTT
 * estimate.  Must NOT be called concurrently with other operations on the same
 * filter instance — callers are responsible for locking.
 *
 * @param f                Filter to update (must be non-NULL).
 * @param measured_rtt_ms  Measured round-trip time in milliseconds.
 * @return                 Updated RTT estimate in ms.
 */
double aethernet_kalman_filter_update(aethernet_kalman_rtt_filter_t *f, double measured_rtt_ms);

// ── Predictive selector ───────────────────────────────────────────────────────

/** Maximum number of transports that can be registered in one selector. */
#define AETHERNET_PREDICTIVE_MAX_TRANSPORTS 32

/**
 * Per-entry storage: one transport pointer plus its Kalman filter.
 * Entries are packed into a flat array; unused entries have transport == NULL.
 */
typedef struct {
    aethernet_transport_t           *transport; /**< Non-owning pointer. NULL = empty slot. */
    aethernet_kalman_rtt_filter_t    filter;
} aethernet_predictive_entry_t;

/**
 * Predictive transport selector.
 *
 * Fixed-size (AETHERNET_PREDICTIVE_MAX_TRANSPORTS slots).  All mutation is
 * protected by an embedded spin-lock so the selector is safe to share
 * across threads.
 *
 * Initialise with aethernet_predictive_selector_init() before use.
 */
typedef struct {
    volatile int              lock;    /**< Spin-lock: 0 = unlocked, 1 = locked. */
    size_t                    count;   /**< Number of registered (non-NULL) entries. */
    aethernet_predictive_entry_t entries[AETHERNET_PREDICTIVE_MAX_TRANSPORTS];
} aethernet_predictive_selector_t;

/**
 * One entry returned by aethernet_predictive_selector_rank().
 */
typedef struct {
    aethernet_transport_t *transport;       /**< Non-owning pointer to the transport.  */
    double              score;           /**< Composite predictive score (higher = better). */
    double              predicted_rtt_ms;/**< Kalman-estimated RTT in ms.           */
    double              rtt_variance;    /**< Posterior RTT variance (ms²).          */
} aethernet_predictive_rank_entry_t;

// ── Lifecycle ─────────────────────────────────────────────────────────────────

/**
 * Initialise a selector struct.  Must be called before any other
 * aethernet_predictive_selector_* functions.
 *
 * @param sel  Selector to initialise (must be non-NULL).
 */
void aethernet_predictive_selector_init(aethernet_predictive_selector_t *sel);

/**
 * Register a transport for Kalman tracking.
 *
 * @param sel             Selector (non-NULL).
 * @param transport       Transport to register (non-NULL, non-owning pointer).
 * @param initial_rtt_ms  Initial RTT prior in ms.  Typical value: 200.0.
 * @return                true on success; false if the selector is full or the
 *                        transport is already registered.
 */
bool aethernet_predictive_selector_register(
    aethernet_predictive_selector_t *sel,
    aethernet_transport_t           *transport,
    double                        initial_rtt_ms);

/**
 * Unregister a transport and free its slot.
 *
 * @param sel        Selector (non-NULL).
 * @param transport  Transport to remove.
 */
void aethernet_predictive_selector_unregister(
    aethernet_predictive_selector_t *sel,
    aethernet_transport_t           *transport);

// ── Observation ───────────────────────────────────────────────────────────────

/**
 * Feed a new send result into the transport's PerTransportMetrics (EWMA) and,
 * for successful sends with rtt_ms > 0, into its Kalman filter.
 *
 * @param sel               Selector (non-NULL).
 * @param transport         Transport that just completed a send.
 * @param rtt_ms            Measured round-trip time in ms (0 = one-way send).
 * @param success           Whether the peer acknowledged receipt.
 * @param bytes_transferred Payload bytes transferred (0 on failure).
 */
void aethernet_predictive_selector_observe(
    aethernet_predictive_selector_t *sel,
    aethernet_transport_t           *transport,
    uint64_t                      rtt_ms,
    bool                          success,
    uint64_t                      bytes_transferred);

// ── Ranking ───────────────────────────────────────────────────────────────────

/**
 * Rank registered transports by predictive score (highest first).
 *
 * Only transports with a vtable->send pointer (= available) are included.
 * Transports whose bandwidth is too low for payload_bytes within 30 s are skipped.
 *
 * @param sel          Selector (non-NULL).
 * @param payload_bytes Intended payload size in bytes.
 * @param out_ranked   Caller-allocated array of at least AETHERNET_PREDICTIVE_MAX_TRANSPORTS
 *                     entries; filled on return.
 * @param out_count    Populated with the number of scored entries.
 */
void aethernet_predictive_selector_rank(
    aethernet_predictive_selector_t     *sel,
    int                               payload_bytes,
    aethernet_predictive_rank_entry_t   *out_ranked,
    size_t                           *out_count);

/**
 * Return the best (highest-scoring) available transport for payload_bytes.
 *
 * @param sel           Selector (non-NULL).
 * @param payload_bytes Intended payload size in bytes.
 * @return              Pointer to the best transport, or NULL if none available.
 */
aethernet_transport_t *aethernet_predictive_selector_best(
    aethernet_predictive_selector_t *sel,
    int                           payload_bytes);

/**
 * Retrieve the current Kalman state for a registered transport.
 *
 * @param sel           Selector (non-NULL).
 * @param transport     Transport to query.
 * @param out_rtt_ms    Populated with the RTT estimate in ms.
 * @param out_drift_ms  Populated with the drift estimate (ms/sample).
 * @param out_variance  Populated with the posterior RTT variance (ms²).
 * @return              true if the transport is registered; false otherwise.
 */
bool aethernet_predictive_selector_kalman_state(
    aethernet_predictive_selector_t *sel,
    aethernet_transport_t           *transport,
    double                       *out_rtt_ms,
    double                       *out_drift_ms,
    double                       *out_variance);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_PREDICTIVE_SELECTOR_H */
