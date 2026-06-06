// SPDX-License-Identifier: MIT
// Aether Transport Metrics — EWMA per-transport metrics, FecCodec vtable,
// and transport ranking for adaptive transport selection.

#include <stdlib.h>
#include <string.h>
#include <math.h>

#include "aethernet/transport.h"

// ── Spin-lock helpers ─────────────────────────────────────────────────────────

static inline void _metrics_lock(volatile int *spin)
{
    /* Portable spin-lock: CAS loop until we acquire. */
    while (__sync_lock_test_and_set(spin, 1)) {
        /* busy-wait */
    }
}

static inline void _metrics_unlock(volatile int *spin)
{
    __sync_lock_release(spin);
}

// ── aethernet_transport_metrics_init ─────────────────────────────────────────────

void aethernet_transport_metrics_init(aethernet_transport_metrics_t *m)
{
    if (!m) return;
    memset(m, 0, sizeof(*m));
    m->ewma_rtt_ms    = 200.0;  /* prior: 200 ms        */
    m->ewma_loss_rate = 0.05;   /* prior: 5 % loss      */
    m->ewma_tput_bps  = 0.0;    /* bootstrapped later   */
}

// ── aethernet_transport_metrics_record_sample ────────────────────────────────────

void aethernet_transport_metrics_record_sample(
    aethernet_transport_metrics_t *m,
    uint64_t rtt_ms,
    bool success,
    uint64_t bytes_transferred)
{
    if (!m) return;

    _metrics_lock(&m->lock);

    m->sample_count++;

    if (rtt_ms > 0) {
        m->ewma_rtt_ms =
            AETHERNET_METRICS_ALPHA * (double)rtt_ms
            + (1.0 - AETHERNET_METRICS_ALPHA) * m->ewma_rtt_ms;
    }

    double loss_obs = success ? 0.0 : 1.0;
    m->ewma_loss_rate =
        AETHERNET_METRICS_ALPHA * loss_obs
        + (1.0 - AETHERNET_METRICS_ALPHA) * m->ewma_loss_rate;

    if (success && rtt_ms > 0 && bytes_transferred > 0) {
        double tput_bps =
            (double)bytes_transferred * 8.0 * 1000.0 / (double)rtt_ms;

        if (m->ewma_tput_bps < 1.0) {
            m->ewma_tput_bps = tput_bps;   /* bootstrap first sample */
        } else {
            m->ewma_tput_bps =
                AETHERNET_METRICS_ALPHA * tput_bps
                + (1.0 - AETHERNET_METRICS_ALPHA) * m->ewma_tput_bps;
        }
    }

    _metrics_unlock(&m->lock);
}

// ── aethernet_transport_metrics_composite_score ──────────────────────────────────

double aethernet_transport_metrics_composite_score(
    const aethernet_transport_metrics_t *m,
    int64_t max_bandwidth_bps,
    int32_t power_cost)
{
    if (power_cost <= 0) power_cost = 1;

    double rtt, loss, tput;

    if (m) {
        /* Cast away const for lock — lock field is volatile, not const-sensitive */
        aethernet_transport_metrics_t *mw = (aethernet_transport_metrics_t *)m;
        _metrics_lock(&mw->lock);
        rtt  = m->ewma_rtt_ms;
        loss = m->ewma_loss_rate;
        tput = m->ewma_tput_bps;
        _metrics_unlock(&mw->lock);
    } else {
        rtt  = 200.0;
        loss = 0.05;
        tput = 0.0;
    }

    double effective_bps = tput;
    double fallback_bps  = (double)max_bandwidth_bps * 0.1;
    if (effective_bps < fallback_bps) effective_bps = fallback_bps;

    double rtt_clamped = rtt < 1.0 ? 1.0 : rtt;

    return (effective_bps / (double)power_cost) * (1.0 - loss) / rtt_clamped;
}

// ── aethernet_transport_rank ─────────────────────────────────────────────────────

/**
 * qsort comparator: descending by score (highest first).
 */
static int _rank_cmp(const void *a, const void *b)
{
    const aethernet_transport_rank_entry_t *ea = (const aethernet_transport_rank_entry_t *)a;
    const aethernet_transport_rank_entry_t *eb = (const aethernet_transport_rank_entry_t *)b;

    if (eb->score > ea->score) return  1;
    if (eb->score < ea->score) return -1;
    return 0;
}

void aethernet_transport_rank(
    aethernet_transport_t **transports,
    size_t n,
    aethernet_transport_rank_entry_t *out_ranked,
    size_t *out_count)
{
    if (!transports || !out_ranked || !out_count) return;
    *out_count = 0;

    for (size_t i = 0; i < n; i++) {
        aethernet_transport_t *t = transports[i];
        if (!t || !t->vtable) continue;

        /* Skip unavailable transports (no send function = not usable) */
        if (!t->vtable->send) continue;

        /* Retrieve live metrics if the transport exposes them */
        aethernet_transport_metrics_t *m = NULL;
        if (t->vtable->get_metrics) {
            m = t->vtable->get_metrics(t->handle);
        }

        double score = aethernet_transport_metrics_composite_score(
            m,
            t->vtable->max_bandwidth_bps,
            t->vtable->power_cost_relative);

        out_ranked[*out_count].transport = t;
        out_ranked[*out_count].score     = score;
        (*out_count)++;
    }

    if (*out_count > 1) {
        qsort(out_ranked, *out_count,
              sizeof(aethernet_transport_rank_entry_t),
              _rank_cmp);
    }
}
