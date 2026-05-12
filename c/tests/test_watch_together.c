// SPDX-License-Identifier: MIT
// Unit tests for WatchTogetherService (streaming.c).

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
#include "aether/streaming.h"

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

// ── Service factory ───────────────────────────────────────────

static aether_watch_together_service_t *make_watch_svc(const char *local_uhid) {
    static aether_mesh_sender_t rs;
    rs.local_uhid = local_uhid;
    rs.send       = rs_send;
    rs.broadcast  = rs_broadcast;
    rs.user_data  = NULL;
    aether_routing_service_t *routing = aether_routing_service_new(&rs);
    return aether_watch_together_service_create(&g_transport, routing, local_uhid);
}

// ── Packet builders ───────────────────────────────────────────

static aether_packet_t *make_watch_sync_pkt(const char *from, const char *json) {
    aether_packet_t *p = aether_packet_new();
    if (!p) return NULL;
    p->type = AETHER_PACKET_TYPE_WATCH_SYNC;
    aether_packet_set_source_uhid(p, from);
    aether_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

static aether_packet_t *make_watch_reaction_pkt(const char *from, const char *json) {
    aether_packet_t *p = aether_packet_new();
    if (!p) return NULL;
    p->type = AETHER_PACKET_TYPE_WATCH_REACTION;
    aether_packet_set_source_uhid(p, from);
    aether_packet_set_payload(p, (const uint8_t *)json, (uint32_t)strlen(json));
    return p;
}

// Deterministic session UUID used across invite/play/pause/seek tests.
static const char *k_sid_str = "aabbccdd-eeff-4001-8002-aabbccddeeff";

// Helper: register an inbound invite so subsequent play/pause/seek have a session.
static void recv_invite(aether_watch_together_service_t *svc) {
    char json[512];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"%s\",\"host_uhid\":\"bob\","
        "\"media_url\":\"https://example.com/stream.m3u8\","
        "\"signal_type\":\"watch_invite\","
        "\"members\":[\"alice\",\"bob\"]}",
        k_sid_str);
    aether_packet_t *pkt = make_watch_sync_pkt("bob", json);
    aether_watch_handle_packet(svc, pkt);
    aether_packet_free(pkt);
}

// ── Callback capture ──────────────────────────────────────────

static int g_invite_count = 0;
static char g_invite_host[64];
static char g_invite_url[256];
static void on_invite(const uint8_t *sid, const char *host, const char *url, void *ud) {
    (void)sid; (void)ud;
    g_invite_count++;
    strncpy(g_invite_host, host ? host : "", 63);
    strncpy(g_invite_url, url ? url : "", 255);
}

static int g_playback_count = 0;
static int g_last_is_playing = -1;
static int64_t g_last_position_ms = -1;
static void on_playback(const uint8_t *sid, int is_playing, int64_t position_ms, void *ud) {
    (void)sid; (void)ud;
    g_playback_count++;
    g_last_is_playing = is_playing;
    g_last_position_ms = position_ms;
}

static int g_reaction_count = 0;
static char g_reaction_from[64];
static char g_reaction_emoji[64];
static void on_reaction(const uint8_t *sid, const char *from, const char *emoji, void *ud) {
    (void)sid; (void)ud;
    g_reaction_count++;
    strncpy(g_reaction_from, from ? from : "", 63);
    strncpy(g_reaction_emoji, emoji ? emoji : "", 63);
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

static void invite_returns_session_id(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = { "bob", "carol" };
    uint8_t session_id[16] = {0};
    int rc = aether_watch_invite_to_session(
        svc, to, 2, "https://example.com/stream.m3u8", session_id);
    assert(rc == 0);
    int nonzero = 0;
    for (int i = 0; i < 16; i++) nonzero += (session_id[i] != 0 ? 1 : 0);
    assert(nonzero > 0);
    aether_watch_together_service_destroy(svc);
}

static void invite_null_media_url_returns_error(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = { "bob" };
    uint8_t session_id[16] = {0};
    int rc = aether_watch_invite_to_session(svc, to, 1, NULL, session_id);
    assert(rc == -1);
    aether_watch_together_service_destroy(svc);
}

static void play_unknown_session_returns_error(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aether_watch_play(svc, unknown, 1000);
    assert(rc == -1);
    aether_watch_together_service_destroy(svc);
}

static void play_known_session_returns_ok(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = { "bob" };
    uint8_t session_id[16] = {0};
    aether_watch_invite_to_session(svc, to, 1, "https://example.com/stream.m3u8", session_id);
    int rc = aether_watch_play(svc, session_id, 5000);
    assert(rc == 0);
    aether_watch_together_service_destroy(svc);
}

static void pause_unknown_session_returns_error(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aether_watch_pause(svc, unknown, 1000);
    assert(rc == -1);
    aether_watch_together_service_destroy(svc);
}

static void pause_known_session_returns_ok(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = { "bob" };
    uint8_t session_id[16] = {0};
    aether_watch_invite_to_session(svc, to, 1, "https://example.com/stream.m3u8", session_id);
    int rc = aether_watch_pause(svc, session_id, 5000);
    assert(rc == 0);
    aether_watch_together_service_destroy(svc);
}

static void seek_unknown_session_returns_error(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aether_watch_seek(svc, unknown, 30000);
    assert(rc == -1);
    aether_watch_together_service_destroy(svc);
}

static void seek_known_session_returns_ok(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = { "bob" };
    uint8_t session_id[16] = {0};
    aether_watch_invite_to_session(svc, to, 1, "https://example.com/stream.m3u8", session_id);
    int rc = aether_watch_seek(svc, session_id, 30000);
    assert(rc == 0);
    aether_watch_together_service_destroy(svc);
}

static void set_speed_unknown_session_returns_error(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aether_watch_set_speed(svc, unknown, 1.5);
    assert(rc == -1);
    aether_watch_together_service_destroy(svc);
}

static void set_speed_known_session_returns_ok(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = { "bob" };
    uint8_t session_id[16] = {0};
    aether_watch_invite_to_session(svc, to, 1, "https://example.com/stream.m3u8", session_id);
    int rc = aether_watch_set_speed(svc, session_id, 2.0);
    assert(rc == 0);
    aether_watch_together_service_destroy(svc);
}

static void send_reaction_unknown_session_returns_error(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    uint8_t unknown[16] = {0x01, 0x02, 0x03};
    int rc = aether_watch_send_reaction(svc, unknown, "thumbsup");
    assert(rc == -1);
    aether_watch_together_service_destroy(svc);
}

static void send_reaction_known_session_returns_ok(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    const char *to[] = { "bob" };
    uint8_t session_id[16] = {0};
    aether_watch_invite_to_session(svc, to, 1, "https://example.com/stream.m3u8", session_id);
    int rc = aether_watch_send_reaction(svc, session_id, "heart");
    assert(rc == 0);
    aether_watch_together_service_destroy(svc);
}

static void handle_packet_inbound_invite_fires_invite_cb(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    g_invite_count = 0;
    aether_watch_set_invite_cb(svc, on_invite, NULL);

    char json[512];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"%s\",\"host_uhid\":\"bob\","
        "\"media_url\":\"https://example.com/stream.m3u8\","
        "\"signal_type\":\"watch_invite\","
        "\"members\":[\"alice\",\"bob\"]}",
        k_sid_str);

    aether_packet_t *pkt = make_watch_sync_pkt("bob", json);
    int rc = aether_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_invite_count == 1);
    assert(strcmp(g_invite_host, "bob") == 0);
    assert(strcmp(g_invite_url, "https://example.com/stream.m3u8") == 0);

    aether_packet_free(pkt);
    aether_watch_together_service_destroy(svc);
}

static void handle_packet_inbound_play_fires_playback_cb(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    g_playback_count = 0; g_last_is_playing = -1;
    aether_watch_set_playback_cb(svc, on_playback, NULL);
    recv_invite(svc);

    // sent_at_ms in the far future keeps RTT compensation non-positive so the
    // result is <= 5000; we only assert is_playing and that the cb fired.
    char json[512];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"%s\",\"from_uhid\":\"bob\","
        "\"position_ms\":5000,"
        "\"sent_at_ms\":9999999999999,"
        "\"signal_type\":\"watch_play\"}",
        k_sid_str);

    aether_packet_t *pkt = make_watch_sync_pkt("bob", json);
    int rc = aether_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_playback_count == 1);
    assert(g_last_is_playing == 1);

    aether_packet_free(pkt);
    aether_watch_together_service_destroy(svc);
}

static void handle_packet_inbound_pause_uses_exact_position(void) {
    // Pause has no RTT compensation — callback must receive the raw position_ms.
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    g_playback_count = 0; g_last_is_playing = -1; g_last_position_ms = -1;
    aether_watch_set_playback_cb(svc, on_playback, NULL);
    recv_invite(svc);

    char json[256];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"%s\",\"from_uhid\":\"bob\","
        "\"position_ms\":12345,"
        "\"signal_type\":\"watch_pause\"}",
        k_sid_str);

    aether_packet_t *pkt = make_watch_sync_pkt("bob", json);
    int rc = aether_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_playback_count == 1);
    assert(g_last_is_playing == 0);
    assert(g_last_position_ms == 12345);

    aether_packet_free(pkt);
    aether_watch_together_service_destroy(svc);
}

static void handle_packet_inbound_seek_fires_playback_cb(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    g_playback_count = 0; g_last_is_playing = -1;
    aether_watch_set_playback_cb(svc, on_playback, NULL);
    recv_invite(svc);

    char json[512];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"%s\",\"from_uhid\":\"bob\","
        "\"position_ms\":60000,"
        "\"sent_at_ms\":9999999999999,"
        "\"signal_type\":\"watch_seek\"}",
        k_sid_str);

    aether_packet_t *pkt = make_watch_sync_pkt("bob", json);
    int rc = aether_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_playback_count == 1);

    aether_packet_free(pkt);
    aether_watch_together_service_destroy(svc);
}

static void handle_packet_inbound_reaction_fires_reaction_cb(void) {
    aether_watch_together_service_t *svc = make_watch_svc("alice");
    g_reaction_count = 0;
    aether_watch_set_reaction_cb(svc, on_reaction, NULL);

    char json[512];
    snprintf(json, sizeof(json),
        "{\"session_id\":\"%s\",\"from_uhid\":\"bob\","
        "\"emoji\":\"lol\","
        "\"sent_at_ms\":1700000000000}",
        k_sid_str);

    aether_packet_t *pkt = make_watch_reaction_pkt("bob", json);
    int rc = aether_watch_handle_packet(svc, pkt);
    assert(rc == 0);
    assert(g_reaction_count == 1);
    assert(strcmp(g_reaction_from, "bob") == 0);
    assert(strcmp(g_reaction_emoji, "lol") == 0);

    aether_packet_free(pkt);
    aether_watch_together_service_destroy(svc);
}

int main(void) {
    printf("Aether Watch Together Service — Unit Tests\n");
    printf("==========================================\n");

    RUN(invite_returns_session_id);
    RUN(invite_null_media_url_returns_error);
    RUN(play_unknown_session_returns_error);
    RUN(play_known_session_returns_ok);
    RUN(pause_unknown_session_returns_error);
    RUN(pause_known_session_returns_ok);
    RUN(seek_unknown_session_returns_error);
    RUN(seek_known_session_returns_ok);
    RUN(set_speed_unknown_session_returns_error);
    RUN(set_speed_known_session_returns_ok);
    RUN(send_reaction_unknown_session_returns_error);
    RUN(send_reaction_known_session_returns_ok);
    RUN(handle_packet_inbound_invite_fires_invite_cb);
    RUN(handle_packet_inbound_play_fires_playback_cb);
    RUN(handle_packet_inbound_pause_uses_exact_position);
    RUN(handle_packet_inbound_seek_fires_playback_cb);
    RUN(handle_packet_inbound_reaction_fires_reaction_cb);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
