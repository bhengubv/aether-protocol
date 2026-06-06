// SPDX-License-Identifier: MIT
// Unit tests for predictive_selector.c — 2-state Kalman RTT filter and scoring.

#include <assert.h>
#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "aethernet/transport.h"
#include "aethernet/predictive_selector.h"

// ── RUN macro ─────────────────────────────────────────────────────────────────

static int tests_run = 0;
#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)

// ── Fake transport helpers ────────────────────────────────────────────────────

static bool fake_send(void *h, const char *p, const uint8_t *d, size_t n)
{
    (void)h; (void)p; (void)d; (void)n;
    return true;
}

/** Make an available transport (send != NULL). */
static void make_transport(
    aethernet_transport_vtable_t *vtable,
    aethernet_transport_t        *transport,
    const char                *name,
    int64_t                    bps,
    int32_t                    power,
    bool                       available)
{
    memset(vtable, 0, sizeof(*vtable));
    vtable->name                 = name;
    vtable->max_bandwidth_bps    = bps;
    vtable->power_cost_relative  = power;
    vtable->max_range_meters     = 100;
    vtable->send                 = available ? fake_send : NULL;
    vtable->get_metrics          = NULL;  /* rank uses default priors */

    transport->vtable = vtable;
    transport->handle = NULL;
}

// ── Kalman filter tests (via selector observe + kalman_state) ─────────────────

static void kalman_converges_on_steady_state(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    assert(aethernet_predictive_selector_register(&sel, &t, 200.0));

    for (int i = 0; i < 50; i++)
        aethernet_predictive_selector_observe(&sel, &t, 100, true, 1000);

    double rtt, drift, variance;
    assert(aethernet_predictive_selector_kalman_state(&sel, &t, &rtt, &drift, &variance));
    /* After 50 identical observations the estimate must be within 5 ms of 100. */
    assert(fabs(rtt - 100.0) < 5.0);
}

static void kalman_variance_decreases_with_observations(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    assert(aethernet_predictive_selector_register(&sel, &t, 200.0));

    /* Record initial variance (before any observations). */
    double rtt0, drift0, var0;
    assert(aethernet_predictive_selector_kalman_state(&sel, &t, &rtt0, &drift0, &var0));

    for (int i = 0; i < 10; i++)
        aethernet_predictive_selector_observe(&sel, &t, 200, true, 1000);

    double rtt1, drift1, var1;
    assert(aethernet_predictive_selector_kalman_state(&sel, &t, &rtt1, &drift1, &var1));
    /* Posterior variance must be strictly smaller than the prior. */
    assert(var1 < var0);
}

static void kalman_detects_positive_drift_for_rising_rtt(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    assert(aethernet_predictive_selector_register(&sel, &t, 100.0));

    for (int i = 0; i < 10; i++)
        aethernet_predictive_selector_observe(&sel, &t, (uint64_t)(100 + (i + 1) * 15), true, 1000);

    double rtt, drift, variance;
    assert(aethernet_predictive_selector_kalman_state(&sel, &t, &rtt, &drift, &variance));
    /* Rising RTT must produce positive drift estimate. */
    assert(drift > 0.0);
}

// ── Selector lifecycle tests ──────────────────────────────────────────────────

static void register_and_rank_fast_transport_first(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt_fast, vt_slow;
    aethernet_transport_t t_fast, t_slow;
    make_transport(&vt_fast, &t_fast, "fast", 1000000L, 1, true);
    make_transport(&vt_slow, &t_slow, "slow", 10000L,   10, true);

    assert(aethernet_predictive_selector_register(&sel, &t_fast, 50.0));
    assert(aethernet_predictive_selector_register(&sel, &t_slow, 150.0));

    /* Feed a few good observations to the fast transport to confirm ranking. */
    for (int i = 0; i < 5; i++)
        aethernet_predictive_selector_observe(&sel, &t_fast, 50, true, 1000);

    aethernet_predictive_rank_entry_t ranked[AETHERNET_PREDICTIVE_MAX_TRANSPORTS];
    size_t count = 0;
    aethernet_predictive_selector_rank(&sel, 100, ranked, &count);

    assert(count == 2);
    assert(ranked[0].transport == &t_fast);
    assert(ranked[0].score > 0.0);
}

static void unavailable_transport_excluded_from_rank(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt_avail, vt_unavail;
    aethernet_transport_t t_avail, t_unavail;
    make_transport(&vt_avail,   &t_avail,   "avail",   500000L, 1, true);
    make_transport(&vt_unavail, &t_unavail, "unavail", 500000L, 1, false);

    assert(aethernet_predictive_selector_register(&sel, &t_avail,   100.0));
    assert(aethernet_predictive_selector_register(&sel, &t_unavail, 100.0));

    aethernet_predictive_rank_entry_t ranked[AETHERNET_PREDICTIVE_MAX_TRANSPORTS];
    size_t count = 0;
    aethernet_predictive_selector_rank(&sel, 0, ranked, &count);

    assert(count == 1);
    assert(ranked[0].transport == &t_avail);
}

static void unregister_removes_transport(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    assert(aethernet_predictive_selector_register(&sel, &t, 100.0));

    aethernet_predictive_selector_unregister(&sel, &t);

    aethernet_predictive_rank_entry_t ranked[AETHERNET_PREDICTIVE_MAX_TRANSPORTS];
    size_t count = 0;
    aethernet_predictive_selector_rank(&sel, 0, ranked, &count);
    assert(count == 0);
}

static void select_best_returns_null_when_empty(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);
    assert(aethernet_predictive_selector_best(&sel, 100) == NULL);
}

static void duplicate_register_is_noop(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    assert(aethernet_predictive_selector_register(&sel, &t, 100.0));
    assert(!aethernet_predictive_selector_register(&sel, &t, 200.0)); /* duplicate: false */

    aethernet_predictive_rank_entry_t ranked[AETHERNET_PREDICTIVE_MAX_TRANSPORTS];
    size_t count = 0;
    aethernet_predictive_selector_rank(&sel, 0, ranked, &count);
    assert(count == 1);
}

static void kalman_state_initial_values_are_correct(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    assert(aethernet_predictive_selector_register(&sel, &t, 123.0));

    double rtt, drift, variance;
    assert(aethernet_predictive_selector_kalman_state(&sel, &t, &rtt, &drift, &variance));
    assert(fabs(rtt   - 123.0) < 1e-9);
    assert(fabs(drift - 0.0)   < 1e-9);
    assert(variance > 0.0);
}

static void kalman_state_unregistered_transport_returns_false(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    /* Never register — state query must return false. */

    double rtt, drift, variance;
    assert(!aethernet_predictive_selector_kalman_state(&sel, &t, &rtt, &drift, &variance));
}

static void rank_returns_positive_score(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    assert(aethernet_predictive_selector_register(&sel, &t, 100.0));

    aethernet_predictive_rank_entry_t ranked[AETHERNET_PREDICTIVE_MAX_TRANSPORTS];
    size_t count = 0;
    aethernet_predictive_selector_rank(&sel, 0, ranked, &count);
    assert(count == 1);
    assert(ranked[0].score > 0.0);
}

static void score_improves_after_good_observations(void)
{
    aethernet_predictive_selector_t sel;
    aethernet_predictive_selector_init(&sel);

    aethernet_transport_vtable_t vt; aethernet_transport_t t;
    make_transport(&vt, &t, "t", 500000, 1, true);
    assert(aethernet_predictive_selector_register(&sel, &t, 200.0));

    aethernet_predictive_rank_entry_t ranked[AETHERNET_PREDICTIVE_MAX_TRANSPORTS];
    size_t count = 0;
    aethernet_predictive_selector_rank(&sel, 0, ranked, &count);
    assert(count == 1);
    double score_before = ranked[0].score;

    for (int i = 0; i < 10; i++)
        aethernet_predictive_selector_observe(&sel, &t, 20, true, 5000);

    aethernet_predictive_selector_rank(&sel, 0, ranked, &count);
    assert(count == 1);
    double score_after = ranked[0].score;

    assert(score_after > score_before);
}

// ── main ──────────────────────────────────────────────────────────────────────

int main(void)
{
    printf("Aether Predictive Selector — Unit Tests\n");
    printf("========================================\n");

    RUN(kalman_converges_on_steady_state);
    RUN(kalman_variance_decreases_with_observations);
    RUN(kalman_detects_positive_drift_for_rising_rtt);
    RUN(register_and_rank_fast_transport_first);
    RUN(unavailable_transport_excluded_from_rank);
    RUN(unregister_removes_transport);
    RUN(select_best_returns_null_when_empty);
    RUN(duplicate_register_is_noop);
    RUN(kalman_state_initial_values_are_correct);
    RUN(kalman_state_unregistered_transport_returns_false);
    RUN(rank_returns_positive_score);
    RUN(score_improves_after_good_observations);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
