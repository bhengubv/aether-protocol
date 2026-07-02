// SPDX-License-Identifier: MIT
// Unit tests for channels.c (ChannelMessageService, PacketType 7).

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/channels.h"

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

// Build a ChannelMessage packet carrying the canonical payload for the given fields, using the same
// public serializer the wire path uses. `message_id` is a 16-byte UUID.
static aethernet_mesh_packet_t *channel_packet(const char *channel_id,
                                               const uint8_t message_id[AETHERNET_PACKET_ID_SIZE],
                                               const char *sender,
                                               const char *content,
                                               int64_t sent_at_ms,
                                               int32_t ttl) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_CHANNEL_MESSAGE;
    aethernet_packet_set_source_uhid(p, sender);
    aethernet_packet_set_destination_uhid(p, "*");
    p->ttl = ttl;
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    bool ok = aethernet_channel_message_payload_serialize(channel_id, message_id, sender, content,
                                                          sent_at_ms, &body, &body_len);
    assert(ok);
    aethernet_packet_set_payload(p, body, body_len);
    free(body);
    return p;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

typedef struct {
    int count;
    char channel_id[64];
    char sender_uhid[64];
    char content[64];
    int64_t sent_at_ms;
} recv_capture_t;

static void on_message(const aethernet_channel_message_t *msg, void *ud) {
    recv_capture_t *c = (recv_capture_t *)ud;
    c->count++;
    snprintf(c->channel_id, sizeof(c->channel_id), "%s", msg->channel_id ? msg->channel_id : "");
    snprintf(c->sender_uhid, sizeof(c->sender_uhid), "%s", msg->sender_uhid ? msg->sender_uhid : "");
    snprintf(c->content, sizeof(c->content), "%s", msg->content ? msg->content : "");
    c->sent_at_ms = msg->sent_at_ms;
}

// ───── Tests ─────────────────────────────────────────────

// Byte-identity gate: aethernet_channel_message_payload_serialize must emit exactly the canonical
// bytes from fixtures/channels/vectors.json for every language SDK.
static void payload_serializes_to_canonical_bytes(void) {
    // Vector "basic": id 0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f
    uint8_t id1[AETHERNET_PACKET_ID_SIZE] = {
        0x0f, 0x7e, 0x5d, 0x3c, 0x1a, 0x2b, 0x4c, 0x5d,
        0x8e, 0x9f, 0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f };
    uint8_t *json = NULL;
    uint32_t len = 0;
    assert(aethernet_channel_message_payload_serialize(
        "res-floor-3", id1, "aether:alice:01", "meeting at 6", 1700000000000LL, &json, &len));
    const char *expected1 =
        "{\"channel_id\":\"res-floor-3\",\"message_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\","
        "\"sender_uhid\":\"aether:alice:01\",\"content\":\"meeting at 6\",\"sent_at_ms\":1700000000000}";
    assert(len == (uint32_t)strlen(expected1));
    assert(memcmp(json, expected1, len) == 0);
    assert(json[len] == '\0');  // serializer null-terminates just past out_len
    free(json);

    // Vector "minimal": all-zero id, empty content, sent_at_ms 0
    uint8_t id2[AETHERNET_PACKET_ID_SIZE] = {0};
    json = NULL; len = 0;
    assert(aethernet_channel_message_payload_serialize("g", id2, "n", "", 0, &json, &len));
    const char *expected2 =
        "{\"channel_id\":\"g\",\"message_id\":\"00000000-0000-0000-0000-000000000000\","
        "\"sender_uhid\":\"n\",\"content\":\"\",\"sent_at_ms\":0}";
    assert(len == (uint32_t)strlen(expected2));
    assert(memcmp(json, expected2, len) == 0);
    free(json);
}

// Publishing floods exactly one ChannelMessage packet (dest "*", TTL default), carrying the
// channel/content/sender. Mirrors Publish_BroadcastsChannelMessage.
static void publish_broadcasts_channel_message(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);

    int delivered = aethernet_channel_publish(svc, "res-floor-3", "meeting at 6");
    assert(delivered == 1);  // fake sender reports 1 delivered

    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_CHANNEL_MESSAGE);
    assert(s.broadcasts[0]->ttl == AETHERNET_DEFAULT_TTL);
    assert(strcmp(s.broadcasts[0]->source_uhid, "aether:alice:01") == 0);
    assert(strcmp(s.broadcasts[0]->destination_uhid, "*") == 0);
    // Body carries the channel id, sender, and content (author preserved for relay hops).
    const char *want =
        "{\"channel_id\":\"res-floor-3\",\"message_id\":\"";
    assert(memcmp(s.broadcasts[0]->payload, want, strlen(want)) == 0);
    assert(strstr((const char *)s.broadcasts[0]->payload, "\"sender_uhid\":\"aether:alice:01\"") != NULL);
    assert(strstr((const char *)s.broadcasts[0]->payload, "\"content\":\"meeting at 6\"") != NULL);

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

// A message on a subscribed channel fires the received callback with the decoded fields. Mirrors
// Handle_SubscribedChannel_RaisesEvent.
static void handle_subscribed_channel_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);
    aethernet_channel_subscribe(svc, "res-floor-3");

    recv_capture_t cap = {0};
    aethernet_channel_set_message_received_cb(svc, on_message, &cap);

    uint8_t mid[AETHERNET_PACKET_ID_SIZE] = {
        0x11, 0x11, 0x11, 0x11, 0x22, 0x22, 0x33, 0x33,
        0x44, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55 };
    aethernet_mesh_packet_t *pkt =
        channel_packet("res-floor-3", mid, "aether:bob:02", "hello floor", 1700000000000LL, 7);
    bool ok = aethernet_channel_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(ok);
    assert(cap.count == 1);
    assert(strcmp(cap.channel_id, "res-floor-3") == 0);
    assert(strcmp(cap.content, "hello floor") == 0);
    assert(strcmp(cap.sender_uhid, "aether:bob:02") == 0);
    assert(cap.sent_at_ms == 1700000000000LL);

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

// A message on an UNsubscribed channel is processed (true) and relayed, but no callback fires.
// Mirrors Handle_UnsubscribedChannel_NoEventButProcessed.
static void handle_unsubscribed_channel_no_event_but_processed(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_channel_set_message_received_cb(svc, on_message, &cap);

    uint8_t mid[AETHERNET_PACKET_ID_SIZE] = {
        0xaa, 0xbb, 0xcc, 0xdd, 0x01, 0x02, 0x03, 0x04,
        0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c };
    aethernet_mesh_packet_t *pkt =
        channel_packet("society-x", mid, "aether:bob:02", "hi", 1LL, 7);
    bool ok = aethernet_channel_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(ok);            // processed + relayed
    assert(cap.count == 0); // but not surfaced — we aren't subscribed
    assert(s.broadcasts_len == 1);  // relayed once

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

// A duplicate message id is dropped: the second handle returns false and fires no second callback.
// Mirrors Handle_DuplicateMessageId_ReturnsFalse.
static void handle_duplicate_message_id_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);
    aethernet_channel_subscribe(svc, "res-floor-3");

    recv_capture_t cap = {0};
    aethernet_channel_set_message_received_cb(svc, on_message, &cap);

    uint8_t mid[AETHERNET_PACKET_ID_SIZE] = {
        0xde, 0xad, 0xbe, 0xef, 0x00, 0x11, 0x22, 0x33,
        0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb };
    aethernet_mesh_packet_t *p1 = channel_packet("res-floor-3", mid, "aether:bob:02", "one", 1LL, 7);
    aethernet_mesh_packet_t *p2 = channel_packet("res-floor-3", mid, "aether:bob:02", "one", 1LL, 7);
    assert(aethernet_channel_handle_packet(svc, p1) == true);
    assert(aethernet_channel_handle_packet(svc, p2) == false);
    aethernet_packet_free(p1);
    aethernet_packet_free(p2);

    assert(cap.count == 1);  // duplicate fired no second callback

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false). Mirrors Handle_WrongPacketType_ReturnsFalse.
static void handle_wrong_packet_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);

    uint8_t mid[AETHERNET_PACKET_ID_SIZE] = {0};
    aethernet_mesh_packet_t *pkt = channel_packet("res-floor-3", mid, "aether:bob:02", "x", 1LL, 7);
    pkt->type = AETHERNET_PACKET_TYPE_DATA;  // not a ChannelMessage
    assert(aethernet_channel_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

// A malformed payload is a benign drop (false). Mirrors the C# HandleAsync JsonException path.
static void handle_malformed_payload_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_CHANNEL_MESSAGE;
    aethernet_packet_set_source_uhid(pkt, "aether:bob:02");
    aethernet_packet_set_destination_uhid(pkt, "*");
    pkt->ttl = 7;
    const char *garbage = "not json {{{";
    aethernet_packet_set_payload(pkt, (const uint8_t *)garbage, (uint32_t)strlen(garbage));
    assert(aethernet_channel_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

// A pure relay (not subscribed) re-floods with TTL decremented. Mirrors Handle_RelaysWhenTtlAllows.
static void handle_relays_when_ttl_allows(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:relay:09");
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);  // not subscribed

    uint8_t mid[AETHERNET_PACKET_ID_SIZE] = {
        0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f, 0x60, 0x71,
        0x82, 0x93, 0xa4, 0xb5, 0xc6, 0xd7, 0xe8, 0xf9 };
    aethernet_mesh_packet_t *pkt = channel_packet("res-floor-3", mid, "aether:bob:02", "hop", 1LL, 5);
    bool ok = aethernet_channel_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(ok);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_CHANNEL_MESSAGE);
    assert(s.broadcasts[0]->ttl == 4);  // decremented from 5

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

// Own message echoed back is de-dup'd via the publish-time seen entry: never surfaced, never
// re-flooded. Exercises the isOwn guard together with publish's seen_add.
static void handle_own_message_is_not_reflooded(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);
    aethernet_channel_subscribe(svc, "res-floor-3");

    recv_capture_t cap = {0};
    aethernet_channel_set_message_received_cb(svc, on_message, &cap);

    // Publish our own message; the flood captures the broadcast (1 so far).
    int delivered = aethernet_channel_publish(svc, "res-floor-3", "mine");
    assert(delivered == 1);
    assert(s.broadcasts_len == 1);

    // Re-feed our own broadcast back into handle: seen-set dedup → false, no callback, no re-flood.
    aethernet_mesh_packet_t *echo = aethernet_packet_clone(s.broadcasts[0]);
    bool ok = aethernet_channel_handle_packet(svc, echo);
    aethernet_packet_free(echo);

    assert(ok == false);            // duplicate message id (added at publish) → dropped
    assert(cap.count == 0);         // never surfaced our own message
    assert(s.broadcasts_len == 1);  // no extra re-flood

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

// Subscribe / unsubscribe / get-subscriptions round-trip.
static void subscriptions_round_trip(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_channel_service_t *svc = aethernet_channel_service_new(&sender);

    char **subs = NULL;
    int n = -1;
    assert(aethernet_channel_get_subscriptions(svc, &subs, &n) == 0);
    assert(n == 0);
    aethernet_channel_subscriptions_free(subs, n);

    aethernet_channel_subscribe(svc, "a");
    aethernet_channel_subscribe(svc, "b");
    aethernet_channel_subscribe(svc, "a");  // idempotent — no duplicate

    subs = NULL; n = -1;
    assert(aethernet_channel_get_subscriptions(svc, &subs, &n) == 2);
    assert(n == 2);
    // Order is not contractually defined; assert set membership.
    bool saw_a = false, saw_b = false;
    for (int i = 0; i < n; i++) {
        if (strcmp(subs[i], "a") == 0) saw_a = true;
        if (strcmp(subs[i], "b") == 0) saw_b = true;
    }
    assert(saw_a && saw_b);
    aethernet_channel_subscriptions_free(subs, n);

    aethernet_channel_unsubscribe(svc, "a");
    subs = NULL; n = -1;
    assert(aethernet_channel_get_subscriptions(svc, &subs, &n) == 1);
    assert(n == 1);
    assert(strcmp(subs[0], "b") == 0);
    aethernet_channel_subscriptions_free(subs, n);

    aethernet_channel_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether Channel Message Service — Unit Tests\n");
    printf("===========================================\n");

    RUN(payload_serializes_to_canonical_bytes);
    RUN(publish_broadcasts_channel_message);
    RUN(handle_subscribed_channel_raises_event);
    RUN(handle_unsubscribed_channel_no_event_but_processed);
    RUN(handle_duplicate_message_id_returns_false);
    RUN(handle_wrong_packet_type_returns_false);
    RUN(handle_malformed_payload_returns_false);
    RUN(handle_relays_when_ttl_allows);
    RUN(handle_own_message_is_not_reflooded);
    RUN(subscriptions_round_trip);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
