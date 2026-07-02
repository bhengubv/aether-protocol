// SPDX-License-Identifier: MIT
// Unit tests for sos.c (SosBroadcastService).

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/sos.h"

#define LOCAL_UHID "local"

// ───── FakeMeshSender ────────────────────────────────────

typedef struct {
    aethernet_mesh_packet_t **broadcasts;
    int broadcasts_len;
    int broadcasts_cap;
    aethernet_mesh_packet_t **unicasts;
    char **unicasts_next_hops;
    int unicasts_len;
    int unicasts_cap;
} fake_state_t;

static bool fake_send(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
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

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts_len; i++) aethernet_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    for (int i = 0; i < s->unicasts_len; i++) {
        aethernet_packet_free(s->unicasts[i]);
        free(s->unicasts_next_hops[i]);
    }
    free(s->unicasts);
    free(s->unicasts_next_hops);
    memset(s, 0, sizeof(*s));
}

static aethernet_mesh_sender_t make_sender(fake_state_t *state) {
    aethernet_mesh_sender_t s = {0};
    s.local_uhid = LOCAL_UHID;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;
}

// ───── Helpers ───────────────────────────────────────────

static aethernet_mesh_packet_t *new_sos_packet(const char *src, int32_t ttl) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_SOS_BROADCAST;
    aethernet_packet_set_source_uhid(p, src);
    aethernet_packet_set_destination_uhid(p, "");
    p->ttl = ttl;
    p->priority = AETHERNET_SOS_PRIORITY;
    const char *body = "{\"broadcast_id\":\"00000000-0000-0000-0000-000000000000\","
                       "\"broadcast_type\":\"sos\",\"message\":\"help\","
                       "\"latitude\":0,\"longitude\":0,\"geohash\":null}";
    aethernet_packet_set_payload(p, (const uint8_t *)body, strlen(body));
    return p;
}

// Build a SosAck packet from `responder` for the given 16-byte broadcast id, stamping the given
// received_at_ms. Uses the same public serializer the wire path uses.
static aethernet_mesh_packet_t *make_ack(const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE],
                                      const char *responder, int64_t received_at_ms) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_SOS_ACK;
    aethernet_packet_set_source_uhid(p, responder);
    aethernet_packet_set_destination_uhid(p, LOCAL_UHID);
    p->ttl = AETHERNET_SOS_TTL;
    p->priority = AETHERNET_SOS_PRIORITY;
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    bool ok = aethernet_sos_ack_payload_serialize(broadcast_id, received_at_ms, &body, &body_len);
    assert(ok);
    aethernet_packet_set_payload(p, body, body_len);
    free(body);
    return p;
}

// Extract the 16-byte broadcast id from a SosAck packet payload via the C JSON of the wire format:
// the payload is exactly {"broadcast_id":"<36-char uuid>",...}, so the uuid string starts after the
// fixed prefix. Test-only convenience; the SDK parses via cJSON in aethernet_sos_handle_ack.
static void broadcast_id_from_ack(const aethernet_mesh_packet_t *ack, uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    const char *prefix = "{\"broadcast_id\":\"";
    size_t plen = strlen(prefix);
    assert(ack->payload_len > plen + 36);
    assert(memcmp(ack->payload, prefix, plen) == 0);
    char hex[3] = {0};
    const char *uuid = (const char *)ack->payload + plen;  // 36-char dashed uuid
    int byte = 0;
    for (int i = 0; i < 36; i++) {
        if (uuid[i] == '-') continue;
        hex[0] = uuid[i];
        hex[1] = uuid[i + 1];
        out[byte++] = (uint8_t)strtoul(hex, NULL, 16);
        i++;
    }
    assert(byte == AETHERNET_PACKET_ID_SIZE);
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

static int received_count = 0;
static void on_sos(const aethernet_sos_alert_t *alert, void *ud) {
    (void)alert; (void)ud;
    received_count++;
}

typedef struct {
    int count;
    char broadcast_type[32];
    char message[64];
    double latitude;
    double longitude;
    char geohash[32];
} sos_capture_t;

static void on_sos_capture(const aethernet_sos_alert_t *alert, void *ud) {
    sos_capture_t *c = (sos_capture_t *)ud;
    c->count++;
    snprintf(c->broadcast_type, sizeof(c->broadcast_type), "%s",
             alert->broadcast_type ? alert->broadcast_type : "");
    snprintf(c->message, sizeof(c->message), "%s",
             alert->message ? alert->message : "");
    c->latitude = alert->latitude;
    c->longitude = alert->longitude;
    snprintf(c->geohash, sizeof(c->geohash), "%s",
             alert->geohash ? alert->geohash : "");
}

typedef struct {
    int count;
    uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE];
    char responder[64];
    int total;
} ack_capture_t;

static void on_sos_acknowledged(const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE],
                                const char *responder, int total, void *ud) {
    ack_capture_t *c = (ack_capture_t *)ud;
    c->count++;
    memcpy(c->broadcast_id, broadcast_id, AETHERNET_PACKET_ID_SIZE);
    snprintf(c->responder, sizeof(c->responder), "%s", responder ? responder : "");
    c->total = total;
}

// ───── Tests ─────────────────────────────────────────────

static void broadcast_floods_and_stores_alert(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    int rc = aethernet_sos_broadcast(svc, "sos", "help", -33.9, 18.4, NULL);
    assert(rc == 0);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_SOS_BROADCAST);
    assert(s.broadcasts[0]->ttl == AETHERNET_SOS_TTL);
    assert(s.broadcasts[0]->priority == AETHERNET_SOS_PRIORITY);

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void broadcast_rate_limited_after_max(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    for (int i = 0; i < AETHERNET_MAX_SOS_BROADCASTS_PER_HOUR; i++) {
        int rc = aethernet_sos_broadcast(svc, "sos", "h", 0, 0, NULL);
        assert(rc == 0);
    }
    int rc = aethernet_sos_broadcast(svc, "sos", "h", 0, 0, NULL);
    assert(rc == 1); // rate-limited

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void broadcast_rejects_null_type(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    int rc = aethernet_sos_broadcast(svc, NULL, "help", 0, 0, NULL);
    assert(rc == -1);

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_drops_duplicate_packet_id(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", AETHERNET_SOS_TTL);
    aethernet_sos_handle_packet(svc, pkt);
    int after_first = s.broadcasts_len;

    // Re-feed the same packet id
    aethernet_mesh_packet_t *pkt2 = aethernet_packet_clone(pkt);
    aethernet_sos_handle_packet(svc, pkt2);
    assert(s.broadcasts_len == after_first);

    aethernet_packet_free(pkt);
    aethernet_packet_free(pkt2);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_ignores_self_originated(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet(LOCAL_UHID, AETHERNET_SOS_TTL);
    aethernet_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 0);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_rebroadcasts_when_ttl_allows(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", 5);
    aethernet_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->ttl == 4);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_does_not_rebroadcast_when_ttl_exhausted(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", 1);
    aethernet_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 0);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_invokes_received_callback(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);
    received_count = 0;
    aethernet_sos_set_received_cb(svc, on_sos, NULL);

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", AETHERNET_SOS_TTL);
    aethernet_sos_handle_packet(svc, pkt);
    assert(received_count == 1);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_decodes_real_sos_body(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    sos_capture_t cap = {0};
    aethernet_sos_set_received_cb(svc, on_sos_capture, &cap);

    // A real SOS envelope from a remote node — a panic alert with a message and a
    // GPS fix. The handler must DECODE these from the payload; the old stub dropped
    // message/lat/long/geohash and hardcoded broadcast_type "sos".
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_SOS_BROADCAST;
    aethernet_packet_set_source_uhid(pkt, "remote-alice");
    aethernet_packet_set_destination_uhid(pkt, "");
    pkt->ttl = 1;  // ttl == 1 → not rebroadcast
    pkt->priority = AETHERNET_SOS_PRIORITY;
    const char *body =
        "{\"broadcast_id\":\"11111111-2222-3333-4444-555555555555\","
        "\"broadcast_type\":\"panic\",\"message\":\"trapped, water rising\","
        "\"latitude\":-33.918600,\"longitude\":18.423300,\"geohash\":\"k3vp\"}";
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));

    aethernet_sos_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(cap.count == 1);
    assert(strcmp(cap.broadcast_type, "panic") == 0);             // decoded, not "sos"
    assert(strcmp(cap.message, "trapped, water rising") == 0);    // decoded, not dropped
    assert(cap.latitude < -33.9185 && cap.latitude > -33.9187);   // decoded GPS lat
    assert(cap.longitude > 18.4232 && cap.longitude < 18.4234);   // decoded GPS lon
    assert(strcmp(cap.geohash, "k3vp") == 0);                     // decoded, not dropped

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void resolve_with_unknown_id_is_safe(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    uint8_t id[AETHERNET_PACKET_ID_SIZE] = {0};
    aethernet_sos_resolve(svc, id);

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

// ───── SosAck path ───────────────────────────────────────

// Byte-identity gate: aethernet_sos_ack_payload_serialize must emit exactly the canonical bytes
// from fixtures/sos/vectors.json for every language SDK.
static void sos_ack_payload_serializes_to_canonical_bytes(void) {
    // Vector 1: id 0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f, ms 1700000000000
    uint8_t id1[AETHERNET_PACKET_ID_SIZE] = {
        0x0f, 0x7e, 0x5d, 0x3c, 0x1a, 0x2b, 0x4c, 0x5d,
        0x8e, 0x9f, 0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f };
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_sos_ack_payload_serialize(id1, 1700000000000LL, &json, &len));
    const char *expected1 =
        "{\"broadcast_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"received_at_ms\":1700000000000}";
    assert(len == (uint32_t)strlen(expected1));
    assert(memcmp(json, expected1, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);

    // Vector 2: all-zero id, ms 0
    uint8_t id2[AETHERNET_PACKET_ID_SIZE] = {0};
    json = NULL; len = 0;
    assert(aethernet_sos_ack_payload_serialize(id2, 0, &json, &len));
    const char *expected2 =
        "{\"broadcast_id\":\"00000000-0000-0000-0000-000000000000\",\"received_at_ms\":0}";
    assert(len == (uint32_t)strlen(expected2));
    assert(memcmp(json, expected2, len) == 0);
    free(json);
}

// A foreign SOS triggers exactly one directed SosAck back to the originator, carrying the SOS's
// broadcast id, sent via the directed send (unicast) — not broadcast. Mirrors
// Handle_ReceivingSos_SendsDirectedAckToOriginator.
static void handle_foreign_sos_sends_directed_ack(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    // The known broadcast id embedded in new_sos_packet's body.
    uint8_t expected_bid[AETHERNET_PACKET_ID_SIZE] = {0};  // 00000000-0000-...-000000000000

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", AETHERNET_SOS_TTL);
    aethernet_sos_handle_packet(svc, pkt);

    // Exactly one directed ack, addressed to the originator.
    assert(s.unicasts_len == 1);
    assert(s.unicasts[0]->type == AETHERNET_PACKET_TYPE_SOS_ACK);
    assert(strcmp(s.unicasts_next_hops[0], "alice") == 0);
    assert(strcmp(s.unicasts[0]->destination_uhid, "alice") == 0);
    assert(strcmp(s.unicasts[0]->source_uhid, LOCAL_UHID) == 0);

    // The ack carries the SOS's broadcast id.
    uint8_t got_bid[AETHERNET_PACKET_ID_SIZE];
    broadcast_id_from_ack(s.unicasts[0], got_bid);
    assert(memcmp(got_bid, expected_bid, AETHERNET_PACKET_ID_SIZE) == 0);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

// Our own SOS (re-handled) must not generate an ack. Mirrors Handle_OwnSos_DoesNotAck.
static void handle_own_sos_does_not_ack(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet(LOCAL_UHID, AETHERNET_SOS_TTL);
    aethernet_sos_handle_packet(svc, pkt);
    assert(s.unicasts_len == 0);  // no directed ack for a self-originated SOS

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

// On the originator, handling an ack records the responder, bumps distinct count to 1, and fires
// the acknowledged callback. Mirrors HandleAck_OnOriginator_RecordsResponderAndRaisesEvent.
static void handle_ack_on_originator_records_and_fires(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    // Originate a real SOS so its alert lives in the active set; recover its broadcast id from the
    // broadcast packet the fake sender captured.
    int rc = aethernet_sos_broadcast(svc, "fire", "north wing", -26.1, 28.0, NULL);
    assert(rc == 0);
    assert(s.broadcasts_len == 1);
    uint8_t bid[AETHERNET_PACKET_ID_SIZE];
    broadcast_id_from_ack(s.broadcasts[0], bid);  // SOS body has the same {"broadcast_id":"..."} prefix

    ack_capture_t cap = {0};
    aethernet_sos_set_acknowledged_cb(svc, on_sos_acknowledged, &cap);

    aethernet_mesh_packet_t *ack = make_ack(bid, "responder-cc", 1700000000000LL);
    rc = aethernet_sos_handle_ack(svc, ack);
    assert(rc == 0);

    assert(cap.count == 1);
    assert(memcmp(cap.broadcast_id, bid, AETHERNET_PACKET_ID_SIZE) == 0);
    assert(strcmp(cap.responder, "responder-cc") == 0);
    assert(cap.total == 1);

    aethernet_packet_free(ack);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

// The same responder acking twice is counted once (dedup) — callback fires only once. Mirrors
// HandleAck_DuplicateResponder_CountedOnce.
static void handle_ack_duplicate_responder_counted_once(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    int rc = aethernet_sos_broadcast(svc, "medical", NULL, 0, 0, NULL);
    assert(rc == 0);
    uint8_t bid[AETHERNET_PACKET_ID_SIZE];
    broadcast_id_from_ack(s.broadcasts[0], bid);

    ack_capture_t cap = {0};
    aethernet_sos_set_acknowledged_cb(svc, on_sos_acknowledged, &cap);

    aethernet_mesh_packet_t *ack1 = make_ack(bid, "responder-cc", 1700000000000LL);
    aethernet_mesh_packet_t *ack2 = make_ack(bid, "responder-cc", 1700000000001LL);
    assert(aethernet_sos_handle_ack(svc, ack1) == 0);
    assert(aethernet_sos_handle_ack(svc, ack2) == 0);

    assert(cap.count == 1);  // second (duplicate) responder fired no callback
    assert(cap.total == 1);

    aethernet_packet_free(ack1);
    aethernet_packet_free(ack2);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

// Two distinct responders drive the distinct count to 2. Mirrors
// HandleAck_TwoDistinctResponders_CountsTwo.
static void handle_ack_two_distinct_responders_counts_two(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    int rc = aethernet_sos_broadcast(svc, "medical", NULL, 0, 0, NULL);
    assert(rc == 0);
    uint8_t bid[AETHERNET_PACKET_ID_SIZE];
    broadcast_id_from_ack(s.broadcasts[0], bid);

    ack_capture_t cap = {0};
    aethernet_sos_set_acknowledged_cb(svc, on_sos_acknowledged, &cap);

    aethernet_mesh_packet_t *ack1 = make_ack(bid, "responder-cc", 1700000000000LL);
    aethernet_mesh_packet_t *ack2 = make_ack(bid, "responder-dd", 1700000000000LL);
    assert(aethernet_sos_handle_ack(svc, ack1) == 0);
    assert(aethernet_sos_handle_ack(svc, ack2) == 0);

    assert(cap.count == 2);
    assert(cap.total == 2);  // last callback carried the running distinct count

    aethernet_packet_free(ack1);
    aethernet_packet_free(ack2);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

// An ack for an SOS this node did not originate is a silent no-op. Mirrors
// HandleAck_UnknownBroadcast_IsNoOp.
static void handle_ack_unknown_broadcast_is_noop(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    ack_capture_t cap = {0};
    aethernet_sos_set_acknowledged_cb(svc, on_sos_acknowledged, &cap);

    uint8_t unknown[AETHERNET_PACKET_ID_SIZE] = {
        0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22,
        0x11, 0x00, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff };
    aethernet_mesh_packet_t *ack = make_ack(unknown, "responder-cc", 1700000000000LL);
    int rc = aethernet_sos_handle_ack(svc, ack);
    assert(rc == 0);        // benign no-op returns success
    assert(cap.count == 0); // callback never fired

    aethernet_packet_free(ack);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is rejected with an error code. Mirrors HandleAck_WrongPacketType_Throws.
static void handle_ack_wrong_type_returns_error(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    uint8_t bid[AETHERNET_PACKET_ID_SIZE] = {0};
    aethernet_mesh_packet_t *ack = make_ack(bid, "responder-cc", 1700000000000LL);
    ack->type = AETHERNET_PACKET_TYPE_DATA;  // not a SosAck
    int rc = aethernet_sos_handle_ack(svc, ack);
    assert(rc == -1);

    aethernet_packet_free(ack);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether SOS Service — Unit Tests\n");
    printf("================================\n");

    RUN(broadcast_floods_and_stores_alert);
    RUN(broadcast_rate_limited_after_max);
    RUN(broadcast_rejects_null_type);
    RUN(handle_drops_duplicate_packet_id);
    RUN(handle_ignores_self_originated);
    RUN(handle_rebroadcasts_when_ttl_allows);
    RUN(handle_does_not_rebroadcast_when_ttl_exhausted);
    RUN(handle_invokes_received_callback);
    RUN(handle_decodes_real_sos_body);
    RUN(resolve_with_unknown_id_is_safe);

    RUN(sos_ack_payload_serializes_to_canonical_bytes);
    RUN(handle_foreign_sos_sends_directed_ack);
    RUN(handle_own_sos_does_not_ack);
    RUN(handle_ack_on_originator_records_and_fires);
    RUN(handle_ack_duplicate_responder_counted_once);
    RUN(handle_ack_two_distinct_responders_counts_two);
    RUN(handle_ack_unknown_broadcast_is_noop);
    RUN(handle_ack_wrong_type_returns_error);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
