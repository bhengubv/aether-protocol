// SPDX-License-Identifier: MIT
// Unit tests for sos.c (SosBroadcastService).

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethermesh/constants.h"
#include "aethermesh/protocol.h"
#include "aethermesh/sos.h"

#define LOCAL_UHID "local"

// ───── FakeMeshSender ────────────────────────────────────

typedef struct {
    aethermesh_mesh_packet_t **broadcasts;
    int broadcasts_len;
    int broadcasts_cap;
    aethermesh_mesh_packet_t **unicasts;
    char **unicasts_next_hops;
    int unicasts_len;
    int unicasts_cap;
} fake_state_t;

static bool fake_send(aethermesh_mesh_sender_t *self, const aethermesh_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->unicasts_len == s->unicasts_cap) {
        s->unicasts_cap = s->unicasts_cap ? s->unicasts_cap * 2 : 8;
        s->unicasts = (aethermesh_mesh_packet_t **)realloc(s->unicasts, sizeof(*s->unicasts) * (size_t)s->unicasts_cap);
        s->unicasts_next_hops = (char **)realloc(s->unicasts_next_hops, sizeof(*s->unicasts_next_hops) * (size_t)s->unicasts_cap);
    }
    s->unicasts[s->unicasts_len] = aethermesh_packet_clone(packet);
    s->unicasts_next_hops[s->unicasts_len] = next_hop_uhid ? strdup(next_hop_uhid) : NULL;
    s->unicasts_len++;
    return true;
}

static int fake_broadcast(aethermesh_mesh_sender_t *self, const aethermesh_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->broadcasts_len == s->broadcasts_cap) {
        s->broadcasts_cap = s->broadcasts_cap ? s->broadcasts_cap * 2 : 8;
        s->broadcasts = (aethermesh_mesh_packet_t **)realloc(s->broadcasts, sizeof(*s->broadcasts) * (size_t)s->broadcasts_cap);
    }
    s->broadcasts[s->broadcasts_len++] = aethermesh_packet_clone(packet);
    return 0;
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts_len; i++) aethermesh_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    for (int i = 0; i < s->unicasts_len; i++) {
        aethermesh_packet_free(s->unicasts[i]);
        free(s->unicasts_next_hops[i]);
    }
    free(s->unicasts);
    free(s->unicasts_next_hops);
    memset(s, 0, sizeof(*s));
}

static aethermesh_mesh_sender_t make_sender(fake_state_t *state) {
    aethermesh_mesh_sender_t s = {0};
    s.local_uhid = LOCAL_UHID;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;
}

// ───── Helpers ───────────────────────────────────────────

static aethermesh_mesh_packet_t *new_sos_packet(const char *src, int32_t ttl) {
    aethermesh_mesh_packet_t *p = aethermesh_packet_new();
    p->type = AETHERMESH_PACKET_TYPE_SOS_BROADCAST;
    aethermesh_packet_set_source_uhid(p, src);
    aethermesh_packet_set_destination_uhid(p, "");
    p->ttl = ttl;
    p->priority = AETHERMESH_SOS_PRIORITY;
    const char *body = "{\"broadcast_id\":\"00000000-0000-0000-0000-000000000000\","
                       "\"broadcast_type\":\"sos\",\"message\":\"help\","
                       "\"latitude\":0,\"longitude\":0,\"geohash\":null}";
    aethermesh_packet_set_payload(p, (const uint8_t *)body, strlen(body));
    return p;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

static int received_count = 0;
static void on_sos(const aethermesh_sos_alert_t *alert, void *ud) {
    (void)alert; (void)ud;
    received_count++;
}

// ───── Tests ─────────────────────────────────────────────

static void broadcast_floods_and_stores_alert(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);

    int rc = aethermesh_sos_broadcast(svc, "sos", "help", -33.9, 18.4, NULL);
    assert(rc == 0);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->type == AETHERMESH_PACKET_TYPE_SOS_BROADCAST);
    assert(s.broadcasts[0]->ttl == AETHERMESH_SOS_TTL);
    assert(s.broadcasts[0]->priority == AETHERMESH_SOS_PRIORITY);

    aethermesh_sos_service_free(svc);
    fake_clear(&s);
}

static void broadcast_rate_limited_after_max(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);

    for (int i = 0; i < AETHERMESH_MAX_SOS_BROADCASTS_PER_HOUR; i++) {
        int rc = aethermesh_sos_broadcast(svc, "sos", "h", 0, 0, NULL);
        assert(rc == 0);
    }
    int rc = aethermesh_sos_broadcast(svc, "sos", "h", 0, 0, NULL);
    assert(rc == 1); // rate-limited

    aethermesh_sos_service_free(svc);
    fake_clear(&s);
}

static void broadcast_rejects_null_type(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);

    int rc = aethermesh_sos_broadcast(svc, NULL, "help", 0, 0, NULL);
    assert(rc == -1);

    aethermesh_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_drops_duplicate_packet_id(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);

    aethermesh_mesh_packet_t *pkt = new_sos_packet("alice", AETHERMESH_SOS_TTL);
    aethermesh_sos_handle_packet(svc, pkt);
    int after_first = s.broadcasts_len;

    // Re-feed the same packet id
    aethermesh_mesh_packet_t *pkt2 = aethermesh_packet_clone(pkt);
    aethermesh_sos_handle_packet(svc, pkt2);
    assert(s.broadcasts_len == after_first);

    aethermesh_packet_free(pkt);
    aethermesh_packet_free(pkt2);
    aethermesh_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_ignores_self_originated(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);

    aethermesh_mesh_packet_t *pkt = new_sos_packet(LOCAL_UHID, AETHERMESH_SOS_TTL);
    aethermesh_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 0);

    aethermesh_packet_free(pkt);
    aethermesh_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_rebroadcasts_when_ttl_allows(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);

    aethermesh_mesh_packet_t *pkt = new_sos_packet("alice", 5);
    aethermesh_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 1);
    assert(s.broadcasts[0]->ttl == 4);

    aethermesh_packet_free(pkt);
    aethermesh_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_does_not_rebroadcast_when_ttl_exhausted(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);

    aethermesh_mesh_packet_t *pkt = new_sos_packet("alice", 1);
    aethermesh_sos_handle_packet(svc, pkt);
    assert(s.broadcasts_len == 0);

    aethermesh_packet_free(pkt);
    aethermesh_sos_service_free(svc);
    fake_clear(&s);
}

static void handle_invokes_received_callback(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);
    received_count = 0;
    aethermesh_sos_set_received_cb(svc, on_sos, NULL);

    aethermesh_mesh_packet_t *pkt = new_sos_packet("alice", AETHERMESH_SOS_TTL);
    aethermesh_sos_handle_packet(svc, pkt);
    assert(received_count == 1);

    aethermesh_packet_free(pkt);
    aethermesh_sos_service_free(svc);
    fake_clear(&s);
}

static void resolve_with_unknown_id_is_safe(void) {
    fake_state_t s = {0};
    aethermesh_mesh_sender_t sender = make_sender(&s);
    aethermesh_sos_service_t *svc = aethermesh_sos_service_new(&sender);

    uint8_t id[AETHERMESH_PACKET_ID_SIZE] = {0};
    aethermesh_sos_resolve(svc, id);

    aethermesh_sos_service_free(svc);
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
    RUN(resolve_with_unknown_id_is_safe);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
