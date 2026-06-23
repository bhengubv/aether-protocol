// SPDX-License-Identifier: MIT
// Unit tests for sos.c (SosBroadcastService).

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/constants.h"
#include "aethernet/protocol.h"
#include "aethernet/sos.h"

#define LOCAL_UHID "local"

// ───── FakeMeshSender ────────────────────────────────────

typedef struct {
    aethernet_mesh_packet_t **broadcasts;
    int broadcasts_len;
    int broadcasts_cap;
    aethernet_mesh_packet_t **unicasts;
    char **unicasts_next_hops;
    int unicasts_len;
    int unicasts_cap;
} fake_state_t;

static bool fake_send(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->unicasts_len == s->unicasts_cap) {
        s->unicasts_cap = s->unicasts_cap ? s->unicasts_cap * 2 : 8;
        s->unicasts = (aethernet_mesh_packet_t **)realloc(s->unicasts, sizeof(*s->unicasts) * (size_t)s->unicasts_cap);
        s->unicasts_next_hops = (char **)realloc(s->unicasts_next_hops, sizeof(*s->unicasts_next_hops) * (size_t)s->unicasts_cap);
    }
    s->unicasts[s->unicasts_len] = aethernet_packet_clone(packet);
    s->unicasts_next_hops[s->unicasts_len] = next_hop_uhid ? strdup(next_hop_uhid) : NULL;
    s->unicasts_len++;
    return true;
}

static int fake_broadcast(aethernet_mesh_sender_t *self, const aethernet_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->broadcasts_len == s->broadcasts_cap) {
        s->broadcasts_cap = s->broadcasts_cap ? s->broadcasts_cap * 2 : 8;
        s->broadcasts = (aethernet_mesh_packet_t **)realloc(s->broadcasts, sizeof(*s->broadcasts) * (size_t)s->broadcasts_cap);
    }
    s->broadcasts[s->broadcasts_len++] = aethernet_packet_clone(packet);
    return 0;
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts_len; i++) aethernet_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    for (int i = 0; i < s->unicasts_len; i++) {
        aethernet_packet_free(s->unicasts[i]);
        free(s->unicasts_next_hops[i]);
    }
    free(s->unicasts);
    free(s->unicasts_next_hops);
    memset(s, 0, sizeof(*s));
}

static aethernet_mesh_sender_t make_sender(fake_state_t *state) {
    aethernet_mesh_sender_t s = {0};
    s.local_uhid = LOCAL_UHID;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;
}

// ───── Helpers ───────────────────────────────────────────

static aethernet_mesh_packet_t *new_sos_packet(const char *src, int32_t ttl) {
    aethernet_mesh_packet_t *p = aethernet_packet_new();
    p->type = AETHERNET_PACKET_TYPE_SOS_BROADCAST;
    aethernet_packet_set_source_uhid(p, src);
    aethernet_packet_set_destination_uhid(p, "");
    p->ttl = ttl;
    p->priority = AETHERNET_SOS_PRIORITY;
    const char *body = "{\"broadcast_id\":\"00000000-0000-0000-0000-000000000000\","
                       "\"broadcast_type\":\"sos\",\"message\":\"help\","
                       "\"latitude\":0,\"longitude\":0,\"geohash\":null}";
    aethernet_packet_set_payload(p, (const uint8_t *)body, strlen(body));
    return p;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

static int received_count = 0;
static void on_sos(const aethernet_sos_alert_t *alert, void *ud) {
    (void)alert; (void)ud;
    received_count++;
}

typedef struct {
    int count;
    char broadcast_type[32];
    char message[64];
    double latitude;
    double longitude;
    char geohash[32];
} sos_capture_t;

static void on_sos_capture(const aethernet_sos_alert_t *alert, void *ud) {
    sos_capture_t *c = (sos_capture_t *)ud;
    c->count++;
    snprintf(c->broadcast_type, sizeof(c->broadcast_type), "%s",
             alert->broadcast_type ? alert->broadcast_type : "");
    snprintf(c->message, sizeof(c->message), "%s",
             alert->message ? alert->message : "");
    c->latitude = alert->latitude;
    c->longitude = alert->longitude;
    snprintf(c->geohash, sizeof(c->geohash), "%s",
             alert->geohash ? alert->geohash : "");
}

// ───── Tests ─────────────────────────────────────────────

static void broadcast_floods_and_stores_alert(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    int rc = aethernet_sos_broadcast(svc, "sos", "help", -33.9, 18.4, NULL);
    assert(rc == 0);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERNET_PACKET_TYPE_SOS_BROADCAST);
    assert(s.broadcasts[0]->ttl == AETHERNET_SOS_TTL);
    assert(s.broadcasts[0]->priority == AETHERNET_SOS_PRIORITY);

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void broadcast_rate_limited_after_max(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    for (int i = 0; i < AETHERNET_MAX_SOS_BROADCASTS_PER_HOUR; i++) {
        int rc = aethernet_sos_broadcast(svc, "sos", "h", 0, 0, NULL);
        assert(rc == 0);
    }
    int rc = aethernet_sos_broadcast(svc, "sos", "h", 0, 0, NULL);
    assert(rc == 1); // rate-limited

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void broadcast_rejects_null_type(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    int rc = aethernet_sos_broadcast(svc, NULL, "help", 0, 0, NULL);
    assert(rc == -1);

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_drops_duplicate_packet_id(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", AETHERNET_SOS_TTL);
    aethernet_sos_handle_packet(svc, pkt);
    int after_first = s.broadcasts_len;

    // Re-feed the same packet id
    aethernet_mesh_packet_t *pkt2 = aethernet_packet_clone(pkt);
    aethernet_sos_handle_packet(svc, pkt2);
    assert(s.broadcasts_len == after_first);

    aethernet_packet_free(pkt);
    aethernet_packet_free(pkt2);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_ignores_self_originated(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet(LOCAL_UHID, AETHERNET_SOS_TTL);
    aethernet_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 0);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_rebroadcasts_when_ttl_allows(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", 5);
    aethernet_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->ttl == 4);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_does_not_rebroadcast_when_ttl_exhausted(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", 1);
    aethernet_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 0);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_invokes_received_callback(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);
    received_count = 0;
    aethernet_sos_set_received_cb(svc, on_sos, NULL);

    aethernet_mesh_packet_t *pkt = new_sos_packet("alice", AETHERNET_SOS_TTL);
    aethernet_sos_handle_packet(svc, pkt);
    assert(received_count == 1);

    aethernet_packet_free(pkt);
    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_decodes_real_sos_body(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    sos_capture_t cap = {0};
    aethernet_sos_set_received_cb(svc, on_sos_capture, &cap);

    // A real SOS envelope from a remote node — a panic alert with a message and a
    // GPS fix. The handler must DECODE these from the payload; the old stub dropped
    // message/lat/long/geohash and hardcoded broadcast_type "sos".
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    pkt->type = AETHERNET_PACKET_TYPE_SOS_BROADCAST;
    aethernet_packet_set_source_uhid(pkt, "remote-alice");
    aethernet_packet_set_destination_uhid(pkt, "");
    pkt->ttl = 1;  // ttl == 1 → not rebroadcast
    pkt->priority = AETHERNET_SOS_PRIORITY;
    const char *body =
        "{\"broadcast_id\":\"11111111-2222-3333-4444-555555555555\","
        "\"broadcast_type\":\"panic\",\"message\":\"trapped, water rising\","
        "\"latitude\":-33.918600,\"longitude\":18.423300,\"geohash\":\"k3vp\"}";
    aethernet_packet_set_payload(pkt, (const uint8_t *)body, (uint32_t)strlen(body));

    aethernet_sos_handle_packet(svc, pkt);
    aethernet_packet_free(pkt);

    assert(cap.count == 1);
    assert(strcmp(cap.broadcast_type, "panic") == 0);             // decoded, not "sos"
    assert(strcmp(cap.message, "trapped, water rising") == 0);    // decoded, not dropped
    assert(cap.latitude < -33.9185 && cap.latitude > -33.9187);   // decoded GPS lat
    assert(cap.longitude > 18.4232 && cap.longitude < 18.4234);   // decoded GPS lon
    assert(strcmp(cap.geohash, "k3vp") == 0);                     // decoded, not dropped

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

static void resolve_with_unknown_id_is_safe(void) {
    fake_state_t s = {0};
    aethernet_mesh_sender_t sender = make_sender(&s);
    aethernet_sos_service_t *svc = aethernet_sos_service_new(&sender);

    uint8_t id[AETHERNET_PACKET_ID_SIZE] = {0};
    aethernet_sos_resolve(svc, id);

    aethernet_sos_service_free(svc);
    fake_clear(&s);
}

int main(void) {
    printf("Aether SOS Service — Unit Tests\n");
    printf("================================\n");

    RUN(broadcast_floods_and_stores_alert);
    RUN(broadcast_rate_limited_after_max);
    RUN(broadcast_rejects_null_type);
    RUN(handle_drops_duplicate_packet_id);
    RUN(handle_ignores_self_originated);
    RUN(handle_rebroadcasts_when_ttl_allows);
    RUN(handle_does_not_rebroadcast_when_ttl_exhausted);
    RUN(handle_invokes_received_callback);
    RUN(handle_decodes_real_sos_body);
    RUN(resolve_with_unknown_id_is_safe);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
