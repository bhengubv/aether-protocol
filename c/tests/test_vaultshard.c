// SPDX-License-Identifier: MIT
// Unit tests for vaultshard.c (VaultShardRequestService, PacketType VaultShardRequest 42).
// Broadcast-transport of a VaultShardRequest over the mesh. A fake mesh sender captures broadcasts as
// cloned packets — mirrors the C# WirePacketsTests FakeMeshSender. Byte-identity gate transcribes
// fixtures/vaultshard/vectors.json vector "basic".

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/vaultshard.h"

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

// Thin string-field extractor for asserting the sent payload without pulling in cJSON. Mirrors the
// prekey test's payload_field. Copies into `out`; returns true on success.
static bool payload_field(const uint8_t *payload, uint32_t len, const char *key,
                          char *out, size_t out_cap) {
    char needle[64];
    snprintf(needle, sizeof(needle), "\"%s\":\"", key);
    char *tmp = (char *)malloc((size_t)len + 1);
    assert(tmp);
    memcpy(tmp, payload, len);
    tmp[len] = '\0';
    const char *p = strstr(tmp, needle);
    if (!p) { free(tmp); return false; }
    p += strlen(needle);
    size_t i = 0;
    while (*p && *p != '"' && i + 1 < out_cap) out[i++] = *p++;
    out[i] = '\0';
    free(tmp);
    return true;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

typedef struct {
    int count;
    char shard_hash[128];
    char requester_uhid[128];
} recv_capture_t;

static void on_request(const aethernet_vault_shard_request_t *r, void *ud) {
    recv_capture_t *cap = (recv_capture_t *)ud;
    cap->count++;
    snprintf(cap->shard_hash, sizeof(cap->shard_hash), "%s", r->shard_hash ? r->shard_hash : "");
    snprintf(cap->requester_uhid, sizeof(cap->requester_uhid), "%s", r->requester_uhid ? r->requester_uhid : "");
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate: the serializer must emit exactly the canonical bytes from
// fixtures/vaultshard/vectors.json vector "basic". Mirrors the C# VaultShardRequest_SerializesToCanonicalBytes.
static void serializes_to_canonical_bytes(void) {
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_vault_shard_request_payload_serialize(
        "QmShardHash789", "aether:bob:02", &json, &len));
    const char *expected =
        "{\"shard_hash\":\"QmShardHash789\",\"requester_uhid\":\"aether:bob:02\"}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);
}

// request_shard emits exactly one VaultShardRequest packet (source local UHID, dest "*", default TTL)
// carrying requester_uhid = the local UHID, returns the fan-out count, and handle_packet on that packet
// fires the callback with the decoded request. Mirrors the C#
// Vault_Request_EmitsShardRequestPacket_AndHandleRaisesEvent.
static void request_emits_packet_and_handle_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:bob:02");
    aethernet_vault_shard_request_service_t *svc = aethernet_vault_shard_request_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_vault_shard_request_set_requested_cb(svc, on_request, &cap);

    int reached = -1;
    assert(aethernet_vault_shard_request_request_shard(svc, "QmShardHash789", &reached));
    assert(reached == 2);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_VAULT_SHARD_REQUEST);
    assert(strcmp(s.broadcasts[0]->source_uhid, "aether:bob:02") == 0);
    assert(strcmp(s.broadcasts[0]->destination_uhid, "*") == 0);
    assert(s.broadcasts[0]->ttl == AETHERNET_DEFAULT_TTL);

    // The sent body carries shard_hash and requester_uhid = the local UHID (requester = sender).
    char shard[128], requester[128];
    assert(payload_field(s.broadcasts[0]->payload, s.broadcasts[0]->payload_len, "shard_hash",
                         shard, sizeof(shard)));
    assert(strcmp(shard, "QmShardHash789") == 0);
    assert(payload_field(s.broadcasts[0]->payload, s.broadcasts[0]->payload_len, "requester_uhid",
                         requester, sizeof(requester)));
    assert(strcmp(requester, "aether:bob:02") == 0);

    assert(aethernet_vault_shard_request_handle_packet(svc, s.broadcasts[0]));
    assert(cap.count == 1);
    assert(strcmp(cap.shard_hash, "QmShardHash789") == 0);
    assert(strcmp(cap.requester_uhid, "aether:bob:02") == 0);

    aethernet_vault_shard_request_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false), no callback. Mirrors the C# Vault_Handle_WrongType_ReturnsFalse.
static void handle_wrong_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_vault_shard_request_service_t *svc = aethernet_vault_shard_request_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_vault_shard_request_set_requested_cb(svc, on_request, &cap);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DATA;
    aethernet_packet_set_source_uhid(pkt, "aether:x:01");
    const char *body = "{}";
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    assert(aethernet_vault_shard_request_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(cap.count == 0);

    aethernet_vault_shard_request_service_free(svc);
    fake_clear(&s);
}

// A malformed payload and an empty-shard_hash payload are both benign drops (false).
static void handle_malformed_and_empty_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_vault_shard_request_service_t *svc = aethernet_vault_shard_request_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_vault_shard_request_set_requested_cb(svc, on_request, &cap);

    aethernet_mesh_packet_t *bad = aethernet_packet_new();
    bad->type = AETHERNET_PACKET_TYPE_VAULT_SHARD_REQUEST;
    aethernet_packet_set_source_uhid(bad, "aether:bob:02");
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(bad, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(aethernet_vault_shard_request_handle_packet(svc, bad) == false);
    aethernet_packet_free(bad);

    aethernet_mesh_packet_t *empty = aethernet_packet_new();
    empty->type = AETHERNET_PACKET_TYPE_VAULT_SHARD_REQUEST;
    aethernet_packet_set_source_uhid(empty, "aether:bob:02");
    const char *empty_sh = "{\"shard_hash\":\"\",\"requester_uhid\":\"aether:bob:02\"}";
    aethernet_packet_set_payload(empty, (const uint8_t *)empty_sh, (uint32_t)strlen(empty_sh));
    assert(aethernet_vault_shard_request_handle_packet(svc, empty) == false);
    aethernet_packet_free(empty);

    assert(cap.count == 0);

    aethernet_vault_shard_request_service_free(svc);
    fake_clear(&s);
}

// An empty shard_hash is rejected by request_shard (C# ArgumentException.ThrowIfNullOrEmpty), nothing sent.
static void request_empty_shard_hash_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:bob:02");
    aethernet_vault_shard_request_service_t *svc = aethernet_vault_shard_request_service_new(&sender);

    int reached = -1;
    assert(aethernet_vault_shard_request_request_shard(svc, "", &reached) == false);
    assert(s.broadcasts_len == 0);

    aethernet_vault_shard_request_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether VaultShardRequest WIRE Service — Unit Tests\n");
    printf("==================================================\n");

    RUN(serializes_to_canonical_bytes);
    RUN(request_emits_packet_and_handle_raises_event);
    RUN(handle_wrong_type_returns_false);
    RUN(handle_malformed_and_empty_returns_false);
    RUN(request_empty_shard_hash_returns_false);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
