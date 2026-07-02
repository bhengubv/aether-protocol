// SPDX-License-Identifier: MIT
// Unit tests for space_breadcrumb.c (SpaceBreadcrumbService, PacketType SpaceBreadcrumb 40).
// Broadcast-transport of a SpaceBreadcrumb over the mesh. A fake mesh sender captures broadcasts as
// cloned packets — mirrors the C# WirePacketsTests FakeMeshSender that captures Broadcasts.
// Byte-identity gates transcribe fixtures/space/vectors.json (emergency_signed + notice_unsigned).

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/space.h"
#include "aethernet/space_breadcrumb.h"

#define LOCAL_UHID "aether:local:01"

// ───── FakeMeshSender ────────────────────────────────────
// Captures broadcasts as cloned packets. Mirrors the C# FakeMeshSender.Broadcasts; BroadcastAsync
// returns 2 delivered.

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
    return 2;  // mirror the C# FakeMeshSender: BroadcastAsync returns 2 delivered
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
    char content_hash[128];
    char geo_hash[64];
    char anchor_uhid[128];
    int64_t created_at_ms;
    int32_t ttl_hours;
    uint8_t type;
    uint32_t signature_len;
    uint8_t signature0;
    uint8_t signature_last;
} recv_capture_t;

static void on_breadcrumb(const aethernet_space_breadcrumb_t *c, void *ud) {
    recv_capture_t *cap = (recv_capture_t *)ud;
    cap->count++;
    snprintf(cap->content_hash, sizeof(cap->content_hash), "%s", c->content_hash ? c->content_hash : "");
    snprintf(cap->geo_hash, sizeof(cap->geo_hash), "%s", c->geo_hash ? c->geo_hash : "");
    snprintf(cap->anchor_uhid, sizeof(cap->anchor_uhid), "%s", c->anchor_uhid ? c->anchor_uhid : "");
    cap->created_at_ms = c->created_at_ms;
    cap->ttl_hours = c->ttl_hours;
    cap->type = c->type;
    cap->signature_len = c->signature_len;
    cap->signature0 = (c->signature && c->signature_len) ? c->signature[0] : 0;
    cap->signature_last = (c->signature && c->signature_len) ? c->signature[c->signature_len - 1] : 0;
}

// Build a stack breadcrumb with borrowed string fields (valid for the duration of the serialize/
// broadcast call that consumes it). `signature`/`signature_len` may be NULL/0 for an unsigned crumb.
static aethernet_space_breadcrumb_t make_crumb(const char *content_hash, const char *geo_hash,
                                               const char *anchor_uhid, int64_t created_at_ms,
                                               int32_t ttl_hours, uint8_t type,
                                               uint8_t *signature, uint32_t signature_len) {
    aethernet_space_breadcrumb_t b;
    memset(&b, 0, sizeof(b));
    b.content_hash = (char *)content_hash;
    b.geo_hash = (char *)geo_hash;
    b.anchor_uhid = (char *)anchor_uhid;
    b.created_at_ms = created_at_ms;
    b.ttl_hours = ttl_hours;
    b.type = type;
    b.signature = signature;
    b.signature_len = signature_len;
    return b;
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate (emergency_signed): the serializer must emit exactly the canonical bytes from
// fixtures/space/vectors.json vector "emergency_signed" (0x99*64 signature). Mirrors the C#
// SpaceBreadcrumb_Emergency_SerializesToCanonicalBytes.
static void emergency_signed_serializes_to_canonical_bytes(void) {
    uint8_t sig[64];
    memset(sig, 0x99, sizeof(sig));
    aethernet_space_breadcrumb_t b = make_crumb(
        "QmContentHashExample123", "u4pruy", "aether:alice:01",
        1700000000000LL, 720, 1 /* Emergency */, sig, 64);

    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_space_breadcrumb_payload_serialize(&b, &json, &len));
    const char *expected =
        "{\"content_hash\":\"QmContentHashExample123\",\"geo_hash\":\"u4pruy\",\"anchor_uhid\":\"aether:alice:01\","
        "\"created_at_ms\":1700000000000,\"ttl_hours\":720,\"type\":1,"
        "\"signature\":\"mZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmQ==\"}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);
}

// Byte-identity gate (notice_unsigned): empty signature must serialize as "" and type/ttl as bare
// ints, created_at_ms 0 as a bare 0. Mirrors the C# SpaceBreadcrumb_NoticeUnsigned_...Bytes.
static void notice_unsigned_serializes_to_canonical_bytes(void) {
    aethernet_space_breadcrumb_t b = make_crumb(
        "QmNotice777", "gcpvj0", "aether:bob:02",
        0, 72, 0 /* Notice */, NULL, 0);

    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_space_breadcrumb_payload_serialize(&b, &json, &len));
    const char *expected =
        "{\"content_hash\":\"QmNotice777\",\"geo_hash\":\"gcpvj0\",\"anchor_uhid\":\"aether:bob:02\","
        "\"created_at_ms\":0,\"ttl_hours\":72,\"type\":0,\"signature\":\"\"}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');
    free(json);
}

// broadcast emits exactly one SpaceBreadcrumb packet (source local UHID, dest "*", default TTL),
// returns the fan-out count, and handle_packet on that packet fires the callback with the decoded
// breadcrumb. Mirrors the C# Space_Broadcast_EmitsBreadcrumbPacket_AndHandleRaisesEvent.
static void broadcast_emits_packet_and_handle_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_space_breadcrumb_service_t *svc = aethernet_space_breadcrumb_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_space_breadcrumb_set_received_cb(svc, on_breadcrumb, &cap);

    uint8_t sig[64];
    memset(sig, 0x99, sizeof(sig));
    aethernet_space_breadcrumb_t crumb = make_crumb(
        "QmX", "u4pruy", "aether:alice:01",
        1700000000000LL, 720, 1 /* Emergency */, sig, 64);

    int reached = -1;
    assert(aethernet_space_breadcrumb_broadcast(svc, &crumb, &reached));
    assert(reached == 2);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_SPACE_BREADCRUMB);
    assert(strcmp(s.broadcasts[0]->source_uhid, "aether:alice:01") == 0);
    assert(strcmp(s.broadcasts[0]->destination_uhid, "*") == 0);
    assert(s.broadcasts[0]->ttl == AETHERNET_DEFAULT_TTL);

    assert(aethernet_space_breadcrumb_handle_packet(svc, s.broadcasts[0]));
    assert(cap.count == 1);
    assert(strcmp(cap.geo_hash, "u4pruy") == 0);
    assert(cap.type == 1 /* Emergency */);
    assert(cap.ttl_hours == 720);
    assert(cap.signature_len == 64);
    assert(cap.signature0 == 0x99);
    assert(cap.signature_last == 0x99);

    aethernet_space_breadcrumb_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false), no callback. Mirrors the C# Space_Handle_WrongType_ReturnsFalse.
static void handle_wrong_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_space_breadcrumb_service_t *svc = aethernet_space_breadcrumb_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_space_breadcrumb_set_received_cb(svc, on_breadcrumb, &cap);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DATA;
    aethernet_packet_set_source_uhid(pkt, "aether:x:01");
    const char *body = "{}";
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    assert(aethernet_space_breadcrumb_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(cap.count == 0);

    aethernet_space_breadcrumb_service_free(svc);
    fake_clear(&s);
}

// A malformed payload and an empty-content_hash payload are both benign drops (false). Guards the
// cJSON parse-fail path and the string.IsNullOrEmpty(ContentHash) guard.
static void handle_malformed_and_empty_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_space_breadcrumb_service_t *svc = aethernet_space_breadcrumb_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_space_breadcrumb_set_received_cb(svc, on_breadcrumb, &cap);

    // Garbage JSON → benign drop.
    aethernet_mesh_packet_t *bad = aethernet_packet_new();
    bad->type = AETHERNET_PACKET_TYPE_SPACE_BREADCRUMB;
    aethernet_packet_set_source_uhid(bad, "aether:bob:02");
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(bad, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(aethernet_space_breadcrumb_handle_packet(svc, bad) == false);
    aethernet_packet_free(bad);

    // Valid JSON but empty content_hash → benign drop.
    aethernet_mesh_packet_t *empty = aethernet_packet_new();
    empty->type = AETHERNET_PACKET_TYPE_SPACE_BREADCRUMB;
    aethernet_packet_set_source_uhid(empty, "aether:bob:02");
    const char *empty_ch =
        "{\"content_hash\":\"\",\"geo_hash\":\"gcpvj0\",\"anchor_uhid\":\"aether:bob:02\","
        "\"created_at_ms\":0,\"ttl_hours\":72,\"type\":0,\"signature\":\"\"}";
    aethernet_packet_set_payload(empty, (const uint8_t *)empty_ch, (uint32_t)strlen(empty_ch));
    assert(aethernet_space_breadcrumb_handle_packet(svc, empty) == false);
    aethernet_packet_free(empty);

    assert(cap.count == 0);

    aethernet_space_breadcrumb_service_free(svc);
    fake_clear(&s);
}

// A round-trip through the unsigned vector: serialize notice_unsigned, decode via handle_packet, and
// confirm the callback sees the same fields with an unsigned (len 0) signature.
static void unsigned_round_trips_through_handle(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:bob:02");
    aethernet_space_breadcrumb_service_t *svc = aethernet_space_breadcrumb_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_space_breadcrumb_set_received_cb(svc, on_breadcrumb, &cap);

    aethernet_space_breadcrumb_t crumb = make_crumb(
        "QmNotice777", "gcpvj0", "aether:bob:02", 0, 72, 0 /* Notice */, NULL, 0);
    int reached = -1;
    assert(aethernet_space_breadcrumb_broadcast(svc, &crumb, &reached));
    assert(reached == 2);
    assert(s.broadcasts_len == 1);

    assert(aethernet_space_breadcrumb_handle_packet(svc, s.broadcasts[0]));
    assert(cap.count == 1);
    assert(strcmp(cap.content_hash, "QmNotice777") == 0);
    assert(strcmp(cap.anchor_uhid, "aether:bob:02") == 0);
    assert(cap.created_at_ms == 0);
    assert(cap.ttl_hours == 72);
    assert(cap.type == 0 /* Notice */);
    assert(cap.signature_len == 0);

    aethernet_space_breadcrumb_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether SpaceBreadcrumb WIRE Service — Unit Tests\n");
    printf("================================================\n");

    RUN(emergency_signed_serializes_to_canonical_bytes);
    RUN(notice_unsigned_serializes_to_canonical_bytes);
    RUN(broadcast_emits_packet_and_handle_raises_event);
    RUN(handle_wrong_type_returns_false);
    RUN(handle_malformed_and_empty_returns_false);
    RUN(unsigned_round_trips_through_handle);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
