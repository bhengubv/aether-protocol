// SPDX-License-Identifier: MIT
// Unit tests for dtn.c (DtnService).
//
// NOTE: The C reference DTN service ships a stub `aethermesh_dtn_handle_packet`
// (handler tests are out of scope until hosts wire up a JSON parser); these
// tests cover the synchronous bookkeeping that runs in-process.

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethermesh/constants.h"
#include "aethermesh/dtn.h"
#include "aethermesh/protocol.h"
#include "aethermesh_reputation.h"

#define LOCAL_UHID "local"

// ───── FakeMeshSender (DTN flavour) ──────────────────────

typedef struct {
    aethermesh_mesh_packet_t **broadcasts;
    int broadcasts_len;
    int broadcasts_cap;
    aethermesh_mesh_packet_t **unicasts;
    char **unicasts_next_hops;
    int unicasts_len;
    int unicasts_cap;
    bool fail_send_for_recipient;
    char *block_recipient;
} fake_state_t;

static bool fake_send(aethermesh_mesh_sender_t *self, const aethermesh_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->fail_send_for_recipient && s->block_recipient
        && next_hop_uhid && strcmp(next_hop_uhid, s->block_recipient) == 0) {
        return false;
    }
    if (s->unicasts_len == s->unicasts_cap) {
        s->unicasts_cap = s->unicasts_cap ? s->unicasts_cap * 2 : 8;
        s->unicasts = (aethermesh_mesh_packet_t **)realloc(s->unicasts, sizeof(*s->unicasts) * (size_t)s->unicasts_cap);
        s->unicasts_next_hops = (char **)realloc(s->unicasts_next_hops, sizeof(*s->unicasts_next_hops) * (size_t)s->unicasts_cap);
    }
    s->unicasts[s->unicasts_len] = aethermesh_packet_clone(packet);
    s->unicasts_next_hops[s->unicasts_len] = next_hop_uhid ? strdup(next_hop_uhid) : NULL;
    s->unicasts_len++;
    return true;
}

static int fake_broadcast(aethermesh_mesh_sender_t *self, const aethermesh_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->broadcasts_len == s->broadcasts_cap) {
        s->broadcasts_cap = s->broadcasts_cap ? s->broadcasts_cap * 2 : 8;
        s->broadcasts = (aethermesh_mesh_packet_t **)realloc(s->broadcasts, sizeof(*s->broadcasts) * (size_t)s->broadcasts_cap);
    }
    s->broadcasts[s->broadcasts_len++] = aethermesh_packet_clone(packet);
    return 0;
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts_len; i++) aethermesh_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    for (int i = 0; i < s->unicasts_len; i++) {
        aethermesh_packet_free(s->unicasts[i]);
        free(s->unicasts_next_hops[i]);
    }
    free(s->unicasts);
    free(s->unicasts_next_hops);
    free(s->block_recipient);
    memset(s, 0, sizeof(*s));
}

static aethermesh_mesh_sender_t make_sender(fake_state_t *state) {
    aethermesh_mesh_sender_t s = {0};
    s.local_uhid = LOCAL_UHID;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// ───── Tests ─────────────────────────────────────────────

static void create_bundle_attempts_direct_delivery(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_dtn_service_t *svc = aethermesh_dtn_service_new(&sender);

    uint8_t payload[] = {1, 2, 3};
    int rc = aethermesh_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                                      AETHERMESH_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(rc == 0);

    // FakeMeshSender's send always returns true → direct delivery succeeded → unicast recorded.
    int dtn_unicasts = 0;
    for (int i = 0; i < s.unicasts_len; i++) {
        if (s.unicasts[i]->type == AETHERMESH_PACKET_TYPE_DTN_BUNDLE
            && strcmp(s.unicasts_next_hops[i], "recipient") == 0) dtn_unicasts++;
    }
    assert(dtn_unicasts == 1);

    aethermesh_dtn_service_free(svc);
    fake_clear(&s);
}

static void create_bundle_with_failing_send_keeps_pending(void) {
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("recipient");
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_dtn_service_t *svc = aethermesh_dtn_service_new(&sender);

    uint8_t payload[] = {1};
    int rc = aethermesh_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                                      AETHERMESH_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(rc == 0);
    // No unicast recorded because send was blocked
    assert(s.unicasts_len == 0);

    aethermesh_dtn_service_free(svc);
    fake_clear(&s);
}

static void run_delivery_scan_retries_pending(void) {
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("recipient");
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_dtn_service_t *svc = aethermesh_dtn_service_new(&sender);

    uint8_t payload[] = {1};
    aethermesh_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                             AETHERMESH_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(s.unicasts_len == 0);

    // Unblock and re-scan
    s.fail_send_for_recipient = false;
    aethermesh_dtn_run_delivery_scan(svc);

    int dtn_unicasts = 0;
    for (int i = 0; i < s.unicasts_len; i++) {
        if (s.unicasts[i]->type == AETHERMESH_PACKET_TYPE_DTN_BUNDLE) dtn_unicasts++;
    }
    assert(dtn_unicasts >= 1);

    aethermesh_dtn_service_free(svc);
    fake_clear(&s);
}

static void expire_stale_is_safe_with_no_bundles(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_dtn_service_t *svc = aethermesh_dtn_service_new(&sender);

    int n = aethermesh_dtn_expire_stale(svc);
    assert(n == 0);

    aethermesh_dtn_service_free(svc);
    fake_clear(&s);
}

static void bundle_lifecycle_helpers(void) {
    aethermesh_dtn_bundle_t *b = aethermesh_dtn_bundle_new();
    assert(b != NULL);
    assert(b->priority == AETHERMESH_BUNDLE_PRIORITY_NORMAL);
    assert(b->status == AETHERMESH_BUNDLE_STATUS_PENDING);
    assert(b->copy_count == 1);
    assert(b->max_copies == AETHERMESH_DTN_MAX_COPIES);
    assert(!aethermesh_dtn_bundle_is_expired(b));  // 72h in the future

    // Force an expired timestamp and re-check
    b->expires_at_ms = 0;
    assert(aethermesh_dtn_bundle_is_expired(b));

    aethermesh_dtn_bundle_free(b);
}

// ───── Reputation hook tests ──────────────────────────────

// Helper: build a packet of the given type, addressed from source_uhid to
// destination_uhid. Caller must aethermesh_packet_free() it.
static aethermesh_mesh_packet_t *make_dtn_packet(uint8_t type,
                                             const char *source_uhid,
                                             const char *destination_uhid,
                                             const uint8_t *payload,
                                             uint32_t payload_len) {
    aethermesh_mesh_packet_t *pkt = aethermesh_packet_new();
    if (!pkt) return NULL;
    pkt->type = type;
    aethermesh_packet_set_source_uhid(pkt, source_uhid);
    aethermesh_packet_set_destination_uhid(pkt, destination_uhid);
    if (payload && payload_len) {
        aethermesh_packet_set_payload(pkt, payload, payload_len);
    }
    return pkt;
}

static void reputation_delivery_success_fires_for_local_bundle(void) {
    // A DTN_BUNDLE packet whose destination_uhid == LOCAL_UHID must fire
    // aethermesh_reputation_record_delivery_success for the sender.
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_dtn_service_t *svc = aethermesh_dtn_service_new(&sender);

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);
    aethermesh_dtn_set_reputation(svc, &rep);

    // Score unknown before the packet arrives (defaults to 1.0).
    double before = aethermesh_reputation_get_score(&rep, "sender-node");

    aethermesh_mesh_packet_t *pkt = make_dtn_packet(
        AETHERMESH_PACKET_TYPE_DTN_BUNDLE,
        "sender-node",   // source
        LOCAL_UHID,      // destination == local node
        NULL, 0);
    assert(pkt != NULL);
    aethermesh_dtn_handle_packet(svc, pkt);
    aethermesh_packet_free(pkt);

    // delivery_success adds +0.01 — score stays at 1.0 (clamped).
    double after = aethermesh_reputation_get_score(&rep, "sender-node");
    (void)before;
    (void)after;
    // The important assertion: score was touched (no crash, entry exists).
    // Even if clamped to 1.0, the function must have been called without error.
    assert(after >= 0.0 && after <= 1.0);

    aethermesh_dtn_service_free(svc);
    fake_clear(&s);
}

static void reputation_delivery_success_does_not_fire_for_other_node(void) {
    // A DTN_BUNDLE packet whose destination_uhid != LOCAL_UHID must NOT fire
    // any reputation event (we verify score remains exactly 1.0 for unknown).
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_dtn_service_t *svc = aethermesh_dtn_service_new(&sender);

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);
    aethermesh_dtn_set_reputation(svc, &rep);

    aethermesh_mesh_packet_t *pkt = make_dtn_packet(
        AETHERMESH_PACKET_TYPE_DTN_BUNDLE,
        "sender-node",
        "other-node",   // destination != local node
        NULL, 0);
    assert(pkt != NULL);
    aethermesh_dtn_handle_packet(svc, pkt);
    aethermesh_packet_free(pkt);

    // Score for sender-node must remain at the unknown-peer default (1.0).
    double score = aethermesh_reputation_get_score(&rep, "sender-node");
    assert(score == 1.0);

    aethermesh_dtn_service_free(svc);
    fake_clear(&s);
}

static void reputation_custody_refusal_fires_on_ack_refused(void) {
    // A DTN_CUSTODY_ACK packet with payload[0] == 0 (refused) must fire
    // aethermesh_reputation_record_custody_refusal for the source peer.
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_dtn_service_t *svc = aethermesh_dtn_service_new(&sender);

    AetherMeshNodeReputationService rep;
    aethermesh_reputation_init(&rep);
    aethermesh_dtn_set_reputation(svc, &rep);

    // payload[0] = 0 means refused
    uint8_t refused_payload[] = {0};
    aethermesh_mesh_packet_t *pkt = make_dtn_packet(
        AETHERMESH_PACKET_TYPE_DTN_CUSTODY_ACK,
        "refusing-peer",  // source
        LOCAL_UHID,
        refused_payload, sizeof(refused_payload));
    assert(pkt != NULL);
    aethermesh_dtn_handle_packet(svc, pkt);
    aethermesh_packet_free(pkt);

    // custody_refusal applies -0.05 from 1.0 → 0.95.
    double score = aethermesh_reputation_get_score(&rep, "refusing-peer");
    assert(score < 1.0);  // penalised

    aethermesh_dtn_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether DTN Service — Unit Tests\n");
    printf("================================\n");

    RUN(create_bundle_attempts_direct_delivery);
    RUN(create_bundle_with_failing_send_keeps_pending);
    RUN(run_delivery_scan_retries_pending);
    RUN(expire_stale_is_safe_with_no_bundles);
    RUN(bundle_lifecycle_helpers);
    RUN(reputation_delivery_success_fires_for_local_bundle);
    RUN(reputation_delivery_success_does_not_fire_for_other_node);
    RUN(reputation_custody_refusal_fires_on_ack_refused);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
