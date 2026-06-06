// SPDX-License-Identifier: MIT
// Unit tests for VideoCallService (streaming.c).

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethermesh/constants.h"
#include "aethermesh/protocol.h"
#include "aethermesh/routing.h"
#include "aethermesh/transport.h"
#include "aethermesh/streaming.h"
#include "aethermesh/voice.h"   // for AETHERMESH_VOICE_STATE_* values

// ── Fake transport ────────────────────────────────────────────

static bool ft_send(void *h, const char *peer, const uint8_t *d, size_t n) {
    (void)h; (void)peer; (void)d; (void)n; return true;
}
static bool ft_is_connected(void *h, const char *peer) {
    (void)h; (void)peer; return false;
}
static aethermesh_transport_vtable_t g_vtable = {
    .name                 = "fake",
    .max_bandwidth_bps    = 1000000,
    .power_cost_relative  = 1,
    .max_range_meters     = 100,
    .send                 = ft_send,
    .is_connected         = ft_is_connected,
    .set_on_data_received = NULL,
    .destroy              = NULL,
    .get_metrics          = NULL,
};
static aethermesh_transport_t g_transport = { .vtable = &g_vtable, .handle = NULL };

// ── Fake routing sender ───────────────────────────────────────

static bool rs_send(aethermesh_mesh_sender_t *s, const aethermesh_mesh_packet_t *p, const char *hop) {
    (void)s; (void)p; (void)hop; return true;
}
static int rs_broadcast(aethermesh_mesh_sender_t *s, const aethermesh_mesh_packet_t *p) {
    (void)s; (void)p; return 0;
}

// ── UUID helpers ──────────────────────────────────────────────

static void test_uuid_str(const uint8_t b[16], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        b[0],b[1],b[2],b[3], b[4],b[5], b[6],b[7],
        b[8],b[9], b[10],b[11],b[12],b[13],b[14],b[15]);
}

// ── Service factory ───────────────────────────────────────────

static aethermesh_video_call_service_t *make_video_svc(const char *local_uhid) {
    static aethermesh_mesh_sender_t rs;
    rs.local_uhid = local_uhid;
    rs.send       = rs_send;
    rs.broadcast  = rs_broadcast;
    rs.user_data  = NULL;
    aethermesh_routing_service_t *routing = aethermesh_routing_service_new(&rs);
    return aethermesh_video_call_service_create(&g_transport, routing, local_uhid);
}

// ── Packet builders ───────────────────────────────────────────

// Video signaling packet carrying arbitrary JSON.
static aethermesh_packet_t *make_video_sig_pkt(const char *from, const char *json) {
    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) return NULL;
    p->type = AETHERMESH_PACKET_TYPE_VIDEO_SIGNALING;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

// Inbound video offer: JSON deliberately omits "signal_type" so the C code
// hits the `if (!sig)` branch and registers it as an incoming call.
static aethermesh_packet_t *make_inbound_offer(
    const char *from, const char *call_id_str,
    const char *video_codec, const char *audio_codec
) {
    char json[512];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"%s\","
        "\"video_codecs\":[\"%s\"],"
        "\"audio_codecs\":[\"%s\"]}",
        call_id_str, from, video_codec, audio_codec);
    return make_video_sig_pkt(from, json);
}

// Binary video frame: [16 callId][4 seq LE][8 ts LE][1 isKeyframe][4 payload].
static aethermesh_packet_t *make_video_frame_pkt(
    const char *from, const uint8_t call_id[16], int is_keyframe
) {
    uint8_t buf[33];   // 29-byte header + 4 bytes of dummy payload
    memcpy(buf, call_id, 16);
    memset(buf + 16, 0, 4); // seq = 0
    memset(buf + 20, 0, 8); // ts  = 0
    buf[28] = (uint8_t)(is_keyframe ? 1 : 0);
    buf[29] = 0xDE; buf[30] = 0xAD; buf[31] = 0xBE; buf[32] = 0xEF;

    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) return NULL;
    p->type = AETHERMESH_PACKET_TYPE_VIDEO_FRAME;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, buf, sizeof(buf));
    return p;
}

// Deterministic call-id bytes for the canonical test UUID
// "aabbccdd-eeff-4001-8002-aabbccddeeff".
static const uint8_t k_test_call_id[16] = {
    0xaa,0xbb,0xcc,0xdd, 0xee,0xff, 0x40,0x01,
    0x80,0x02, 0xaa,0xbb,0xcc,0xdd,0xee,0xff
};
static const char *k_test_call_id_str = "aabbccdd-eeff-4001-8002-aabbccddeeff";

// ── Callback capture ──────────────────────────────────────────

static int g_incoming_count = 0;
static char g_incoming_from[64];
static void on_incoming(const uint8_t *cid, const char *from,
                        const char **vcod, int vc, const char **acod, int ac, void *ud) {
    (void)cid; (void)vcod; (void)vc; (void)acod; (void)ac; (void)ud;
    g_incoming_count++;
    strncpy(g_incoming_from, from ? from : "", 63);
}

static int g_state_count = 0;
static int g_last_state = -1;
static void on_state_changed(const uint8_t *cid, int state, void *ud) {
    (void)cid; (void)ud;
    g_state_count++;
    g_last_state = state;
}

static int g_frame_count = 0;
static int g_last_is_keyframe = -1;
static void on_frame(const uint8_t *cid, const uint8_t *video, size_t vlen,
                     int is_kf, int64_t ts, void *ud) {
    (void)cid; (void)video; (void)vlen; (void)ts; (void)ud;
    g_frame_count++;
    g_last_is_keyframe = is_kf;
}

static int g_kfr_count = 0;
static void on_kfr(const uint8_t *cid, void *ud) {
    (void)cid; (void)ud;
    g_kfr_count++;
}

static int g_quality_count = 0;
static char g_last_quality[64];
static void on_quality(const uint8_t *cid, const char *quality, void *ud) {
    (void)cid; (void)ud;
    g_quality_count++;
    strncpy(g_last_quality, quality ? quality : "", 63);
}

// ── Test runner ───────────────────────────────────────────────

#define RUN(name) do { \
    printf("TEST: " #name "..."); \
    name(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)
static int tests_run = 0;

// ── Tests ─────────────────────────────────────────────────────

static void send_offer_returns_call_id(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vcod[] = { "h264", "vp8" };
    const char *acod[] = { "opus" };
    uint8_t call_id[16] = {0};
    int rc = aethermesh_video_send_offer(svc, "bob", vcod, 2, acod, 1, call_id);
    assert(rc == 0);
    int nonzero = 0;
    for (int i = 0; i < 16; i++) nonzero += (call_id[i] != 0 ? 1 : 0);
    assert(nonzero > 0);
    aethermesh_video_call_service_destroy(svc);
}

static void send_offer_null_to_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    uint8_t call_id[16] = {0};
    int rc = aethermesh_video_send_offer(svc, NULL, NULL, 0, NULL, 0, call_id);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void accept_call_unknown_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aethermesh_video_accept_call(svc, unknown);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void accept_call_outgoing_returns_error(void) {
    // Outgoing call (sender) cannot accept its own offer.
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vcod[] = { "h264" };
    uint8_t call_id[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vcod, 1, NULL, 0, call_id);
    int rc = aethermesh_video_accept_call(svc, call_id);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void accept_call_incoming_returns_ok(void) {
    // alice receives an inbound offer from bob; she then accepts it.
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    aethermesh_packet_t *pkt = make_inbound_offer("bob", k_test_call_id_str, "h264", "opus");
    aethermesh_video_handle_packet(svc, pkt);
    aethermesh_packet_free(pkt);

    int rc = aethermesh_video_accept_call(svc, k_test_call_id);
    assert(rc == 0);
    aethermesh_video_call_service_destroy(svc);
}

static void hang_up_unknown_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aethermesh_video_hang_up(svc, unknown);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void hang_up_known_returns_ok(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vcod[] = { "h264" };
    uint8_t call_id[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vcod, 1, NULL, 0, call_id);
    int rc = aethermesh_video_hang_up(svc, call_id);
    assert(rc == 0);
    aethermesh_video_call_service_destroy(svc);
}

static void send_frame_unknown_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    const uint8_t data[] = { 0xDE, 0xAD };
    int rc = aethermesh_video_send_frame(svc, unknown, data, sizeof(data), 0);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void send_frame_not_connected_returns_error(void) {
    // Call in OUTGOING state (offer sent) — send_frame must fail.
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vcod[] = { "h264" };
    uint8_t call_id[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vcod, 1, NULL, 0, call_id);
    const uint8_t data[] = { 0xDE, 0xAD };
    int rc = aethermesh_video_send_frame(svc, call_id, data, sizeof(data), 0);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void send_frame_connected_returns_ok(void) {
    // alice: receive offer → INCOMING; accept → CONNECTED; send_frame → ok.
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    aethermesh_packet_t *offer = make_inbound_offer("bob", k_test_call_id_str, "h264", "opus");
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);

    aethermesh_video_accept_call(svc, k_test_call_id);

    const uint8_t data[] = { 0x01, 0x02, 0x03, 0x04 };
    int rc = aethermesh_video_send_frame(svc, k_test_call_id, data, sizeof(data), 1);
    assert(rc == 0);
    aethermesh_video_call_service_destroy(svc);
}

static void request_keyframe_unknown_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aethermesh_video_request_keyframe(svc, unknown);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void request_keyframe_known_returns_ok(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vcod[] = { "h264" };
    uint8_t call_id[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vcod, 1, NULL, 0, call_id);
    int rc = aethermesh_video_request_keyframe(svc, call_id);
    assert(rc == 0);
    aethermesh_video_call_service_destroy(svc);
}

static void notify_quality_unknown_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aethermesh_video_notify_quality_change(svc, unknown, "low");
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void notify_quality_known_returns_ok(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vcod[] = { "h264" };
    uint8_t call_id[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vcod, 1, NULL, 0, call_id);
    int rc = aethermesh_video_notify_quality_change(svc, call_id, "high");
    assert(rc == 0);
    aethermesh_video_call_service_destroy(svc);
}

static void handle_packet_inbound_offer_fires_incoming_cb(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    g_incoming_count = 0;
    aethermesh_video_set_incoming_cb(svc, on_incoming, NULL);

    aethermesh_packet_t *pkt = make_inbound_offer("bob", k_test_call_id_str, "vp8", "opus");
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_incoming_count == 1);
    assert(strcmp(g_incoming_from, "bob") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void handle_packet_accept_fires_state_cb_connected(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    g_state_count = 0; g_last_state = -1;
    aethermesh_video_set_state_changed_cb(svc, on_state_changed, NULL);

    // alice sends offer → call in OUTGOING state
    const char *vcod[] = { "h264" };
    uint8_t call_id[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vcod, 1, NULL, 0, call_id);

    // bob accepts
    char id_str[37];
    test_uuid_str(call_id, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"video_accept\"}",
        id_str);
    aethermesh_packet_t *pkt = make_video_sig_pkt("bob", json);
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_state_count == 1);
    assert(g_last_state == AETHERMESH_VOICE_STATE_CONNECTED);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void handle_packet_hangup_fires_state_cb_ended(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    g_state_count = 0; g_last_state = -1;
    aethermesh_video_set_state_changed_cb(svc, on_state_changed, NULL);

    const char *vcod[] = { "h264" };
    uint8_t call_id[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vcod, 1, NULL, 0, call_id);

    char id_str[37];
    test_uuid_str(call_id, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"video_hangup\"}",
        id_str);
    aethermesh_packet_t *pkt = make_video_sig_pkt("bob", json);
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_state_count == 1);
    assert(g_last_state == AETHERMESH_VOICE_STATE_ENDED);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void handle_packet_keyframe_request_fires_kfr_cb(void) {
    // alice receives offer from bob, then receives a keyframe_request.
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    g_kfr_count = 0;
    aethermesh_video_set_keyframe_request_cb(svc, on_kfr, NULL);

    aethermesh_packet_t *offer = make_inbound_offer("bob", k_test_call_id_str, "h264", "opus");
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);

    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"keyframe_request\"}",
        k_test_call_id_str);
    aethermesh_packet_t *pkt = make_video_sig_pkt("bob", json);
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_kfr_count == 1);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void handle_packet_quality_change_fires_quality_cb(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    g_quality_count = 0;
    aethermesh_video_set_quality_changed_cb(svc, on_quality, NULL);

    aethermesh_packet_t *offer = make_inbound_offer("bob", k_test_call_id_str, "h264", "opus");
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);

    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\","
        "\"signal_type\":\"quality_change\",\"quality\":\"low\"}",
        k_test_call_id_str);
    aethermesh_packet_t *pkt = make_video_sig_pkt("bob", json);
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_quality_count == 1);
    assert(strcmp(g_last_quality, "low") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void handle_packet_video_frame_fires_frame_cb(void) {
    // alice: receive offer → accept → CONNECTED; then receives a video frame.
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    g_frame_count = 0; g_last_is_keyframe = -1;
    aethermesh_video_set_frame_cb(svc, on_frame, NULL);

    aethermesh_packet_t *offer = make_inbound_offer("bob", k_test_call_id_str, "h264", "opus");
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);
    aethermesh_video_accept_call(svc, k_test_call_id);

    aethermesh_packet_t *frame = make_video_frame_pkt("bob", k_test_call_id, 1);
    int rc = aethermesh_video_handle_packet(svc, frame);
    assert(rc == 0);
    assert(g_frame_count == 1);
    assert(g_last_is_keyframe == 1);

    aethermesh_packet_free(frame);
    aethermesh_video_call_service_destroy(svc);
}

int main(void) {
    printf("Aether Video Call Service — Unit Tests\n");
    printf("======================================\n");

    RUN(send_offer_returns_call_id);
    RUN(send_offer_null_to_returns_error);
    RUN(accept_call_unknown_returns_error);
    RUN(accept_call_outgoing_returns_error);
    RUN(accept_call_incoming_returns_ok);
    RUN(hang_up_unknown_returns_error);
    RUN(hang_up_known_returns_ok);
    RUN(send_frame_unknown_returns_error);
    RUN(send_frame_not_connected_returns_error);
    RUN(send_frame_connected_returns_ok);
    RUN(request_keyframe_unknown_returns_error);
    RUN(request_keyframe_known_returns_ok);
    RUN(notify_quality_unknown_returns_error);
    RUN(notify_quality_known_returns_ok);
    RUN(handle_packet_inbound_offer_fires_incoming_cb);
    RUN(handle_packet_accept_fires_state_cb_connected);
    RUN(handle_packet_hangup_fires_state_cb_ended);
    RUN(handle_packet_keyframe_request_fires_kfr_cb);
    RUN(handle_packet_quality_change_fires_quality_cb);
    RUN(handle_packet_video_frame_fires_frame_cb);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
