// SPDX-License-Identifier: MIT
// Unit tests for aethermesh_gossip.c (ReputationGossipService).
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

#include "aethermesh_reputation.h"
#include "aethermesh_gossip.h"

// ─── Test runner ─────────────────────────────────────────────────────────────

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

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
static AetherMeshGossipCallbacks make_callbacks(const char *local_uhid)
{
    AetherMeshGossipCallbacks cb;
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
// Used to inject inbound packets into aethermesh_gossip_handle without going
// through aethermesh_gossip_broadcast (which would stamp timestamp_ms = 0 from the
// stub clock, matching the freshness check perfectly).

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

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    int delivered = aethermesh_gossip_broadcast(svc, "peer-a", -0.10, "bad-behaviour");
    /* fake_broadcast always returns 3 */
    assert(delivered == 3);
    assert(g_broadcast_count == 1);

    aethermesh_gossip_destroy(svc);
}

// ─── 2. broadcast_payload_has_correct_fields ─────────────────────────────────

static void broadcast_payload_has_correct_fields(void)
{
    reset_globals();

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    aethermesh_gossip_broadcast(svc, "peer-b", -0.05, "flood");

    /* The signed (identity-transformed) packet must contain both UHIDs. */
    assert(strstr(g_last_packet, "\"reporter_uhid\":\"local-node\"") != NULL);
    assert(strstr(g_last_packet, "\"target_uhid\":\"peer-b\"") != NULL);
    assert(strstr(g_last_packet, "\"type\":52") != NULL);

    aethermesh_gossip_destroy(svc);
}

// ─── 3. broadcast_clamps_delta_above_1 ───────────────────────────────────────

static void broadcast_clamps_delta_above_1(void)
{
    reset_globals();

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* Pass a delta of +5.0 — should be clamped to 1.0 in the packet. */
    aethermesh_gossip_broadcast(svc, "peer-c", 5.0, "bonus");

    /* The JSON should contain score_delta:1.000000 (or close). */
    assert(strstr(g_last_packet, "\"score_delta\":1.000000") != NULL);

    aethermesh_gossip_destroy(svc);
}

// ─── 4. broadcast_clamps_delta_below_minus_1 ─────────────────────────────────

static void broadcast_clamps_delta_below_minus_1(void)
{
    reset_globals();

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* Pass a delta of -9.9 — should be clamped to -1.0. */
    aethermesh_gossip_broadcast(svc, "peer-d", -9.9, "punish");

    assert(strstr(g_last_packet, "\"score_delta\":-1.000000") != NULL);

    aethermesh_gossip_destroy(svc);
}

// ─── 5. handle_wrong_type_returns_false ──────────────────────────────────────

static void handle_wrong_type_returns_false(void)
{
    reset_globals();

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* Build a packet with type 99 (not 52). */
    char pkt[2048];
    snprintf(pkt, sizeof(pkt),
        "{\"type\":99,\"source_uhid\":\"remote\",\"destination_uhid\":\"*\","
        "\"ttl\":3,\"payload\":"
        "{\"reporter_uhid\":\"remote\",\"target_uhid\":\"victim\","
        "\"score_delta\":-0.10,\"timestamp_ms\":0,\"reason\":\"test\"},"
        "\"timestamp_ms\":0}");

    bool accepted = aethermesh_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == false);

    /* Victim's score must be untouched (1.0). */
    double score = aethermesh_reputation_get_score(&rep, "victim");
    assert(fabs(score - 1.0) < 1e-9);

    aethermesh_gossip_destroy(svc);
}

// ─── 6. handle_invalid_signature_returns_false ───────────────────────────────

static void handle_invalid_signature_returns_false(void)
{
    reset_globals();
    g_verify_ok = false;   /* make verify always fail */

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "remote-a", "target-a", -0.20, 0LL);

    bool accepted = aethermesh_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == false);

    /* Score must be untouched. */
    double score = aethermesh_reputation_get_score(&rep, "target-a");
    assert(fabs(score - 1.0) < 1e-9);

    aethermesh_gossip_destroy(svc);
}

// ─── 7. handle_stale_timestamp_returns_false ─────────────────────────────────
// The stub clock returns 0 (get_now_ms).
// FRESHNESS_WINDOW_MS = 300000 ms (5 min).
// A timestamp of -(300001) is more than 5 min stale.

static void handle_stale_timestamp_returns_false(void)
{
    reset_globals();

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* timestamp_ms = -(FRESHNESS_WINDOW_MS + 1) → stale */
    long long stale_ts = -((long long)AETHERMESH_GOSSIP_FRESHNESS_MS + 1LL);
    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "remote-b", "target-b", -0.10, stale_ts);

    bool accepted = aethermesh_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == false);

    double score = aethermesh_reputation_get_score(&rep, "target-b");
    assert(fabs(score - 1.0) < 1e-9);

    aethermesh_gossip_destroy(svc);
}

// ─── 8. handle_own_gossip_returns_false ──────────────────────────────────────
// A packet whose reporter_uhid matches local_uhid must be silently dropped.

static void handle_own_gossip_returns_false(void)
{
    reset_globals();

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* reporter = local-node (same as local_uhid) */
    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "local-node", "target-c", -0.10, 0LL);

    bool accepted = aethermesh_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == false);

    double score = aethermesh_reputation_get_score(&rep, "target-c");
    assert(fabs(score - 1.0) < 1e-9);

    aethermesh_gossip_destroy(svc);
}

// ─── 9. handle_unknown_reporter_full_delta ───────────────────────────────────
// Unknown reporter → R = 1.0.  effective = 1.0 × delta = delta.
// score_delta = -0.20, reporter unknown → target score = 1.0 + (-0.20 × 1.0) = 0.80.

static void handle_unknown_reporter_full_delta(void)
{
    reset_globals();

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "unknown-reporter", "target-d", -0.20, 0LL);

    bool accepted = aethermesh_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == true);

    /* R = 1.0 (unknown), effective = -0.20 × 1.0 = -0.20 → 1.0 - 0.20 = 0.80 */
    double score = aethermesh_reputation_get_score(&rep, "target-d");
    assert(fabs(score - 0.80) < 1e-9);

    aethermesh_gossip_destroy(svc);
}

// ─── 10. handle_degraded_reporter_weighted_delta ─────────────────────────────
// Reporter has R = 0.5 → effective = delta × 0.5.
// score_delta = -0.20, R = 0.5 → effective = -0.10 → target score = 0.90.

static void handle_degraded_reporter_weighted_delta(void)
{
    reset_globals();

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);

    /* Degrade the reporter's score to 0.5 (5 × -0.10 from 1.0). */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.95 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.90 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.85 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.80 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.75 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.70 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.65 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.60 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.55 */
    aethermesh_reputation_record_rreq_flood(&rep, "degraded-reporter"); /* -0.05 → 0.50 */

    double reporter_score = aethermesh_reputation_get_score(&rep, "degraded-reporter");
    assert(fabs(reporter_score - 0.50) < 1e-9);

    AetherMeshGossipCallbacks cb = make_callbacks("local-node");
    AetherMeshGossipService *svc = aethermesh_gossip_create(&rep, &cb);
    assert(svc != NULL);

    /* delta = -0.20, R = 0.50 → effective = -0.10 → target = 1.0 - 0.10 = 0.90 */
    char pkt[2048];
    build_packet(pkt, sizeof(pkt), "degraded-reporter", "target-e", -0.20, 0LL);

    bool accepted = aethermesh_gossip_handle(svc, pkt, NULL, 0);
    assert(accepted == true);

    double score = aethermesh_reputation_get_score(&rep, "target-e");
    assert(fabs(score - 0.90) < 1e-9);

    aethermesh_gossip_destroy(svc);
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

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
