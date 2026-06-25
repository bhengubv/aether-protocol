// SPDX-License-Identifier: MIT
// Unit tests for dtn.c (DtnService).
//
// The bundle / custody-ack / delivery-receipt wire is the canonical binary DTN
// envelope (aethernet/dtn_envelope.h). These tests drive aethernet_dtn_handle_packet
// with real binary envelopes and assert observable side effects: the custody-ack
// and delivery-receipt packets the service emits, the hop-counted bundle it
// forwards, and the GeohashEpidemicStrategy replication targets it picks.

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/dtn.h"
#include "aethernet/dtn_envelope.h"
#include "aethernet/protocol.h"
#include "aethernet/routing.h"
#include "aethernet_reputation.h"

#define LOCAL_UHID "local"

// ───── FakeMeshSender (DTN flavour) ──────────────────────

typedef struct {
    aethernet_mesh_packet_t **broadcasts;
    int broadcasts_len;
    int broadcasts_cap;
    aethernet_mesh_packet_t **unicasts;
    char **unicasts_next_hops;
    int unicasts_len;
    int unicasts_cap;
    bool fail_send_for_recipient;
    char *block_recipient;
    const aethernet_peer_info_t *peers;  // borrowed; surfaced via connected_peers
    int peers_len;
} fake_state_t;

static bool fake_send(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->fail_send_for_recipient && s->block_recipient
        && next_hop_uhid && strcmp(next_hop_uhid, s->block_recipient) == 0) {
        return false;
    }
    if (s->unicasts_len == s->unicasts_cap) {
        s->unicasts_cap = s->unicasts_cap ? s->unicasts_cap * 2 : 8;
        s->unicasts = (aethernet_mesh_packet_t **)realloc(s->unicasts, sizeof(*s->unicasts) * (size_t)s->unicasts_cap);
        s->unicasts_next_hops = (char **)realloc(s->unicasts_next_hops, sizeof(*s->unicasts_next_hops) * (size_t)s->unicasts_cap);
    }
    s->unicasts[s->unicasts_len] = aethernet_packet_clone(packet);
    s->unicasts_next_hops[s->unicasts_len] = next_hop_uhid ? strdup(next_hop_uhid) : NULL;
    s->unicasts_len++;
    return true;
}

static int fake_broadcast(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->broadcasts_len == s->broadcasts_cap) {
        s->broadcasts_cap = s->broadcasts_cap ? s->broadcasts_cap * 2 : 8;
        s->broadcasts = (aethernet_mesh_packet_t **)realloc(s->broadcasts, sizeof(*s->broadcasts) * (size_t)s->broadcasts_cap);
    }
    s->broadcasts[s->broadcasts_len++] = aethernet_packet_clone(packet);
    return 0;
}

static int fake_connected_peers(aethernet_mesh_sender_t *self, aethernet_peer_info_t *out, int max) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    int n = s->peers_len < max ? s->peers_len : max;
    for (int i = 0; i < n; i++) out[i] = s->peers[i];
    return n;
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts_len; i++) aethernet_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    for (int i = 0; i < s->unicasts_len; i++) {
        aethernet_packet_free(s->unicasts[i]);
        free(s->unicasts_next_hops[i]);
    }
    free(s->unicasts);
    free(s->unicasts_next_hops);
    free(s->block_recipient);
    memset(s, 0, sizeof(*s));
}

static aethernet_mesh_sender_t make_sender(fake_state_t *state) {
    aethernet_mesh_sender_t s = {0};
    s.local_uhid = LOCAL_UHID;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;  // connected_peers stays NULL; peer-aware tests opt in explicitly
}

// Find the first recorded unicast of `type` whose next-hop equals `next_hop`.
static aethernet_mesh_packet_t *find_unicast(fake_state_t *s, uint8_t type, const char *next_hop) {
    for (int i = 0; i < s->unicasts_len; i++) {
        if (s->unicasts[i]->type == type
            && s->unicasts_next_hops[i] && next_hop
            && strcmp(s->unicasts_next_hops[i], next_hop) == 0) {
            return s->unicasts[i];
        }
    }
    return NULL;
}

static int count_unicasts_of_type(fake_state_t *s, uint8_t type) {
    int n = 0;
    for (int i = 0; i < s->unicasts_len; i++)
        if (s->unicasts[i]->type == type) n++;
    return n;
}

// Build a binary DTN_BUNDLE packet. The inner bundle's id is derived from
// `id_seed` (id[i] = id_seed + i) so a later custody-ack / receipt can target it.
static aethernet_mesh_packet_t *make_bundle_packet(
        const char *sender_uhid, const char *recipient_uhid,
        const char *recipient_last_geohash,
        int32_t hop_count, uint8_t priority, uint8_t id_seed,
        const uint8_t *payload, uint32_t payload_len) {
    aethernet_dtn_bundle_t *b = aethernet_dtn_bundle_new();
    for (int i = 0; i < AETHERNET_PACKET_ID_SIZE; i++) b->id[i] = (uint8_t)(id_seed + i);
    b->sender_uhid = sender_uhid ? strdup(sender_uhid) : NULL;
    b->recipient_uhid = recipient_uhid ? strdup(recipient_uhid) : NULL;
    b->recipient_last_geohash = recipient_last_geohash ? strdup(recipient_last_geohash) : NULL;
    b->hop_count = hop_count;
    b->priority = priority;
    if (payload && payload_len) {
        b->encrypted_payload = (uint8_t *)malloc(payload_len);
        memcpy(b->encrypted_payload, payload, payload_len);
        b->encrypted_payload_len = payload_len;
    }
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    aethernet_dtn_bundle_encode(b, &body, &body_len);
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DTN_BUNDLE;
    aethernet_packet_set_source_uhid(pkt, sender_uhid);
    aethernet_packet_set_destination_uhid(pkt, recipient_uhid);
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);
    aethernet_dtn_bundle_free(b);
    return pkt;
}

// Build a custody-ack packet (binary envelope) for the bundle with id seed `id_seed`.
static aethernet_mesh_packet_t *make_custody_ack_packet(const char *source, uint8_t id_seed, bool accepted) {
    uint8_t id[AETHERNET_PACKET_ID_SIZE];
    for (int i = 0; i < AETHERNET_PACKET_ID_SIZE; i++) id[i] = (uint8_t)(id_seed + i);
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    aethernet_dtn_custody_ack_encode(id, accepted, &body, &body_len);
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DTN_CUSTODY_ACK;
    aethernet_packet_set_source_uhid(pkt, source);
    aethernet_packet_set_destination_uhid(pkt, LOCAL_UHID);
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);
    return pkt;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// ───── Create / scan / expire lifecycle ──────────────────

static void create_bundle_attempts_direct_delivery(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    uint8_t payload[] = {1, 2, 3};
    int rc = aethernet_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                                      AETHERNET_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(rc == 0);

    // FakeMeshSender's send always returns true → direct delivery succeeded → unicast recorded.
    int dtn_unicasts = 0;
    for (int i = 0; i < s.unicasts_len; i++) {
        if (s.unicasts[i]->type == AETHERNET_PACKET_TYPE_DTN_BUNDLE
            && strcmp(s.unicasts_next_hops[i], "recipient") == 0) dtn_unicasts++;
    }
    assert(dtn_unicasts == 1);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void create_bundle_with_failing_send_keeps_pending(void) {
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("recipient");
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    uint8_t payload[] = {1};
    int rc = aethernet_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                                      AETHERNET_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(rc == 0);
    // No unicast recorded because send was blocked
    assert(s.unicasts_len == 0);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void run_delivery_scan_retries_pending(void) {
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("recipient");
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    uint8_t payload[] = {1};
    aethernet_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                             AETHERNET_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(s.unicasts_len == 0);

    // Unblock and re-scan
    s.fail_send_for_recipient = false;
    aethernet_dtn_run_delivery_scan(svc);

    int dtn_unicasts = count_unicasts_of_type(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE);
    assert(dtn_unicasts >= 1);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void expire_stale_is_safe_with_no_bundles(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    int n = aethernet_dtn_expire_stale(svc);
    assert(n == 0);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void bundle_lifecycle_helpers(void) {
    aethernet_dtn_bundle_t *b = aethernet_dtn_bundle_new();
    assert(b != NULL);
    assert(b->priority == AETHERNET_BUNDLE_PRIORITY_NORMAL);
    assert(b->status == AETHERNET_BUNDLE_STATUS_PENDING);
    assert(b->copy_count == 1);
    assert(b->max_copies == AETHERNET_DTN_MAX_COPIES);
    assert(!aethernet_dtn_bundle_is_expired(b));  // 72h in the future

    // Force an expired timestamp and re-check
    b->expires_at_ms = 0;
    assert(aethernet_dtn_bundle_is_expired(b));

    aethernet_dtn_bundle_free(b);
}

// ───── Reputation hooks ──────────────────────────────────

static void reputation_delivery_success_fires_for_local_bundle(void) {
    // A bundle whose recipient == LOCAL_UHID fires record_delivery_success for
    // the packet source. delivery_success only nudges the score up, so we first
    // penalise the source (below the 1.0 ceiling) to make the bump observable.
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);
    aethernet_dtn_set_reputation(svc, &rep);

    aethernet_reputation_record_custody_refusal(&rep, "remote-sender");  // 1.0 → below ceiling
    double before = aethernet_reputation_get_score(&rep, "remote-sender");
    assert(before < 1.0);

    aethernet_mesh_packet_t *pkt = make_bundle_packet(
        "remote-sender", LOCAL_UHID, NULL, 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x01, NULL, 0);
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    double after = aethernet_reputation_get_score(&rep, "remote-sender");
    assert(after > before);  // delivery_success raised the score

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void reputation_delivery_success_does_not_fire_for_other_node(void) {
    // A transit bundle (recipient != LOCAL_UHID) must NOT fire delivery_success;
    // the source's score stays at the unknown-peer default (1.0).
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);
    aethernet_dtn_set_reputation(svc, &rep);

    aethernet_mesh_packet_t *pkt = make_bundle_packet(
        "sender-node", "other-node", NULL, 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x02, NULL, 0);
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    double score = aethernet_reputation_get_score(&rep, "sender-node");
    assert(score == 1.0);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void reputation_custody_refusal_fires_on_ack_refused(void) {
    // A custody-ack with accepted == false fires record_custody_refusal for the source.
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    AetherNetNodeReputationService rep;
    aethernet_reputation_init(&rep);
    aethernet_dtn_set_reputation(svc, &rep);

    aethernet_mesh_packet_t *pkt = make_custody_ack_packet("refusing-peer", 0x03, false);
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    double score = aethernet_reputation_get_score(&rep, "refusing-peer");
    assert(score < 1.0);  // penalised

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

// ───── OnBundleReceived (Issue #59) ──────────────────────

typedef struct {
    int count;
    char *last_sender_uhid;
    char *last_recipient_uhid;
    uint8_t last_priority;
    int32_t last_hop_count;
    uint8_t last_payload[16];
    uint32_t last_payload_len;
} on_received_state_t;

static void on_received_capture(const aethernet_dtn_bundle_received_event_t *evt, void *user_data) {
    on_received_state_t *s = (on_received_state_t *)user_data;
    s->count++;
    free(s->last_sender_uhid);
    free(s->last_recipient_uhid);
    s->last_sender_uhid = evt->sender_uhid ? strdup(evt->sender_uhid) : NULL;
    s->last_recipient_uhid = evt->recipient_uhid ? strdup(evt->recipient_uhid) : NULL;
    s->last_priority = evt->priority;
    s->last_hop_count = evt->hop_count;
    s->last_payload_len = evt->encrypted_payload_len > sizeof(s->last_payload)
        ? (uint32_t)sizeof(s->last_payload)
        : evt->encrypted_payload_len;
    if (evt->encrypted_payload && s->last_payload_len > 0) {
        memcpy(s->last_payload, evt->encrypted_payload, s->last_payload_len);
    }
}

static void bundle_received_fires_for_local_recipient(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    on_received_state_t cap = {0};
    aethernet_dtn_set_bundle_received_callback(svc, on_received_capture, &cap);

    uint8_t payload[] = {0x01, 0x02, 0x03, 0x04};
    aethernet_mesh_packet_t *pkt = make_bundle_packet(
        "remote-sender", LOCAL_UHID, NULL, 0,
        (uint8_t)AETHERNET_BUNDLE_PRIORITY_HIGH, 0x04, payload, sizeof(payload));
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(cap.count == 1);
    assert(cap.last_sender_uhid && strcmp(cap.last_sender_uhid, "remote-sender") == 0);
    assert(cap.last_recipient_uhid && strcmp(cap.last_recipient_uhid, LOCAL_UHID) == 0);
    assert(cap.last_priority == (uint8_t)AETHERNET_BUNDLE_PRIORITY_HIGH);
    assert(cap.last_payload_len == 4);
    assert(memcmp(cap.last_payload, payload, 4) == 0);

    free(cap.last_sender_uhid);
    free(cap.last_recipient_uhid);
    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void bundle_received_decodes_real_hop_count(void) {
    // The handler must DECODE hop_count from the binary envelope — a regression to
    // a hardcoded 0 makes this assertion fail.
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    on_received_state_t cap = {0};
    aethernet_dtn_set_bundle_received_callback(svc, on_received_capture, &cap);

    uint8_t payload[] = {1, 2, 3, 4};
    aethernet_mesh_packet_t *pkt = make_bundle_packet(
        "remote-sender", LOCAL_UHID, NULL, /*hop_count=*/7,
        (uint8_t)AETHERNET_BUNDLE_PRIORITY_HIGH, 0x05, payload, sizeof(payload));
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(cap.count == 1);
    assert(cap.last_hop_count == 7);  // decoded from the envelope; fails if hardcoded 0

    free(cap.last_sender_uhid);
    free(cap.last_recipient_uhid);
    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void bundle_received_does_not_fire_for_other_recipient(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    on_received_state_t cap = {0};
    aethernet_dtn_set_bundle_received_callback(svc, on_received_capture, &cap);

    uint8_t payload[] = {0xff};
    aethernet_mesh_packet_t *pkt = make_bundle_packet(
        "remote-sender", "someone-else", NULL, 0,
        AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x06, payload, sizeof(payload));
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(cap.count == 0);

    free(cap.last_sender_uhid);
    free(cap.last_recipient_uhid);
    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void bundle_received_unset_callback_does_not_crash(void) {
    // No callback registered → handler must complete cleanly (and still ack/receipt).
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    uint8_t payload[] = {0x09};
    aethernet_mesh_packet_t *pkt = make_bundle_packet(
        "alice", LOCAL_UHID, NULL, 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x07, payload, sizeof(payload));
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

// ───── Store-and-forward custody ─────────────────────────

static void transit_bundle_accepts_custody_and_hop_counts(void) {
    // A third-party bundle with capacity is accepted: an ACCEPTED custody-ack goes
    // back to the source, and the stored bundle is hop-counted 0→1 — proven by
    // forwarding it on the next scan and decoding the forwarded hop_count.
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    aethernet_mesh_packet_t *pkt = make_bundle_packet(
        "origin", "dest", NULL, /*hop_count=*/0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x50, NULL, 0);
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    // Accepted custody-ack back to the source.
    aethernet_mesh_packet_t *ack = find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_CUSTODY_ACK, "origin");
    assert(ack != NULL);
    uint8_t ack_id[AETHERNET_PACKET_ID_SIZE];
    bool accepted = false;
    assert(aethernet_dtn_custody_ack_decode(ack->payload, ack->payload_len, ack_id, &accepted));
    assert(accepted == true);

    // The stored InCustody bundle is forwarded on the scan with hop_count incremented.
    aethernet_dtn_run_delivery_scan(svc);
    aethernet_mesh_packet_t *fwd = find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE, "dest");
    assert(fwd != NULL);
    aethernet_dtn_bundle_t *decoded = aethernet_dtn_bundle_decode(fwd->payload, fwd->payload_len);
    assert(decoded != NULL);
    assert(decoded->hop_count == 1);
    aethernet_dtn_bundle_free(decoded);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void at_capacity_refuses_custody(void) {
    // With the store at AETHERNET_DTN_MAX_BUNDLES_PER_NODE active bundles, a new
    // transit bundle is refused (negative custody-ack) and NOT stored.
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("cap-dest");  // keep pre-filled bundles Pending (active)
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    for (int i = 0; i < AETHERNET_DTN_MAX_BUNDLES_PER_NODE; i++) {
        aethernet_dtn_create_bundle(svc, "cap-dest", NULL, 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, NULL);
    }

    aethernet_mesh_packet_t *pkt = make_bundle_packet(
        "origin", "other-dest", NULL, 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x60, NULL, 0);
    aethernet_dtn_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    // Refused custody-ack to the source.
    aethernet_mesh_packet_t *ack = find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_CUSTODY_ACK, "origin");
    assert(ack != NULL);
    uint8_t ack_id[AETHERNET_PACKET_ID_SIZE];
    bool accepted = true;
    assert(aethernet_dtn_custody_ack_decode(ack->payload, ack->payload_len, ack_id, &accepted));
    assert(accepted == false);

    // Refused bundle was not stored: a scan never forwards anything to "other-dest".
    aethernet_dtn_run_delivery_scan(svc);
    assert(find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE, "other-dest") == NULL);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void delivery_receipt_marks_bundle_delivered(void) {
    // Hold a bundle in custody, then receive a delivery receipt for its id; the
    // bundle is marked Delivered and is no longer re-forwarded by a scan.
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("dest");  // keep the bundle InCustody (direct delivery fails)
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    aethernet_mesh_packet_t *bpkt = make_bundle_packet(
        "origin", "dest", NULL, 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x70, NULL, 0);
    aethernet_dtn_handle_packet(svc, bpkt);
    aethernet_packet_free(bpkt);

    // Build a delivery receipt for the same id.
    uint8_t id[AETHERNET_PACKET_ID_SIZE];
    for (int i = 0; i < AETHERNET_PACKET_ID_SIZE; i++) id[i] = (uint8_t)(0x70 + i);
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    assert(aethernet_dtn_delivery_receipt_encode(id, "dest", 2, 1, 1710528000000LL, &body, &body_len));
    aethernet_mesh_packet_t *rpkt = aethernet_packet_new();
    rpkt->type = AETHERNET_PACKET_TYPE_DTN_DELIVERY_RECEIPT;
    aethernet_packet_set_source_uhid(rpkt, "dest");
    aethernet_packet_set_destination_uhid(rpkt, LOCAL_UHID);
    aethernet_packet_set_payload(rpkt, body, body_len);
    free(body);
    aethernet_dtn_handle_packet(svc, rpkt);
    aethernet_packet_free(rpkt);

    // Unblock and scan: a Delivered bundle is never re-forwarded.
    s.fail_send_for_recipient = false;
    aethernet_dtn_run_delivery_scan(svc);
    assert(find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE, "dest") == NULL);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

// ───── Custody-ack copy bookkeeping ──────────────────────

static const aethernet_peer_info_t THREE_CARRIERS[3] = {
    { .uhid = "c1", .geohash = NULL, .capabilities = AETHERNET_CAP_DTN_CARRIER, .reliability_score = 10, .is_blocked = false },
    { .uhid = "c2", .geohash = NULL, .capabilities = AETHERNET_CAP_DTN_CARRIER, .reliability_score = 20, .is_blocked = false },
    { .uhid = "c3", .geohash = NULL, .capabilities = AETHERNET_CAP_DTN_CARRIER, .reliability_score = 30, .is_blocked = false },
};

static void positive_custody_ack_increments_copy_count(void) {
    // Two positive custody-acks raise copy_count 1→3 (== max_copies), so the scan
    // declines to replicate. Without the increments it would send 2 copies.
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("far-dest");  // force replication, not direct delivery
    s.peers = THREE_CARRIERS;
    s.peers_len = 3;
    aethernet_mesh_sender_t sender = make_sender(&s);
    sender.connected_peers = fake_connected_peers;
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    aethernet_mesh_packet_t *bpkt = make_bundle_packet(
        "origin", "far-dest", NULL, 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x20, NULL, 0);
    aethernet_dtn_handle_packet(svc, bpkt);  // stored InCustody, copy_count=1, max_copies=3
    aethernet_packet_free(bpkt);

    for (int i = 0; i < 2; i++) {
        aethernet_mesh_packet_t *ack = make_custody_ack_packet("c1", 0x20, true);
        aethernet_dtn_handle_packet(svc, ack);  // copy_count 1→2→3
        aethernet_packet_free(ack);
    }

    aethernet_dtn_run_delivery_scan(svc);
    // copy_count(3) >= max_copies(3) → no replication bundles emitted.
    assert(count_unicasts_of_type(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE) == 0);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void negative_custody_ack_does_not_increment_copy_count(void) {
    // A negative custody-ack leaves copy_count at 1, so the scan replicates the
    // remaining 2 slots (proving no increment happened).
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("far-dest");
    s.peers = THREE_CARRIERS;
    s.peers_len = 3;
    aethernet_mesh_sender_t sender = make_sender(&s);
    sender.connected_peers = fake_connected_peers;
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    aethernet_mesh_packet_t *bpkt = make_bundle_packet(
        "origin", "far-dest", NULL, 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x30, NULL, 0);
    aethernet_dtn_handle_packet(svc, bpkt);  // copy_count=1, max_copies=3
    aethernet_packet_free(bpkt);

    aethernet_mesh_packet_t *ack = make_custody_ack_packet("c1", 0x30, false);
    aethernet_dtn_handle_packet(svc, ack);  // copy_count stays 1
    aethernet_packet_free(ack);

    aethernet_dtn_run_delivery_scan(svc);
    // slots = max_copies(3) - copy_count(1) = 2 → exactly 2 replication copies.
    assert(count_unicasts_of_type(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE) == 2);

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

// ───── GeohashEpidemicStrategy replication ───────────────

static void replication_prefers_closer_geohash(void) {
    // local geohash "u4"; recipient last geohash "u4pruy" (local_prox=2). Peer
    // "near" (prox 6) and "mid" (prox 4) pass the filter; "far" (prox 0) is below
    // local_prox and is excluded. With 2 slots the copies go to near then mid.
    static const aethernet_peer_info_t PEERS[3] = {
        { .uhid = "mid",  .geohash = "u4pr",   .capabilities = AETHERNET_CAP_DTN_CARRIER, .reliability_score = 90, .is_blocked = false },
        { .uhid = "near", .geohash = "u4pruy", .capabilities = AETHERNET_CAP_DTN_CARRIER, .reliability_score = 50, .is_blocked = false },
        { .uhid = "far",  .geohash = "gbsuv",  .capabilities = AETHERNET_CAP_DTN_CARRIER, .reliability_score = 99, .is_blocked = false },
    };
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("geo-dest");  // force replication
    s.peers = PEERS;
    s.peers_len = 3;
    aethernet_mesh_sender_t sender = make_sender(&s);
    sender.local_geohash = "u4";
    sender.connected_peers = fake_connected_peers;
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    aethernet_mesh_packet_t *bpkt = make_bundle_packet(
        "origin", "geo-dest", "u4pruy", 0, AETHERNET_BUNDLE_PRIORITY_NORMAL, 0x40, NULL, 0);
    aethernet_dtn_handle_packet(svc, bpkt);  // copy_count=1, max_copies=3 → 2 slots
    aethernet_packet_free(bpkt);

    aethernet_dtn_run_delivery_scan(svc);

    assert(count_unicasts_of_type(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE) == 2);
    assert(find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE, "near") != NULL);
    assert(find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE, "mid") != NULL);
    assert(find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE, "far") == NULL);  // too far

    aethernet_dtn_service_free(svc);
    fake_clear(&s);
}

static void replication_sos_fans_out_to_carriers(void) {
    // An SOS bundle ignores geohash ranking and fans out to the first eligible
    // carriers in order up to the copy cap (2 slots → c1, c2).
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("sos-dest");
    s.peers = THREE_CARRIERS;
    s.peers_len = 3;
    aethernet_mesh_sender_t sender = make_sender(&s);
    sender.connected_peers = fake_connected_peers;
    aethernet_dtn_service_t *svc = aethernet_dtn_service_new(&sender);

    aethernet_mesh_packet_t *bpkt = make_bundle_packet(
        "origin", "sos-dest", NULL, 0, AETHERNET_BUNDLE_PRIORITY_SOS, 0x80, NULL, 0);
    aethernet_dtn_handle_packet(svc, bpkt);
    aethernet_packet_free(bpkt);

    aethernet_dtn_run_delivery_scan(svc);

    assert(count_unicasts_of_type(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE) == 2);
    assert(find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE, "c1") != NULL);
    assert(find_unicast(&s, AETHERNET_PACKET_TYPE_DTN_BUNDLE, "c2") != NULL);

    aethernet_dtn_service_free(svc);
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
    RUN(bundle_received_fires_for_local_recipient);
    RUN(bundle_received_decodes_real_hop_count);
    RUN(bundle_received_does_not_fire_for_other_recipient);
    RUN(bundle_received_unset_callback_does_not_crash);
    RUN(transit_bundle_accepts_custody_and_hop_counts);
    RUN(at_capacity_refuses_custody);
    RUN(delivery_receipt_marks_bundle_delivered);
    RUN(positive_custody_ack_increments_copy_count);
    RUN(negative_custody_ack_does_not_increment_copy_count);
    RUN(replication_prefers_closer_geohash);
    RUN(replication_sos_fans_out_to_carriers);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
