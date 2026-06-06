// SPDX-License-Identifier: MIT
// Unit tests for group_voice.c (GroupVoiceCallService).

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
#include "aethermesh/voice.h"

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

static aethermesh_group_voice_service_t *make_group_svc(const char *local_uhid) {
    static aethermesh_mesh_sender_t rs;
    rs.local_uhid = local_uhid;
    rs.send       = rs_send;
    rs.broadcast  = rs_broadcast;
    rs.user_data  = NULL;
    aethermesh_routing_service_t *routing = aethermesh_routing_service_new(&rs);
    return aethermesh_group_voice_service_create(&g_transport, routing, local_uhid);
}

// ── Packet builder ────────────────────────────────────────────

static aethermesh_packet_t *make_group_sig_pkt(const char *from, const char *json) {
    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) return NULL;
    p->type = AETHERMESH_PACKET_TYPE_VOICE_SIGNALING;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

// Binary group voice frame: [16 callId][4 seq LE][8 ts LE][1 isSilence][4 keyGen LE][N audio]
static aethermesh_packet_t *make_group_voice_frame(const uint8_t call_id[16]) {
    uint8_t buf[33 + 4];
    memcpy(buf, call_id, 16);
    memset(buf + 16, 0, 4);    // seq = 0
    memset(buf + 20, 0, 8);    // ts = 0
    buf[28] = 0;                // is_silence = false
    buf[29] = 0; buf[30] = 0; buf[31] = 0; buf[32] = 0; // key_generation = 0
    buf[33] = 0xDE; buf[34] = 0xAD; buf[35] = 0xBE; buf[36] = 0xEF;

    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) return NULL;
    p->type = AETHERMESH_PACKET_TYPE_VOICE_CALL;
    aethermesh_packet_set_source_uhid(p, "bob");
    aethermesh_packet_set_payload(p, buf, sizeof(buf));
    return p;
}

// ── Callback capture ──────────────────────────────────────────

static int g_invite_count = 0;
static char g_invite_from[64];
static void on_invite(const uint8_t *cid, const char *from, const char **codecs, int cc, void *ud) {
    (void)cid; (void)codecs; (void)cc; (void)ud;
    g_invite_count++;
    strncpy(g_invite_from, from ? from : "", 63);
}

static int g_member_joined_count = 0;
static char g_member_joined_uhid[64];
static void on_member_joined(const uint8_t *cid, const char *uhid, void *ud) {
    (void)cid; (void)ud;
    g_member_joined_count++;
    strncpy(g_member_joined_uhid, uhid ? uhid : "", 63);
}

static int g_member_left_count = 0;
static char g_member_left_uhid[64];
static void on_member_left(const uint8_t *cid, const char *uhid, void *ud) {
    (void)cid; (void)ud;
    g_member_left_count++;
    strncpy(g_member_left_uhid, uhid ? uhid : "", 63);
}

static int g_frame_count = 0;
static void on_frame(const uint8_t *cid, const char *from, const uint8_t *audio, size_t alen,
                     int sil, uint32_t key_gen, int64_t ts, void *ud) {
    (void)cid; (void)from; (void)audio; (void)alen; (void)sil; (void)key_gen; (void)ts; (void)ud;
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

static void invite_returns_call_id(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    const char *to[] = { "bob", "carol" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    int rc = aethermesh_group_voice_invite(svc, to, 2, codecs, 1, call_id);
    assert(rc == 0);
    // UUID should have non-zero bytes
    int nonzero = 0;
    for (int i = 0; i < 16; i++) nonzero += (call_id[i] != 0 ? 1 : 0);
    assert(nonzero > 0);
    aethermesh_group_voice_service_destroy(svc);
}

static void invite_null_service_returns_error(void) {
    uint8_t call_id[16] = {0};
    int rc = aethermesh_group_voice_invite(NULL, NULL, 0, NULL, 0, call_id);
    assert(rc == -1);
}

static void join_unknown_call_returns_error(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aethermesh_group_voice_join(svc, unknown);
    assert(rc == -1);
    aethermesh_group_voice_service_destroy(svc);
}

static void join_known_call_returns_ok(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    const char *to[] = { "bob" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aethermesh_group_voice_invite(svc, to, 1, codecs, 1, call_id);
    int rc = aethermesh_group_voice_join(svc, call_id);
    assert(rc == 0);
    aethermesh_group_voice_service_destroy(svc);
}

static void leave_unknown_call_returns_error(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aethermesh_group_voice_leave(svc, unknown);
    assert(rc == -1);
    aethermesh_group_voice_service_destroy(svc);
}

static void leave_known_call_returns_ok(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    const char *to[] = { "bob" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aethermesh_group_voice_invite(svc, to, 1, codecs, 1, call_id);
    int rc = aethermesh_group_voice_leave(svc, call_id);
    assert(rc == 0);
    aethermesh_group_voice_service_destroy(svc);
}

static void kick_unknown_call_returns_error(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aethermesh_group_voice_kick(svc, unknown, "bob");
    assert(rc == -1);
    aethermesh_group_voice_service_destroy(svc);
}

static void kick_known_call_returns_ok(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    const char *to[] = { "bob", "carol" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aethermesh_group_voice_invite(svc, to, 2, codecs, 1, call_id);
    int rc = aethermesh_group_voice_kick(svc, call_id, "carol");
    assert(rc == 0);
    aethermesh_group_voice_service_destroy(svc);
}

static void send_frame_unknown_call_returns_error(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    const uint8_t audio[] = { 0xAA, 0xBB };
    int rc = aethermesh_group_voice_send_frame(svc, unknown, audio, sizeof(audio), 0, 0);
    assert(rc == -1);
    aethermesh_group_voice_service_destroy(svc);
}

static void send_frame_known_call_returns_ok(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    const char *to[] = { "bob" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aethermesh_group_voice_invite(svc, to, 1, codecs, 1, call_id);
    const uint8_t audio[] = { 0xDE, 0xAD, 0xBE, 0xEF };
    int rc = aethermesh_group_voice_send_frame(svc, call_id, audio, sizeof(audio), 0, 0);
    assert(rc == 0);
    aethermesh_group_voice_service_destroy(svc);
}

static void handle_packet_invite_fires_invite_cb(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");
    g_invite_count = 0;
    aethermesh_group_voice_set_invite_cb(svc, on_invite, NULL);

    // Fixed call UUID for deterministic JSON
    const char *id_str = "aabbccdd-eeff-4001-8002-aabbccddeeff";
    char json[512];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\","
        "\"signal_type\":\"group_invite\","
        "\"codecs\":[\"opus\"],"
        "\"members\":[\"alice\",\"bob\"]}",
        id_str);

    aethermesh_packet_t *pkt = make_group_sig_pkt("bob", json);
    int rc = aethermesh_group_voice_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_invite_count == 1);
    assert(strcmp(g_invite_from, "bob") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_group_voice_service_destroy(svc);
}

static void handle_packet_invite_then_join_succeeds(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");

    const char *id_str = "aabbccdd-eeff-4001-8002-aabbccddeeff";
    uint8_t call_id[16] = {
        0xaa,0xbb,0xcc,0xdd, 0xee,0xff, 0x40,0x01,
        0x80,0x02, 0xaa,0xbb,0xcc,0xdd,0xee,0xff
    };
    char json[512];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\","
        "\"signal_type\":\"group_invite\","
        "\"codecs\":[\"opus\"],"
        "\"members\":[\"alice\",\"bob\"]}",
        id_str);

    aethermesh_packet_t *pkt = make_group_sig_pkt("bob", json);
    aethermesh_group_voice_handle_packet(svc, pkt);
    aethermesh_packet_free(pkt);

    // join must succeed now the call was registered
    int rc = aethermesh_group_voice_join(svc, call_id);
    assert(rc == 0);
    aethermesh_group_voice_service_destroy(svc);
}

static void handle_packet_join_fires_member_joined_cb(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");

    // Create a call first
    const char *to[] = { "bob" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aethermesh_group_voice_invite(svc, to, 1, codecs, 1, call_id);

    g_member_joined_count = 0;
    aethermesh_group_voice_set_member_joined_cb(svc, on_member_joined, NULL);

    char id_str[37];
    test_uuid_str(call_id, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"uhid\":\"carol\",\"signal_type\":\"group_join\"}",
        id_str);

    aethermesh_packet_t *pkt = make_group_sig_pkt("carol", json);
    int rc = aethermesh_group_voice_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_member_joined_count == 1);
    assert(strcmp(g_member_joined_uhid, "carol") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_group_voice_service_destroy(svc);
}

static void handle_packet_leave_fires_member_left_cb(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");

    const char *to[] = { "bob" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aethermesh_group_voice_invite(svc, to, 1, codecs, 1, call_id);

    g_member_left_count = 0;
    aethermesh_group_voice_set_member_left_cb(svc, on_member_left, NULL);

    char id_str[37];
    test_uuid_str(call_id, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"uhid\":\"bob\",\"signal_type\":\"group_leave\"}",
        id_str);

    aethermesh_packet_t *pkt = make_group_sig_pkt("bob", json);
    int rc = aethermesh_group_voice_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_member_left_count == 1);
    assert(strcmp(g_member_left_uhid, "bob") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_group_voice_service_destroy(svc);
}

static void handle_packet_kick_fires_member_left_cb(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");

    const char *to[] = { "bob", "carol" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aethermesh_group_voice_invite(svc, to, 2, codecs, 1, call_id);

    g_member_left_count = 0;
    aethermesh_group_voice_set_member_left_cb(svc, on_member_left, NULL);

    char id_str[37];
    test_uuid_str(call_id, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"kicked_uhid\":\"carol\",\"by_uhid\":\"alice\","
        "\"signal_type\":\"group_kick\"}",
        id_str);

    aethermesh_packet_t *pkt = make_group_sig_pkt("alice", json);
    int rc = aethermesh_group_voice_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_member_left_count == 1);
    assert(strcmp(g_member_left_uhid, "carol") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_group_voice_service_destroy(svc);
}

static void handle_packet_kick_self_fires_member_left_cb(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");

    // Receive invite from bob
    const char *id_str = "aabbccdd-eeff-4001-8002-aabbccddeeff";
    char json[512];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\","
        "\"signal_type\":\"group_invite\","
        "\"codecs\":[\"opus\"],"
        "\"members\":[\"alice\",\"bob\"]}",
        id_str);
    aethermesh_packet_t *invite_pkt = make_group_sig_pkt("bob", json);
    aethermesh_group_voice_handle_packet(svc, invite_pkt);
    aethermesh_packet_free(invite_pkt);

    g_member_left_count = 0;
    aethermesh_group_voice_set_member_left_cb(svc, on_member_left, NULL);

    // Bob kicks alice (local)
    char kick_json[512];
    snprintf(kick_json, sizeof(kick_json),
        "{\"call_id\":\"%s\",\"kicked_uhid\":\"alice\",\"by_uhid\":\"bob\","
        "\"signal_type\":\"group_kick\"}",
        id_str);
    aethermesh_packet_t *kick_pkt = make_group_sig_pkt("bob", kick_json);
    aethermesh_group_voice_handle_packet(svc, kick_pkt);
    aethermesh_packet_free(kick_pkt);

    assert(g_member_left_count == 1);
    assert(strcmp(g_member_left_uhid, "alice") == 0);

    aethermesh_group_voice_service_destroy(svc);
}

static void handle_packet_voice_frame_fires_frame_cb(void) {
    aethermesh_group_voice_service_t *svc = make_group_svc("alice");

    const char *to[] = { "bob" };
    const char *codecs[] = { "opus" };
    uint8_t call_id[16] = {0};
    aethermesh_group_voice_invite(svc, to, 1, codecs, 1, call_id);

    g_frame_count = 0;
    aethermesh_group_voice_set_frame_cb(svc, on_frame, NULL);

    aethermesh_packet_t *frame_pkt = make_group_voice_frame(call_id);
    int rc = aethermesh_group_voice_handle_packet(svc, frame_pkt);
    assert(rc == 0);
    assert(g_frame_count == 1);

    aethermesh_packet_free(frame_pkt);
    aethermesh_group_voice_service_destroy(svc);
}

int main(void) {
    printf("Aether Group Voice Service — Unit Tests\n");
    printf("========================================\n");

    RUN(invite_returns_call_id);
    RUN(invite_null_service_returns_error);
    RUN(join_unknown_call_returns_error);
    RUN(join_known_call_returns_ok);
    RUN(leave_unknown_call_returns_error);
    RUN(leave_known_call_returns_ok);
    RUN(kick_unknown_call_returns_error);
    RUN(kick_known_call_returns_ok);
    RUN(send_frame_unknown_call_returns_error);
    RUN(send_frame_known_call_returns_ok);
    RUN(handle_packet_invite_fires_invite_cb);
    RUN(handle_packet_invite_then_join_succeeds);
    RUN(handle_packet_join_fires_member_joined_cb);
    RUN(handle_packet_leave_fires_member_left_cb);
    RUN(handle_packet_kick_fires_member_left_cb);
    RUN(handle_packet_kick_self_fires_member_left_cb);
    RUN(handle_packet_voice_frame_fires_frame_cb);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
