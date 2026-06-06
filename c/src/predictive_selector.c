// SPDX-License-Identifier: MIT
// Predictive transport selector — 2-state Kalman RTT filter implementation.
//
// See: c/include/aethermesh/predictive_selector.h for the public API.

#include <math.h>
#include <string.h>
#include <stdlib.h>
#include <stddef.h>

#include "aethermesh/predictive_selector.h"

// ── Spin-lock helpers (same pattern as transport_metrics.c) ───────────────────

static inline void _sel_lock(volatile int *spin)
{
    while (__sync_lock_test_and_set(spin, 1)) { /* busy-wait */ }
}

static inline void _sel_unlock(volatile int *spin)
{
    __sync_lock_release(spin);
}

// ── aethermesh_kalman_filter_init ─────────────────────────────────────────────────

void aethermesh_kalman_filter_init(aethermesh_kalman_rtt_filter_t *f, double initial_rtt_ms)
{
    if (!f) return;
    f->q_rtt   = 25.0;
    f->q_drift = 5.0;
    f->r       = 100.0;
    f->rtt     = initial_rtt_ms;
    f->drift   = 0.0;
    f->p00     = 400.0;   /* large initial RTT uncertainty */
    f->p01     = 0.0;
    f->p11     = 100.0;   /* large initial drift uncertainty */
}

// ── aethermesh_kalman_filter_update ───────────────────────────────────────────────

double aethermesh_kalman_filter_update(aethermesh_kalman_rtt_filter_t *f, double measured_rtt_ms)
{
    if (!f) return 0.0;

    /* ── 1. Predict ──────────────────────────────────────────────────────────*/
    /* x_pred = F * x  (F = [[1,1],[0,1]]) */
    double rtt_pred   = f->rtt + f->drift;
    double drift_pred = f->drift;

    /* P_pred = F * P * F^T + Q  (F = [[1,1],[0,1]]) */
    double pp00 = f->p00 + 2.0 * f->p01 + f->p11 + f->q_rtt;
    double pp01 = f->p01 + f->p11;
    double pp11 = f->p11 + f->q_drift;

    /* ── 2. Kalman gain (H = [1, 0]) ─────────────────────────────────────────*/
    /* S = H * P_pred * H^T + R = pp00 + R */
    double s  = pp00 + f->r;
    double k0 = pp00 / s;
    double k1 = pp01 / s;

    /* ── 3. Update ────────────────────────────────────────────────────────────*/
    double innovation = measured_rtt_ms - rtt_pred;
    f->rtt   = rtt_pred   + k0 * innovation;
    f->drift = drift_pred + k1 * innovation;

    /* P = (I − K*H) * P_pred */
    f->p00 = (1.0 - k0) * pp00;
    f->p01 = (1.0 - k0) * pp01;
    f->p11 = -k1 * pp01 + pp11;

    /* Clamp to prevent numerical drift below zero. */
    if (f->p00 < 1e-6) f->p00 = 1e-6;
    if (f->p11 < 1e-6) f->p11 = 1e-6;

    return f->rtt;
}

// ── aethermesh_predictive_selector_init ──────────────────────────────────────────

void aethermesh_predictive_selector_init(aethermesh_predictive_selector_t *sel)
{
    if (!sel) return;
    memset(sel, 0, sizeof(*sel));
    /* All entries already NULL (zero-initialised). count = 0. */
}

// ── aethermesh_predictive_selector_register ──────────────────────────────────────

bool aethermesh_predictive_selector_register(
    aethermesh_predictive_selector_t *sel,
    aethermesh_transport_t           *transport,
    double                        initial_rtt_ms)
{
    if (!sel || !transport) return false;

    _sel_lock(&sel->lock);

    /* Check if already registered. */
    for (size_t i = 0; i < AETHERMESH_PREDICTIVE_MAX_TRANSPORTS; i++) {
        if (sel->entries[i].transport == transport) {
            _sel_unlock(&sel->lock);
            return false; /* already registered — no-op */
        }
    }

    /* Find an empty slot. */
    for (size_t i = 0; i < AETHERMESH_PREDICTIVE_MAX_TRANSPORTS; i++) {
        if (sel->entries[i].transport == NULL) {
            sel->entries[i].transport = transport;
            aethermesh_kalman_filter_init(&sel->entries[i].filter, initial_rtt_ms);
            sel->count++;
            _sel_unlock(&sel->lock);
            return true;
        }
    }

    _sel_unlock(&sel->lock);
    return false; /* table full */
}

// ── aethermesh_predictive_selector_unregister ────────────────────────────────────

void aethermesh_predictive_selector_unregister(
    aethermesh_predictive_selector_t *sel,
    aethermesh_transport_t           *transport)
{
    if (!sel || !transport) return;

    _sel_lock(&sel->lock);
    for (size_t i = 0; i < AETHERMESH_PREDICTIVE_MAX_TRANSPORTS; i++) {
        if (sel->entries[i].transport == transport) {
            memset(&sel->entries[i], 0, sizeof(sel->entries[i]));
            if (sel->count > 0) sel->count--;
            break;
        }
    }
    _sel_unlock(&sel->lock);
}

// ── aethermesh_predictive_selector_observe ───────────────────────────────────────

void aethermesh_predictive_selector_observe(
    aethermesh_predictive_selector_t *sel,
    aethermesh_transport_t           *transport,
    uint64_t                      rtt_ms,
    bool                          success,
    uint64_t                      bytes_transferred)
{
    if (!sel || !transport) return;

    /* Forward to PerTransportMetrics EWMA if the transport exposes it. */
    if (transport->vtable && transport->vtable->get_metrics) {
        aethermesh_transport_metrics_t *m =
            transport->vtable->get_metrics(transport->handle);
        if (m) {
            aethermesh_transport_metrics_record_sample(m, rtt_ms, success, bytes_transferred);
        }
    }

    /* Only successful sends with rtt_ms > 0 update the Kalman state. */
    if (rtt_ms == 0 || !success) return;

    _sel_lock(&sel->lock);
    for (size_t i = 0; i < AETHERMESH_PREDICTIVE_MAX_TRANSPORTS; i++) {
        if (sel->entries[i].transport == transport) {
            aethermesh_kalman_filter_update(&sel->entries[i].filter, (double)rtt_ms);
            break;
        }
    }
    _sel_unlock(&sel->lock);
}

// ── Rank comparator (descending by score) ────────────────────────────────────

static int _pred_rank_cmp(const void *a, const void *b)
{
    const aethermesh_predictive_rank_entry_t *ea = (const aethermesh_predictive_rank_entry_t *)a;
    const aethermesh_predictive_rank_entry_t *eb = (const aethermesh_predictive_rank_entry_t *)b;
    if (eb->score > ea->score) return  1;
    if (eb->score < ea->score) return -1;
    return 0;
}

// ── aethermesh_predictive_selector_rank ──────────────────────────────────────────

void aethermesh_predictive_selector_rank(
    aethermesh_predictive_selector_t     *sel,
    int                               payload_bytes,
    aethermesh_predictive_rank_entry_t   *out_ranked,
    size_t                           *out_count)
{
    if (!sel || !out_ranked || !out_count) return;
    *out_count = 0;

    _sel_lock(&sel->lock);

    for (size_t i = 0; i < AETHERMESH_PREDICTIVE_MAX_TRANSPORTS; i++) {
        aethermesh_transport_t *t = sel->entries[i].transport;
        if (!t || !t->vtable) continue;

        /* Skip unavailable transports (no send function = not usable). */
        if (!t->vtable->send) continue;

        /* Skip transports too slow for this payload (30 s ceiling). */
        if (t->vtable->max_bandwidth_bps > 0) {
            double serial_sec = (double)payload_bytes * 8.0
                              / (double)t->vtable->max_bandwidth_bps;
            if (serial_sec > 30.0) continue;
        }

        /* Retrieve live EWMA metrics if available. */
        aethermesh_transport_metrics_t *m = NULL;
        if (t->vtable->get_metrics) {
            m = t->vtable->get_metrics(t->handle);
        }

        /* Kalman state. */
        const aethermesh_kalman_rtt_filter_t *f = &sel->entries[i].filter;
        double kalman_rtt = f->rtt < 1.0 ? 1.0 : f->rtt;
        double variance   = f->p00;
        double stddev     = sqrt(variance);

        int32_t power_cost = t->vtable->power_cost_relative;
        if (power_cost <= 0) power_cost = 1;

        double loss_rate, effective_bps;

        if (m) {
            /* Read metric doubles directly — sel->lock is already held, so we
               must NOT acquire m->lock (deadlock risk if another thread holds
               m->lock and waits on sel->lock).  For scoring purposes, a
               slightly stale double read is fully acceptable; torn reads of
               IEEE-754 doubles are practically impossible on aligned x86-64
               accesses, and the worst case is a misordered sample, not data
               corruption. */
            loss_rate    = m->ewma_loss_rate;
            double tput  = m->ewma_tput_bps;
            double fallback = (double)t->vtable->max_bandwidth_bps * 0.1;
            effective_bps = (tput > fallback) ? tput : fallback;
        } else {
            loss_rate     = 0.05;
            effective_bps = (double)t->vtable->max_bandwidth_bps * 0.1;
        }

        /* Reliability factor: 1.0 at σ=0, ~0.5 at σ=100 ms. */
        double reliability_factor = 1.0 / (1.0 + stddev / 100.0);
        double score =
            (effective_bps / (double)power_cost)
            * (1.0 - loss_rate)
            / kalman_rtt
            * reliability_factor;

        out_ranked[*out_count].transport        = t;
        out_ranked[*out_count].score            = score;
        out_ranked[*out_count].predicted_rtt_ms = kalman_rtt;
        out_ranked[*out_count].rtt_variance     = variance;
        (*out_count)++;
    }

    _sel_unlock(&sel->lock);

    /* Sort descending by score. */
    if (*out_count > 1) {
        qsort(out_ranked, *out_count,
              sizeof(aethermesh_predictive_rank_entry_t),
              _pred_rank_cmp);
    }
}

// ── aethermesh_predictive_selector_best ──────────────────────────────────────────

aethermesh_transport_t *aethermesh_predictive_selector_best(
    aethermesh_predictive_selector_t *sel,
    int                           payload_bytes)
{
    if (!sel) return NULL;

    aethermesh_predictive_rank_entry_t ranked[AETHERMESH_PREDICTIVE_MAX_TRANSPORTS];
    size_t count = 0;
    aethermesh_predictive_selector_rank(sel, payload_bytes, ranked, &count);

    return (count > 0) ? ranked[0].transport : NULL;
}

// ── aethermesh_predictive_selector_kalman_state ───────────────────────────────────

bool aethermesh_predictive_selector_kalman_state(
    aethermesh_predictive_selector_t *sel,
    aethermesh_transport_t           *transport,
    double                       *out_rtt_ms,
    double                       *out_drift_ms,
    double                       *out_variance)
{
    if (!sel || !transport) return false;

    _sel_lock(&sel->lock);
    for (size_t i = 0; i < AETHERMESH_PREDICTIVE_MAX_TRANSPORTS; i++) {
        if (sel->entries[i].transport == transport) {
            const aethermesh_kalman_rtt_filter_t *f = &sel->entries[i].filter;
            if (out_rtt_ms)    *out_rtt_ms    = f->rtt;
            if (out_drift_ms)  *out_drift_ms  = f->drift;
            if (out_variance)  *out_variance  = f->p00;
            _sel_unlock(&sel->lock);
            return true;
        }
    }
    _sel_unlock(&sel->lock);
    return false;
}
