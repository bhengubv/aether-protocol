// SPDX-License-Identifier: MIT
// Unit tests for aethernet_anomaly.c (BehavioralAnomalyDetector).

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "aethernet_reputation.h"
#include "aethernet_anomaly.h"

// ─── Test runner ─────────────────────────────────────────────────────────────

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// ─── Shared helper: build options with small thresholds ──────────────────────

static AetherNetAnomalyOptions test_opts(void)
{
    AetherNetAnomalyOptions o;
    o.volume_window_ms        = 100;   /* small window for easy testing      */
    o.volume_spike_multiplier = 2.0;   /* fires when count > 2 × ewma        */
    o.ewma_alpha              = 0.20;
    o.scatter_window_ms       = 60000;
    o.scatter_threshold       = 3;     /* fire when > 3 unique dests         */
    o.geohash_prefix_length   = 4;
    o.geohash_rate_limit_ms   = 0;     /* every mismatch fires (overridden per test) */
    return o;
}

// ─── 1. volume_no_spike_first_window ─────────────────────────────────────────
// The first completed window only seeds the EWMA baseline; it never fires.

static void volume_no_spike_first_window(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    /* Send 20 packets from "src-a" within window[0] (t=0..99). */
    for (int i = 0; i < 20; i++) {
        aethernet_anomaly_observe_packet(det, "src-a", "dst-x", (int64_t)i);
    }
    /* Roll into window[1]: timestamp 100 triggers the roll. */
    aethernet_anomaly_observe_packet(det, "src-a", "dst-x", 100);

    /* Score must be 1.0 — first window only seeds EWMA, no flood fired. */
    double score = aethernet_reputation_get_score(&rep, "src-a");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 2. volume_spike_fires ────────────────────────────────────────────────────
// Window 0: 5 packets → seeds EWMA = 5.
// Window 1: 11 packets > 2.0 × 5 = 10 → fires rreq_flood (score −0.05).

static void volume_spike_fires(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    /* Window 0: t=0..4 → 5 packets (window_start = 0, window_count = 5). */
    for (int i = 0; i < 5; i++) {
        aethernet_anomaly_observe_packet(det, "src-b", "dst-y", (int64_t)i);
    }

    /* t=100 rolls window 0 → EWMA = 5.  window 1 starts, window_count = 1. */
    aethernet_anomaly_observe_packet(det, "src-b", "dst-y", 100);

    /* Window 1: add 10 more packets at t=101..110 → total window_count = 11. */
    for (int i = 1; i <= 10; i++) {
        aethernet_anomaly_observe_packet(det, "src-b", "dst-y", (int64_t)(100 + i));
    }

    /* t=200 rolls window 1 → count=11 > 2.0×5=10 → fires rreq_flood. */
    aethernet_anomaly_observe_packet(det, "src-b", "dst-y", 200);

    double score = aethernet_reputation_get_score(&rep, "src-b");
    /* rreq_flood: −0.05 → 0.95 */
    assert(fabs(score - 0.95) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 3. volume_no_spike_same_window ──────────────────────────────────────────
// Many packets within the SAME window never trigger a spike.

static void volume_no_spike_same_window(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    /* 99 packets all within t=0..99 (volume_window_ms=100). */
    for (int i = 0; i < 99; i++) {
        aethernet_anomaly_observe_packet(det, "src-c", "dst-z", (int64_t)i);
    }

    double score = aethernet_reputation_get_score(&rep, "src-c");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 4. scatter_below_threshold ──────────────────────────────────────────────
// scatter_threshold=3 → exactly 3 unique dests does NOT fire.

static void scatter_below_threshold(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    /* 3 unique destinations — at threshold, not over it. */
    aethernet_anomaly_observe_packet(det, "src-d", "d1", 1000);
    aethernet_anomaly_observe_packet(det, "src-d", "d2", 1001);
    aethernet_anomaly_observe_packet(det, "src-d", "d3", 1002);

    double score = aethernet_reputation_get_score(&rep, "src-d");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 5. scatter_at_threshold ─────────────────────────────────────────────────
// 4 unique dests > scatter_threshold(3) → fires rreq_flood.

static void scatter_at_threshold(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    aethernet_anomaly_observe_packet(det, "src-e", "e1", 2000);
    aethernet_anomaly_observe_packet(det, "src-e", "e2", 2001);
    aethernet_anomaly_observe_packet(det, "src-e", "e3", 2002);
    /* 4th unique dest → unique count = 4 > 3 → fires. */
    aethernet_anomaly_observe_packet(det, "src-e", "e4", 2003);

    double score = aethernet_reputation_get_score(&rep, "src-e");
    /* rreq_flood: −0.05 → 0.95. */
    assert(fabs(score - 0.95) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 6. scatter_old_entries_pruned ───────────────────────────────────────────
// Entries outside scatter_window_ms are not counted as unique.

static void scatter_old_entries_pruned(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    /* Use a tiny scatter window so we can expire entries easily. */
    AetherNetAnomalyOptions o = test_opts();
    o.scatter_window_ms = 5000; /* 5 seconds */

    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    /* t=0: 3 unique dests in the past (will be old). */
    aethernet_anomaly_observe_packet(det, "src-f", "f1", 0);
    aethernet_anomaly_observe_packet(det, "src-f", "f2", 1);
    aethernet_anomaly_observe_packet(det, "src-f", "f3", 2);

    /* t=6000: well outside the 5 s window.
       Only 1 new unique dest — total live unique = 1 → no fire. */
    aethernet_anomaly_observe_packet(det, "src-f", "f4", 6000);

    double score = aethernet_reputation_get_score(&rep, "src-f");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 7. geohash_match_no_fire ────────────────────────────────────────────────
// When claimed and observed routing prefixes match, no signal is fired.

static void geohash_match_no_fire(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    aethernet_anomaly_observe_geohash_claim(det, "node-g",
                                          "abcd1234",  /* claimed   */
                                          "abcd5678"); /* observed  */
    /* Prefixes "abcd" == "abcd" → no fire. */
    double score = aethernet_reputation_get_score(&rep, "node-g");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 8. geohash_mismatch_fires ───────────────────────────────────────────────
// Different prefix → fires sig_failure (−0.20).

static void geohash_mismatch_fires(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    o.geohash_rate_limit_ms = 0; /* every mismatch fires */
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    aethernet_anomaly_observe_geohash_claim(det, "node-h",
                                          "xyzw1234",  /* claimed   */
                                          "abcd5678"); /* observed  */
    /* "xyzw" != "abcd" → sig_failure: 1.0 − 0.20 = 0.80. */
    double score = aethernet_reputation_get_score(&rep, "node-h");
    assert(fabs(score - 0.80) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 9. geohash_rate_limit ───────────────────────────────────────────────────
// With rate_limit_ms > 0 the second mismatch within the window does NOT fire.

static void geohash_rate_limit(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    o.geohash_rate_limit_ms = 60000; /* 60 s rate limit */
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    /* First mismatch → fires. */
    aethernet_anomaly_observe_geohash_claim(det, "node-i",
                                          "xyzw1234",
                                          "abcd5678");

    /* Second mismatch within rate-limit window → does NOT fire. */
    aethernet_anomaly_observe_geohash_claim(det, "node-i",
                                          "xyzw1234",
                                          "abcd5678");

    double score = aethernet_reputation_get_score(&rep, "node-i");
    /* Only one sig_failure fired: 1.0 − 0.20 = 0.80. */
    assert(fabs(score - 0.80) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── 10. spk_sig_failure_passthrough ─────────────────────────────────────────
// observe_spk_sig_failure is a direct passthrough to record_sig_failure.

static void spk_sig_failure_passthrough(void)
{
    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetAnomalyOptions o = test_opts();
    AetherNetBehavioralAnomalyDetector *det = aethernet_anomaly_create(&rep, &o);
    assert(det != NULL);

    aethernet_anomaly_observe_spk_sig_failure(det, "node-j");

    double score = aethernet_reputation_get_score(&rep, "node-j");
    /* sig_failure: 1.0 − 0.20 = 0.80. */
    assert(fabs(score - 0.80) < 1e-9);

    aethernet_anomaly_destroy(det);
}

// ─── main ─────────────────────────────────────────────────────────────────────

int main(void)
{
    printf("Aether BehavioralAnomalyDetector — Unit Tests\n");
    printf("===============================================\n");

    RUN(volume_no_spike_first_window);
    RUN(volume_spike_fires);
    RUN(volume_no_spike_same_window);
    RUN(scatter_below_threshold);
    RUN(scatter_at_threshold);
    RUN(scatter_old_entries_pruned);
    RUN(geohash_match_no_fire);
    RUN(geohash_mismatch_fires);
    RUN(geohash_rate_limit);
    RUN(spk_sig_failure_passthrough);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
