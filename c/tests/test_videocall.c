// SPDX-License-Identifier: MIT
// Unit tests for videocall.c (VideoCallControlService, PacketType 27 call-control).
// Directed signalling — a fake mesh sender captures directed sends (mirrors the C#
// VideoCallControlTests FakeMeshSender that captures (packet, nextHop) pairs).

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/videocall.h"

#define LOCAL_UHID "aether:local:01"

// ───── FakeMeshSender ────────────────────────────────────
// Captures directed sends as (cloned packet, next-hop) pairs. Mirrors the C# FakeMeshSender.Sends.

typedef struct {
    aethernet_mesh_packet_t **sends;   // cloned directed packets
    char                    **hops;    // owned next-hop UHIDs, parallel to sends
    int sends_len;
    int sends_cap;
} fake_state_t;

static char *dup_cstr(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static bool fake_send(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->sends_len == s->sends_cap) {
        s->sends_cap = s->sends_cap ? s->sends_cap * 2 : 8;
        s->sends = (aethernet_mesh_packet_t **)realloc(s->sends, sizeof(*s->sends) * (size_t)s->sends_cap);
        s->hops  = (char **)realloc(s->hops, sizeof(*s->hops) * (size_t)s->sends_cap);
    }
    s->sends[s->sends_len] = aethernet_packet_clone(packet);
    s->hops[s->sends_len]  = dup_cstr(next_hop_uhid);
    s->sends_len++;
    return true;  // mirror the C# FakeMeshSender: SendAsync returns true delivered
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->sends_len; i++) {
        aethernet_packet_free(s->sends[i]);
        free(s->hops[i]);
    }
    free(s->sends);
    free(s->hops);
    memset(s, 0, sizeof(*s));
}

static aethernet_mesh_sender_t make_sender(fake_state_t *state, const char *local_uhid) {
    aethernet_mesh_sender_t s = {0};
    s.local_uhid = local_uhid;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = NULL;
    s.user_data = state;
    return s;
}

// ───── Helpers ───────────────────────────────────────────

// Build a VideoCall control packet carrying the canonical payload for the given fields, using the
// same public serializer the wire path uses. `call_id` is a 16-byte UUID.
static aethernet_mesh_packet_t *control_packet(const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                               const char *action,
                                               const char *from_uhid,
                                               int64_t sent_at_ms) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_VIDEO_CALL;
    aethernet_packet_set_source_uhid(p, from_uhid);
    aethernet_packet_set_destination_uhid(p, LOCAL_UHID);
    p->ttl = AETHERNET_DEFAULT_TTL;
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    bool ok = aethernet_video_call_control_payload_serialize(call_id, action, sent_at_ms, &body, &body_len);
    assert(ok);
    aethernet_packet_set_payload(p, body, body_len);
    free(body);
    return p;
}

// Extract the "action" string value from a JSON payload (thin, for asserting the sent verb without
// pulling in cJSON). Copies into `out`. Returns true on success.
static bool payload_action(const uint8_t *payload, uint32_t len, char *out, size_t out_cap) {
    const char *needle = "\"action\":\"";
    // NUL-terminate a working copy so strstr is safe.
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
    uint8_t call_id[AETHERNET_PACKET_ID_SIZE];
    char action[64];
    char from_uhid[64];
} state_capture_t;

static void on_state_changed(const aethernet_video_call_state_changed_t *e, void *ud) {
    state_capture_t *c = (state_capture_t *)ud;
    c->count++;
    memcpy(c->call_id, e->call_id, AETHERNET_PACKET_ID_SIZE);
    snprintf(c->action, sizeof(c->action), "%s", e->action ? e->action : "");
    snprintf(c->from_uhid, sizeof(c->from_uhid), "%s", e->from_uhid ? e->from_uhid : "");
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate: aethernet_video_call_control_payload_serialize must emit exactly the canonical
// bytes from fixtures/videocall/vectors.json for every language SDK.
static void payload_serializes_to_canonical_bytes(void) {
    // Vector "ring": id 0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f, action ring, sent_at_ms 1700000000000
    uint8_t id1[AETHERNET_PACKET_ID_SIZE] = {
        0x0f, 0x7e, 0x5d, 0x3c, 0x1a, 0x2b, 0x4c, 0x5d,
        0x8e, 0x9f, 0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f };
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_video_call_control_payload_serialize(id1, "ring", 1700000000000LL, &json, &len));
    const char *expected1 =
        "{\"call_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"action\":\"ring\",\"sent_at_ms\":1700000000000}";
    assert(len == (uint32_t)strlen(expected1));
    assert(memcmp(json, expected1, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);

    // Vector "hangup": all-zero id, action hangup, sent_at_ms 0
    uint8_t id2[AETHERNET_PACKET_ID_SIZE] = {0};
    json = NULL; len = 0;
    assert(aethernet_video_call_control_payload_serialize(id2, "hangup", 0, &json, &len));
    const char *expected2 =
        "{\"call_id\":\"00000000-0000-0000-0000-000000000000\",\"action\":\"hangup\",\"sent_at_ms\":0}";
    assert(len == (uint32_t)strlen(expected2));
    assert(memcmp(json, expected2, len) == 0);
    free(json);
}

// Ringing a peer mints a non-zero call id and directed-sends exactly one "ring" VideoCall packet to
// that peer, carrying the minted call id. Mirrors Ring_SendsDirectedRingToPeer_AndReturnsCallId.
static void ring_sends_directed_ring_to_peer(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_video_call_control_service_t *svc = aethernet_video_call_control_service_new(&sender);

    uint8_t call_id[AETHERNET_PACKET_ID_SIZE];
    bool ok = aethernet_video_call_control_ring(svc, "aether:bob:02", call_id);
    assert(ok);

    // call id is non-zero (Guid.Empty check in C#)
    uint8_t zero[AETHERNET_PACKET_ID_SIZE] = {0};
    assert(memcmp(call_id, zero, AETHERNET_PACKET_ID_SIZE) != 0);

    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_VIDEO_CALL);
    assert(strcmp(s.hops[0], "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->destination_uhid, "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->source_uhid, "aether:alice:01") == 0);
    assert(s.sends[0]->ttl == AETHERNET_DEFAULT_TTL);

    char action[64];
    assert(payload_action(s.sends[0]->payload, s.sends[0]->payload_len, action, sizeof(action)));
    assert(strcmp(action, "ring") == 0);
    // The sent body carries the minted call id in canonical dashed form.
    char id_canon[37];
    snprintf(id_canon, sizeof(id_canon),
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        call_id[0], call_id[1], call_id[2], call_id[3], call_id[4], call_id[5], call_id[6], call_id[7],
        call_id[8], call_id[9], call_id[10], call_id[11], call_id[12], call_id[13], call_id[14], call_id[15]);
    assert(strstr((const char *)s.sends[0]->payload, id_canon) != NULL);

    aethernet_video_call_control_service_free(svc);
    fake_clear(&s);
}

// accept/decline/hangup each directed-send exactly one packet with the matching verb and call id to
// the peer. Mirrors Respond_SendsDirectedActionToPeer.
static void respond_sends_directed_action_to_peer(void) {
    const char *verbs[3] = {"accept", "decline", "hangup"};
    for (int v = 0; v < 3; v++) {
        fake_state_t s = {0};
        aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
        aethernet_video_call_control_service_t *svc = aethernet_video_call_control_service_new(&sender);

        uint8_t call_id[AETHERNET_PACKET_ID_SIZE] = {
            0xab, 0xcd, 0xef, 0x01, 0x23, 0x45, 0x46, 0x78,
            0x89, 0xab, 0xcd, 0xef, 0x01, 0x23, 0x45, 0x67 };

        bool ok;
        if (v == 0) ok = aethernet_video_call_control_accept(svc, call_id, "aether:bob:02");
        else if (v == 1) ok = aethernet_video_call_control_decline(svc, call_id, "aether:bob:02");
        else ok = aethernet_video_call_control_hangup(svc, call_id, "aether:bob:02");
        assert(ok);

        assert(s.sends_len == 1);
        assert(strcmp(s.hops[0], "aether:bob:02") == 0);
        assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_VIDEO_CALL);

        char action[64];
        assert(payload_action(s.sends[0]->payload, s.sends[0]->payload_len, action, sizeof(action)));
        assert(strcmp(action, verbs[v]) == 0);
        // call id echoed in the body
        assert(strstr((const char *)s.sends[0]->payload,
            "abcdef01-2345-4678-89ab-cdef01234567") != NULL);

        aethernet_video_call_control_service_free(svc);
        fake_clear(&s);
    }
}

// An inbound control signal fires the call-state-changed callback with the decoded call id, action,
// and the packet source as from_uhid. Mirrors Handle_RaisesCallStateChanged.
static void handle_raises_call_state_changed(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_video_call_control_service_t *svc = aethernet_video_call_control_service_new(&sender);

    state_capture_t cap = {0};
    aethernet_video_call_control_set_state_changed_cb(svc, on_state_changed, &cap);

    uint8_t cid[AETHERNET_PACKET_ID_SIZE] = {
        0x0f, 0x7e, 0x5d, 0x3c, 0x1a, 0x2b, 0x4c, 0x5d,
        0x8e, 0x9f, 0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f };
    aethernet_mesh_packet_t *pkt = control_packet(cid, "ring", "aether:bob:02", 1LL);
    bool ok = aethernet_video_call_control_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(ok);
    assert(cap.count == 1);
    assert(memcmp(cap.call_id, cid, AETHERNET_PACKET_ID_SIZE) == 0);
    assert(strcmp(cap.action, "ring") == 0);
    assert(strcmp(cap.from_uhid, "aether:bob:02") == 0);

    aethernet_video_call_control_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false), no callback. Mirrors Handle_WrongPacketType_ReturnsFalse.
static void handle_wrong_packet_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_video_call_control_service_t *svc = aethernet_video_call_control_service_new(&sender);

    state_capture_t cap = {0};
    aethernet_video_call_control_set_state_changed_cb(svc, on_state_changed, &cap);

    uint8_t cid[AETHERNET_PACKET_ID_SIZE] = {0};
    aethernet_mesh_packet_t *pkt = control_packet(cid, "ring", "aether:bob:02", 1LL);
    pkt->type = AETHERNET_PACKET_TYPE_DATA;  // not a VideoCall
    assert(aethernet_video_call_control_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(cap.count == 0);

    aethernet_video_call_control_service_free(svc);
    fake_clear(&s);
}

// A malformed payload is a benign drop (false). Mirrors the C# HandleAsync JsonException path.
static void handle_malformed_payload_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_video_call_control_service_t *svc = aethernet_video_call_control_service_new(&sender);

    state_capture_t cap = {0};
    aethernet_video_call_control_set_state_changed_cb(svc, on_state_changed, &cap);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_VIDEO_CALL;
    aethernet_packet_set_source_uhid(pkt, "aether:bob:02");
    aethernet_packet_set_destination_uhid(pkt, LOCAL_UHID);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(pkt, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(aethernet_video_call_control_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(cap.count == 0);

    aethernet_video_call_control_service_free(svc);
    fake_clear(&s);
}

// An empty action is malformed → benign drop (false), no callback. Mirrors the C#
// string.IsNullOrEmpty(body.Action) guard.
static void handle_empty_action_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_video_call_control_service_t *svc = aethernet_video_call_control_service_new(&sender);

    state_capture_t cap = {0};
    aethernet_video_call_control_set_state_changed_cb(svc, on_state_changed, &cap);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_VIDEO_CALL;
    aethernet_packet_set_source_uhid(pkt, "aether:bob:02");
    aethernet_packet_set_destination_uhid(pkt, LOCAL_UHID);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    const char *body =
        "{\"call_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"action\":\"\",\"sent_at_ms\":1}";
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    assert(aethernet_video_call_control_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(cap.count == 0);

    aethernet_video_call_control_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether Video Call-Control Service — Unit Tests\n");
    printf("==============================================\n");

    RUN(payload_serializes_to_canonical_bytes);
    RUN(ring_sends_directed_ring_to_peer);
    RUN(respond_sends_directed_action_to_peer);
    RUN(handle_raises_call_state_changed);
    RUN(handle_wrong_packet_type_returns_false);
    RUN(handle_malformed_payload_returns_false);
    RUN(handle_empty_action_returns_false);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
