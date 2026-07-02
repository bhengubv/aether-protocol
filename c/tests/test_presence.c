// SPDX-License-Identifier: MIT
// Unit tests for presence.c (PresenceService, PacketType PresenceBeacon 21 / PresenceQuery 22).
// Broadcast-transport of a presence beacon/query over the mesh. A fake mesh sender captures broadcasts
// as cloned packets — mirrors the C# PresenceEridAnnounceTests FakeMeshSender that captures Broadcasts.
// Byte-identity gates transcribe fixtures/presence/vectors.json (available + hidden_offline beacons,
// scoped query).

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/presence.h"

#define LOCAL_UHID "aether:local:01"

// ───── FakeMeshSender ────────────────────────────────────
// Captures broadcasts as cloned packets. Mirrors the C# FakeMeshSender.Broadcasts; BroadcastAsync
// returns 4 delivered (matching the C# PresenceEridAnnounceTests FakeMeshSender).

typedef struct {
    aethernet_mesh_packet_t **broadcasts;
    int broadcasts_len;
    int broadcasts_cap;
} fake_state_t;

static int fake_broadcast(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->broadcasts_len == s->broadcasts_cap) {
        s->broadcasts_cap = s->broadcasts_cap ? s->broadcasts_cap * 2 : 8;
        s->broadcasts = (aethernet_mesh_packet_t **)realloc(s->broadcasts, sizeof(*s->broadcasts) * (size_t)s->broadcasts_cap);
    }
    s->broadcasts[s->broadcasts_len++] = aethernet_packet_clone(packet);
    return 4;  // mirror the C# FakeMeshSender: BroadcastAsync returns 4 delivered
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts_len; i++) aethernet_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    memset(s, 0, sizeof(*s));
}

static aethernet_mesh_sender_t make_sender(fake_state_t *state, const char *local_uhid) {
    aethernet_mesh_sender_t s = {0};
    s.local_uhid = local_uhid;
    s.local_geohash = NULL;
    s.send = NULL;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;
}

// ───── Helpers ───────────────────────────────────────────

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

typedef struct {
    int count;
    char erid[64];
    char geohash[64];
    int32_t capabilities;
    int32_t status;
    int64_t sent_at_ms;
    char from_uhid[128];
} beacon_capture_t;

static void on_beacon(const aethernet_presence_beacon_received_t *e, void *ud) {
    beacon_capture_t *c = (beacon_capture_t *)ud;
    c->count++;
    snprintf(c->erid, sizeof(c->erid), "%s", e->beacon->erid ? e->beacon->erid : "");
    snprintf(c->geohash, sizeof(c->geohash), "%s", e->beacon->geohash ? e->beacon->geohash : "");
    c->capabilities = e->beacon->capabilities;
    c->status = e->beacon->status;
    c->sent_at_ms = e->beacon->sent_at_ms;
    snprintf(c->from_uhid, sizeof(c->from_uhid), "%s", e->from_uhid ? e->from_uhid : "");
}

typedef struct {
    int count;
    uint8_t query_id[AETHERNET_PACKET_ID_SIZE];
    char geohash[64];
    char from_uhid[128];
} query_capture_t;

static void on_query(const aethernet_presence_query_received_t *e, void *ud) {
    query_capture_t *c = (query_capture_t *)ud;
    c->count++;
    memcpy(c->query_id, e->query_id, AETHERNET_PACKET_ID_SIZE);
    snprintf(c->geohash, sizeof(c->geohash), "%s", e->geohash ? e->geohash : "");
    snprintf(c->from_uhid, sizeof(c->from_uhid), "%s", e->from_uhid ? e->from_uhid : "");
}

// Build a stack beacon with borrowed string fields (valid for the duration of the serialize/broadcast
// call that consumes it).
static aethernet_presence_beacon_t make_beacon(const char *erid, const char *geohash,
                                               int32_t capabilities, int32_t status,
                                               int64_t sent_at_ms) {
    aethernet_presence_beacon_t b;
    memset(&b, 0, sizeof(b));
    b.erid = erid;
    b.geohash = geohash;
    b.capabilities = capabilities;
    b.status = status;
    b.sent_at_ms = sent_at_ms;
    return b;
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate (available): the beacon serializer must emit exactly the canonical bytes from
// fixtures/presence/vectors.json vector "available". Mirrors the C#
// Beacon_Available_SerializesToCanonicalBytes.
static void beacon_available_serializes_to_canonical_bytes(void) {
    aethernet_presence_beacon_t b = make_beacon("3B38HPPFG9JXE37Q", "u4pru", 73, 1, 1700000000000LL);
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_presence_beacon_payload_serialize(&b, &json, &len));
    const char *expected =
        "{\"erid\":\"3B38HPPFG9JXE37Q\",\"geohash\":\"u4pru\",\"capabilities\":73,\"status\":1,\"sent_at_ms\":1700000000000}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);
}

// Byte-identity gate (hidden_offline): empty geohash serializes as "", capabilities/status/sent_at_ms
// as bare ints (0/5/0). Mirrors the C# Beacon_HiddenOffline_SerializesToCanonicalBytes.
static void beacon_hidden_offline_serializes_to_canonical_bytes(void) {
    aethernet_presence_beacon_t b = make_beacon("0Z5BD0HB1Q7W76MY", "", 0, 5, 0);
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_presence_beacon_payload_serialize(&b, &json, &len));
    const char *expected =
        "{\"erid\":\"0Z5BD0HB1Q7W76MY\",\"geohash\":\"\",\"capabilities\":0,\"status\":5,\"sent_at_ms\":0}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');
    free(json);
}

// Byte-identity gate (scoped): the query serializer must emit exactly the canonical bytes from
// fixtures/presence/vectors.json vector "scoped". Mirrors the C# Query_SerializesToCanonicalBytes.
static void query_serializes_to_canonical_bytes(void) {
    // Vector "scoped": query_id 11112222-3333-4444-5555-666677778888, geohash u4pru.
    uint8_t id[AETHERNET_PACKET_ID_SIZE] = {
        0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44,
        0x55, 0x55, 0x66, 0x66, 0x77, 0x77, 0x88, 0x88 };
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_presence_query_payload_serialize(id, "u4pru", &json, &len));
    const char *expected =
        "{\"query_id\":\"11112222-3333-4444-5555-666677778888\",\"geohash\":\"u4pru\"}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');
    free(json);
}

// broadcast_beacon emits exactly one PresenceBeacon packet (source local UHID, dest "*", default TTL),
// returns the fan-out count, and handle_packet on that packet fires the beacon callback with the
// decoded beacon + packet source as from_uhid. Mirrors the C#
// BroadcastBeacon_EmitsBeaconPacket_AndHandleRaisesEvent.
static void broadcast_beacon_emits_packet_and_handle_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_presence_service_t *svc = aethernet_presence_service_new(&sender);

    beacon_capture_t cap = {0};
    aethernet_presence_set_beacon_received_cb(svc, on_beacon, &cap);

    aethernet_presence_beacon_t beacon =
        make_beacon("3B38HPPFG9JXE37Q", "u4pru", 73, 1, 1700000000000LL);

    int reached = -1;
    assert(aethernet_presence_broadcast_beacon(svc, &beacon, &reached));
    assert(reached == 4);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_PRESENCE_BEACON);
    assert(strcmp(s.broadcasts[0]->source_uhid, "aether:alice:01") == 0);
    assert(strcmp(s.broadcasts[0]->destination_uhid, "*") == 0);
    assert(s.broadcasts[0]->ttl == AETHERNET_DEFAULT_TTL);

    assert(aethernet_presence_handle_packet(svc, s.broadcasts[0]));
    assert(cap.count == 1);
    assert(strcmp(cap.erid, "3B38HPPFG9JXE37Q") == 0);
    assert(strcmp(cap.geohash, "u4pru") == 0);
    assert(cap.capabilities == 73);
    assert(cap.status == 1);
    assert(cap.sent_at_ms == 1700000000000LL);
    assert(strcmp(cap.from_uhid, "aether:alice:01") == 0);

    aethernet_presence_service_free(svc);
    fake_clear(&s);
}

// query mints a non-zero query id and broadcasts exactly one PresenceQuery carrying that id + geohash;
// handle_packet on it fires the query callback with the echoed id + geohash. Mirrors the C#
// Query_EmitsQueryPacket_AndHandleRaisesEvent.
static void query_emits_packet_and_handle_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:bob:02");
    aethernet_presence_service_t *svc = aethernet_presence_service_new(&sender);

    query_capture_t cap = {0};
    aethernet_presence_set_query_received_cb(svc, on_query, &cap);

    uint8_t qid[AETHERNET_PACKET_ID_SIZE];
    int reached = -1;
    assert(aethernet_presence_query(svc, "u4pru", qid, &reached));
    assert(reached == 4);

    // query id is non-zero (Guid.Empty check in C#).
    uint8_t zero[AETHERNET_PACKET_ID_SIZE] = {0};
    assert(memcmp(qid, zero, AETHERNET_PACKET_ID_SIZE) != 0);

    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_PRESENCE_QUERY);
    assert(strcmp(s.broadcasts[0]->source_uhid, "aether:bob:02") == 0);
    assert(strcmp(s.broadcasts[0]->destination_uhid, "*") == 0);

    // The sent body carries the minted query id in canonical dashed form + the geohash.
    char id_canon[37];
    snprintf(id_canon, sizeof(id_canon),
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        qid[0], qid[1], qid[2], qid[3], qid[4], qid[5], qid[6], qid[7],
        qid[8], qid[9], qid[10], qid[11], qid[12], qid[13], qid[14], qid[15]);
    assert(strstr((const char *)s.broadcasts[0]->payload, id_canon) != NULL);
    assert(strstr((const char *)s.broadcasts[0]->payload, "\"geohash\":\"u4pru\"") != NULL);

    assert(aethernet_presence_handle_packet(svc, s.broadcasts[0]));
    assert(cap.count == 1);
    assert(memcmp(cap.query_id, qid, AETHERNET_PACKET_ID_SIZE) == 0);
    assert(strcmp(cap.geohash, "u4pru") == 0);
    assert(strcmp(cap.from_uhid, "aether:bob:02") == 0);

    aethernet_presence_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false), no callback. Mirrors the C#
// Presence_Handle_WrongType_ReturnsFalse.
static void handle_wrong_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_presence_service_t *svc = aethernet_presence_service_new(&sender);

    beacon_capture_t bcap = {0};
    query_capture_t  qcap = {0};
    aethernet_presence_set_beacon_received_cb(svc, on_beacon, &bcap);
    aethernet_presence_set_query_received_cb(svc, on_query, &qcap);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DATA;
    aethernet_packet_set_source_uhid(pkt, "aether:x:01");
    const char *body = "{}";
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    assert(aethernet_presence_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(bcap.count == 0);
    assert(qcap.count == 0);

    aethernet_presence_service_free(svc);
    fake_clear(&s);
}

// A PresenceBeacon with an empty erid is a benign drop (false), no callback. Mirrors the C#
// Presence_Handle_BeaconWithEmptyErid_ReturnsFalse. Also covers the malformed-JSON benign-drop path.
static void handle_beacon_empty_erid_and_malformed_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_presence_service_t *svc = aethernet_presence_service_new(&sender);

    beacon_capture_t cap = {0};
    aethernet_presence_set_beacon_received_cb(svc, on_beacon, &cap);

    // Valid JSON but empty erid → benign drop.
    aethernet_mesh_packet_t *empty = aethernet_packet_new();
    empty->type = AETHERNET_PACKET_TYPE_PRESENCE_BEACON;
    aethernet_packet_set_source_uhid(empty, "aether:x:01");
    const char *empty_erid =
        "{\"erid\":\"\",\"geohash\":\"u4pru\",\"capabilities\":0,\"status\":1,\"sent_at_ms\":0}";
    aethernet_packet_set_payload(empty, (const uint8_t *)empty_erid, (uint32_t)strlen(empty_erid));
    assert(aethernet_presence_handle_packet(svc, empty) == false);
    aethernet_packet_free(empty);

    // Garbage JSON on a beacon packet → benign drop.
    aethernet_mesh_packet_t *bad = aethernet_packet_new();
    bad->type = AETHERNET_PACKET_TYPE_PRESENCE_BEACON;
    aethernet_packet_set_source_uhid(bad, "aether:x:01");
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(bad, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(aethernet_presence_handle_packet(svc, bad) == false);
    aethernet_packet_free(bad);

    assert(cap.count == 0);

    aethernet_presence_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether Presence WIRE Service — Unit Tests\n");
    printf("=========================================\n");

    RUN(beacon_available_serializes_to_canonical_bytes);
    RUN(beacon_hidden_offline_serializes_to_canonical_bytes);
    RUN(query_serializes_to_canonical_bytes);
    RUN(broadcast_beacon_emits_packet_and_handle_raises_event);
    RUN(query_emits_packet_and_handle_raises_event);
    RUN(handle_wrong_type_returns_false);
    RUN(handle_beacon_empty_erid_and_malformed_returns_false);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
