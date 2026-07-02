// SPDX-License-Identifier: MIT
// Unit tests for prekey.c (PreKeyExchangeService, PacketType PreKeyRequest 25 / PreKeyResponse 26).
// Directed request/response transport of a PreKeyBundle over the mesh. A fake mesh sender captures
// directed sends as (cloned packet, next-hop) pairs — mirrors the C# PreKeyExchangeTests
// FakeMeshSender that captures (packet, nextHop) pairs.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/prekey.h"

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

// The C# SampleBundle: uhid, 0x11*32 identity, 0x22*32 identity_x25519, pre_key_id 4242,
// 0x33*32 pre_key, signed_pre_key_id 77, 0x44*32 signed_pre_key, 0x55*64 signature. `uhid` points
// into caller storage; the service/serializer copy what they retain.
static aethernet_pre_key_exchange_bundle_t sample_bundle(const char *uhid) {
    aethernet_pre_key_exchange_bundle_t b;
    memset(&b, 0, sizeof(b));
    b.uhid = (char *)uhid;  // borrowed for the duration of the call that consumes it
    memset(b.identity_key, 0x11, 32);
    memset(b.identity_key_x25519, 0x22, 32);
    b.pre_key_id = 4242;
    memset(b.pre_key, 0x33, 32);
    b.signed_pre_key_id = 77;
    memset(b.signed_pre_key, 0x44, 32);
    memset(b.signed_pre_key_signature, 0x55, 64);
    return b;
}

// Build a PreKeyRequest packet carrying the canonical payload, using the same public serializer the
// wire path uses. `request_id` is a 16-byte UUID.
static aethernet_mesh_packet_t *request_packet(const uint8_t request_id[AETHERNET_PACKET_ID_SIZE],
                                               const char *requester_uhid,
                                               const char *from_uhid) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_PREKEY_REQUEST;
    aethernet_packet_set_source_uhid(p, from_uhid);
    aethernet_packet_set_destination_uhid(p, LOCAL_UHID);
    p->ttl = AETHERNET_DEFAULT_TTL;
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    bool ok = aethernet_pre_key_request_payload_serialize(request_id, requester_uhid, &body, &body_len);
    assert(ok);
    aethernet_packet_set_payload(p, body, body_len);
    free(body);
    return p;
}

// Build a PreKeyResponse packet carrying `bundle` for `request_id`, from `from_uhid`.
static aethernet_mesh_packet_t *response_packet(const uint8_t request_id[AETHERNET_PACKET_ID_SIZE],
                                                const aethernet_pre_key_exchange_bundle_t *bundle,
                                                const char *from_uhid) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_PREKEY_RESPONSE;
    aethernet_packet_set_source_uhid(p, from_uhid);
    aethernet_packet_set_destination_uhid(p, LOCAL_UHID);
    p->ttl = AETHERNET_DEFAULT_TTL;
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    bool ok = aethernet_pre_key_response_payload_serialize(request_id, bundle, &body, &body_len);
    assert(ok);
    aethernet_packet_set_payload(p, body, body_len);
    free(body);
    return p;
}

// Extract a string field value from a JSON payload (thin, for asserting sent values without pulling
// in cJSON). Copies into `out`. Returns true on success.
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
    uint8_t request_id[AETHERNET_PACKET_ID_SIZE];
    char from_uhid[128];
    char bundle_uhid[128];
    int32_t pre_key_id;
    uint8_t identity_key0;
    uint8_t signature63;
    int signature_len_ok;  // 1 if all 64 signature bytes were 0x55
} recv_capture_t;

static void on_bundle_received(const aethernet_pre_key_bundle_received_t *e, void *ud) {
    recv_capture_t *c = (recv_capture_t *)ud;
    c->count++;
    memcpy(c->request_id, e->request_id, AETHERNET_PACKET_ID_SIZE);
    snprintf(c->from_uhid, sizeof(c->from_uhid), "%s", e->from_uhid ? e->from_uhid : "");
    snprintf(c->bundle_uhid, sizeof(c->bundle_uhid), "%s", e->bundle->uhid ? e->bundle->uhid : "");
    c->pre_key_id = e->bundle->pre_key_id;
    c->identity_key0 = e->bundle->identity_key[0];
    c->signature63 = e->bundle->signed_pre_key_signature[63];
    c->signature_len_ok = 1;
    for (int i = 0; i < 64; i++)
        if (e->bundle->signed_pre_key_signature[i] != 0x55) c->signature_len_ok = 0;
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate (request): the request serializer must emit exactly the canonical bytes from
// fixtures/prekey/vectors.json vector "request". Mirrors RequestPayload_SerializesToCanonicalBytes.
static void request_serializes_to_canonical_bytes(void) {
    // Vector "request": id 11112222-3333-4444-5555-666677778888, requester aether:alice:01
    uint8_t id[AETHERNET_PACKET_ID_SIZE] = {
        0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44,
        0x55, 0x55, 0x66, 0x66, 0x77, 0x77, 0x88, 0x88 };
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_pre_key_request_payload_serialize(id, "aether:alice:01", &json, &len));
    const char *expected =
        "{\"request_id\":\"11112222-3333-4444-5555-666677778888\",\"requester_uhid\":\"aether:alice:01\"}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);
}

// Byte-identity gate (response): the response serializer must emit exactly the canonical bytes from
// fixtures/prekey/vectors.json vector "response". Mirrors ResponsePayload_SerializesToCanonicalBytes.
static void response_serializes_to_canonical_bytes(void) {
    // Vector "response": id 7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a, uhid aether:bob:02, constant fills.
    uint8_t id[AETHERNET_PACKET_ID_SIZE] = {
        0x7a, 0x1e, 0x9c, 0x4d, 0x2b, 0x3f, 0x4a, 0x5e,
        0x8c, 0x6d, 0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a };
    aethernet_pre_key_exchange_bundle_t b = sample_bundle("aether:bob:02");
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_pre_key_response_payload_serialize(id, &b, &json, &len));
    const char *expected =
        "{\"request_id\":\"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a\",\"uhid\":\"aether:bob:02\","
        "\"identity_key\":\"ERERERERERERERERERERERERERERERERERERERERERE=\","
        "\"identity_key_x25519\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\","
        "\"pre_key_id\":4242,\"pre_key\":\"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=\","
        "\"signed_pre_key_id\":77,\"signed_pre_key\":\"REREREREREREREREREREREREREREREREREREREREREQ=\","
        "\"signed_pre_key_signature\":\"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==\"}";
    assert(len == (uint32_t)strlen(expected));
    assert(memcmp(json, expected, len) == 0);
    assert(json[len] == '\0');
    free(json);
}

// A round-trip: serialize a response, decode it through handle_packet, and confirm the cached bundle
// matches the original fields. Mirrors ResponsePayload_RoundTripsThroughBundle (exercised via the
// service so the base64 decode path is covered too).
static void response_round_trips_through_bundle(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_pre_key_exchange_service_t *svc = aethernet_pre_key_exchange_service_new(&sender);

    uint8_t reqid[AETHERNET_PACKET_ID_SIZE] = {
        0x7a, 0x1e, 0x9c, 0x4d, 0x2b, 0x3f, 0x4a, 0x5e,
        0x8c, 0x6d, 0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a };
    aethernet_pre_key_exchange_bundle_t orig = sample_bundle("aether:bob:02");
    aethernet_mesh_packet_t *pkt = response_packet(reqid, &orig, "aether:bob:02");
    assert(aethernet_pre_key_exchange_handle_packet(svc, pkt));
    aethernet_packet_free(pkt);

    aethernet_pre_key_exchange_bundle_t back;
    assert(aethernet_pre_key_exchange_get_received_bundle(svc, "aether:bob:02", &back));
    assert(strcmp(back.uhid, "aether:bob:02") == 0);
    assert(back.pre_key_id == 4242);
    assert(back.signed_pre_key_id == 77);
    for (int i = 0; i < 32; i++) {
        assert(back.identity_key[i] == 0x11);
        assert(back.identity_key_x25519[i] == 0x22);
        assert(back.pre_key[i] == 0x33);
        assert(back.signed_pre_key[i] == 0x44);
    }
    for (int i = 0; i < 64; i++) assert(back.signed_pre_key_signature[i] == 0x55);
    aethernet_pre_key_bundle_free(&back);

    aethernet_pre_key_exchange_service_free(svc);
    fake_clear(&s);
}

// request_bundle mints a non-zero request id and directed-sends exactly one PreKeyRequest to the
// peer, carrying the local UHID as requester_uhid. Mirrors
// Request_SendsDirectedPreKeyRequest_AndReturnsId.
static void request_sends_directed_pre_key_request(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_pre_key_exchange_service_t *svc = aethernet_pre_key_exchange_service_new(&sender);

    uint8_t req_id[AETHERNET_PACKET_ID_SIZE];
    assert(aethernet_pre_key_exchange_request_bundle(svc, "aether:bob:02", req_id));

    // request id is non-zero (Guid.Empty check in C#)
    uint8_t zero[AETHERNET_PACKET_ID_SIZE] = {0};
    assert(memcmp(req_id, zero, AETHERNET_PACKET_ID_SIZE) != 0);

    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_PREKEY_REQUEST);
    assert(strcmp(s.hops[0], "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->destination_uhid, "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->source_uhid, "aether:alice:01") == 0);
    assert(s.sends[0]->ttl == AETHERNET_DEFAULT_TTL);

    char requester[128];
    assert(payload_field(s.sends[0]->payload, s.sends[0]->payload_len, "requester_uhid",
                         requester, sizeof(requester)));
    assert(strcmp(requester, "aether:alice:01") == 0);

    // The sent body carries the minted request id in canonical dashed form.
    char id_canon[37];
    snprintf(id_canon, sizeof(id_canon),
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        req_id[0], req_id[1], req_id[2], req_id[3], req_id[4], req_id[5], req_id[6], req_id[7],
        req_id[8], req_id[9], req_id[10], req_id[11], req_id[12], req_id[13], req_id[14], req_id[15]);
    assert(strstr((const char *)s.sends[0]->payload, id_canon) != NULL);

    aethernet_pre_key_exchange_service_free(svc);
    fake_clear(&s);
}

// A PreKeyRequest with a local bundle set → directed-send exactly one PreKeyResponse to the
// requester, echoing the request id and carrying the bundle. Mirrors
// HandleRequest_WithLocalBundle_SendsDirectedResponseToRequester.
static void handle_request_with_local_bundle_sends_response(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:bob:02");
    aethernet_pre_key_exchange_service_t *svc = aethernet_pre_key_exchange_service_new(&sender);

    aethernet_pre_key_exchange_bundle_t local = sample_bundle("aether:bob:02");
    assert(aethernet_pre_key_exchange_set_local_bundle(svc, &local));

    uint8_t req_id[AETHERNET_PACKET_ID_SIZE] = {
        0xde, 0xad, 0xbe, 0xef, 0x00, 0x11, 0x42, 0x33,
        0x84, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb };
    aethernet_mesh_packet_t *reqpkt = request_packet(req_id, "aether:alice:01", "aether:alice:01");
    assert(aethernet_pre_key_exchange_handle_packet(svc, reqpkt));
    aethernet_packet_free(reqpkt);

    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_PREKEY_RESPONSE);
    assert(strcmp(s.hops[0], "aether:alice:01") == 0);
    assert(strcmp(s.sends[0]->destination_uhid, "aether:alice:01") == 0);

    char uhid[128], reqid_str[64];
    assert(payload_field(s.sends[0]->payload, s.sends[0]->payload_len, "uhid", uhid, sizeof(uhid)));
    assert(strcmp(uhid, "aether:bob:02") == 0);
    assert(payload_field(s.sends[0]->payload, s.sends[0]->payload_len, "request_id",
                         reqid_str, sizeof(reqid_str)));
    assert(strcmp(reqid_str, "deadbeef-0011-4233-8455-66778899aabb") == 0);
    // pre_key_id 4242 is present as a bare int; signature field present.
    assert(strstr((const char *)s.sends[0]->payload, "\"pre_key_id\":4242") != NULL);
    assert(strstr((const char *)s.sends[0]->payload, "\"signed_pre_key_signature\":\"") != NULL);

    aethernet_pre_key_exchange_service_free(svc);
    fake_clear(&s);
}

// A PreKeyRequest with no local bundle set → false, nothing sent. Mirrors
// HandleRequest_NoLocalBundle_ReturnsFalse_AndSendsNothing.
static void handle_request_no_local_bundle_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_pre_key_exchange_service_t *svc = aethernet_pre_key_exchange_service_new(&sender);

    uint8_t req_id[AETHERNET_PACKET_ID_SIZE] = {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x47, 0x08,
        0x89, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10 };
    aethernet_mesh_packet_t *reqpkt = request_packet(req_id, "aether:alice:01", "aether:alice:01");
    assert(aethernet_pre_key_exchange_handle_packet(svc, reqpkt) == false);
    aethernet_packet_free(reqpkt);
    assert(s.sends_len == 0);

    aethernet_pre_key_exchange_service_free(svc);
    fake_clear(&s);
}

// A PreKeyResponse caches the bundle by uhid and fires the callback with the echoed request id,
// packet source as from_uhid, and the decoded bundle. Mirrors
// HandleResponse_CachesBundle_AndRaisesEvent.
static void handle_response_caches_and_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_pre_key_exchange_service_t *svc = aethernet_pre_key_exchange_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_pre_key_exchange_set_bundle_received_cb(svc, on_bundle_received, &cap);

    uint8_t req_id[AETHERNET_PACKET_ID_SIZE] = {
        0x7a, 0x1e, 0x9c, 0x4d, 0x2b, 0x3f, 0x4a, 0x5e,
        0x8c, 0x6d, 0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a };
    aethernet_pre_key_exchange_bundle_t bob = sample_bundle("aether:bob:02");
    aethernet_mesh_packet_t *resppkt = response_packet(req_id, &bob, "aether:bob:02");
    assert(aethernet_pre_key_exchange_handle_packet(svc, resppkt));
    aethernet_packet_free(resppkt);

    assert(cap.count == 1);
    assert(memcmp(cap.request_id, req_id, AETHERNET_PACKET_ID_SIZE) == 0);
    assert(strcmp(cap.from_uhid, "aether:bob:02") == 0);
    assert(strcmp(cap.bundle_uhid, "aether:bob:02") == 0);
    assert(cap.pre_key_id == 4242);
    assert(cap.identity_key0 == 0x11);
    assert(cap.signature63 == 0x55);
    assert(cap.signature_len_ok == 1);

    aethernet_pre_key_exchange_bundle_t cached;
    assert(aethernet_pre_key_exchange_get_received_bundle(svc, "aether:bob:02", &cached));
    assert(cached.pre_key_id == 4242);
    aethernet_pre_key_bundle_free(&cached);

    aethernet_pre_key_exchange_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false), no callback. Mirrors Handle_WrongPacketType_ReturnsFalse.
static void handle_wrong_packet_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_pre_key_exchange_service_t *svc = aethernet_pre_key_exchange_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_pre_key_exchange_set_bundle_received_cb(svc, on_bundle_received, &cap);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DATA;
    aethernet_packet_set_source_uhid(pkt, "aether:x:01");
    const char *body = "{}";
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));
    assert(aethernet_pre_key_exchange_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(cap.count == 0);

    aethernet_pre_key_exchange_service_free(svc);
    fake_clear(&s);
}

// A malformed payload is a benign drop (false) for both request and response types. Mirrors the C#
// HandleAsync JsonException path.
static void handle_malformed_payload_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_pre_key_exchange_service_t *svc = aethernet_pre_key_exchange_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_pre_key_exchange_set_bundle_received_cb(svc, on_bundle_received, &cap);

    // Malformed request: garbage JSON, with a local bundle set so a valid request WOULD reply.
    aethernet_pre_key_exchange_bundle_t local = sample_bundle(LOCAL_UHID);
    assert(aethernet_pre_key_exchange_set_local_bundle(svc, &local));

    aethernet_mesh_packet_t *reqpkt = aethernet_packet_new();
    reqpkt->type = AETHERNET_PACKET_TYPE_PREKEY_REQUEST;
    aethernet_packet_set_source_uhid(reqpkt, "aether:bob:02");
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(reqpkt, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(aethernet_pre_key_exchange_handle_packet(svc, reqpkt) == false);
    aethernet_packet_free(reqpkt);
    assert(s.sends_len == 0);

    // Malformed response: valid JSON but a byte[] field with the wrong decoded length (truncated
    // identity_key) → rejected. Guards the base64 exact-length check.
    aethernet_mesh_packet_t *resppkt = aethernet_packet_new();
    resppkt->type = AETHERNET_PACKET_TYPE_PREKEY_RESPONSE;
    aethernet_packet_set_source_uhid(resppkt, "aether:bob:02");
    const char *short_ik =
        "{\"request_id\":\"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a\",\"uhid\":\"aether:bob:02\","
        "\"identity_key\":\"ERERERE=\","  // decodes to 5 bytes, not 32
        "\"identity_key_x25519\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\","
        "\"pre_key_id\":4242,\"pre_key\":\"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=\","
        "\"signed_pre_key_id\":77,\"signed_pre_key\":\"REREREREREREREREREREREREREREREREREREREREREQ=\","
        "\"signed_pre_key_signature\":\"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==\"}";
    aethernet_packet_set_payload(resppkt, (const uint8_t *)short_ik, (uint32_t)strlen(short_ik));
    assert(aethernet_pre_key_exchange_handle_packet(svc, resppkt) == false);
    aethernet_packet_free(resppkt);
    assert(cap.count == 0);

    aethernet_pre_key_exchange_service_free(svc);
    fake_clear(&s);
}

// set_local_bundle / get_local_bundle round-trip; get on an unset service is false. Mirrors the C#
// SetLocalBundle / GetLocalBundle accessors.
static void local_bundle_get_set(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:bob:02");
    aethernet_pre_key_exchange_service_t *svc = aethernet_pre_key_exchange_service_new(&sender);

    aethernet_pre_key_exchange_bundle_t out;
    assert(aethernet_pre_key_exchange_get_local_bundle(svc, &out) == false);  // none set yet

    aethernet_pre_key_exchange_bundle_t local = sample_bundle("aether:bob:02");
    assert(aethernet_pre_key_exchange_set_local_bundle(svc, &local));

    assert(aethernet_pre_key_exchange_get_local_bundle(svc, &out));
    assert(strcmp(out.uhid, "aether:bob:02") == 0);
    assert(out.pre_key_id == 4242);
    assert(out.signed_pre_key_id == 77);
    assert(out.identity_key[0] == 0x11);
    aethernet_pre_key_bundle_free(&out);

    // get_received_bundle for an unknown uhid is false.
    aethernet_pre_key_exchange_bundle_t none;
    assert(aethernet_pre_key_exchange_get_received_bundle(svc, "aether:nobody:99", &none) == false);

    aethernet_pre_key_exchange_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether PreKey Exchange Service — Unit Tests\n");
    printf("===========================================\n");

    RUN(request_serializes_to_canonical_bytes);
    RUN(response_serializes_to_canonical_bytes);
    RUN(response_round_trips_through_bundle);
    RUN(request_sends_directed_pre_key_request);
    RUN(handle_request_with_local_bundle_sends_response);
    RUN(handle_request_no_local_bundle_returns_false);
    RUN(handle_response_caches_and_raises_event);
    RUN(handle_wrong_packet_type_returns_false);
    RUN(handle_malformed_payload_returns_false);
    RUN(local_bundle_get_set);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
