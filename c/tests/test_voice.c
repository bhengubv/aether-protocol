// SPDX-License-Identifier: MIT
// Unit tests for voice.c (VoiceCallService — 1-to-1).

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aether/constants.h"
#include "aether/protocol.h"
#include "aether/routing.h"
#include "aether/transport.h"
#include "aether/voice.h"

// ── Fake transport ────────────────────────────────────────────

static bool ft_send(void *h, const char *peer, const uint8_t *d, size_t n) {
    (void)h; (void)peer; (void)d; (void)n; return true;
}
static bool ft_is_connected(void *h, const char *peer) {
    (void)h; (void)peer; return false;
}
static aether_transport_vtable_t g_vtable = {
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
static aether_transport_t g_transport = { .vtable = &g_vtable, .handle = NULL };

// ── Fake routing sender ───────────────────────────────────────

static bool rs_send(aether_mesh_sender_t *s, const aether_mesh_packet_t *p, const char *hop) {
    (void)s; (void)p; (void)hop; return true;
}
static int rs_broadcast(aether_mesh_sender_t *s, const aether_mesh_packet_t *p) {
    (void)s; (void)p; return 0;
}

// ── Helper: UUID bytes → string ───────────────────────────────

static void test_uuid_str(const uint8_t b[16], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        b[0],b[1],b[2],b[3], b[4],b[5], b[6],b[7],
        b[8],b[9], b[10],b[11],b[12],b[13],b[14],b[15]);
}

// ── Helper: create voice service with fresh routing ───────────

static aether_voice_service_t *make_voice_svc(const char *local_uhid) {
    static aether_mesh_sender_t rs;
    rs.local_uhid = local_uhid;
    rs.send       = rs_send;
    rs.broadcast  = rs_broadcast;
    rs.user_data  = NULL;
    aether_routing_service_t *routing = aether_routing_service_new(&rs);
    return aether_voice_service_create(&g_transport, routing, local_uhid);
}

// ── Helper: create VoiceSignaling packet from JSON string ─────

static aether_packet_t *make_signal_pkt(const char *from, const char *json) {
    aether_packet_t *p = aether_packet_new();
    if (!p) return NULL;
    p->type = AETHER_PACKET_TYPE_VOICE_SIGNALING;
    aether_packet_set_source_uhid(p, from);
    aether_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

// ── Helper: create VoiceCall binary frame packet ─────────────

static aether_packet_t *make_frame_pkt(const char *from, const uint8_t call_id[16]) {
    // Layout: [16 UUID BE][4 seq LE][8 ts LE][1 is_silence][4 audio]
    uint8_t buf[33];
    memcpy(buf, call_id, 16);
    memset(buf + 16, 0, 4);   // seq = 0
    memset(buf + 20, 0, 8);   // timestamp_ms = 0
    buf[28] = 0;               // is_silence = false
    buf[29] = 0xAA; buf[30] = 0xBB; buf[31] = 0xCC; buf[32] = 0xDD;

    aether_packet_t *p = aether_packet_new();
    if (!p) return NULL;
    p->type = AETHER_PACKET_TYPE_VOICE_CALL;
    aether_packet_set_source_uhid(p, from);
    aether_packet_set_payload(p, buf, sizeof(buf));
    return p;
}

// Fixed test UUID: aabbccdd-eeff-4001-8002-aabbccddeeff
static const uint8_t TEST_CALL_ID[16] = {
    0xaa,0xbb,0xcc,0xdd, 0xee,0xff, 0x40,0x01,
    0x80,0x02, 0xaa,0xbb,0xcc,0xdd,0xee,0xff
};
#define TEST_OFFER_JSON \
    "{\"call_id\":\"aabbccdd-eeff-4001-8002-aabbccddeeff\"," \
    "\"from_uhid\":\"bob\",\"codecs\":[\"opus\"],\"sample_rate_hz\":48000}"
#define TEST_ACCEPT_JSON \
    "{\"call_id\":\"aabbccdd-eeff-4001-8002-aabbccddeeff\"," \
    "\"from_uhid\":\"bob\",\"signal_type\":\"accept\"}"
#define TEST_HANGUP_JSON \
    "{\"call_id\":\"aabbccdd-eeff-4001-8002-aabbccddeeff\"," \
    "\"from_uhid\":\"bob\",\"signal_type\":\"hangup\"}"

// ── Callback capture ──────────────────────────────────────────

static int g_incoming_count = 0;
static char g_incoming_from[64];
static void on_incoming(const uint8_t cid[16], const char *from,
                         const char **codecs, int cc, int sr, void *ud) {
    (void)cid; (void)codecs; (void)cc; (void)sr; (void)ud;
    g_incoming_count++;
    strncpy(g_incoming_from, from ? from : "", 63);
}

static int g_state_count = 0;
static int g_last_state = -1;
static void on_state(const uint8_t cid[16], aether_voice_call_state_t s, void *ud) {
    (void)cid; (void)ud;
    g_state_count++;
    g_last_state = (int)s;
}

static int g_frame_count = 0;
static void on_frame(const uint8_t *cid, const uint8_t *audio, size_t alen,
                     int sil, int64_t ts, void *ud) {
    (void)cid; (void)audio; (void)alen; (void)sil; (void)ts; (void)ud;
    g_frame_count++;
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
    aether_voice_service_t *svc = make_voice_svc("alice");
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    int rc = aether_voice_send_offer(svc, "bob", codecs, 1, 48000, call_id);
    assert(rc == 0);
    // UUID v4 should have non-zero bytes
    int nonzero = 0;
    for (int i = 0; i < 16; i++) nonzero += (call_id[i] != 0 ? 1 : 0);
    assert(nonzero > 0);
    aether_voice_service_destroy(svc);
}

static void send_offer_null_uhid_returns_error(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    int rc = aether_voice_send_offer(svc, NULL, codecs, 1, 48000, call_id);
    assert(rc == -1);
    aether_voice_service_destroy(svc);
}

static void handle_offer_fires_incoming_cb(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    g_incoming_count = 0;
    aether_voice_set_incoming_cb(svc, on_incoming, NULL);

    aether_packet_t *pkt = make_signal_pkt("bob", TEST_OFFER_JSON);
    int rc = aether_voice_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_incoming_count == 1);
    assert(strcmp(g_incoming_from, "bob") == 0);

    aether_packet_free(pkt);
    aether_voice_service_destroy(svc);
}

static void handle_accept_fires_state_changed_connected(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aether_voice_send_offer(svc, "bob", codecs, 1, 48000, call_id);

    g_state_count = 0; g_last_state = -1;
    aether_voice_set_state_changed_cb(svc, on_state, NULL);

    // Build accept JSON with the generated call_id
    char id_str[37];
    test_uuid_str(call_id, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"accept\"}", id_str);

    aether_packet_t *pkt = make_signal_pkt("bob", json);
    int rc = aether_voice_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_state_count == 1);
    assert(g_last_state == (int)AETHER_VOICE_STATE_CONNECTED);

    aether_packet_free(pkt);
    aether_voice_service_destroy(svc);
}

static void handle_hangup_fires_state_changed_ended(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    // Set up inbound session via offer
    aether_packet_t *offer = make_signal_pkt("bob", TEST_OFFER_JSON);
    aether_voice_handle_packet(svc, offer);
    aether_packet_free(offer);

    g_state_count = 0; g_last_state = -1;
    aether_voice_set_state_changed_cb(svc, on_state, NULL);

    aether_packet_t *pkt = make_signal_pkt("bob", TEST_HANGUP_JSON);
    int rc = aether_voice_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_state_count == 1);
    assert(g_last_state == (int)AETHER_VOICE_STATE_ENDED);

    aether_packet_free(pkt);
    aether_voice_service_destroy(svc);
}

static void accept_call_transitions_to_connected(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    // Receive inbound offer
    aether_packet_t *offer = make_signal_pkt("bob", TEST_OFFER_JSON);
    aether_voice_handle_packet(svc, offer);
    aether_packet_free(offer);

    g_state_count = 0; g_last_state = -1;
    aether_voice_set_state_changed_cb(svc, on_state, NULL);

    int rc = aether_voice_accept_call(svc, TEST_CALL_ID);
    assert(rc == 0);
    assert(g_state_count == 1);
    assert(g_last_state == (int)AETHER_VOICE_STATE_CONNECTED);

    aether_voice_service_destroy(svc);
}

static void accept_call_unknown_returns_error(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aether_voice_accept_call(svc, unknown);
    assert(rc == -1);
    aether_voice_service_destroy(svc);
}

static void hang_up_fires_ended_callback(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aether_voice_send_offer(svc, "bob", codecs, 1, 48000, call_id);

    g_state_count = 0; g_last_state = -1;
    aether_voice_set_state_changed_cb(svc, on_state, NULL);

    int rc = aether_voice_hang_up(svc, call_id);
    assert(rc == 0);
    assert(g_state_count == 1);
    assert(g_last_state == (int)AETHER_VOICE_STATE_ENDED);

    aether_voice_service_destroy(svc);
}

static void hang_up_unknown_returns_error(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aether_voice_hang_up(svc, unknown);
    assert(rc == -1);
    aether_voice_service_destroy(svc);
}

static void send_frame_not_connected_returns_error(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aether_voice_send_offer(svc, "bob", codecs, 1, 48000, call_id);
    // Still OUTGOING — send_frame must fail
    const uint8_t audio[] = {1, 2, 3, 4};
    int rc = aether_voice_send_frame(svc, call_id, audio, sizeof(audio), 0);
    assert(rc == -1);
    aether_voice_service_destroy(svc);
}

static void send_frame_connected_returns_ok(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aether_voice_send_offer(svc, "bob", codecs, 1, 48000, call_id);

    // Transition to Connected by feeding an accept
    char id_str[37];
    test_uuid_str(call_id, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"accept\"}", id_str);
    aether_packet_t *pkt = make_signal_pkt("bob", json);
    aether_voice_handle_packet(svc, pkt);
    aether_packet_free(pkt);

    const uint8_t audio[] = {1, 2, 3, 4};
    int rc = aether_voice_send_frame(svc, call_id, audio, sizeof(audio), 0);
    assert(rc == 0);
    aether_voice_service_destroy(svc);
}

static void handle_frame_fires_frame_cb(void) {
    aether_voice_service_t *svc = make_voice_svc("alice");
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aether_voice_send_offer(svc, "bob", codecs, 1, 48000, call_id);

    // Transition to Connected
    char id_str[37];
    test_uuid_str(call_id, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"accept\"}", id_str);
    aether_packet_t *accept_pkt = make_signal_pkt("bob", json);
    aether_voice_handle_packet(svc, accept_pkt);
    aether_packet_free(accept_pkt);

    g_frame_count = 0;
    aether_voice_set_frame_cb(svc, on_frame, NULL);

    aether_packet_t *frame_pkt = make_frame_pkt("bob", call_id);
    int rc = aether_voice_handle_packet(svc, frame_pkt);
    assert(rc == 0);
    assert(g_frame_count == 1);

    aether_packet_free(frame_pkt);
    aether_voice_service_destroy(svc);
}

int main(void) {
    printf("Aether Voice Service — Unit Tests\n");
    printf("==================================\n");

    RUN(send_offer_returns_call_id);
    RUN(send_offer_null_uhid_returns_error);
    RUN(handle_offer_fires_incoming_cb);
    RUN(handle_accept_fires_state_changed_connected);
    RUN(handle_hangup_fires_state_changed_ended);
    RUN(accept_call_transitions_to_connected);
    RUN(accept_call_unknown_returns_error);
    RUN(hang_up_fires_ended_callback);
    RUN(hang_up_unknown_returns_error);
    RUN(send_frame_not_connected_returns_error);
    RUN(send_frame_connected_returns_ok);
    RUN(handle_frame_fires_frame_cb);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
