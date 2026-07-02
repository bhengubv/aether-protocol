// SPDX-License-Identifier: MIT
// Unit tests for erid_announce.c (EridAnnounceService, PacketType EridAnnounce 56).
// Directed transport of an already-Signal-encrypted ERID announcement over the mesh. A fake mesh sender
// captures directed sends as (cloned packet, next-hop) pairs — mirrors the C# PresenceEridAnnounceTests
// FakeMeshSender that captures (packet, nextHop) pairs.
//
// Also re-pins the SHARED ERID-announcement frame byte-identity: the existing erid.c
// EridAnnouncementCodec (aethernet_erid_announcement_encode) must reproduce fixtures/erid
// announcement_encode_hex for the routing key from routing_key_hex (epochSeconds 900, eridLength 16).
// Mirrors the C# EridAnnouncementCodec_MatchesCanonicalFrame.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/erid.h"
#include "aethernet/erid_announce.h"

#define LOCAL_UHID "aether:local:01"

// ───── FakeMeshSender ────────────────────────────────────
// Captures directed sends as (cloned packet, next-hop) pairs. Mirrors the C# FakeMeshSender.Sends;
// SendAsync returns true delivered.

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

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

typedef struct {
    int count;
    uint32_t len;
    uint8_t bytes[256];
    char from_uhid[128];
} recv_capture_t;

static void on_announce(const aethernet_erid_announce_received_t *e, void *ud) {
    recv_capture_t *c = (recv_capture_t *)ud;
    c->count++;
    c->len = e->len;
    if (e->encrypted_announcement && e->len && e->len <= sizeof(c->bytes))
        memcpy(c->bytes, e->encrypted_announcement, e->len);
    snprintf(c->from_uhid, sizeof(c->from_uhid), "%s", e->from_uhid ? e->from_uhid : "");
}

// Parse a hex string into bytes. Returns the byte count, or -1 on odd length / bad digit.
static int hex_decode(const char *hex, uint8_t *out, size_t out_cap) {
    size_t hlen = strlen(hex);
    if (hlen % 2 != 0 || hlen / 2 > out_cap) return -1;
    for (size_t i = 0; i < hlen; i += 2) {
        int hi, lo;
        char ch = hex[i], cl = hex[i + 1];
        if (ch >= '0' && ch <= '9') hi = ch - '0';
        else if (ch >= 'a' && ch <= 'f') hi = ch - 'a' + 10;
        else if (ch >= 'A' && ch <= 'F') hi = ch - 'A' + 10;
        else return -1;
        if (cl >= '0' && cl <= '9') lo = cl - '0';
        else if (cl >= 'a' && cl <= 'f') lo = cl - 'a' + 10;
        else if (cl >= 'A' && cl <= 'F') lo = cl - 'A' + 10;
        else return -1;
        out[i / 2] = (uint8_t)((hi << 4) | lo);
    }
    return (int)(hlen / 2);
}

static void hex_encode(const uint8_t *bytes, size_t len, char *out) {
    static const char digits[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2]     = digits[(bytes[i] >> 4) & 0xF];
        out[i * 2 + 1] = digits[bytes[i] & 0xF];
    }
    out[len * 2] = '\0';
}

// ───── Tests ─────────────────────────────────────────────

// send emits exactly one directed EridAnnounce packet (type 56, dest = peer, source local UHID, default
// TTL) whose opaque payload is a copy of the encrypted announcement; handle_packet on that packet fires
// the callback with the same bytes + packet source as from_uhid. Mirrors the C#
// EridAnnounce_Send_EmitsDirectedPacket_AndHandleRaisesEvent.
static void send_emits_directed_packet_and_handle_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_erid_announce_service_t *svc = aethernet_erid_announce_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_erid_announce_set_received_cb(svc, on_announce, &cap);

    uint8_t enc[] = { 1, 2, 3, 4, 5 };  // opaque Signal-encrypted announcement
    assert(aethernet_erid_announce_send(svc, "aether:bob:02", enc, (uint32_t)sizeof(enc)));

    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_ERID_ANNOUNCE);
    assert(strcmp(s.hops[0], "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->destination_uhid, "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->source_uhid, "aether:alice:01") == 0);
    assert(s.sends[0]->ttl == AETHERNET_DEFAULT_TTL);
    assert(s.sends[0]->payload_len == sizeof(enc));
    assert(memcmp(s.sends[0]->payload, enc, sizeof(enc)) == 0);

    // Re-point the cloned packet's source (as the C# test sets sent.Packet.SourceUhid) and handle it.
    aethernet_packet_set_source_uhid(s.sends[0], "aether:bob:02");
    assert(aethernet_erid_announce_handle_packet(svc, s.sends[0]));
    assert(cap.count == 1);
    assert(cap.len == sizeof(enc));
    assert(memcmp(cap.bytes, enc, sizeof(enc)) == 0);
    assert(strcmp(cap.from_uhid, "aether:bob:02") == 0);

    aethernet_erid_announce_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type OR an empty body is a no-op (false), no callback. Mirrors the C#
// EridAnnounce_Handle_WrongTypeOrEmpty_ReturnsFalse.
static void handle_wrong_type_or_empty_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, LOCAL_UHID);
    aethernet_erid_announce_service_t *svc = aethernet_erid_announce_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_erid_announce_set_received_cb(svc, on_announce, &cap);

    // Wrong type (Data), non-empty body → false.
    aethernet_mesh_packet_t *wrong = aethernet_packet_new();
    wrong->type = AETHERNET_PACKET_TYPE_DATA;
    aethernet_packet_set_source_uhid(wrong, "aether:x:01");
    const uint8_t one[] = { 1 };
    aethernet_packet_set_payload(wrong, one, (uint32_t)sizeof(one));
    assert(aethernet_erid_announce_handle_packet(svc, wrong) == false);
    aethernet_packet_free(wrong);

    // Right type (EridAnnounce), empty body → false.
    aethernet_mesh_packet_t *empty = aethernet_packet_new();
    empty->type = AETHERNET_PACKET_TYPE_ERID_ANNOUNCE;
    aethernet_packet_set_source_uhid(empty, "aether:x:01");
    // payload stays NULL/0 (aethernet_packet_new zeroes it).
    assert(aethernet_erid_announce_handle_packet(svc, empty) == false);
    aethernet_packet_free(empty);

    assert(cap.count == 0);

    aethernet_erid_announce_service_free(svc);
    fake_clear(&s);
}

// send with an empty peer, a NULL/zero-length announcement, or a NULL service is rejected (false),
// nothing sent. Guards the C# ArgumentException paths.
static void send_invalid_args_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_erid_announce_service_t *svc = aethernet_erid_announce_service_new(&sender);

    uint8_t enc[] = { 9, 8, 7 };
    assert(aethernet_erid_announce_send(svc, "", enc, (uint32_t)sizeof(enc)) == false);      // empty peer
    assert(aethernet_erid_announce_send(svc, "aether:bob:02", NULL, 0) == false);            // NULL body
    assert(aethernet_erid_announce_send(svc, "aether:bob:02", enc, 0) == false);             // zero len
    assert(aethernet_erid_announce_send(NULL, "aether:bob:02", enc, (uint32_t)sizeof(enc)) == false); // NULL svc
    assert(s.sends_len == 0);

    aethernet_erid_announce_service_free(svc);
    fake_clear(&s);
}

// Re-pin the shared ERID-announcement frame byte-identity (existing erid.c codec) against the
// fixtures/erid vectors: encode(routing_key = routing_key_hex, epochSeconds 900, eridLength 16) must
// equal announcement_encode_hex. Mirrors the C# EridAnnouncementCodec_MatchesCanonicalFrame.
static void erid_announcement_codec_matches_canonical_frame(void) {
    // fixtures/erid/vectors.json: routing_key_hex.
    const char *routing_key_hex =
        "8f3aa76cdbe9a2b47c5813504023a77bda134c31aa096b51392fb29cdd57ddca";
    // fixtures/erid/vectors.json: announcement_encode_hex.
    const char *announce_hex =
        "41455244010000038400000010000000208f3aa76cdbe9a2b47c5813504023a77bda134c31aa096b51392fb29cdd57ddca";

    uint8_t rk[AETHERNET_ERID_ROUTING_KEY_SIZE];
    int rk_len = hex_decode(routing_key_hex, rk, sizeof(rk));
    assert(rk_len == AETHERNET_ERID_ROUTING_KEY_SIZE);

    uint8_t frame[AETHERNET_ERID_ANNOUNCE_HEADER_LEN + AETHERNET_ERID_ROUTING_KEY_SIZE];
    size_t frame_len = 0;
    assert(aethernet_erid_announcement_encode(rk, (size_t)rk_len,
                                              /*epoch_seconds=*/900, /*erid_length=*/16,
                                              frame, sizeof(frame), &frame_len));

    char frame_hex[sizeof(frame) * 2 + 1];
    hex_encode(frame, frame_len, frame_hex);
    assert(strcmp(frame_hex, announce_hex) == 0);
}

int main(void) {
    printf("Aether EridAnnounce WIRE Service — Unit Tests\n");
    printf("=============================================\n");

    RUN(send_emits_directed_packet_and_handle_raises_event);
    RUN(handle_wrong_type_or_empty_returns_false);
    RUN(send_invalid_args_returns_false);
    RUN(erid_announcement_codec_matches_canonical_frame);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
