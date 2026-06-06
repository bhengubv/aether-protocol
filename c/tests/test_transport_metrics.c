// SPDX-License-Identifier: MIT
// Unit tests for transport_metrics.c — EWMA metrics init, recording, composite
// score, and transport ranking.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "aethernet/transport.h"

// ── Test runner ───────────────────────────────────────────────

#define RUN(name) do { \
    printf("TEST: " #name "..."); \
    name(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)
static int tests_run = 0;

// ── Helpers ───────────────────────────────────────────────────

static bool ft_send(void *h, const char *p, const uint8_t *d, size_t n) {
    (void)h; (void)p; (void)d; (void)n; return true;
}
static bool ft_is_connected(void *h, const char *p) {
    (void)h; (void)p; return true;
}

// Per-transport metrics + vtables for the ranking tests.
static aethernet_transport_metrics_t *g_metrics_A = NULL;
static aethernet_transport_metrics_t *g_metrics_B = NULL;

static aethernet_transport_metrics_t *get_metrics_A(void *h) { (void)h; return g_metrics_A; }
static aethernet_transport_metrics_t *get_metrics_B(void *h) { (void)h; return g_metrics_B; }

// Each transport in the ranking test needs its own vtable (separate static
// variables) so that both can coexist with correct field values.
static aethernet_transport_vtable_t g_vt_A;
static aethernet_transport_vtable_t g_vt_B;

static void fill_vtable(aethernet_transport_vtable_t *vt, int64_t bps, int32_t power,
                        aethernet_transport_metrics_t *(*get_m)(void *))
{
    vt->name                 = "test";
    vt->max_bandwidth_bps    = bps;
    vt->power_cost_relative  = power;
    vt->max_range_meters     = 100;
    vt->send                 = ft_send;
    vt->is_connected         = ft_is_connected;
    vt->set_on_data_received = NULL;
    vt->destroy              = NULL;
    vt->get_metrics          = get_m;
}

// ── Tests ─────────────────────────────────────────────────────

static void metrics_init_sets_conservative_priors(void) {
    aethernet_transport_metrics_t m;
    aethernet_transport_metrics_init(&m);
    // Documented priors: 200ms RTT, 5% loss, 0 bps throughput
    assert(m.ewma_rtt_ms    == 200.0);
    assert(m.ewma_loss_rate == 0.05);
    assert(m.ewma_tput_bps  == 0.0);
    assert(m.sample_count   == 0);
}

static void metrics_record_success_increments_sample_count(void) {
    aethernet_transport_metrics_t m;
    aethernet_transport_metrics_init(&m);
    aethernet_transport_metrics_record_sample(&m, 50, true, 1000);
    assert(m.sample_count == 1);
}

static void metrics_record_success_decreases_loss_rate(void) {
    aethernet_transport_metrics_t m;
    aethernet_transport_metrics_init(&m);
    double prior_loss = m.ewma_loss_rate;   // 0.05
    // A successful send: loss_obs = 0.0
    // new_loss = 0.2 * 0.0 + 0.8 * 0.05 = 0.04
    aethernet_transport_metrics_record_sample(&m, 50, true, 500);
    assert(m.ewma_loss_rate < prior_loss);
}

static void metrics_record_failure_increases_loss_rate(void) {
    aethernet_transport_metrics_t m;
    aethernet_transport_metrics_init(&m);
    double prior_loss = m.ewma_loss_rate;   // 0.05
    // A failed send: loss_obs = 1.0
    // new_loss = 0.2 * 1.0 + 0.8 * 0.05 = 0.24
    aethernet_transport_metrics_record_sample(&m, 0, false, 0);
    assert(m.ewma_loss_rate > prior_loss);
}

static void metrics_record_success_bootstraps_throughput(void) {
    aethernet_transport_metrics_t m;
    aethernet_transport_metrics_init(&m);
    assert(m.ewma_tput_bps == 0.0);
    // 1000 bytes in 100ms → 80 000 bps; initial tput < 1 so it gets bootstrapped
    aethernet_transport_metrics_record_sample(&m, 100, true, 1000);
    assert(m.ewma_tput_bps > 0.0);
}

static void metrics_record_rtt_updates_ewma_rtt(void) {
    aethernet_transport_metrics_t m;
    aethernet_transport_metrics_init(&m);
    // Feed a very low RTT sample — EWMA should move toward it
    aethernet_transport_metrics_record_sample(&m, 10, true, 100);
    assert(m.ewma_rtt_ms < 200.0);   // pulled down from prior 200
}

static void composite_score_null_metrics_returns_positive(void) {
    double score = aethernet_transport_metrics_composite_score(NULL, 1000000LL, 10);
    assert(score > 0.0);
}

static void composite_score_higher_bandwidth_is_better(void) {
    aethernet_transport_metrics_t m;
    aethernet_transport_metrics_init(&m);
    // Same metrics, same power, but different declared max bandwidth
    double s_lo = aethernet_transport_metrics_composite_score(&m, 100000LL,  1);
    double s_hi = aethernet_transport_metrics_composite_score(&m, 10000000LL, 1);
    assert(s_hi > s_lo);
}

static void composite_score_lower_power_cost_is_better(void) {
    aethernet_transport_metrics_t m;
    aethernet_transport_metrics_init(&m);
    double s_cheap = aethernet_transport_metrics_composite_score(&m, 1000000LL, 1);
    double s_pricey = aethernet_transport_metrics_composite_score(&m, 1000000LL, 10);
    assert(s_cheap > s_pricey);
}

static void transport_rank_orders_by_score_descending(void) {
    aethernet_transport_metrics_t mA, mB;
    aethernet_transport_metrics_init(&mA);
    aethernet_transport_metrics_init(&mB);
    g_metrics_A = &mA;
    g_metrics_B = &mB;

    // Transport A: 10x more bandwidth → higher composite score.
    fill_vtable(&g_vt_A, 10000000LL, 1, get_metrics_A);
    fill_vtable(&g_vt_B,   100000LL, 1, get_metrics_B);

    aethernet_transport_t tA = { &g_vt_A, NULL };
    aethernet_transport_t tB = { &g_vt_B, NULL };

    aethernet_transport_t *arr[2] = { &tB, &tA };   // B first in input
    aethernet_transport_rank_entry_t ranked[2];
    size_t count = 0;
    aethernet_transport_rank(arr, 2, ranked, &count);
    assert(count == 2);
    // A should be ranked first (higher score)
    assert(ranked[0].transport == &tA);
    assert(ranked[1].transport == &tB);
    assert(ranked[0].score > ranked[1].score);
}

static void transport_rank_null_vtable_transport_skipped(void) {
    aethernet_transport_t bad;
    bad.vtable = NULL;
    bad.handle = NULL;

    aethernet_transport_metrics_t mA;
    aethernet_transport_metrics_init(&mA);
    g_metrics_A = &mA;
    fill_vtable(&g_vt_A, 1000000LL, 1, get_metrics_A);
    aethernet_transport_t good = { &g_vt_A, NULL };

    aethernet_transport_t *arr[2] = { &bad, &good };
    aethernet_transport_rank_entry_t ranked[2];
    size_t count = 0;
    aethernet_transport_rank(arr, 2, ranked, &count);
    // Only the good transport should be ranked
    assert(count == 1);
    assert(ranked[0].transport == &good);
}

// ── main ─────────────────────────────────────────────────────

int main(void) {
    printf("Aether Transport Metrics — Unit Tests\n");
    printf("=====================================\n");

    RUN(metrics_init_sets_conservative_priors);
    RUN(metrics_record_success_increments_sample_count);
    RUN(metrics_record_success_decreases_loss_rate);
    RUN(metrics_record_failure_increases_loss_rate);
    RUN(metrics_record_success_bootstraps_throughput);
    RUN(metrics_record_rtt_updates_ewma_rtt);
    RUN(composite_score_null_metrics_returns_positive);
    RUN(composite_score_higher_bandwidth_is_better);
    RUN(composite_score_lower_power_cost_is_better);
    RUN(transport_rank_orders_by_score_descending);
    RUN(transport_rank_null_vtable_transport_skipped);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
