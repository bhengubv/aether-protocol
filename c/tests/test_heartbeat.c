// SPDX-License-Identifier: MIT
// Unit tests for heartbeat.c (HeartbeatService, PacketType 10).

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/heartbeat.h"

#define LOCAL_UHID "aether:local:01"

// ───── FakeMeshSender ────────────────────────────────────

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
    return 1;  // mirror the C# FakeMeshSender: BroadcastAsync returns 1 delivered
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

// Build a Heartbeat packet from `source` carrying the canonical payload for (sequence, sent_at_ms),
// using the same public serializer the wire path uses.
static aethernet_mesh_packet_t *heartbeat_from(const char *source, int32_t sequence, int64_t sent_at_ms) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_HEARTBEAT;
    aethernet_packet_set_source_uhid(p, source);
    aethernet_packet_set_destination_uhid(p, "*");
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    bool ok = aethernet_heartbeat_payload_serialize(sequence, sent_at_ms, &body, &body_len);
    assert(ok);
    aethernet_packet_set_payload(p, body, body_len);
    free(body);
    return p;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

typedef struct {
    int count;
    char uhid[64];
    int32_t last_sequence;
    int64_t last_sent_at_ms;
} seen_capture_t;

static void on_peer_seen(const aethernet_peer_liveness_t *liveness, void *ud) {
    seen_capture_t *c = (seen_capture_t *)ud;
    c->count++;
    snprintf(c->uhid, sizeof(c->uhid), "%s", liveness->uhid ? liveness->uhid : "");
    c->last_sequence = liveness->last_sequence;
    c->last_sent_at_ms = liveness->last_sent_at_ms;
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate: aethernet_heartbeat_payload_serialize must emit exactly the canonical bytes
// from fixtures/heartbeat/vectors.json for every language SDK.
static void payload_serializes_to_canonical_bytes(void) {
    // Vector "basic": sequence 1, sent_at_ms 1700000000000
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_heartbeat_payload_serialize(1, 1700000000000LL, &json, &len));
    const char *expected1 = "{\"sequence\":1,\"sent_at_ms\":1700000000000}";
    assert(len == (uint32_t)strlen(expected1));
    assert(memcmp(json, expected1, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);

    // Vector "zero": sequence 0, sent_at_ms 0
    json = NULL; len = 0;
    assert(aethernet_heartbeat_payload_serialize(0, 0, &json, &len));
    const char *expected2 = "{\"sequence\":0,\"sent_at_ms\":0}";
    assert(len == (uint32_t)strlen(expected2));
    assert(memcmp(json, expected2, len) == 0);
    free(json);
}

// Two sends broadcast two Heartbeat packets (TTL 1, dest "*") with incrementing sequence 1 then 2.
// Mirrors Send_BroadcastsHeartbeat_WithIncrementingSequence.
static void send_broadcasts_with_incrementing_sequence(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_heartbeat_service_t *svc = aethernet_heartbeat_service_new(&sender);

    int d1 = aethernet_heartbeat_send(svc);
    int d2 = aethernet_heartbeat_send(svc);
    assert(d1 == 1);  // fake sender reports 1 delivered
    assert(d2 == 1);

    assert(s.broadcasts_len == 2);
    for (int i = 0; i < 2; i++) {
        assert(s.broadcasts[i]->type == AETHERNET_PACKET_TYPE_HEARTBEAT);
        assert(s.broadcasts[i]->ttl == 1);
        assert(strcmp(s.broadcasts[i]->source_uhid, LOCAL_UHID) == 0);
        assert(strcmp(s.broadcasts[i]->destination_uhid, "*") == 0);
    }
    // First beat is sequence 1, second is 2 (canonical serialization).
    const char *want1 = "{\"sequence\":1,";
    const char *want2 = "{\"sequence\":2,";
    assert(memcmp(s.broadcasts[0]->payload, want1, strlen(want1)) == 0);
    assert(memcmp(s.broadcasts[1]->payload, want2, strlen(want2)) == 0);

    aethernet_heartbeat_service_free(svc);
    fake_clear(&s);
}

// Handling a foreign heartbeat records the peer, fires the peer-seen callback with the decoded
// fields, and adds the peer to the known set. Mirrors Handle_RecordsPeerAndRaisesEvent.
static void handle_records_peer_and_fires(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_heartbeat_service_t *svc = aethernet_heartbeat_service_new(&sender);

    seen_capture_t cap = {0};
    aethernet_heartbeat_set_peer_seen_cb(svc, on_peer_seen, &cap);

    aethernet_mesh_packet_t *pkt = heartbeat_from("aether:peer:aa", 7, 1700000000000LL);
    bool ok = aethernet_heartbeat_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(ok);
    assert(cap.count == 1);
    assert(strcmp(cap.uhid, "aether:peer:aa") == 0);
    assert(cap.last_sequence == 7);
    assert(cap.last_sent_at_ms == 1700000000000LL);

    aethernet_peer_liveness_t *known = NULL;
    int n = 0;
    assert(aethernet_heartbeat_get_known_peers(svc, &known, &n) == 1);
    assert(n == 1);
    assert(strcmp(known[0].uhid, "aether:peer:aa") == 0);
    assert(known[0].last_sequence == 7);
    aethernet_peer_liveness_list_free(known, n);

    aethernet_heartbeat_service_free(svc);
    fake_clear(&s);
}

// A second heartbeat from the same peer refreshes the single record (no duplicate). Mirrors
// Handle_RefreshesExistingPeer.
static void handle_refreshes_existing_peer(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_heartbeat_service_t *svc = aethernet_heartbeat_service_new(&sender);

    aethernet_mesh_packet_t *p1 = heartbeat_from("aether:peer:aa", 1, 1000LL);
    aethernet_mesh_packet_t *p2 = heartbeat_from("aether:peer:aa", 2, 2000LL);
    assert(aethernet_heartbeat_handle_packet(svc, p1));
    assert(aethernet_heartbeat_handle_packet(svc, p2));
    aethernet_packet_free(p1);
    aethernet_packet_free(p2);

    aethernet_peer_liveness_t *known = NULL;
    int n = 0;
    assert(aethernet_heartbeat_get_known_peers(svc, &known, &n) == 1);
    assert(n == 1);  // still a single peer record
    assert(known[0].last_sequence == 2);
    aethernet_peer_liveness_list_free(known, n);

    aethernet_heartbeat_service_free(svc);
    fake_clear(&s);
}

// Our own heartbeat echoed back is ignored (false, no peer recorded). Mirrors
// Handle_OwnHeartbeat_IsIgnored.
static void handle_own_heartbeat_is_ignored(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_heartbeat_service_t *svc = aethernet_heartbeat_service_new(&sender);

    aethernet_mesh_packet_t *pkt = heartbeat_from(LOCAL_UHID, 1, 1000LL);
    bool ok = aethernet_heartbeat_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);
    assert(!ok);

    aethernet_peer_liveness_t *known = NULL;
    int n = 0;
    assert(aethernet_heartbeat_get_known_peers(svc, &known, &n) == 0);
    assert(n == 0);
    aethernet_peer_liveness_list_free(known, n);

    aethernet_heartbeat_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false). Mirrors Handle_WrongPacketType_ReturnsFalse.
static void handle_wrong_packet_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_heartbeat_service_t *svc = aethernet_heartbeat_service_new(&sender);

    aethernet_mesh_packet_t *pkt = heartbeat_from("aether:peer:aa", 1, 1000LL);
    pkt->type = AETHERNET_PACKET_TYPE_DATA;  // not a Heartbeat
    assert(!aethernet_heartbeat_handle_packet(svc, pkt));
    aethernet_packet_free(pkt);

    aethernet_heartbeat_service_free(svc);
    fake_clear(&s);
}

// A malformed payload is a benign drop (false). Not explicitly in the C# theory data, but mirrors
// the C# HandleAsync JsonException path (returns false).
static void handle_malformed_payload_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_heartbeat_service_t *svc = aethernet_heartbeat_service_new(&sender);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_HEARTBEAT;
    aethernet_packet_set_source_uhid(pkt, "aether:peer:aa");
    aethernet_packet_set_destination_uhid(pkt, "*");
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(pkt, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(!aethernet_heartbeat_handle_packet(svc, pkt));
    aethernet_packet_free(pkt);

    aethernet_peer_liveness_t *known = NULL;
    int n = 0;
    assert(aethernet_heartbeat_get_known_peers(svc, &known, &n) == 0);
    aethernet_peer_liveness_list_free(known, n);

    aethernet_heartbeat_service_free(svc);
    fake_clear(&s);
}

// A just-received heartbeat is live within a generous window; a negative window excludes it
// (deterministic proof the filter filters, no wall-clock race). Mirrors
// GetLivePeers_IncludesRecentlySeenPeer.
static void get_live_peers_includes_recently_seen(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_heartbeat_service_t *svc = aethernet_heartbeat_service_new(&sender);

    aethernet_mesh_packet_t *pkt = heartbeat_from("aether:peer:aa", 1, 1000LL);
    assert(aethernet_heartbeat_handle_packet(svc, pkt));
    aethernet_packet_free(pkt);

    aethernet_peer_liveness_t *live = NULL;
    int n = 0;
    assert(aethernet_heartbeat_get_live_peers(svc, 3600, &live, &n) == 1);
    assert(n == 1);
    assert(strcmp(live[0].uhid, "aether:peer:aa") == 0);
    aethernet_peer_liveness_list_free(live, n);

    // Negative window pushes the recency horizon into the future → excludes even a just-seen peer.
    live = NULL; n = 0;
    assert(aethernet_heartbeat_get_live_peers(svc, -1, &live, &n) == 0);
    assert(n == 0);
    aethernet_peer_liveness_list_free(live, n);

    aethernet_heartbeat_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether Heartbeat Service — Unit Tests\n");
    printf("=====================================\n");

    RUN(payload_serializes_to_canonical_bytes);
    RUN(send_broadcasts_with_incrementing_sequence);
    RUN(handle_records_peer_and_fires);
    RUN(handle_refreshes_existing_peer);
    RUN(handle_own_heartbeat_is_ignored);
    RUN(handle_wrong_packet_type_returns_false);
    RUN(handle_malformed_payload_returns_false);
    RUN(get_live_peers_includes_recently_seen);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
