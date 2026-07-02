// SPDX-License-Identifier: MIT
// Unit tests for forge_announce.c (ForgeAnnounceService, PacketType ForgeAnnounce 41).
// Broadcast-transport of a ForgeAnnounce over the mesh. A fake mesh sender captures broadcasts as
// cloned packets — mirrors the C# WirePacketsTests FakeMeshSender. Byte-identity gate transcribes
// fixtures/forge/vectors.json vector "basic".

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/forge_announce.h"

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
    char package_id[128];
    char content_hash[128];
    int64_t size_bytes;
    int64_t announced_at_ms;
} recv_capture_t;

static void on_announce(const aethernet_forge_announce_t *a, void *ud) {
    recv_capture_t *cap = (recv_capture_t *)ud;
    cap->count++;
    snprintf(cap->package_id, sizeof(cap->package_id), "%s", a->package_id ? a->package_id : "");
    snprintf(cap->content_hash, sizeof(cap->content_hash), "%s", a->content_hash ? a->content_hash : "");
    cap->size_bytes = a->size_bytes;
    cap->announced_at_ms = a->announced_at_ms;
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate: the serializer must emit exactly the canonical bytes from
// fixtures/forge/vectors.json vector "basic". Mirrors the C# ForgeAnnounce_SerializesToCanonicalBytes.
static void serializes_to_canonical_bytes(void) {
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_forge_announce_payload_serialize(
        "npm:react@18.2.0", "QmForgeHash456", 294912LL, 1700000000000LL, &json, &len));
    const char *expected =
        "{\"package_id\":\"npm:react@18.2.0\",\"content_hash\":\"QmForgeHash456\","
        "\"size_bytes\":294912,\"announced_at_ms\":1700000000000}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);
}

// broadcast emits exactly one ForgeAnnounce packet (source local UHID, dest "*", default TTL),
// returns the fan-out count, and handle_packet on that packet fires the callback with the decoded
// announcement. Mirrors the C# Forge_Broadcast_EmitsAnnouncePacket_AndHandleRaisesEvent.
static void broadcast_emits_packet_and_handle_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_forge_announce_service_t *svc = aethernet_forge_announce_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_forge_announce_set_received_cb(svc, on_announce, &cap);

    int reached = -1;
    assert(aethernet_forge_announce_broadcast(
        svc, "npm:react@18.2.0", "QmForgeHash456", 294912LL, 1700000000000LL, &reached));
    assert(reached == 2);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_FORGE_ANNOUNCE);
    assert(strcmp(s.broadcasts[0]->source_uhid, "aether:alice:01") == 0);
    assert(strcmp(s.broadcasts[0]->destination_uhid, "*") == 0);
    assert(s.broadcasts[0]->ttl == AETHERNET_DEFAULT_TTL);

    assert(aethernet_forge_announce_handle_packet(svc, s.broadcasts[0]));
    assert(cap.count == 1);
    assert(strcmp(cap.package_id, "npm:react@18.2.0") == 0);
    assert(strcmp(cap.content_hash, "QmForgeHash456") == 0);
    assert(cap.size_bytes == 294912LL);
    assert(cap.announced_at_ms == 1700000000000LL);

    aethernet_forge_announce_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false), no callback. Mirrors the C# Forge_Handle_WrongType_ReturnsFalse.
static void handle_wrong_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_forge_announce_service_t *svc = aethernet_forge_announce_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_forge_announce_set_received_cb(svc, on_announce, &cap);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DATA;
    aethernet_packet_set_source_uhid(pkt, "aether:x:01");
    const char *body = "{}";
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    assert(aethernet_forge_announce_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(cap.count == 0);

    aethernet_forge_announce_service_free(svc);
    fake_clear(&s);
}

// A malformed payload and an empty-package_id payload are both benign drops (false).
static void handle_malformed_and_empty_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_forge_announce_service_t *svc = aethernet_forge_announce_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_forge_announce_set_received_cb(svc, on_announce, &cap);

    aethernet_mesh_packet_t *bad = aethernet_packet_new();
    bad->type = AETHERNET_PACKET_TYPE_FORGE_ANNOUNCE;
    aethernet_packet_set_source_uhid(bad, "aether:bob:02");
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(bad, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(aethernet_forge_announce_handle_packet(svc, bad) == false);
    aethernet_packet_free(bad);

    aethernet_mesh_packet_t *empty = aethernet_packet_new();
    empty->type = AETHERNET_PACKET_TYPE_FORGE_ANNOUNCE;
    aethernet_packet_set_source_uhid(empty, "aether:bob:02");
    const char *empty_pkg =
        "{\"package_id\":\"\",\"content_hash\":\"QmForgeHash456\","
        "\"size_bytes\":294912,\"announced_at_ms\":1700000000000}";
    aethernet_packet_set_payload(empty, (const uint8_t *)empty_pkg, (uint32_t)strlen(empty_pkg));
    assert(aethernet_forge_announce_handle_packet(svc, empty) == false);
    aethernet_packet_free(empty);

    assert(cap.count == 0);

    aethernet_forge_announce_service_free(svc);
    fake_clear(&s);
}

// An empty package_id is rejected by broadcast (C# ArgumentException.ThrowIfNullOrEmpty), nothing sent.
static void broadcast_empty_package_id_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_forge_announce_service_t *svc = aethernet_forge_announce_service_new(&sender);

    int reached = -1;
    assert(aethernet_forge_announce_broadcast(svc, "", "QmX", 1, 1, &reached) == false);
    assert(s.broadcasts_len == 0);

    aethernet_forge_announce_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether ForgeAnnounce WIRE Service — Unit Tests\n");
    printf("==============================================\n");

    RUN(serializes_to_canonical_bytes);
    RUN(broadcast_emits_packet_and_handle_raises_event);
    RUN(handle_wrong_type_returns_false);
    RUN(handle_malformed_and_empty_returns_false);
    RUN(broadcast_empty_package_id_returns_false);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
