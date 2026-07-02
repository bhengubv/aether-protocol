// SPDX-License-Identifier: MIT
// Unit tests for bandwidth_wire.c — the ABMF WIRE bindings: BandwidthProbe(53), BandwidthAck(54),
// BandwidthGossip(55). Binary LITTLE-ENDIAN byte-identity gates against fixtures/bandwidth/vectors.json
// + send/handle behaviour. Mirrors the green C# BandwidthWireTests.
//
// A fake mesh sender captures directed sends as (cloned packet, next-hop) pairs and broadcasts as
// cloned packets — mirrors the C# FakeMeshSender (Sends + Broadcasts, BroadcastAsync returns 3).

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/bandwidth_wire.h"

// ───── FakeMeshSender ────────────────────────────────────
// Directed sends -> (cloned packet, next-hop); broadcasts -> cloned packet. broadcast() returns 3 to
// mirror the C# FakeMeshSender.BroadcastAsync => Task.FromResult(3).

typedef struct {
    aethernet_mesh_packet_t **sends;    // cloned directed packets
    char                    **hops;     // owned next-hop UHIDs, parallel to sends
    int sends_len;
    int sends_cap;

    aethernet_mesh_packet_t **broadcasts;  // cloned broadcast packets
    int broadcasts_len;
    int broadcasts_cap;
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
    return true;  // mirror the C# FakeMeshSender: SendAsync returns true
}

static int fake_broadcast(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->broadcasts_len == s->broadcasts_cap) {
        s->broadcasts_cap = s->broadcasts_cap ? s->broadcasts_cap * 2 : 8;
        s->broadcasts = (aethernet_mesh_packet_t **)realloc(s->broadcasts, sizeof(*s->broadcasts) * (size_t)s->broadcasts_cap);
    }
    s->broadcasts[s->broadcasts_len] = aethernet_packet_clone(packet);
    s->broadcasts_len++;
    return 3;  // mirror the C# FakeMeshSender: BroadcastAsync returns 3
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->sends_len; i++) {
        aethernet_packet_free(s->sends[i]);
        free(s->hops[i]);
    }
    free(s->sends);
    free(s->hops);
    for (int i = 0; i < s->broadcasts_len; i++)
        aethernet_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    memset(s, 0, sizeof(*s));
}

static aethernet_mesh_sender_t make_sender(fake_state_t *state, const char *local_uhid) {
    aethernet_mesh_sender_t s = {0};
    s.local_uhid = local_uhid;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;
}

// ───── Hex helper ────────────────────────────────────────

static void to_hex(const uint8_t *b, size_t n, char *out) {
    static const char *hexd = "0123456789abcdef";
    for (size_t i = 0; i < n; i++) {
        out[2 * i]     = hexd[(b[i] >> 4) & 0xF];
        out[2 * i + 1] = hexd[b[i] & 0xF];
    }
    out[2 * n] = '\0';
}

// ───── Captures ──────────────────────────────────────────

typedef struct {
    int      count;
    uint32_t sequence;
    int64_t  sender_send_us;
    char     from_uhid[128];
} probe_capture_t;

static void on_probe(const aethernet_bw_probe_received_t *e, void *ud) {
    probe_capture_t *c = (probe_capture_t *)ud;
    c->count++;
    c->sequence       = e->probe.sequence;
    c->sender_send_us = e->probe.sender_send_us;
    snprintf(c->from_uhid, sizeof(c->from_uhid), "%s", e->from_uhid ? e->from_uhid : "");
}

typedef struct {
    int      count;
    uint32_t sequence;
    int64_t  sender_send_us;
    int64_t  receiver_receive_us;
    int64_t  receiver_send_us;
    int64_t  sender_receive_us;
    int32_t  probe_bytes;
} ack_capture_t;

static void on_ack(const aethernet_bw_probe_ack_t *a, void *ud) {
    ack_capture_t *c = (ack_capture_t *)ud;
    c->count++;
    c->sequence            = a->sequence;
    c->sender_send_us      = a->sender_send_us;
    c->receiver_receive_us = a->receiver_receive_us;
    c->receiver_send_us    = a->receiver_send_us;
    c->sender_receive_us   = a->sender_receive_us;
    c->probe_bytes         = a->probe_bytes;
}

typedef struct {
    int                       count;
    int64_t                   btlbw_bps;
    int64_t                   rtprop_us;
    aethernet_bw_confidence_t confidence;
    char                      peer_uhid[128];
} gossip_capture_t;

static void on_gossip(const aethernet_bw_gossip_t *g, void *ud) {
    gossip_capture_t *c = (gossip_capture_t *)ud;
    c->count++;
    c->btlbw_bps  = g->btlbw_bps;
    c->rtprop_us  = g->rtprop_us;
    c->confidence = g->confidence;
    snprintf(c->peer_uhid, sizeof(c->peer_uhid), "%s", g->peer_uhid);
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// ───── Byte-identity gates (fixtures/bandwidth/vectors.json) ─────

// Probe(53): sequence=42, sender_send_us=1700000000000000 → "2a00000000401e18240a0600".
static void probe_serializes_to_canonical_bytes(void) {
    aethernet_bw_probe_t p = { .sequence = 42, .sender_send_us = 1700000000000000LL };
    uint8_t buf[AETHERNET_BW_WIRE_PROBE_SIZE];
    size_t n = aethernet_bw_wire_probe_serialize(&p, buf, sizeof(buf));
    assert(n == AETHERNET_BW_WIRE_PROBE_SIZE);
    char hex[2 * AETHERNET_BW_WIRE_PROBE_SIZE + 1];
    to_hex(buf, n, hex);
    assert(strcmp(hex, "2a00000000401e18240a0600") == 0);
}

// Ack(54): seq=42, send=1700000000000000, recv_recv=1700000000012345, recv_send=1700000000013000,
// probe_bytes=1200, sender_receive_us=999 (local-only, must NOT change the wire bytes).
static void ack_serializes_to_canonical_bytes(void) {
    aethernet_bw_probe_ack_t a;
    memset(&a, 0, sizeof(a));
    a.sequence            = 42;
    a.sender_send_us      = 1700000000000000LL;
    a.receiver_receive_us = 1700000000012345LL;
    a.receiver_send_us    = 1700000000013000LL;
    a.sender_receive_us   = 999;  // local-only
    a.probe_bytes         = 1200;
    uint8_t buf[AETHERNET_BW_WIRE_ACK_SIZE];
    size_t n = aethernet_bw_wire_ack_serialize(&a, buf, sizeof(buf));
    assert(n == AETHERNET_BW_WIRE_ACK_SIZE);
    char hex[2 * AETHERNET_BW_WIRE_ACK_SIZE + 1];
    to_hex(buf, n, hex);
    assert(strcmp(hex, "2a00000000401e18240a060039701e18240a0600c8721e18240a0600b0040000") == 0);
}

// Gossip(55): btlbw_bps=5000000, rtprop_us=25000, confidence=Medium(2) → "404b4c0000000000a861000002".
// peer_uhid/transport_name/measured_at are not on the wire.
static void gossip_serializes_to_canonical_bytes(void) {
    aethernet_bw_gossip_t g;
    memset(&g, 0, sizeof(g));
    snprintf(g.peer_uhid, sizeof(g.peer_uhid), "%s", "peer");
    snprintf(g.transport_name, sizeof(g.transport_name), "%s", "tp");
    g.btlbw_bps           = 5000000;
    g.rtprop_us           = 25000;
    g.confidence          = AETHERNET_BW_CONFIDENCE_MEDIUM;
    g.measured_at_unix_ms = 123456789;
    uint8_t buf[AETHERNET_BW_WIRE_GOSSIP_SIZE];
    size_t n = aethernet_bw_wire_gossip_serialize(&g, buf, sizeof(buf));
    assert(n == AETHERNET_BW_WIRE_GOSSIP_SIZE);
    char hex[2 * AETHERNET_BW_WIRE_GOSSIP_SIZE + 1];
    to_hex(buf, n, hex);
    assert(strcmp(hex, "404b4c0000000000a861000002") == 0);
}

// Ack round-trips with sender_receive_us zeroed (not on wire). Mirrors
// Ack_RoundTrips_SenderReceiveUsZeroed.
static void ack_round_trips_sender_receive_zeroed(void) {
    aethernet_bw_probe_ack_t a;
    memset(&a, 0, sizeof(a));
    a.sequence = 7; a.sender_send_us = 100; a.receiver_receive_us = 200;
    a.receiver_send_us = 300; a.sender_receive_us = 400; a.probe_bytes = 512;

    uint8_t buf[AETHERNET_BW_WIRE_ACK_SIZE];
    assert(aethernet_bw_wire_ack_serialize(&a, buf, sizeof(buf)) == AETHERNET_BW_WIRE_ACK_SIZE);

    aethernet_bw_probe_ack_t back;
    assert(aethernet_bw_wire_ack_deserialize(buf, sizeof(buf), &back));
    assert(back.sequence == 7u);
    assert(back.sender_send_us == 100);
    assert(back.receiver_receive_us == 200);
    assert(back.receiver_send_us == 300);
    assert(back.sender_receive_us == 0);  // not on wire
    assert(back.probe_bytes == 512);
}

// Probe/gossip round-trips (exercise the deserialize path directly).
static void probe_gossip_round_trip(void) {
    aethernet_bw_probe_t p = { .sequence = 9, .sender_send_us = 123 };
    uint8_t pbuf[AETHERNET_BW_WIRE_PROBE_SIZE];
    assert(aethernet_bw_wire_probe_serialize(&p, pbuf, sizeof(pbuf)) == AETHERNET_BW_WIRE_PROBE_SIZE);
    aethernet_bw_probe_t pb;
    assert(aethernet_bw_wire_probe_deserialize(pbuf, sizeof(pbuf), &pb));
    assert(pb.sequence == 9u && pb.sender_send_us == 123);

    aethernet_bw_gossip_t g;
    memset(&g, 0, sizeof(g));
    g.btlbw_bps = 5000000; g.rtprop_us = 25000; g.confidence = AETHERNET_BW_CONFIDENCE_MEDIUM;
    uint8_t gbuf[AETHERNET_BW_WIRE_GOSSIP_SIZE];
    assert(aethernet_bw_wire_gossip_serialize(&g, gbuf, sizeof(gbuf)) == AETHERNET_BW_WIRE_GOSSIP_SIZE);
    aethernet_bw_gossip_t gb;
    assert(aethernet_bw_wire_gossip_deserialize(gbuf, sizeof(gbuf), &gb));
    assert(gb.btlbw_bps == 5000000 && gb.rtprop_us == 25000);
    assert(gb.confidence == AETHERNET_BW_CONFIDENCE_MEDIUM);
    assert(gb.peer_uhid[0] == '\0');  // service fills this, not the codec
}

// Short buffers are rejected by every deserializer (bounds-check guard).
static void short_buffers_rejected(void) {
    uint8_t buf[32] = {0};
    aethernet_bw_probe_t p;
    aethernet_bw_probe_ack_t a;
    aethernet_bw_gossip_t g;
    assert(aethernet_bw_wire_probe_deserialize(buf, 11, &p) == false);   // need 12
    assert(aethernet_bw_wire_ack_deserialize(buf, 31, &a) == false);     // need 32
    assert(aethernet_bw_wire_gossip_deserialize(buf, 12, &g) == false);  // need 13
    // Boundary: exact sizes accepted.
    assert(aethernet_bw_wire_probe_deserialize(buf, 12, &p) == true);
    assert(aethernet_bw_wire_ack_deserialize(buf, 32, &a) == true);
    assert(aethernet_bw_wire_gossip_deserialize(buf, 13, &g) == true);
}

// ───── Behaviour ─────────────────────────────────────────

// SendProbe emits exactly one directed BandwidthProbe to the peer. Mirrors
// SendProbe_EmitsDirectedProbe.
static void send_probe_emits_directed_probe(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:a:01");
    aethernet_bw_wire_service_t *svc = aethernet_bw_wire_service_new(&sender);

    aethernet_bw_probe_t p = { .sequence = 42, .sender_send_us = 1700000000000000LL };
    assert(aethernet_bw_wire_service_send_probe(svc, "aether:b:02", &p));

    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_BANDWIDTH_PROBE);
    assert(strcmp(s.hops[0], "aether:b:02") == 0);
    assert(strcmp(s.sends[0]->destination_uhid, "aether:b:02") == 0);
    assert(strcmp(s.sends[0]->source_uhid, "aether:a:01") == 0);
    assert(s.sends[0]->ttl == AETHERNET_DEFAULT_TTL);
    assert(s.sends[0]->payload_len == AETHERNET_BW_WIRE_PROBE_SIZE);

    aethernet_bw_wire_service_free(svc);
    fake_clear(&s);
}

// SendAck emits exactly one directed BandwidthAck. Mirrors SendAck_EmitsDirectedAck.
static void send_ack_emits_directed_ack(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_bw_wire_service_t *svc = aethernet_bw_wire_service_new(&sender);

    aethernet_bw_probe_ack_t a;
    memset(&a, 0, sizeof(a));
    a.sequence = 1; a.sender_send_us = 2; a.receiver_receive_us = 3;
    a.receiver_send_us = 4; a.sender_receive_us = 5; a.probe_bytes = 6;
    assert(aethernet_bw_wire_service_send_ack(svc, "aether:b:02", &a));

    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_BANDWIDTH_ACK);
    assert(strcmp(s.hops[0], "aether:b:02") == 0);
    assert(s.sends[0]->payload_len == AETHERNET_BW_WIRE_ACK_SIZE);

    aethernet_bw_wire_service_free(svc);
    fake_clear(&s);
}

// BroadcastGossip emits gossip (out_count == 3) and Handle raises the gossip event with the source
// peer stamped in. Mirrors BroadcastGossip_EmitsGossip_AndHandleRaisesEvent_WithSourcePeer.
static void broadcast_gossip_and_handle_raises_with_source(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_bw_wire_service_t *svc = aethernet_bw_wire_service_new(&sender);

    aethernet_bw_gossip_t g;
    memset(&g, 0, sizeof(g));
    g.btlbw_bps = 5000000; g.rtprop_us = 25000; g.confidence = AETHERNET_BW_CONFIDENCE_MEDIUM;

    int reached = -1;
    assert(aethernet_bw_wire_service_broadcast_gossip(svc, &g, &reached));
    assert(reached == 3);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_BANDWIDTH_GOSSIP);
    assert(strcmp(s.broadcasts[0]->destination_uhid, "*") == 0);

    // Take the broadcast packet, stamp a source, and feed it back through handle.
    gossip_capture_t cap = {0};
    aethernet_bw_wire_service_set_gossip_received_cb(svc, on_gossip, &cap);

    aethernet_mesh_packet_t *pkt = aethernet_packet_clone(s.broadcasts[0]);
    aethernet_packet_set_source_uhid(pkt, "aether:peer:09");
    assert(aethernet_bw_wire_service_handle_packet(svc, pkt));
    aethernet_packet_free(pkt);

    assert(cap.count == 1);
    assert(cap.btlbw_bps == 5000000);
    assert(cap.rtprop_us == 25000);
    assert(cap.confidence == AETHERNET_BW_CONFIDENCE_MEDIUM);
    assert(strcmp(cap.peer_uhid, "aether:peer:09") == 0);

    aethernet_bw_wire_service_free(svc);
    fake_clear(&s);
}

// Handle of a BandwidthProbe raises probe-received with the packet source. Mirrors
// Handle_Probe_RaisesProbeReceived_WithSource.
static void handle_probe_raises_with_source(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_bw_wire_service_t *svc = aethernet_bw_wire_service_new(&sender);

    probe_capture_t cap = {0};
    aethernet_bw_wire_service_set_probe_received_cb(svc, on_probe, &cap);

    aethernet_bw_probe_t p = { .sequence = 9, .sender_send_us = 123 };
    uint8_t body[AETHERNET_BW_WIRE_PROBE_SIZE];
    assert(aethernet_bw_wire_probe_serialize(&p, body, sizeof(body)) == AETHERNET_BW_WIRE_PROBE_SIZE);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_BANDWIDTH_PROBE;
    aethernet_packet_set_source_uhid(pkt, "aether:x:01");
    aethernet_packet_set_payload(pkt, body, sizeof(body));
    assert(aethernet_bw_wire_service_handle_packet(svc, pkt));
    aethernet_packet_free(pkt);

    assert(cap.count == 1);
    assert(cap.sequence == 9u);
    assert(cap.sender_send_us == 123);
    assert(strcmp(cap.from_uhid, "aether:x:01") == 0);

    aethernet_bw_wire_service_free(svc);
    fake_clear(&s);
}

// Handle of a BandwidthAck raises ack-received (sequence + probe_bytes intact, sender_receive_us
// zeroed). Mirrors Handle_Ack_RaisesAckReceived.
static void handle_ack_raises_ack_received(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_bw_wire_service_t *svc = aethernet_bw_wire_service_new(&sender);

    ack_capture_t cap = {0};
    aethernet_bw_wire_service_set_ack_received_cb(svc, on_ack, &cap);

    aethernet_bw_probe_ack_t a;
    memset(&a, 0, sizeof(a));
    a.sequence = 3; a.sender_send_us = 10; a.receiver_receive_us = 20;
    a.receiver_send_us = 30; a.sender_receive_us = 0; a.probe_bytes = 64;
    uint8_t body[AETHERNET_BW_WIRE_ACK_SIZE];
    assert(aethernet_bw_wire_ack_serialize(&a, body, sizeof(body)) == AETHERNET_BW_WIRE_ACK_SIZE);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_BANDWIDTH_ACK;
    aethernet_packet_set_source_uhid(pkt, "aether:x:01");
    aethernet_packet_set_payload(pkt, body, sizeof(body));
    assert(aethernet_bw_wire_service_handle_packet(svc, pkt));
    aethernet_packet_free(pkt);

    assert(cap.count == 1);
    assert(cap.sequence == 3u);
    assert(cap.probe_bytes == 64);
    assert(cap.sender_receive_us == 0);

    aethernet_bw_wire_service_free(svc);
    fake_clear(&s);
}

// Handle of the wrong packet type returns false and fires no callback. Mirrors
// Handle_WrongType_ReturnsFalse.
static void handle_wrong_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_bw_wire_service_t *svc = aethernet_bw_wire_service_new(&sender);

    probe_capture_t pc = {0};
    ack_capture_t   ac = {0};
    gossip_capture_t gc = {0};
    aethernet_bw_wire_service_set_probe_received_cb(svc, on_probe, &pc);
    aethernet_bw_wire_service_set_ack_received_cb(svc, on_ack, &ac);
    aethernet_bw_wire_service_set_gossip_received_cb(svc, on_gossip, &gc);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DATA;
    const uint8_t empty[1] = {0};
    aethernet_packet_set_payload(pkt, empty, 0);
    assert(aethernet_bw_wire_service_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);

    assert(pc.count == 0 && ac.count == 0 && gc.count == 0);

    aethernet_bw_wire_service_free(svc);
    fake_clear(&s);
}

// Handle of a bandwidth type with a short/truncated body returns false (malformed → dropped).
static void handle_short_body_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_bw_wire_service_t *svc = aethernet_bw_wire_service_new(&sender);

    probe_capture_t pc = {0};
    aethernet_bw_wire_service_set_probe_received_cb(svc, on_probe, &pc);

    // A BandwidthProbe packet with only 4 payload bytes (needs 12) → dropped.
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_BANDWIDTH_PROBE;
    aethernet_packet_set_source_uhid(pkt, "aether:x:01");
    const uint8_t stub[4] = { 0x2a, 0x00, 0x00, 0x00 };
    aethernet_packet_set_payload(pkt, stub, sizeof(stub));
    assert(aethernet_bw_wire_service_handle_packet(svc, pkt) == false);
    aethernet_packet_free(pkt);
    assert(pc.count == 0);

    aethernet_bw_wire_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether Bandwidth WIRE Bindings — Unit Tests\n");
    printf("===========================================\n");

    RUN(probe_serializes_to_canonical_bytes);
    RUN(ack_serializes_to_canonical_bytes);
    RUN(gossip_serializes_to_canonical_bytes);
    RUN(ack_round_trips_sender_receive_zeroed);
    RUN(probe_gossip_round_trip);
    RUN(short_buffers_rejected);
    RUN(send_probe_emits_directed_probe);
    RUN(send_ack_emits_directed_ack);
    RUN(broadcast_gossip_and_handle_raises_with_source);
    RUN(handle_probe_raises_with_source);
    RUN(handle_ack_raises_ack_received);
    RUN(handle_wrong_type_returns_false);
    RUN(handle_short_body_returns_false);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
