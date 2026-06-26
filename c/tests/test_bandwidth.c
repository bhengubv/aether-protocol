// SPDX-License-Identifier: MIT
// Unit tests for aethernet_bandwidth.c — W18-5 ABMF C port.
//
// Mirrors the coverage disciplines used in test_uri.c:
//   - assert() for each invariant.
//   - RUN() macro records count and prints OK per test.
//   - Final summary with total count.

#define _POSIX_C_SOURCE 200809L  // clock_gettime/CLOCK_REALTIME under strict -std=c11

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#ifdef _WIN32
#  include <windows.h>
#  define SLEEP_MS(ms) Sleep(ms)
#else
#  include <unistd.h>
#  define SLEEP_MS(ms) usleep((unsigned int)(ms) * 1000u)
#endif

#include "aethernet/aethernet_bandwidth.h"

/* ── Test runner ─────────────────────────────────────────────────────────── */

#define RUN(name) do {                              \
    printf("TEST: " #name "...");                   \
    name();                                         \
    printf(" OK\n");                                \
    tests_run++;                                    \
} while (0)

static int tests_run = 0;

/* ── Helpers ─────────────────────────────────────────────────────────────── */

/* Current wall-clock in microseconds (good enough for test timestamps). */
static int64_t ts_us(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000000LL + (int64_t)ts.tv_nsec / 1000LL;
}

/* ── Tests: probe_ack_compute_derived ────────────────────────────────────── */

/* RTT formula: (sender_receive − sender_send) − (receiver_send − receiver_receive) */
static void probe_ack_rtt_basic(void)
{
    aethernet_bw_probe_ack_t ack = {0};
    ack.sender_send_us       = 1000000;  /* t=1.000000 s */
    ack.receiver_receive_us  = 1000020;  /* +20 µs one-way (approx) */
    ack.receiver_send_us     = 1000025;  /* receiver processing = 5 µs */
    ack.sender_receive_us    = 1000050;  /* t=1.000050 s */
    ack.probe_bytes          = 1200;
    aethernet_bw_probe_ack_compute_derived(&ack);
    /* Expected RTT = (50 - 0) - (25 - 20) = 50 - 5 = 45 µs */
    assert(ack.rtt_us == 45);
    /* Expected forward OWD = 1000020 - 1000000 = 20 µs */
    assert(ack.forward_owd_us == 20);
}

/* RTT with zero receiver processing time. */
static void probe_ack_rtt_zero_processing(void)
{
    aethernet_bw_probe_ack_t ack = {0};
    ack.sender_send_us      = 0;
    ack.receiver_receive_us = 1000;
    ack.receiver_send_us    = 1000;   /* processing = 0 */
    ack.sender_receive_us   = 2000;
    ack.probe_bytes         = 100;
    aethernet_bw_probe_ack_compute_derived(&ack);
    /* RTT = 2000 - 0 - (1000 - 1000) = 2000 */
    assert(ack.rtt_us == 2000);
    assert(ack.forward_owd_us == 1000);
}

/* NULL pointer safety. */
static void probe_ack_null_safe(void)
{
    aethernet_bw_probe_ack_compute_derived(NULL);  /* must not crash */
}

/* ── Tests: estimator_new initial state ─────────────────────────────────── */

static void estimator_new_initial_state(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    assert(e != NULL);
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.confidence == AETHERNET_BW_CONFIDENCE_NONE);
    assert(strcmp(s.transport_name, "BLE") == 0);
    assert(s.loss_rate == 0.0);
    /* The BtlBw window starts EMPTY, but the initial display snapshot reports
     * max_bps as btlbw_bps (matching the C# reference), so btlbw > 0. */
    assert(s.btlbw_bps > 0);
    assert(s.rto_us >= 200000LL);   /* clamped ≥ 200 ms */
    assert(s.rto_us <= 60000000LL); /* clamped ≤ 60 s  */
    aethernet_bw_estimator_free(e);
}

/* NULL transport_name should return NULL. */
static void estimator_new_null_name_returns_null(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new(NULL, 1000000LL);
    assert(e == NULL);
}

/* ── Tests: record_delivery confidence progression ───────────────────────── */

/* After 1 delivery, confidence should be Low. */
static void estimator_delivery_one_round_low(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 100000000LL);
    assert(e != NULL);
    int64_t base = ts_us();
    aethernet_bw_estimator_record_delivery(e, 1200, base, base + 10000);  /* 10 ms one-way */
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.confidence == AETHERNET_BW_CONFIDENCE_LOW);
    aethernet_bw_estimator_free(e);
}

/* After 5 deliveries, confidence should be Medium. */
static void estimator_delivery_five_rounds_medium(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 100000000LL);
    assert(e != NULL);
    int64_t base = ts_us();
    for (int i = 0; i < 5; i++) {
        int64_t send = base + (int64_t)i * 20000;
        aethernet_bw_estimator_record_delivery(e, 1400, send, send + 10000);
    }
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.confidence == AETHERNET_BW_CONFIDENCE_MEDIUM);
    aethernet_bw_estimator_free(e);
}

/* After 20 deliveries, confidence should be High. */
static void estimator_delivery_twenty_rounds_high(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("NearLink", 50000000LL);
    assert(e != NULL);
    int64_t base = ts_us();
    for (int i = 0; i < 20; i++) {
        int64_t send = base + (int64_t)i * 5000;
        aethernet_bw_estimator_record_delivery(e, 1400, send, send + 2000);
    }
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.confidence == AETHERNET_BW_CONFIDENCE_HIGH);
    aethernet_bw_estimator_free(e);
}

/* Delivery with elapsed ≤ 0 should be rejected (no-op). */
static void estimator_delivery_invalid_timestamps_ignored(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    assert(e != NULL);
    int64_t base = ts_us();
    /* deliver_us == send_us → invalid, must not change probe_rounds. */
    aethernet_bw_estimator_record_delivery(e, 1000, base, base);
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.confidence == AETHERNET_BW_CONFIDENCE_NONE);
    aethernet_bw_estimator_free(e);
}

/* ── Tests: record_loss ──────────────────────────────────────────────────── */

/* Recording loss should increase the loss_rate from 0. */
static void estimator_loss_increases_rate(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    assert(e != NULL);
    aethernet_bw_sample_t before, after;
    aethernet_bw_estimator_get_sample(e, &before);
    assert(before.loss_rate == 0.0);

    aethernet_bw_estimator_record_loss(e, 500);
    aethernet_bw_estimator_get_sample(e, &after);
    assert(after.loss_rate > 0.0);
    assert(after.loss_rate <= 1.0);
    aethernet_bw_estimator_free(e);
}

/* Multiple loss events keep rate ≤ 1.0. */
static void estimator_loss_clamped_at_one(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    assert(e != NULL);
    for (int i = 0; i < 100; i++) {
        aethernet_bw_estimator_record_loss(e, 500);
    }
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.loss_rate <= 1.0);
    aethernet_bw_estimator_free(e);
}

/* ── Tests: warm_from_gossip ─────────────────────────────────────────────── */

/* Gossip warms a fresh (None) estimator. */
static void estimator_gossip_seeds_none_estimator(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 0LL);
    assert(e != NULL);
    aethernet_bw_sample_t before;
    aethernet_bw_estimator_get_sample(e, &before);
    assert(before.confidence == AETHERNET_BW_CONFIDENCE_NONE);

    aethernet_bw_estimator_warm_from_gossip(e, 5000000LL, 20000LL, AETHERNET_BW_CONFIDENCE_LOW);
    aethernet_bw_sample_t after;
    aethernet_bw_estimator_get_sample(e, &after);
    /* Should now have Low confidence (warmed, no own probe rounds). */
    assert(after.confidence == AETHERNET_BW_CONFIDENCE_LOW);
    assert(after.btlbw_bps > 0);
    aethernet_bw_estimator_free(e);
}

/* Gossip must NOT downgrade an estimate with existing probe rounds. */
static void estimator_gossip_does_not_downgrade(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 100000000LL);
    assert(e != NULL);

    /* Do 20 probes → High confidence. */
    int64_t base = ts_us();
    for (int i = 0; i < 20; i++) {
        int64_t send = base + (int64_t)i * 1000;
        aethernet_bw_estimator_record_delivery(e, 1400, send, send + 500);
    }
    aethernet_bw_sample_t high;
    aethernet_bw_estimator_get_sample(e, &high);
    assert(high.confidence == AETHERNET_BW_CONFIDENCE_HIGH);

    /* Gossip should have no effect (probe_rounds > 0). */
    aethernet_bw_estimator_warm_from_gossip(e, 100LL, 999000LL, AETHERNET_BW_CONFIDENCE_NONE);
    aethernet_bw_sample_t after;
    aethernet_bw_estimator_get_sample(e, &after);
    assert(after.confidence == AETHERNET_BW_CONFIDENCE_HIGH);
    aethernet_bw_estimator_free(e);
}

/* Gossip called twice must not downgrade (already warmed). */
static void estimator_gossip_idempotent(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 0LL);
    assert(e != NULL);
    aethernet_bw_estimator_warm_from_gossip(e, 1000000LL, 10000LL, AETHERNET_BW_CONFIDENCE_LOW);
    aethernet_bw_estimator_warm_from_gossip(e, 1LL, 999999LL, AETHERNET_BW_CONFIDENCE_NONE);
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    /* Still Low — second gossip was rejected. */
    assert(s.confidence == AETHERNET_BW_CONFIDENCE_LOW);
    aethernet_bw_estimator_free(e);
}

/* ── Tests: apply_phy_hint ───────────────────────────────────────────────── */

/* Very weak signal (-100 dBm) → cap = 40 000 bps. */
static void estimator_phy_hint_weak_signal_cap(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    assert(e != NULL);

    aethernet_bw_estimator_apply_phy_hint(e, -100);
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    /* effective_bps ≤ 40 000 bps. */
    assert(s.effective_bps <= 40000LL);
    assert(s.phy_cap_bps == 40000LL);
    aethernet_bw_estimator_free(e);
}

/* Strong signal (-40 dBm) → cap = 600 Mbps, far above BLE max_bps. */
static void estimator_phy_hint_strong_signal_no_cap(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 50000000LL);
    assert(e != NULL);

    aethernet_bw_estimator_apply_phy_hint(e, -40);
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    /* phy_cap = 600 Mbps; effective = min(50 Mbps, 600 Mbps) = 50 Mbps. */
    assert(s.phy_cap_bps == 600000000LL);
    assert(s.effective_bps == s.btlbw_bps);
    aethernet_bw_estimator_free(e);
}

/* -67 dBm threshold → cap = 200 Mbps. */
static void estimator_phy_hint_mid_signal(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 1000000000LL);
    assert(e != NULL);
    aethernet_bw_estimator_apply_phy_hint(e, -67);
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.phy_cap_bps == 200000000LL);
    aethernet_bw_estimator_free(e);
}

/* ── Tests: record_probe_result ──────────────────────────────────────────── */

/* Valid probe result updates confidence. */
static void estimator_probe_result_updates_confidence(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 100000000LL);
    assert(e != NULL);
    aethernet_bw_probe_ack_t ack = {0};
    ack.sender_send_us      = 0;
    ack.receiver_receive_us = 5000;
    ack.receiver_send_us    = 5001;
    ack.sender_receive_us   = 10002;
    ack.probe_bytes         = 1200;
    aethernet_bw_probe_ack_compute_derived(&ack);  /* rtt_us = 10002 - 0 - 1 = 10001 */

    aethernet_bw_estimator_record_probe_result(e, &ack, ts_us());
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.confidence == AETHERNET_BW_CONFIDENCE_LOW);
    aethernet_bw_estimator_free(e);
}

/* A probe with rtt_us ≤ 0 must be rejected (no confidence change). */
static void estimator_probe_result_invalid_rtt_rejected(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    assert(e != NULL);
    aethernet_bw_probe_ack_t ack = {0};
    /* rtt_us will be 0 — invalid. */
    ack.sender_send_us      = 100;
    ack.receiver_receive_us = 100;
    ack.receiver_send_us    = 100;
    ack.sender_receive_us   = 100;
    aethernet_bw_probe_ack_compute_derived(&ack);
    assert(ack.rtt_us == 0);

    aethernet_bw_estimator_record_probe_result(e, &ack, ts_us());
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.confidence == AETHERNET_BW_CONFIDENCE_NONE);
    aethernet_bw_estimator_free(e);
}

/* ── Tests: RTO clamping ─────────────────────────────────────────────────── */

static void estimator_rto_clamped(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    assert(e != NULL);
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    assert(s.rto_us >= 200000LL);   /* ≥ 200 ms */
    assert(s.rto_us <= 60000000LL); /* ≤ 60 s   */
    aethernet_bw_estimator_free(e);
}

/* ── Tests: node_monitor ─────────────────────────────────────────────────── */

/* Start/stop with no transports must not crash or deadlock. */
static void monitor_start_stop_empty(void)
{
    aethernet_node_monitor_t *m = aethernet_node_monitor_new();
    assert(m != NULL);
    aethernet_node_monitor_start(m, 50);
    SLEEP_MS(60);
    aethernet_node_monitor_stop(m);
    aethernet_node_monitor_free(m);
}

/* Initial snapshot should be Offline with zero rates. */
static void monitor_initial_snapshot_offline(void)
{
    aethernet_node_monitor_t *m = aethernet_node_monitor_new();
    assert(m != NULL);
    aethernet_node_snapshot_t snap;
    aethernet_node_monitor_get_snapshot(m, &snap);
    assert(snap.state == AETHERNET_NODE_OFFLINE);
    assert(snap.ingress_bps == 0);
    assert(snap.egress_bps  == 0);
    assert(!snap.has_activity);
    aethernet_node_monitor_free(m);
}

/* Callback counter for the next test. */
static volatile int g_cb_count = 0;
static void test_cb(const aethernet_node_snapshot_t *snap, void *user)
{
    (void)snap; (void)user;
    g_cb_count++;
}

/* Callback is invoked by the background thread. */
static void monitor_callback_fires(void)
{
    aethernet_node_monitor_t *m = aethernet_node_monitor_new();
    assert(m != NULL);
    g_cb_count = 0;
    aethernet_node_monitor_set_callback(m, test_cb, NULL);
    aethernet_node_monitor_start(m, 30);
    SLEEP_MS(120);  /* ≥ 2 ticks at 30 ms interval */
    aethernet_node_monitor_stop(m);
    assert(g_cb_count >= 2);
    aethernet_node_monitor_free(m);
}

/* Register a transport, record traffic, get a snapshot. */
static void monitor_register_and_snapshot(void)
{
    aethernet_bw_estimator_t  *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    aethernet_node_monitor_t  *m = aethernet_node_monitor_new();
    assert(e && m);

    aethernet_node_monitor_register(m, "BLE", e);
    aethernet_node_monitor_record_ingress(m, "BLE", 1024);
    aethernet_node_monitor_record_egress(m,  "BLE", 512);

    aethernet_node_monitor_start(m, 30);
    SLEEP_MS(80);
    aethernet_node_monitor_stop(m);

    aethernet_node_snapshot_t snap;
    aethernet_node_monitor_get_snapshot(m, &snap);
    /* total_bps should reflect the calculated rates. */
    assert(snap.total_bps >= 0);

    aethernet_node_monitor_free(m);
    aethernet_bw_estimator_free(e);
}

/* Double-stop must not crash. */
static void monitor_double_stop_safe(void)
{
    aethernet_node_monitor_t *m = aethernet_node_monitor_new();
    assert(m != NULL);
    aethernet_node_monitor_start(m, 100);
    SLEEP_MS(20);
    aethernet_node_monitor_stop(m);
    aethernet_node_monitor_stop(m);  /* second stop: no-op */
    aethernet_node_monitor_free(m);
}

/* Free without start must not crash. */
static void monitor_free_without_start(void)
{
    aethernet_node_monitor_t *m = aethernet_node_monitor_new();
    assert(m != NULL);
    aethernet_node_monitor_free(m);
}

/* NULL-safety across all public APIs. */
static void monitor_null_safety(void)
{
    aethernet_node_monitor_get_snapshot(NULL, NULL);
    aethernet_node_monitor_register(NULL, "BLE", NULL);
    aethernet_node_monitor_record_ingress(NULL, "BLE", 100);
    aethernet_node_monitor_record_egress(NULL, "BLE", 100);
    aethernet_node_monitor_record_ingress_peer(NULL, "BLE", "peerA", 100);
    aethernet_node_monitor_record_egress_peer(NULL, "BLE", "peerA", 100);
    aethernet_node_monitor_start(NULL, 100);
    aethernet_node_monitor_stop(NULL);
    aethernet_node_monitor_set_callback(NULL, NULL, NULL);
    aethernet_node_monitor_free(NULL);
    /* Must reach here without crashing. */
}

/* ── Tests: peer-aware active-peer tracking (GAP 2) ───────────────────────── */

/* Two distinct peers with egress → snapshot.active_peers >= 2 after a tick. */
static void monitor_active_peers_two_distinct(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    aethernet_node_monitor_t *m = aethernet_node_monitor_new();
    assert(e && m);

    aethernet_node_monitor_register(m, "BLE", e);
    aethernet_node_monitor_start(m, 30);

    /* Keep stamping both peers across ticks so they stay inside the idle
     * window regardless of tick timing. */
    aethernet_node_snapshot_t snap;
    int seen2 = 0;
    for (int i = 0; i < 10; i++) {
        aethernet_node_monitor_record_egress_peer(m, "BLE", "peer-A", 512);
        aethernet_node_monitor_record_egress_peer(m, "BLE", "peer-B", 512);
        SLEEP_MS(20);
        aethernet_node_monitor_get_snapshot(m, &snap);
        if (snap.active_peers >= 2) { seen2 = 1; break; }
    }
    aethernet_node_monitor_stop(m);
    assert(seen2);

    /* Re-recording the same peer must not inflate the count beyond 2. */
    aethernet_node_monitor_get_snapshot(m, &snap);
    assert(snap.active_peers == 2);

    aethernet_node_monitor_free(m);
    aethernet_bw_estimator_free(e);
}

/* Transport-only egress (no peer) → active_peers stays 0. */
static void monitor_active_peers_no_peer_zero(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("BLE", 2000000LL);
    aethernet_node_monitor_t *m = aethernet_node_monitor_new();
    assert(e && m);

    aethernet_node_monitor_register(m, "BLE", e);
    aethernet_node_monitor_start(m, 30);
    for (int i = 0; i < 4; i++) {
        aethernet_node_monitor_record_egress(m, "BLE", 1024);   /* no peer */
        aethernet_node_monitor_record_ingress(m, "BLE", 1024);  /* no peer */
        SLEEP_MS(20);
    }
    SLEEP_MS(40);
    aethernet_node_monitor_stop(m);

    aethernet_node_snapshot_t snap;
    aethernet_node_monitor_get_snapshot(m, &snap);
    assert(snap.active_peers == 0);

    aethernet_node_monitor_free(m);
    aethernet_bw_estimator_free(e);
}

/* Peer-aware ingress also counts a peer. */
static void monitor_active_peers_ingress_counts(void)
{
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 50000000LL);
    aethernet_node_monitor_t *m = aethernet_node_monitor_new();
    assert(e && m);

    aethernet_node_monitor_register(m, "WiFi", e);
    aethernet_node_monitor_start(m, 30);

    aethernet_node_snapshot_t snap;
    int seen1 = 0;
    for (int i = 0; i < 10; i++) {
        aethernet_node_monitor_record_ingress_peer(m, "WiFi", "peer-X", 4096);
        SLEEP_MS(20);
        aethernet_node_monitor_get_snapshot(m, &snap);
        if (snap.active_peers >= 1) { seen1 = 1; break; }
    }
    aethernet_node_monitor_stop(m);
    assert(seen1);

    aethernet_node_monitor_free(m);
    aethernet_bw_estimator_free(e);
}

/* ── Tests: BandwidthDirector (GAP 1) ─────────────────────────────────────── */

/* new / register / apply_gossip seeds matrix / get_estimate returns it. */
static void director_apply_gossip_seeds_matrix(void)
{
    aethernet_bw_director_t  *d = aethernet_bw_director_new();
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 0LL);
    assert(d && e);
    aethernet_bw_director_register(d, e);

    /* No estimate before gossip. */
    aethernet_bw_sample_t before;
    assert(!aethernet_bw_director_get_estimate(d, "peer-1", "WiFi", &before));

    aethernet_bw_gossip_t g = {0};
    strcpy(g.peer_uhid, "peer-1");
    strcpy(g.transport_name, "WiFi");
    g.btlbw_bps  = 5000000LL;
    g.rtprop_us  = 20000LL;
    g.confidence = AETHERNET_BW_CONFIDENCE_LOW;
    g.measured_at_unix_ms = 0;
    aethernet_bw_director_apply_gossip(d, &g);

    aethernet_bw_sample_t out;
    assert(aethernet_bw_director_get_estimate(d, "peer-1", "WiFi", &out));
    assert(out.btlbw_bps > 0);
    /* Estimator was warmed → confidence Low (warmed, no own probe rounds). */
    assert(out.confidence == AETHERNET_BW_CONFIDENCE_LOW);

    aethernet_bw_director_free(d);
    aethernet_bw_estimator_free(e);
}

/* apply_gossip for an unregistered transport is a no-op (no matrix seed). */
static void director_apply_gossip_unregistered_noop(void)
{
    aethernet_bw_director_t  *d = aethernet_bw_director_new();
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 0LL);
    assert(d && e);
    aethernet_bw_director_register(d, e);

    aethernet_bw_gossip_t g = {0};
    strcpy(g.peer_uhid, "peer-9");
    strcpy(g.transport_name, "NearLink");  /* not registered */
    g.btlbw_bps  = 9000000LL;
    g.rtprop_us  = 5000LL;
    g.confidence = AETHERNET_BW_CONFIDENCE_HIGH;
    aethernet_bw_director_apply_gossip(d, &g);

    aethernet_bw_sample_t out;
    assert(!aethernet_bw_director_get_estimate(d, "peer-9", "NearLink", &out));

    aethernet_bw_director_free(d);
    aethernet_bw_estimator_free(e);
}

/* recommend_transport returns a registered transport when the matrix is populated. */
static void director_recommend_returns_registered(void)
{
    aethernet_bw_director_t  *d  = aethernet_bw_director_new();
    aethernet_bw_estimator_t *eb = aethernet_bw_estimator_new("BLE", 2000000LL);
    aethernet_bw_estimator_t *ew = aethernet_bw_estimator_new("WiFi", 50000000LL);
    assert(d && eb && ew);
    aethernet_bw_director_register(d, eb);
    aethernet_bw_director_register(d, ew);

    /* Seed both transports for peer-7 via gossip. */
    aethernet_bw_gossip_t gb = {0};
    strcpy(gb.peer_uhid, "peer-7"); strcpy(gb.transport_name, "BLE");
    gb.btlbw_bps = 1500000LL; gb.rtprop_us = 30000LL;
    gb.confidence = AETHERNET_BW_CONFIDENCE_LOW;
    aethernet_bw_director_apply_gossip(d, &gb);

    aethernet_bw_gossip_t gw = {0};
    strcpy(gw.peer_uhid, "peer-7"); strcpy(gw.transport_name, "WiFi");
    gw.btlbw_bps = 40000000LL; gw.rtprop_us = 15000LL;
    gw.confidence = AETHERNET_BW_CONFIDENCE_MEDIUM;
    aethernet_bw_director_apply_gossip(d, &gw);

    /* Small payload (<= BDP for both): score = (available/power)*1.5*conf.
     * WiFi has far higher available bandwidth → should win. */
    char name[64] = {0};
    assert(aethernet_bw_director_recommend_transport(d, "peer-7", 100, name, sizeof(name)));
    assert(name[0] != '\0');
    assert(strcmp(name, "BLE") == 0 || strcmp(name, "WiFi") == 0);
    assert(strcmp(name, "WiFi") == 0);  /* higher available_bps wins */

    aethernet_bw_director_free(d);
    aethernet_bw_estimator_free(eb);
    aethernet_bw_estimator_free(ew);
}

/* recommend_transport with no matrix data falls back to lowest-power-cost
 * registered transport (NearLink cost=1 < BLE cost=2). */
static void director_recommend_fallback_lowest_power(void)
{
    aethernet_bw_director_t  *d  = aethernet_bw_director_new();
    aethernet_bw_estimator_t *eb = aethernet_bw_estimator_new("BLE", 2000000LL);
    aethernet_bw_estimator_t *en = aethernet_bw_estimator_new("NearLink", 50000000LL);
    assert(d && eb && en);
    aethernet_bw_director_register(d, eb);
    aethernet_bw_director_register(d, en);

    char name[64] = {0};
    /* peer with no matrix entries → fallback path. */
    assert(aethernet_bw_director_recommend_transport(d, "unknown-peer", 1000, name, sizeof(name)));
    assert(strcmp(name, "NearLink") == 0);

    aethernet_bw_director_free(d);
    aethernet_bw_estimator_free(eb);
    aethernet_bw_estimator_free(en);
}

/* recommend_transport with no registered transports at all → false. */
static void director_recommend_no_transports_false(void)
{
    aethernet_bw_director_t *d = aethernet_bw_director_new();
    assert(d);
    char name[64] = {0};
    assert(!aethernet_bw_director_recommend_transport(d, "peer", 100, name, sizeof(name)));
    aethernet_bw_director_free(d);
}

/* build_gossip returns false at None confidence, true after warmup. */
static void director_build_gossip_none_then_warm(void)
{
    aethernet_bw_director_t  *d = aethernet_bw_director_new();
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 0LL);
    assert(d && e);
    aethernet_bw_director_register(d, e);

    /* Fresh estimator → confidence None → build_gossip false. */
    aethernet_bw_gossip_t out;
    assert(!aethernet_bw_director_build_gossip(d, "peer-2", "WiFi", &out));

    /* Warm the estimator directly → confidence Low. */
    aethernet_bw_estimator_warm_from_gossip(e, 8000000LL, 12000LL,
                                            AETHERNET_BW_CONFIDENCE_LOW);

    assert(aethernet_bw_director_build_gossip(d, "peer-2", "WiFi", &out));
    assert(strcmp(out.peer_uhid, "peer-2") == 0);
    assert(strcmp(out.transport_name, "WiFi") == 0);
    assert(out.btlbw_bps > 0);
    assert(out.confidence == AETHERNET_BW_CONFIDENCE_LOW);

    aethernet_bw_director_free(d);
    aethernet_bw_estimator_free(e);
}

/* build_gossip for an unregistered transport returns false. */
static void director_build_gossip_unregistered_false(void)
{
    aethernet_bw_director_t  *d = aethernet_bw_director_new();
    aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("WiFi", 0LL);
    assert(d && e);
    aethernet_bw_director_register(d, e);
    aethernet_bw_estimator_warm_from_gossip(e, 8000000LL, 12000LL,
                                            AETHERNET_BW_CONFIDENCE_LOW);

    aethernet_bw_gossip_t out;
    assert(!aethernet_bw_director_build_gossip(d, "peer-2", "CircleLink", &out));

    aethernet_bw_director_free(d);
    aethernet_bw_estimator_free(e);
}

/* Round-trip: build_gossip on node A, apply_gossip on node B, B can estimate. */
static void director_gossip_round_trip(void)
{
    /* Node A. */
    aethernet_bw_director_t  *dA = aethernet_bw_director_new();
    aethernet_bw_estimator_t *eA = aethernet_bw_estimator_new("WiFi", 0LL);
    aethernet_bw_director_register(dA, eA);
    aethernet_bw_estimator_warm_from_gossip(eA, 6000000LL, 18000LL,
                                            AETHERNET_BW_CONFIDENCE_LOW);

    aethernet_bw_gossip_t payload;
    assert(aethernet_bw_director_build_gossip(dA, "node-B", "WiFi", &payload));

    /* Node B applies A's gossip. */
    aethernet_bw_director_t  *dB = aethernet_bw_director_new();
    aethernet_bw_estimator_t *eB = aethernet_bw_estimator_new("WiFi", 0LL);
    aethernet_bw_director_register(dB, eB);
    aethernet_bw_director_apply_gossip(dB, &payload);

    aethernet_bw_sample_t out;
    assert(aethernet_bw_director_get_estimate(dB, "node-B", "WiFi", &out));
    assert(out.btlbw_bps > 0);

    aethernet_bw_director_free(dA);
    aethernet_bw_director_free(dB);
    aethernet_bw_estimator_free(eA);
    aethernet_bw_estimator_free(eB);
}

/* NULL-safety across director APIs. */
static void director_null_safety(void)
{
    aethernet_bw_sample_t s;
    aethernet_bw_gossip_t g;
    char name[16];
    assert(!aethernet_bw_director_get_estimate(NULL, "p", "t", &s));
    assert(!aethernet_bw_director_recommend_transport(NULL, "p", 1, name, sizeof(name)));
    assert(!aethernet_bw_director_build_gossip(NULL, "p", "t", &g));
    aethernet_bw_director_register(NULL, NULL);
    aethernet_bw_director_apply_gossip(NULL, NULL);
    aethernet_bw_director_free(NULL);
    /* Must reach here without crashing. */
}

/* ── Main ─────────────────────────────────────────────────────────────────── */

int main(void)
{
    printf("=== ABMF Bandwidth tests ===\n");

    /* probe_ack */
    RUN(probe_ack_rtt_basic);
    RUN(probe_ack_rtt_zero_processing);
    RUN(probe_ack_null_safe);

    /* estimator initial state */
    RUN(estimator_new_initial_state);
    RUN(estimator_new_null_name_returns_null);

    /* record_delivery confidence */
    RUN(estimator_delivery_one_round_low);
    RUN(estimator_delivery_five_rounds_medium);
    RUN(estimator_delivery_twenty_rounds_high);
    RUN(estimator_delivery_invalid_timestamps_ignored);

    /* record_loss */
    RUN(estimator_loss_increases_rate);
    RUN(estimator_loss_clamped_at_one);

    /* warm_from_gossip */
    RUN(estimator_gossip_seeds_none_estimator);
    RUN(estimator_gossip_does_not_downgrade);
    RUN(estimator_gossip_idempotent);

    /* apply_phy_hint */
    RUN(estimator_phy_hint_weak_signal_cap);
    RUN(estimator_phy_hint_strong_signal_no_cap);
    RUN(estimator_phy_hint_mid_signal);

    /* record_probe_result */
    RUN(estimator_probe_result_updates_confidence);
    RUN(estimator_probe_result_invalid_rtt_rejected);

    /* RTO clamping */
    RUN(estimator_rto_clamped);

    /* node monitor */
    RUN(monitor_start_stop_empty);
    RUN(monitor_initial_snapshot_offline);
    RUN(monitor_callback_fires);
    RUN(monitor_register_and_snapshot);
    RUN(monitor_double_stop_safe);
    RUN(monitor_free_without_start);
    RUN(monitor_null_safety);

    /* peer-aware active-peer tracking (GAP 2) */
    RUN(monitor_active_peers_two_distinct);
    RUN(monitor_active_peers_no_peer_zero);
    RUN(monitor_active_peers_ingress_counts);

    /* BandwidthDirector (GAP 1) */
    RUN(director_apply_gossip_seeds_matrix);
    RUN(director_apply_gossip_unregistered_noop);
    RUN(director_recommend_returns_registered);
    RUN(director_recommend_fallback_lowest_power);
    RUN(director_recommend_no_transports_false);
    RUN(director_build_gossip_none_then_warm);
    RUN(director_build_gossip_unregistered_false);
    RUN(director_gossip_round_trip);
    RUN(director_null_safety);

    printf("\n=== %d tests passed ===\n", tests_run);
    return 0;
}
