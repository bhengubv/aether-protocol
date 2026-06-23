// SPDX-License-Identifier: MIT
// Unit tests for aethernet_gossip.c (ReputationGossipService).
//
// Fake callbacks keep the tests self-contained: no real crypto, no network.
// sign_packet copies the input unchanged (identity transform).
// verify_packet returns a value controlled per-test via g_verify_ok.
// broadcast records the last packet and increments a counter.

#ifdef _MSC_VER
#  define _CRT_SECURE_NO_WARNINGS
#endif

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <stdbool.h>
#include <time.h>

#include "aethernet_reputation.h"
#include "aethernet_gossip.h"

// ─── Test runner ─────────────────────────────────────────────────────────────

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// Real wall-clock milliseconds — identical to the production get_now_ms() in
// aethernet_gossip.c. Acceptance tests timestamp packets at test_now_ms() so the
// freshness check runs against the REAL clock. If get_now_ms() ever regressed to
// a constant (the old "return 0" stub), a fresh packet would read as ~55 years
// stale and handle_clock_is_real_not_zero would fail — the stub can't come back.
static long long test_now_ms(void)
{
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

// ─── Fake callback state ──────────────────────────────────────────────────────

static int    g_broadcast_count = 0;
static char   g_last_packet[4096];
static bool   g_verify_ok = true;      /* set false to simulate bad sig        */

static int fake_broadcast(const char *json_packet, void *ctx)
{
    (void)ctx;
    g_broadcast_count++;
    strncpy(g_last_packet, json_packet, sizeof(g_last_packet) - 1);
    g_last_packet[sizeof(g_last_packet) - 1] = '\0';
    /* Pretend we delivered to 3 peers. */
    return 3;
}

/* Identity sign — copies input to output unchanged. */
static bool fake_sign(const char *json_in, char *json_out, size_t out_len, void *ctx)
{
    (void)ctx;
    strncpy(json_out, json_in, out_len - 1);
    json_out[out_len - 1] = '\0';
    return true;
}

static bool fake_verify(const char *json_packet,
                        const uint8_t *sender_pub_key, size_t key_len,
                        void *ctx)
{
    (void)json_packet;
    (void)sender_pub_key;
    (void)key_len;
    (void)ctx;
    return g_verify_ok;
}

/* Build a standard callbacks struct for local_uhid "local-node". */
static AetherNetGossipCallbacks make_callbacks(const char *local_uhid)
{
    AetherNetGossipCallbacks cb;
    memset(&cb, 0, sizeof(cb));
    cb.local_uhid    = local_uhid;
    cb.broadcast     = fake_broadcast;
    cb.broadcast_ctx = NULL;
    cb.sign_packet   = fake_sign;
    cb.sign_ctx      = NULL;
    cb.verify_packet = fake_verify;
    cb.verify_ctx    = NULL;
    return cb;
}

/* Reset per-test globals. */
static void reset_globals(void)
{
    g_broadcast_count = 0;
    memset(g_last_packet, 0, sizeof(g_last_packet));
    g_verify_ok = true;
}

// ─── Helper: build a gossip packet JSON string manually ───────────────────────
// Used to inject inbound packets into aethernet_gossip_handle. The caller passes
// an explicit timestamp_ms: acceptance tests pass test_now_ms() (fresh against the
// real clock); the staleness test passes a value older than the freshness window.

static void build_packet(char *buf, size_t buf_len,
                          const char *reporter, const char *target,
                          double delta, long long timestamp_ms)
{
    /* Inner payload */
    char payload[512];
    snprintf(payload, sizeof(payload),
        "{\"reporter_uhid\":\"%s\",\"target_uhid\":\"%s\","
        "\"score_delta\":%.6f,\"timestamp_ms\":%lld,\"reason\":\"test\"}",
        reporter, target, delta, timestamp_ms);

    /* Outer envelope */
    snprintf(buf, buf_len,
        "{\"type\":52,\"source_uhid\":\"%s\",\"destination_uhid\":\"*\","
        "\"ttl\":3,\"payload\":%s,\"timestamp_ms\":%lld}",
        reporter, payload, timestamp_ms);
}

// ─── 1. broadcast_returns_delivered_count ────────────────────────────────────

static void broadcast_returns_delivered_count(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    int delivered = aethernet_gossip_broadcast(svc, "peer-a", -0.10, "bad-behaviour");
    /* fake_broadcast always returns 3 */
    assert(delivered == 3);
    assert(g_broadcast_count == 1);

    aethernet_gossip_destroy(svc);
}

// ─── 2. broadcast_payload_has_correct_fields ─────────────────────────────────

static void broadcast_payload_has_correct_fields(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    aethernet_gossip_broadcast(svc, "peer-b", -0.05, "flood");

    /* The signed (identity-transformed) packet must contain both UHIDs. */
    assert(strstr(g_last_packet, "\"reporter_uhid\":\"local-node\"") != NULL);
    assert(strstr(g_last_packet, "\"target_uhid\":\"peer-b\"") != NULL);
    assert(strstr(g_last_packet, "\"type\":52") != NULL);

    aethernet_gossip_destroy(svc);
}

// ─── 3. broadcast_clamps_delta_above_1 ───────────────────────────────────────

static void broadcast_clamps_delta_above_1(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* Pass a delta of +5.0 — should be clamped to 1.0 in the packet. */
    aethernet_gossip_broadcast(svc, "peer-c", 5.0, "bonus");

    /* The JSON should contain score_delta:1.000000 (or close). */
    assert(strstr(g_last_packet, "\"score_delta\":1.000000") != NULL);

    aethernet_gossip_destroy(svc);
}

// ─── 4. broadcast_clamps_delta_below_minus_1 ─────────────────────────────────

static void broadcast_clamps_delta_below_minus_1(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* Pass a delta of -9.9 — should be clamped to -1.0. */
    aethernet_gossip_broadcast(svc, "peer-d", -9.9, "punish");

    assert(strstr(g_last_packet, "\"score_delta\":-1.000000") != NULL);

    aethernet_gossip_destroy(svc);
}

// ─── 5. handle_wrong_type_returns_false ──────────────────────────────────────

static void handle_wrong_type_returns_false(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* Build a packet with type 99 (not 52). */
    char pkt[2048];
    snprintf(pkt, sizeof(pkt),
        "{\"type\":99,\"source_uhid\":\"remote\",\"destination_uhid\":\"*\","
        "\"ttl\":3,\"payload\":"
        "{\"reporter_uhid\":\"remote\",\"target_uhid\":\"victim\","
        "\"score_delta\":-0.10,\"timestamp_ms\":0,\"reason\":\"test\"},"
        "\"timestamp_ms\":0}");

    bool accepted = aethernet_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == false);

    /* Victim's score must be untouched (1.0). */
    double score = aethernet_reputation_get_score(&rep, "victim");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_gossip_destroy(svc);
}

// ─── 6. handle_invalid_signature_returns_false ───────────────────────────────

static void handle_invalid_signature_returns_false(void)
{
    reset_globals();
    g_verify_ok = false;   /* make verify always fail */

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "remote-a", "target-a", -0.20, 0LL);

    bool accepted = aethernet_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == false);

    /* Score must be untouched. */
    double score = aethernet_reputation_get_score(&rep, "target-a");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_gossip_destroy(svc);
}

// ─── 7. handle_stale_timestamp_returns_false ─────────────────────────────────
// FRESHNESS_WINDOW_MS = 300000 ms (5 min). A packet timestamped more than 5 min
// before the real clock is stale and must be rejected.

static void handle_stale_timestamp_returns_false(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* A timestamp older than the freshness window, relative to the real clock. */
    long long stale_ts = test_now_ms() - ((long long)AETHERNET_GOSSIP_FRESHNESS_MS + 60000LL);
    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "remote-b", "target-b", -0.10, stale_ts);

    bool accepted = aethernet_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == false);

    double score = aethernet_reputation_get_score(&rep, "target-b");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_gossip_destroy(svc);
}

// ─── 8. handle_own_gossip_returns_false ──────────────────────────────────────
// A packet whose reporter_uhid matches local_uhid must be silently dropped.

static void handle_own_gossip_returns_false(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* reporter = local-node (same as local_uhid). Fresh timestamp so the
       rejection is genuinely the own-gossip guard, not staleness (freshness is
       checked first in the handler). */
    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "local-node", "target-c", -0.10, test_now_ms());

    bool accepted = aethernet_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == false);

    double score = aethernet_reputation_get_score(&rep, "target-c");
    assert(fabs(score - 1.0) < 1e-9);

    aethernet_gossip_destroy(svc);
}

// ─── 9. handle_unknown_reporter_full_delta ───────────────────────────────────
// Unknown reporter → R = 1.0.  effective = 1.0 × delta = delta.
// score_delta = -0.20, reporter unknown → target score = 1.0 + (-0.20 × 1.0) = 0.80.

static void handle_unknown_reporter_full_delta(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "unknown-reporter", "target-d", -0.20, test_now_ms());

    bool accepted = aethernet_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == true);

    /* R = 1.0 (unknown), effective = -0.20 × 1.0 = -0.20 → 1.0 - 0.20 = 0.80 */
    double score = aethernet_reputation_get_score(&rep, "target-d");
    assert(fabs(score - 0.80) < 1e-9);

    aethernet_gossip_destroy(svc);
}

// ─── 10. handle_degraded_reporter_weighted_delta ─────────────────────────────
// Reporter has R = 0.5 → effective = delta × 0.5.
// score_delta = -0.20, R = 0.5 → effective = -0.10 → target score = 0.90.

static void handle_degraded_reporter_weighted_delta(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    /* Degrade the reporter's score to 0.5 (5 × -0.10 from 1.0). */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.95 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.90 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.85 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.80 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.75 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.70 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.65 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.60 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.55 */
    aethernet_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.50 */

    double reporter_score = aethernet_reputation_get_score(&rep, "degraded-reporter");
    assert(fabs(reporter_score - 0.50) < 1e-9);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* delta = -0.20, R = 0.50 → effective = -0.10 → target = 1.0 - 0.10 = 0.90 */
    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "degraded-reporter", "target-e", -0.20, test_now_ms());

    bool accepted = aethernet_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == true);

    double score = aethernet_reputation_get_score(&rep, "target-e");
    assert(fabs(score - 0.90) < 1e-9);

    aethernet_gossip_destroy(svc);
}

// ─── 11. handle_clock_is_real_not_zero (regression guard) ────────────────────
// A valid packet timestamped at the real "now" must be ACCEPTED; the same packet
// dated one hour ago must be REJECTED. If get_now_ms() ever reverts to a constant
// stub (the old "return 0"), the fresh packet reads as decades stale and this
// test fails — so the clock stub can never silently return.

static void handle_clock_is_real_not_zero(void)
{
    reset_globals();

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);

    AetherNetGossipCallbacks cb = make_callbacks("local-node");
    AetherNetGossipService *svc = aethernet_gossip_create(&rep, &cb);
    assert(svc != NULL);

    char fresh_pkt[2048];
    build_packet(fresh_pkt, sizeof(fresh_pkt), "rep-now", "tgt-now", -0.10, test_now_ms());
    assert(aethernet_gossip_handle(svc, fresh_pkt, NULL, 0) == true);

    char old_pkt[2048];
    build_packet(old_pkt, sizeof(old_pkt), "rep-old", "tgt-old", -0.10,
                 test_now_ms() - 3600000LL); /* 1 hour ago → stale */
    assert(aethernet_gossip_handle(svc, old_pkt, NULL, 0) == false);

    aethernet_gossip_destroy(svc);
}

// ─── main ─────────────────────────────────────────────────────────────────────

int main(void)
{
    printf("Aether ReputationGossipService — Unit Tests\n");
    printf("============================================\n");

    RUN(broadcast_returns_delivered_count);
    RUN(broadcast_payload_has_correct_fields);
    RUN(broadcast_clamps_delta_above_1);
    RUN(broadcast_clamps_delta_below_minus_1);
    RUN(handle_wrong_type_returns_false);
    RUN(handle_invalid_signature_returns_false);
    RUN(handle_stale_timestamp_returns_false);
    RUN(handle_own_gossip_returns_false);
    RUN(handle_unknown_reporter_full_delta);
    RUN(handle_degraded_reporter_weighted_delta);
    RUN(handle_clock_is_real_not_zero);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
