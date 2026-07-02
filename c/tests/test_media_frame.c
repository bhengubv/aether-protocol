// SPDX-License-Identifier: MIT
// Unit tests for media_frame.c (VoicePtt 15 + ScreenShare 32 directed media frames). BINARY frames
// sharing the 29-byte header (call_id big-endian, sequence/timestamp little-endian, flag). Mirrors
// the green C# MediaFrameTests: byte-identity gates (2 voice_ptt + 2 screen_share, incl. all-zero +
// empty-payload) transcribed from fixtures/media/vectors.json, plus send/handle behaviour. A fake
// mesh sender captures directed sends as (cloned packet, next-hop) pairs — mirrors the C#
// FakeMeshSender that captures (packet, nextHop) pairs.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/media_frame.h"

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

// Parse a compact (dash-stripped) or canonical dashed UUID string into 16 bytes in WRITTEN order —
// i.e. big-endian / network order, matching the C# Guid.TryWriteBytes(bigEndian:true) and voice.c's
// call_id memcpy. E.g. "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f" → {0x0f,0x7e,0x5d,0x3c,0x1a,...}.
static void parse_uuid_be(const char *s, uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    char compact[33];
    int ci = 0;
    for (int i = 0; s[i] && ci < 32; i++) {
        if (s[i] == '-') continue;
        compact[ci++] = s[i];
    }
    assert(ci == 32);
    compact[32] = '\0';
    for (int i = 0; i < 16; i++) {
        unsigned int byte;
        char tmp[3] = { compact[i * 2], compact[i * 2 + 1], 0 };
        int n = sscanf(tmp, "%02x", &byte);
        assert(n == 1);
        out[i] = (uint8_t)byte;
    }
}

// Lowercase hex of `buf[0..len)` into `out` (>= 2*len+1 bytes). Mirrors the C# Convert.ToHexString +
// ToLowerInvariant used by the reference byte-identity assertions.
static void to_hex(const uint8_t *buf, uint32_t len, char *out) {
    static const char *h = "0123456789abcdef";
    for (uint32_t i = 0; i < len; i++) {
        out[i * 2]     = h[(buf[i] >> 4) & 0xF];
        out[i * 2 + 1] = h[buf[i] & 0xF];
    }
    out[len * 2] = '\0';
}

// The shared CallId used across the C# MediaFrameTests / vectors.json non-zero vectors.
#define CALL_ID_STR "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// Serialize a VoicePtt frame from raw fields and assert its lowercase hex equals `expected`.
static void assert_voice_ptt_hex(const char *call_id_str, uint32_t seq, int64_t ts, bool silence,
                                 const uint8_t *payload, uint32_t payload_len, const char *expected) {
    aethernet_voice_ptt_frame_t f;
    memset(&f, 0, sizeof(f));
    parse_uuid_be(call_id_str, f.call_id);
    f.sequence = seq;
    f.timestamp_ms = ts;
    f.is_silence = silence;
    f.encoded_payload = payload;
    f.encoded_payload_len = payload_len;

    uint8_t *buf = NULL;
    uint32_t len = 0;
    assert(aethernet_voice_ptt_frame_serialize(&f, &buf, &len));
    char hex[256];
    assert(len * 2 < sizeof(hex));
    to_hex(buf, len, hex);
    assert(strcmp(hex, expected) == 0);
    free(buf);
}

static void assert_screen_share_hex(const char *call_id_str, uint32_t seq, int64_t ts, bool keyframe,
                                    const uint8_t *payload, uint32_t payload_len, const char *expected) {
    aethernet_screen_share_frame_t f;
    memset(&f, 0, sizeof(f));
    parse_uuid_be(call_id_str, f.call_id);
    f.sequence = seq;
    f.timestamp_ms = ts;
    f.is_keyframe = keyframe;
    f.encoded_payload = payload;
    f.encoded_payload_len = payload_len;

    uint8_t *buf = NULL;
    uint32_t len = 0;
    assert(aethernet_screen_share_frame_serialize(&f, &buf, &len));
    char hex[256];
    assert(len * 2 < sizeof(hex));
    to_hex(buf, len, hex);
    assert(strcmp(hex, expected) == 0);
    free(buf);
}

// ───── Byte-identity gates (fixtures/media/vectors.json) ─────────────────────

// voice_ptt vector "frame": seq 42, ts 1700000000000, is_silence false, payload aabbcc.
static void voice_ptt_frame_serializes_to_canonical_bytes(void) {
    const uint8_t payload[] = { 0xAA, 0xBB, 0xCC };
    assert_voice_ptt_hex(CALL_ID_STR, 42, 1700000000000LL, false, payload, sizeof(payload),
        "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2a0000000068e5cf8b01000000aabbcc");
}

// voice_ptt vector "silence_empty": seq 43, ts 1700000000020, is_silence true, empty payload.
static void voice_ptt_silence_empty_serializes_to_canonical_bytes(void) {
    assert_voice_ptt_hex(CALL_ID_STR, 43, 1700000000020LL, true, NULL, 0,
        "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2b0000001468e5cf8b01000001");
}

// screen_share vector "keyframe": seq 7, ts 1700000000000, is_keyframe true, payload 11223344.
static void screen_share_keyframe_serializes_to_canonical_bytes(void) {
    const uint8_t payload[] = { 0x11, 0x22, 0x33, 0x44 };
    assert_screen_share_hex(CALL_ID_STR, 7, 1700000000000LL, true, payload, sizeof(payload),
        "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f070000000068e5cf8b0100000111223344");
}

// screen_share vector "delta_empty": all-zero call id, seq 0, ts 0, is_keyframe false, empty payload.
static void screen_share_delta_empty_serializes_to_canonical_bytes(void) {
    assert_screen_share_hex("00000000-0000-0000-0000-000000000000", 0, 0, false, NULL, 0,
        "0000000000000000000000000000000000000000000000000000000000");
}

// ───── Round-trips ───────────────────────────────────────────────────────────

static void voice_ptt_round_trips(void) {
    const uint8_t payload[] = { 1, 2, 3, 4, 5 };
    aethernet_voice_ptt_frame_t f;
    memset(&f, 0, sizeof(f));
    parse_uuid_be(CALL_ID_STR, f.call_id);
    f.sequence = 99;
    f.timestamp_ms = 123456789LL;
    f.is_silence = true;
    f.encoded_payload = payload;
    f.encoded_payload_len = sizeof(payload);

    uint8_t *buf = NULL;
    uint32_t len = 0;
    assert(aethernet_voice_ptt_frame_serialize(&f, &buf, &len));

    aethernet_voice_ptt_frame_t back;
    assert(aethernet_voice_ptt_frame_deserialize(buf, len, &back));

    uint8_t expected_id[AETHERNET_PACKET_ID_SIZE];
    parse_uuid_be(CALL_ID_STR, expected_id);
    assert(memcmp(back.call_id, expected_id, AETHERNET_PACKET_ID_SIZE) == 0);
    assert(back.sequence == 99);
    assert(back.timestamp_ms == 123456789LL);
    assert(back.is_silence == true);
    assert(back.encoded_payload_len == sizeof(payload));
    assert(memcmp(back.encoded_payload, payload, sizeof(payload)) == 0);
    free(buf);
}

static void screen_share_round_trips_keyframe_and_call_id_big_endian(void) {
    const uint8_t payload[] = { 0xFF };
    aethernet_screen_share_frame_t f;
    memset(&f, 0, sizeof(f));
    parse_uuid_be(CALL_ID_STR, f.call_id);
    f.sequence = 5;
    f.timestamp_ms = 999LL;
    f.is_keyframe = true;
    f.encoded_payload = payload;
    f.encoded_payload_len = sizeof(payload);

    uint8_t *buf = NULL;
    uint32_t len = 0;
    assert(aethernet_screen_share_frame_serialize(&f, &buf, &len));

    // call_id is written big-endian: the first serialized byte must be 0x0f (the UUID's leading byte),
    // NOT the .NET mixed-endian 0x3c. Mirrors the C# CallIdBigEndian round-trip assertion.
    assert(buf[0] == 0x0f);

    aethernet_screen_share_frame_t back;
    assert(aethernet_screen_share_frame_deserialize(buf, len, &back));

    uint8_t expected_id[AETHERNET_PACKET_ID_SIZE];
    parse_uuid_be(CALL_ID_STR, expected_id);
    assert(memcmp(back.call_id, expected_id, AETHERNET_PACKET_ID_SIZE) == 0);
    assert(back.is_keyframe == true);
    assert(back.encoded_payload_len == 1);
    assert(back.encoded_payload[0] == 0xFF);
    free(buf);
}

// ───── Capture struct for the frame-received callbacks ───────────────────────

typedef struct {
    int      count;
    uint8_t  call_id[AETHERNET_PACKET_ID_SIZE];
    uint32_t sequence;
    bool     flag;              // is_silence / is_keyframe
    char     from_uhid[128];
    uint32_t payload_len;
    uint8_t  payload[64];
} recv_capture_t;

static void on_voice_ptt(const aethernet_voice_ptt_frame_received_t *e, void *ud) {
    recv_capture_t *c = (recv_capture_t *)ud;
    c->count++;
    memcpy(c->call_id, e->frame->call_id, AETHERNET_PACKET_ID_SIZE);
    c->sequence = e->frame->sequence;
    c->flag = e->frame->is_silence;
    snprintf(c->from_uhid, sizeof(c->from_uhid), "%s", e->from_uhid ? e->from_uhid : "");
    c->payload_len = e->frame->encoded_payload_len;
    if (c->payload_len && c->payload_len <= sizeof(c->payload))
        memcpy(c->payload, e->frame->encoded_payload, c->payload_len);
}

static void on_screen_share(const aethernet_screen_share_frame_received_t *e, void *ud) {
    recv_capture_t *c = (recv_capture_t *)ud;
    c->count++;
    memcpy(c->call_id, e->frame->call_id, AETHERNET_PACKET_ID_SIZE);
    c->sequence = e->frame->sequence;
    c->flag = e->frame->is_keyframe;
    snprintf(c->from_uhid, sizeof(c->from_uhid), "%s", e->from_uhid ? e->from_uhid : "");
    c->payload_len = e->frame->encoded_payload_len;
    if (c->payload_len && c->payload_len <= sizeof(c->payload))
        memcpy(c->payload, e->frame->encoded_payload, c->payload_len);
}

// ───── Behaviour ─────────────────────────────────────────────────────────────

// send_frame emits exactly one directed VoicePtt(15) packet to the peer; feeding that packet back
// through handle_packet fires the callback with the frame + source UHID. Mirrors the C#
// VoicePtt_Send_EmitsDirectedFrame_AndHandleRaisesEvent.
static void voice_ptt_send_emits_directed_frame_and_handle_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_voice_ptt_service_t *svc = aethernet_voice_ptt_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_voice_ptt_service_set_frame_received_cb(svc, on_voice_ptt, &cap);

    const uint8_t payload[] = { 0xAA, 0xBB, 0xCC };
    aethernet_voice_ptt_frame_t frame;
    memset(&frame, 0, sizeof(frame));
    parse_uuid_be(CALL_ID_STR, frame.call_id);
    frame.sequence = 42;
    frame.timestamp_ms = 1700000000000LL;
    frame.encoded_payload = payload;
    frame.encoded_payload_len = sizeof(payload);

    assert(aethernet_voice_ptt_service_send_frame(svc, "aether:bob:02", &frame));
    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_VOICE_PTT);
    assert(strcmp(s.hops[0], "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->destination_uhid, "aether:bob:02") == 0);
    assert(strcmp(s.sends[0]->source_uhid, "aether:alice:01") == 0);
    assert(s.sends[0]->ttl == AETHERNET_DEFAULT_TTL);

    // Feed the captured packet (from alice) back through handle → callback fires.
    aethernet_packet_set_source_uhid(s.sends[0], "aether:alice:01");
    assert(aethernet_voice_ptt_service_handle_packet(svc, s.sends[0]));
    assert(cap.count == 1);
    assert(cap.sequence == 42);
    assert(strcmp(cap.from_uhid, "aether:alice:01") == 0);
    assert(cap.payload_len == 3);
    assert(cap.payload[0] == 0xAA && cap.payload[1] == 0xBB && cap.payload[2] == 0xCC);

    aethernet_voice_ptt_service_free(svc);
    fake_clear(&s);
}

// send_frame emits exactly one directed ScreenShare(32) packet; handle fires the callback with the
// keyframe flag + sequence. Mirrors the C# ScreenShare_Send_EmitsDirectedFrame_AndHandleRaisesEvent.
static void screen_share_send_emits_directed_frame_and_handle_raises_event(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_screen_share_service_t *svc = aethernet_screen_share_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_screen_share_service_set_frame_received_cb(svc, on_screen_share, &cap);

    const uint8_t payload[] = { 0x11, 0x22, 0x33, 0x44 };
    aethernet_screen_share_frame_t frame;
    memset(&frame, 0, sizeof(frame));
    parse_uuid_be(CALL_ID_STR, frame.call_id);
    frame.sequence = 7;
    frame.timestamp_ms = 1700000000000LL;
    frame.is_keyframe = true;
    frame.encoded_payload = payload;
    frame.encoded_payload_len = sizeof(payload);

    assert(aethernet_screen_share_service_send_frame(svc, "aether:bob:02", &frame));
    assert(s.sends_len == 1);
    assert(s.sends[0]->type == AETHERNET_PACKET_TYPE_SCREEN_SHARE);
    assert(strcmp(s.hops[0], "aether:bob:02") == 0);

    assert(aethernet_screen_share_service_handle_packet(svc, s.sends[0]));
    assert(cap.count == 1);
    assert(cap.flag == true);   // is_keyframe
    assert(cap.sequence == 7);

    aethernet_screen_share_service_free(svc);
    fake_clear(&s);
}

// A wrong packet type is a no-op (false), no callback, for both services. Mirrors the C#
// Handle_WrongType_ReturnsFalse.
static void handle_wrong_type_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_voice_ptt_service_t *vp = aethernet_voice_ptt_service_new(&sender);
    aethernet_screen_share_service_t *ss = aethernet_screen_share_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_voice_ptt_service_set_frame_received_cb(vp, on_voice_ptt, &cap);
    aethernet_screen_share_service_set_frame_received_cb(ss, on_screen_share, &cap);

    // A Data(3) packet with a 40-byte payload: valid frame length, wrong type → both drop.
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_DATA;
    uint8_t body[40] = {0};
    aethernet_packet_set_payload(pkt, body, sizeof(body));

    assert(aethernet_voice_ptt_service_handle_packet(vp, pkt) == false);
    assert(aethernet_screen_share_service_handle_packet(ss, pkt) == false);
    assert(cap.count == 0);
    aethernet_packet_free(pkt);

    aethernet_voice_ptt_service_free(vp);
    aethernet_screen_share_service_free(ss);
    fake_clear(&s);
}

// A short (< 29-byte) payload of the correct type is a benign drop (false), no callback. Mirrors the
// C# Handle_ShortFrame_ReturnsFalse.
static void handle_short_frame_returns_false(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_voice_ptt_service_t *vp = aethernet_voice_ptt_service_new(&sender);
    aethernet_screen_share_service_t *ss = aethernet_screen_share_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_voice_ptt_service_set_frame_received_cb(vp, on_voice_ptt, &cap);
    aethernet_screen_share_service_set_frame_received_cb(ss, on_screen_share, &cap);

    // 10 bytes: shorter than the 29-byte header → too short.
    aethernet_mesh_packet_t *vpkt = aethernet_packet_new();
    vpkt->type = AETHERNET_PACKET_TYPE_VOICE_PTT;
    uint8_t body[10] = {0};
    aethernet_packet_set_payload(vpkt, body, sizeof(body));
    assert(aethernet_voice_ptt_service_handle_packet(vp, vpkt) == false);
    aethernet_packet_free(vpkt);

    aethernet_mesh_packet_t *spkt = aethernet_packet_new();
    spkt->type = AETHERNET_PACKET_TYPE_SCREEN_SHARE;
    aethernet_packet_set_payload(spkt, body, sizeof(body));
    assert(aethernet_screen_share_service_handle_packet(ss, spkt) == false);
    aethernet_packet_free(spkt);

    assert(cap.count == 0);

    aethernet_voice_ptt_service_free(vp);
    aethernet_screen_share_service_free(ss);
    fake_clear(&s);
}

// An exactly-29-byte payload (header only, empty payload) is accepted → callback with a zero-length
// payload. Guards the header-boundary of the length check.
static void handle_empty_payload_frame_is_accepted(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:local:01");
    aethernet_voice_ptt_service_t *vp = aethernet_voice_ptt_service_new(&sender);

    recv_capture_t cap = {0};
    aethernet_voice_ptt_service_set_frame_received_cb(vp, on_voice_ptt, &cap);

    aethernet_voice_ptt_frame_t frame;
    memset(&frame, 0, sizeof(frame));
    parse_uuid_be(CALL_ID_STR, frame.call_id);
    frame.sequence = 43;
    frame.timestamp_ms = 1700000000020LL;
    frame.is_silence = true;
    frame.encoded_payload = NULL;
    frame.encoded_payload_len = 0;

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    assert(aethernet_voice_ptt_frame_serialize(&frame, &body, &body_len));
    assert(body_len == 29);

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_VOICE_PTT;
    aethernet_packet_set_source_uhid(pkt, "aether:bob:02");
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);

    assert(aethernet_voice_ptt_service_handle_packet(vp, pkt));
    assert(cap.count == 1);
    assert(cap.sequence == 43);
    assert(cap.flag == true);
    assert(cap.payload_len == 0);
    assert(strcmp(cap.from_uhid, "aether:bob:02") == 0);
    aethernet_packet_free(pkt);

    aethernet_voice_ptt_service_free(vp);
    fake_clear(&s);
}

// send_frame with an empty/NULL peer or NULL frame is a no-op (false), nothing sent. Guards the
// C# ArgumentException.ThrowIfNullOrEmpty(peerUhid) contract.
static void send_frame_rejects_bad_args(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s, "aether:alice:01");
    aethernet_voice_ptt_service_t *vp = aethernet_voice_ptt_service_new(&sender);
    aethernet_screen_share_service_t *ss = aethernet_screen_share_service_new(&sender);

    aethernet_voice_ptt_frame_t vf;
    memset(&vf, 0, sizeof(vf));
    parse_uuid_be(CALL_ID_STR, vf.call_id);
    aethernet_screen_share_frame_t sf;
    memset(&sf, 0, sizeof(sf));
    parse_uuid_be(CALL_ID_STR, sf.call_id);

    assert(aethernet_voice_ptt_service_send_frame(vp, "", &vf) == false);      // empty peer
    assert(aethernet_voice_ptt_service_send_frame(vp, "aether:bob:02", NULL) == false); // NULL frame
    assert(aethernet_screen_share_service_send_frame(ss, "", &sf) == false);
    assert(aethernet_screen_share_service_send_frame(ss, "aether:bob:02", NULL) == false);
    assert(s.sends_len == 0);

    aethernet_voice_ptt_service_free(vp);
    aethernet_screen_share_service_free(ss);
    fake_clear(&s);
}

int main(void) {
    printf("Aether Media Frame (VoicePtt 15 / ScreenShare 32) — Unit Tests\n");
    printf("==============================================================\n");

    RUN(voice_ptt_frame_serializes_to_canonical_bytes);
    RUN(voice_ptt_silence_empty_serializes_to_canonical_bytes);
    RUN(screen_share_keyframe_serializes_to_canonical_bytes);
    RUN(screen_share_delta_empty_serializes_to_canonical_bytes);
    RUN(voice_ptt_round_trips);
    RUN(screen_share_round_trips_keyframe_and_call_id_big_endian);
    RUN(voice_ptt_send_emits_directed_frame_and_handle_raises_event);
    RUN(screen_share_send_emits_directed_frame_and_handle_raises_event);
    RUN(handle_wrong_type_returns_false);
    RUN(handle_short_frame_returns_false);
    RUN(handle_empty_payload_frame_is_accepted);
    RUN(send_frame_rejects_bad_args);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
