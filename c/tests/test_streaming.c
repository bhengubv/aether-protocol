// SPDX-License-Identifier: MIT
// Unit tests for streaming.c (StreamingService, VideoCallService, WatchTogetherService).

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
#include "aethermesh/voice.h"   // for aethermesh_voice_call_state_t values

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

// ── Helper: UUID bytes → string ───────────────────────────────

static void test_uuid_str(const uint8_t b[16], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        b[0],b[1],b[2],b[3], b[4],b[5], b[6],b[7],
        b[8],b[9], b[10],b[11],b[12],b[13],b[14],b[15]);
}

// ── Helper: create routing service with empty cache ───────────

static aethermesh_routing_service_t *make_routing(const char *local_uhid) {
    static aethermesh_mesh_sender_t rs;
    rs.local_uhid = local_uhid;
    rs.send       = rs_send;
    rs.broadcast  = rs_broadcast;
    rs.user_data  = NULL;
    return aethermesh_routing_service_new(&rs);
}

// ── Helper: make Streaming service ───────────────────────────

static aethermesh_streaming_service_t *make_stream_svc(const char *local_uhid) {
    return aethermesh_streaming_service_create(&g_transport, make_routing(local_uhid), local_uhid);
}

// ── Helper: make VideoCall service ───────────────────────────

static aethermesh_video_call_service_t *make_video_svc(const char *local_uhid) {
    return aethermesh_video_call_service_create(&g_transport, make_routing(local_uhid), local_uhid);
}

// ── Helper: make WatchTogether service ───────────────────────

static aethermesh_watch_together_service_t *make_watch_svc(const char *local_uhid) {
    return aethermesh_watch_together_service_create(&g_transport, make_routing(local_uhid), local_uhid);
}

// ── Helper: build STREAM_ANNOUNCE packet from JSON ───────────

static aethermesh_packet_t *make_announce_pkt(const char *from, const char *json) {
    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) return NULL;
    p->type = AETHERMESH_PACKET_TYPE_STREAM_ANNOUNCE;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

// ── Helper: build STREAM_SEGMENT binary packet ───────────────
// Layout: [16 stream_id BE][4 seq LE][8 ts LE][1 is_keyframe][N data]

static aethermesh_packet_t *make_segment_pkt(const char *from, const uint8_t stream_id[16]) {
    const uint8_t data[] = {0xDE, 0xAD, 0xBE, 0xEF};
    size_t total = 16 + 4 + 8 + 1 + sizeof(data);
    uint8_t *buf = (uint8_t *)calloc(1, total);
    if (!buf) return NULL;
    memcpy(buf, stream_id, 16);
    // seq = 0 (bytes 16-19 already zero)
    // ts  = 0 (bytes 20-27 already zero)
    buf[28] = 1; // is_keyframe = true
    memcpy(buf + 29, data, sizeof(data));

    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) { free(buf); return NULL; }
    p->type = AETHERMESH_PACKET_TYPE_STREAM_SEGMENT;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, buf, (uint32_t)total);
    free(buf);
    return p;
}

// ── Helper: build VIDEO_SIGNALING packet from JSON ───────────

static aethermesh_packet_t *make_video_signal_pkt(const char *from, const char *json) {
    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) return NULL;
    p->type = AETHERMESH_PACKET_TYPE_VIDEO_SIGNALING;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

// ── Helper: build VIDEO_FRAME binary packet ───────────────────
// Layout: [16 call_id BE][4 seq LE][8 ts LE][1 is_keyframe][N video]

static aethermesh_packet_t *make_video_frame_pkt(const char *from, const uint8_t call_id[16]) {
    const uint8_t video[] = {0x11, 0x22, 0x33, 0x44};
    size_t total = 16 + 4 + 8 + 1 + sizeof(video);
    uint8_t *buf = (uint8_t *)calloc(1, total);
    if (!buf) return NULL;
    memcpy(buf, call_id, 16);
    buf[28] = 1; // is_keyframe = true
    memcpy(buf + 29, video, sizeof(video));

    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) { free(buf); return NULL; }
    p->type = AETHERMESH_PACKET_TYPE_VIDEO_FRAME;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, buf, (uint32_t)total);
    free(buf);
    return p;
}

// ── Helper: build WATCH_SYNC JSON packet ──────────────────────

static aethermesh_packet_t *make_watch_sync_pkt(const char *from, const char *json) {
    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) return NULL;
    p->type = AETHERMESH_PACKET_TYPE_WATCH_SYNC;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

// ── Helper: build WATCH_REACTION JSON packet ──────────────────

static aethermesh_packet_t *make_watch_reaction_pkt(const char *from, const char *json) {
    aethermesh_packet_t *p = aethermesh_packet_new();
    if (!p) return NULL;
    p->type = AETHERMESH_PACKET_TYPE_WATCH_REACTION;
    aethermesh_packet_set_source_uhid(p, from);
    aethermesh_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

// Fixed test stream UUID: ccddaabb-eeff-4002-8003-112233445566
static const uint8_t TEST_STREAM_ID[16] = {
    0xcc,0xdd,0xaa,0xbb, 0xee,0xff, 0x40,0x02,
    0x80,0x03, 0x11,0x22,0x33,0x44,0x55,0x66
};
#define TEST_STREAM_ID_STR "ccddaabb-eeff-4002-8003-112233445566"

#define TEST_ANNOUNCE_JSON \
    "{\"stream_id\":\"" TEST_STREAM_ID_STR "\"," \
    "\"publisher_uhid\":\"bob\",\"title\":\"My Stream\"," \
    "\"mime_type\":\"audio/opus\",\"signal_type\":\"announce\"}"

#define TEST_END_JSON \
    "{\"stream_id\":\"" TEST_STREAM_ID_STR "\"," \
    "\"publisher_uhid\":\"bob\",\"signal_type\":\"end\"}"

// Fixed test call UUID: aabbccdd-eeff-4001-8002-aabbccddeeff
static const uint8_t TEST_CALL_ID[16] = {
    0xaa,0xbb,0xcc,0xdd, 0xee,0xff, 0x40,0x01,
    0x80,0x02, 0xaa,0xbb,0xcc,0xdd,0xee,0xff
};
#define TEST_CALL_ID_STR "aabbccdd-eeff-4001-8002-aabbccddeeff"

#define TEST_VIDEO_OFFER_JSON \
    "{\"call_id\":\"" TEST_CALL_ID_STR "\"," \
    "\"from_uhid\":\"bob\"," \
    "\"video_codecs\":[\"h264\"],\"audio_codecs\":[\"opus\"]}"

#define TEST_VIDEO_ACCEPT_JSON \
    "{\"call_id\":\"" TEST_CALL_ID_STR "\"," \
    "\"from_uhid\":\"bob\",\"signal_type\":\"video_accept\"}"

#define TEST_VIDEO_HANGUP_JSON \
    "{\"call_id\":\"" TEST_CALL_ID_STR "\"," \
    "\"from_uhid\":\"bob\",\"signal_type\":\"video_hangup\"}"

// Fixed test session UUID: bbccddee-ff00-4003-8004-223344556677
static const uint8_t TEST_SESSION_ID[16] = {
    0xbb,0xcc,0xdd,0xee, 0xff,0x00, 0x40,0x03,
    0x80,0x04, 0x22,0x33,0x44,0x55,0x66,0x77
};
#define TEST_SESSION_ID_STR "bbccddee-ff00-4003-8004-223344556677"

// ── Callback capture ──────────────────────────────────────────

// Streaming callbacks
static int g_announced_count = 0;
static char g_announced_title[128];
static void on_announced(const uint8_t *sid, const char *pub, const char *title, void *ud) {
    (void)sid; (void)pub; (void)ud;
    g_announced_count++;
    strncpy(g_announced_title, title ? title : "", 127);
}

static int g_ended_count = 0;
static void on_ended(const uint8_t *sid, void *ud) {
    (void)sid; (void)ud;
    g_ended_count++;
}

static int g_segment_count = 0;
static int g_segment_keyframe = 0;
static void on_segment(const uint8_t *sid, const uint8_t *data, size_t len,
                        int is_kf, int64_t ts, uint32_t seq, void *ud) {
    (void)sid; (void)data; (void)len; (void)ts; (void)seq; (void)ud;
    g_segment_count++;
    g_segment_keyframe = is_kf;
}

// VideoCall callbacks
static int g_video_incoming_count = 0;
static char g_video_incoming_from[64];
static void on_video_incoming(const uint8_t *cid, const char *from,
                               const char **vc, int vcc, const char **ac, int acc, void *ud) {
    (void)cid; (void)vc; (void)vcc; (void)ac; (void)acc; (void)ud;
    g_video_incoming_count++;
    strncpy(g_video_incoming_from, from ? from : "", 63);
}

static int g_video_state_count = 0;
static int g_video_last_state = -1;
static void on_video_state(const uint8_t *cid, int state, void *ud) {
    (void)cid; (void)ud;
    g_video_state_count++;
    g_video_last_state = state;
}

static int g_video_frame_count = 0;
static int g_video_frame_keyframe = 0;
static void on_video_frame(const uint8_t *cid, const uint8_t *video, size_t len,
                            int is_kf, int64_t ts, void *ud) {
    (void)cid; (void)video; (void)len; (void)ts; (void)ud;
    g_video_frame_count++;
    g_video_frame_keyframe = is_kf;
}

static int g_keyframe_req_count = 0;
static void on_keyframe_req(const uint8_t *cid, void *ud) {
    (void)cid; (void)ud;
    g_keyframe_req_count++;
}

static int g_quality_count = 0;
static char g_quality_last[64];
static void on_quality_changed(const uint8_t *cid, const char *quality, void *ud) {
    (void)cid; (void)ud;
    g_quality_count++;
    strncpy(g_quality_last, quality ? quality : "", 63);
}

// WatchTogether callbacks
static int g_watch_invite_count = 0;
static char g_watch_invite_url[256];
static void on_watch_invite(const uint8_t *sid, const char *host, const char *url, void *ud) {
    (void)sid; (void)host; (void)ud;
    g_watch_invite_count++;
    strncpy(g_watch_invite_url, url ? url : "", 255);
}

static int g_watch_playback_count = 0;
static int g_watch_playback_playing = -1;
static void on_watch_playback(const uint8_t *sid, int is_playing, int64_t pos_ms, void *ud) {
    (void)sid; (void)pos_ms; (void)ud;
    g_watch_playback_count++;
    g_watch_playback_playing = is_playing;
}

static int g_watch_reaction_count = 0;
static char g_watch_reaction_emoji[64];
static void on_watch_reaction(const uint8_t *sid, const char *from, const char *emoji, void *ud) {
    (void)sid; (void)from; (void)ud;
    g_watch_reaction_count++;
    strncpy(g_watch_reaction_emoji, emoji ? emoji : "", 63);
}

// ── Test runner ───────────────────────────────────────────────

#define RUN(name) do { \
    printf("TEST: " #name "..."); \
    name(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)
static int tests_run = 0;

// ════════════════════════════════════════════════════════════
// StreamingService tests
// ════════════════════════════════════════════════════════════

static void stream_start_returns_stream_id(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    uint8_t sid[16] = {0};
    int rc = aethermesh_streaming_start(svc, "My Stream", "audio/opus", sid);
    assert(rc == 0);
    int nonzero = 0;
    for (int i = 0; i < 16; i++) nonzero += (sid[i] != 0 ? 1 : 0);
    assert(nonzero > 0);
    aethermesh_streaming_service_destroy(svc);
}

static void stream_start_null_title_returns_error(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    uint8_t sid[16] = {0};
    int rc = aethermesh_streaming_start(svc, NULL, "audio/opus", sid);
    assert(rc == -1);
    aethermesh_streaming_service_destroy(svc);
}

static void stream_end_unknown_returns_error(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    uint8_t unknown[16] = {0x01,0x02,0x03};
    int rc = aethermesh_streaming_end(svc, unknown);
    assert(rc == -1);
    aethermesh_streaming_service_destroy(svc);
}

static void stream_start_then_end_returns_ok(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    uint8_t sid[16] = {0};
    assert(aethermesh_streaming_start(svc, "Live", "video/h264", sid) == 0);
    int rc = aethermesh_streaming_end(svc, sid);
    assert(rc == 0);
    aethermesh_streaming_service_destroy(svc);
}

static void handle_announce_fires_announced_cb(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    g_announced_count = 0;
    aethermesh_streaming_set_announced_cb(svc, on_announced, NULL);

    aethermesh_packet_t *pkt = make_announce_pkt("bob", TEST_ANNOUNCE_JSON);
    int rc = aethermesh_streaming_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_announced_count == 1);
    assert(strcmp(g_announced_title, "My Stream") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_streaming_service_destroy(svc);
}

static void handle_announce_then_end_fires_ended_cb(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    g_ended_count = 0;
    aethermesh_streaming_set_ended_cb(svc, on_ended, NULL);

    aethermesh_packet_t *ann = make_announce_pkt("bob", TEST_ANNOUNCE_JSON);
    aethermesh_streaming_handle_packet(svc, ann);
    aethermesh_packet_free(ann);

    aethermesh_packet_t *end_pkt = make_announce_pkt("bob", TEST_END_JSON);
    int rc = aethermesh_streaming_handle_packet(svc, end_pkt);
    assert(rc == 0);
    assert(g_ended_count == 1);

    aethermesh_packet_free(end_pkt);
    aethermesh_streaming_service_destroy(svc);
}

static void stream_subscribe_returns_ok(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    int rc = aethermesh_streaming_subscribe(svc, TEST_STREAM_ID, "bob");
    assert(rc == 0);
    aethermesh_streaming_service_destroy(svc);
}

static void stream_subscribe_then_segment_fires_cb(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    g_segment_count = 0;
    aethermesh_streaming_set_segment_cb(svc, on_segment, NULL);

    // Subscribe to make rec->subscribed = true
    int rc = aethermesh_streaming_subscribe(svc, TEST_STREAM_ID, "bob");
    assert(rc == 0);

    // Feed a STREAM_SEGMENT packet with same stream_id
    aethermesh_packet_t *seg = make_segment_pkt("bob", TEST_STREAM_ID);
    rc = aethermesh_streaming_handle_packet(svc, seg);
    assert(rc == 0);
    assert(g_segment_count == 1);
    assert(g_segment_keyframe == 1);

    aethermesh_packet_free(seg);
    aethermesh_streaming_service_destroy(svc);
}

static void stream_segment_without_subscribe_no_callback(void) {
    aethermesh_streaming_service_t *svc = make_stream_svc("alice");
    g_segment_count = 0;
    aethermesh_streaming_set_segment_cb(svc, on_segment, NULL);

    // Feed segment without subscribing first — rec not found, callback must NOT fire
    aethermesh_packet_t *seg = make_segment_pkt("bob", TEST_STREAM_ID);
    aethermesh_streaming_handle_packet(svc, seg);
    assert(g_segment_count == 0);

    aethermesh_packet_free(seg);
    aethermesh_streaming_service_destroy(svc);
}

// ════════════════════════════════════════════════════════════
// VideoCallService tests
// ════════════════════════════════════════════════════════════

static void video_send_offer_returns_call_id(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vc[] = {"h264"};
    const char *ac[] = {"opus"};
    uint8_t cid[16] = {0};
    int rc = aethermesh_video_send_offer(svc, "bob", vc, 1, ac, 1, cid);
    assert(rc == 0);
    int nonzero = 0;
    for (int i = 0; i < 16; i++) nonzero += (cid[i] != 0 ? 1 : 0);
    assert(nonzero > 0);
    aethermesh_video_call_service_destroy(svc);
}

static void video_send_offer_null_uhid_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vc[] = {"h264"};
    const char *ac[] = {"opus"};
    uint8_t cid[16] = {0};
    int rc = aethermesh_video_send_offer(svc, NULL, vc, 1, ac, 1, cid);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void video_handle_offer_fires_incoming_cb(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    g_video_incoming_count = 0;
    aethermesh_video_set_incoming_cb(svc, on_video_incoming, NULL);

    aethermesh_packet_t *pkt = make_video_signal_pkt("bob", TEST_VIDEO_OFFER_JSON);
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_video_incoming_count == 1);
    assert(strcmp(g_video_incoming_from, "bob") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void video_handle_accept_fires_state_connected(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vc[] = {"h264"};
    const char *ac[] = {"opus"};
    uint8_t cid[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vc, 1, ac, 1, cid);

    g_video_state_count = 0; g_video_last_state = -1;
    aethermesh_video_set_state_changed_cb(svc, on_video_state, NULL);

    char id_str[37];
    test_uuid_str(cid, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"video_accept\"}", id_str);

    aethermesh_packet_t *pkt = make_video_signal_pkt("bob", json);
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_video_state_count == 1);
    assert(g_video_last_state == (int)AETHERMESH_VOICE_STATE_CONNECTED);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void video_handle_hangup_fires_state_ended(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    // Get inbound offer to create session
    aethermesh_packet_t *offer = make_video_signal_pkt("bob", TEST_VIDEO_OFFER_JSON);
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);

    g_video_state_count = 0; g_video_last_state = -1;
    aethermesh_video_set_state_changed_cb(svc, on_video_state, NULL);

    aethermesh_packet_t *pkt = make_video_signal_pkt("bob", TEST_VIDEO_HANGUP_JSON);
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_video_state_count == 1);
    assert(g_video_last_state == (int)AETHERMESH_VOICE_STATE_ENDED);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void video_accept_call_transitions_to_connected(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    aethermesh_packet_t *offer = make_video_signal_pkt("bob", TEST_VIDEO_OFFER_JSON);
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);

    g_video_state_count = 0; g_video_last_state = -1;
    aethermesh_video_set_state_changed_cb(svc, on_video_state, NULL);

    int rc = aethermesh_video_accept_call(svc, TEST_CALL_ID);
    assert(rc == 0);
    assert(g_video_state_count == 1);
    assert(g_video_last_state == (int)AETHERMESH_VOICE_STATE_CONNECTED);

    aethermesh_video_call_service_destroy(svc);
}

static void video_accept_call_unknown_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    uint8_t unknown[16] = {0x05, 0x06, 0x07};
    int rc = aethermesh_video_accept_call(svc, unknown);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void video_hang_up_fires_ended_callback(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vc[] = {"h264"};
    const char *ac[] = {"opus"};
    uint8_t cid[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vc, 1, ac, 1, cid);

    g_video_state_count = 0; g_video_last_state = -1;
    aethermesh_video_set_state_changed_cb(svc, on_video_state, NULL);

    int rc = aethermesh_video_hang_up(svc, cid);
    assert(rc == 0);
    assert(g_video_state_count == 1);
    assert(g_video_last_state == (int)AETHERMESH_VOICE_STATE_ENDED);

    aethermesh_video_call_service_destroy(svc);
}

static void video_send_frame_not_connected_returns_error(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vc[] = {"h264"};
    const char *ac[] = {"opus"};
    uint8_t cid[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vc, 1, ac, 1, cid);
    // Still OUTGOING
    const uint8_t frame[] = {0x01, 0x02, 0x03};
    int rc = aethermesh_video_send_frame(svc, cid, frame, sizeof(frame), 1);
    assert(rc == -1);
    aethermesh_video_call_service_destroy(svc);
}

static void video_send_frame_connected_returns_ok(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vc[] = {"h264"};
    const char *ac[] = {"opus"};
    uint8_t cid[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vc, 1, ac, 1, cid);

    // Transition to Connected
    char id_str[37];
    test_uuid_str(cid, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"video_accept\"}", id_str);
    aethermesh_packet_t *acc = make_video_signal_pkt("bob", json);
    aethermesh_video_handle_packet(svc, acc);
    aethermesh_packet_free(acc);

    const uint8_t frame[] = {0x01, 0x02, 0x03, 0x04};
    int rc = aethermesh_video_send_frame(svc, cid, frame, sizeof(frame), 1);
    assert(rc == 0);
    aethermesh_video_call_service_destroy(svc);
}

static void video_handle_frame_fires_frame_cb(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vc[] = {"h264"};
    const char *ac[] = {"opus"};
    uint8_t cid[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vc, 1, ac, 1, cid);

    // Transition to Connected
    char id_str[37];
    test_uuid_str(cid, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"video_accept\"}", id_str);
    aethermesh_packet_t *acc = make_video_signal_pkt("bob", json);
    aethermesh_video_handle_packet(svc, acc);
    aethermesh_packet_free(acc);

    g_video_frame_count = 0;
    aethermesh_video_set_frame_cb(svc, on_video_frame, NULL);

    aethermesh_packet_t *frm = make_video_frame_pkt("bob", cid);
    int rc = aethermesh_video_handle_packet(svc, frm);
    assert(rc == 0);
    assert(g_video_frame_count == 1);
    assert(g_video_frame_keyframe == 1);

    aethermesh_packet_free(frm);
    aethermesh_video_call_service_destroy(svc);
}

static void video_request_keyframe_returns_ok(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    const char *vc[] = {"h264"};
    const char *ac[] = {"opus"};
    uint8_t cid[16] = {0};
    aethermesh_video_send_offer(svc, "bob", vc, 1, ac, 1, cid);

    // Transition to Connected
    char id_str[37];
    test_uuid_str(cid, id_str);
    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"%s\",\"from_uhid\":\"bob\",\"signal_type\":\"video_accept\"}", id_str);
    aethermesh_packet_t *acc = make_video_signal_pkt("bob", json);
    aethermesh_video_handle_packet(svc, acc);
    aethermesh_packet_free(acc);

    int rc = aethermesh_video_request_keyframe(svc, cid);
    assert(rc == 0);
    aethermesh_video_call_service_destroy(svc);
}

static void video_handle_keyframe_request_fires_cb(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    aethermesh_packet_t *offer = make_video_signal_pkt("bob", TEST_VIDEO_OFFER_JSON);
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);

    // Accept so state = Connected
    int rc = aethermesh_video_accept_call(svc, TEST_CALL_ID);
    assert(rc == 0);

    g_keyframe_req_count = 0;
    aethermesh_video_set_keyframe_request_cb(svc, on_keyframe_req, NULL);

    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"" TEST_CALL_ID_STR "\",\"from_uhid\":\"bob\",\"signal_type\":\"keyframe_request\"}");
    aethermesh_packet_t *pkt = make_video_signal_pkt("bob", json);
    rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_keyframe_req_count == 1);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

static void video_notify_quality_change_returns_ok(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    aethermesh_packet_t *offer = make_video_signal_pkt("bob", TEST_VIDEO_OFFER_JSON);
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);

    int rc = aethermesh_video_accept_call(svc, TEST_CALL_ID);
    assert(rc == 0);

    rc = aethermesh_video_notify_quality_change(svc, TEST_CALL_ID, "720p");
    assert(rc == 0);
    aethermesh_video_call_service_destroy(svc);
}

static void video_handle_quality_change_fires_cb(void) {
    aethermesh_video_call_service_t *svc = make_video_svc("alice");
    aethermesh_packet_t *offer = make_video_signal_pkt("bob", TEST_VIDEO_OFFER_JSON);
    aethermesh_video_handle_packet(svc, offer);
    aethermesh_packet_free(offer);
    aethermesh_video_accept_call(svc, TEST_CALL_ID);

    g_quality_count = 0;
    aethermesh_video_set_quality_changed_cb(svc, on_quality_changed, NULL);

    char json[256];
    snprintf(json, sizeof(json),
        "{\"call_id\":\"" TEST_CALL_ID_STR "\",\"from_uhid\":\"bob\","
        "\"signal_type\":\"quality_change\",\"quality\":\"480p\"}");
    aethermesh_packet_t *pkt = make_video_signal_pkt("bob", json);
    int rc = aethermesh_video_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_quality_count == 1);
    assert(strcmp(g_quality_last, "480p") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_video_call_service_destroy(svc);
}

// ════════════════════════════════════════════════════════════
// WatchTogetherService tests
// ════════════════════════════════════════════════════════════

static void watch_invite_to_session_returns_session_id(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = {"bob", "carol"};
    uint8_t sid[16] = {0};
    int rc = aethermesh_watch_invite_to_session(svc, to, 2, "https://example.com/movie", sid);
    assert(rc == 0);
    int nonzero = 0;
    for (int i = 0; i < 16; i++) nonzero += (sid[i] != 0 ? 1 : 0);
    assert(nonzero > 0);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_invite_null_media_url_returns_error(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = {"bob"};
    uint8_t sid[16] = {0};
    int rc = aethermesh_watch_invite_to_session(svc, to, 1, NULL, sid);
    assert(rc == -1);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_handle_invite_fires_invite_cb(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    g_watch_invite_count = 0;
    aethermesh_watch_set_invite_cb(svc, on_watch_invite, NULL);

    char json[512];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"" TEST_SESSION_ID_STR "\",\"host_uhid\":\"bob\","
        "\"media_url\":\"https://example.com/video\",\"signal_type\":\"watch_invite\","
        "\"members\":[\"bob\",\"alice\"]}");
    aethermesh_packet_t *pkt = make_watch_sync_pkt("bob", json);
    int rc = aethermesh_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_watch_invite_count == 1);
    assert(strcmp(g_watch_invite_url, "https://example.com/video") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_play_on_known_session_returns_ok(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = {"bob"};
    uint8_t sid[16] = {0};
    aethermesh_watch_invite_to_session(svc, to, 1, "https://example.com/m", sid);

    int rc = aethermesh_watch_play(svc, sid, 5000);
    assert(rc == 0);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_pause_on_known_session_returns_ok(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = {"bob"};
    uint8_t sid[16] = {0};
    aethermesh_watch_invite_to_session(svc, to, 1, "https://example.com/m", sid);

    int rc = aethermesh_watch_pause(svc, sid, 12000);
    assert(rc == 0);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_seek_on_known_session_returns_ok(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = {"bob"};
    uint8_t sid[16] = {0};
    aethermesh_watch_invite_to_session(svc, to, 1, "https://example.com/m", sid);

    int rc = aethermesh_watch_seek(svc, sid, 60000);
    assert(rc == 0);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_set_speed_on_known_session_returns_ok(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = {"bob"};
    uint8_t sid[16] = {0};
    aethermesh_watch_invite_to_session(svc, to, 1, "https://example.com/m", sid);

    int rc = aethermesh_watch_set_speed(svc, sid, 1.5);
    assert(rc == 0);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_handle_play_fires_playback_cb(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    g_watch_playback_count = 0;
    aethermesh_watch_set_playback_cb(svc, on_watch_playback, NULL);

    char json[256];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"" TEST_SESSION_ID_STR "\",\"signal_type\":\"watch_play\","
        "\"position_ms\":5000,\"sent_at_ms\":0}");
    aethermesh_packet_t *pkt = make_watch_sync_pkt("bob", json);
    int rc = aethermesh_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_watch_playback_count == 1);
    assert(g_watch_playback_playing == 1);

    aethermesh_packet_free(pkt);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_handle_pause_fires_playback_cb(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    g_watch_playback_count = 0;
    aethermesh_watch_set_playback_cb(svc, on_watch_playback, NULL);

    char json[256];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"" TEST_SESSION_ID_STR "\",\"signal_type\":\"watch_pause\","
        "\"position_ms\":12000,\"sent_at_ms\":0}");
    aethermesh_packet_t *pkt = make_watch_sync_pkt("bob", json);
    int rc = aethermesh_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_watch_playback_count == 1);
    assert(g_watch_playback_playing == 0);

    aethermesh_packet_free(pkt);
    aethermesh_watch_together_service_destroy(svc);
}

static void watch_handle_reaction_fires_reaction_cb(void) {
    aethermesh_watch_together_service_t *svc = make_watch_svc("alice");
    g_watch_reaction_count = 0;
    aethermesh_watch_set_reaction_cb(svc, on_watch_reaction, NULL);

    char json[256];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"" TEST_SESSION_ID_STR "\",\"from_uhid\":\"bob\","
        "\"emoji\":\"thumbsup\",\"sent_at_ms\":0}");
    aethermesh_packet_t *pkt = make_watch_reaction_pkt("bob", json);
    int rc = aethermesh_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_watch_reaction_count == 1);
    assert(strcmp(g_watch_reaction_emoji, "thumbsup") == 0);

    aethermesh_packet_free(pkt);
    aethermesh_watch_together_service_destroy(svc);
}

// ── main ──────────────────────────────────────────────────────

int main(void) {
    printf("Aether Streaming Services — Unit Tests\n");
    printf("=======================================\n");

    printf("\n--- StreamingService ---\n");
    RUN(stream_start_returns_stream_id);
    RUN(stream_start_null_title_returns_error);
    RUN(stream_end_unknown_returns_error);
    RUN(stream_start_then_end_returns_ok);
    RUN(handle_announce_fires_announced_cb);
    RUN(handle_announce_then_end_fires_ended_cb);
    RUN(stream_subscribe_returns_ok);
    RUN(stream_subscribe_then_segment_fires_cb);
    RUN(stream_segment_without_subscribe_no_callback);

    printf("\n--- VideoCallService ---\n");
    RUN(video_send_offer_returns_call_id);
    RUN(video_send_offer_null_uhid_returns_error);
    RUN(video_handle_offer_fires_incoming_cb);
    RUN(video_handle_accept_fires_state_connected);
    RUN(video_handle_hangup_fires_state_ended);
    RUN(video_accept_call_transitions_to_connected);
    RUN(video_accept_call_unknown_returns_error);
    RUN(video_hang_up_fires_ended_callback);
    RUN(video_send_frame_not_connected_returns_error);
    RUN(video_send_frame_connected_returns_ok);
    RUN(video_handle_frame_fires_frame_cb);
    RUN(video_request_keyframe_returns_ok);
    RUN(video_handle_keyframe_request_fires_cb);
    RUN(video_notify_quality_change_returns_ok);
    RUN(video_handle_quality_change_fires_cb);

    printf("\n--- WatchTogetherService ---\n");
    RUN(watch_invite_to_session_returns_session_id);
    RUN(watch_invite_null_media_url_returns_error);
    RUN(watch_handle_invite_fires_invite_cb);
    RUN(watch_play_on_known_session_returns_ok);
    RUN(watch_pause_on_known_session_returns_ok);
    RUN(watch_seek_on_known_session_returns_ok);
    RUN(watch_set_speed_on_known_session_returns_ok);
    RUN(watch_handle_play_fires_playback_cb);
    RUN(watch_handle_pause_fires_playback_cb);
    RUN(watch_handle_reaction_fires_reaction_cb);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
