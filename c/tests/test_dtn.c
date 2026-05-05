// SPDX-License-Identifier: MIT
// Unit tests for dtn.c (DtnService).
//
// NOTE: The C reference DTN service ships a stub `aether_dtn_handle_packet`
// (handler tests are out of scope until hosts wire up a JSON parser); these
// tests cover the synchronous bookkeeping that runs in-process.

#define _POSIX_C_SOURCE 200809L  // strdup, etc.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aether/constants.h"
#include "aether/dtn.h"
#include "aether/protocol.h"

#define LOCAL_UHID "local"

// ───── FakeMeshSender (DTN flavour) ──────────────────────

typedef struct {
    aether_mesh_packet_t **broadcasts;
    int broadcasts_len;
    int broadcasts_cap;
    aether_mesh_packet_t **unicasts;
    char **unicasts_next_hops;
    int unicasts_len;
    int unicasts_cap;
    bool fail_send_for_recipient;
    char *block_recipient;
} fake_state_t;

static bool fake_send(aether_mesh_sender_t *self, const aether_mesh_packet_t *packet, const char *next_hop_uhid) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->fail_send_for_recipient && s->block_recipient
        && next_hop_uhid && strcmp(next_hop_uhid, s->block_recipient) == 0) {
        return false;
    }
    if (s->unicasts_len == s->unicasts_cap) {
        s->unicasts_cap = s->unicasts_cap ? s->unicasts_cap * 2 : 8;
        s->unicasts = (aether_mesh_packet_t **)realloc(s->unicasts, sizeof(*s->unicasts) * (size_t)s->unicasts_cap);
        s->unicasts_next_hops = (char **)realloc(s->unicasts_next_hops, sizeof(*s->unicasts_next_hops) * (size_t)s->unicasts_cap);
    }
    s->unicasts[s->unicasts_len] = aether_packet_clone(packet);
    s->unicasts_next_hops[s->unicasts_len] = next_hop_uhid ? strdup(next_hop_uhid) : NULL;
    s->unicasts_len++;
    return true;
}

static int fake_broadcast(aether_mesh_sender_t *self, const aether_mesh_packet_t *packet) {
    fake_state_t *s = (fake_state_t *)self->user_data;
    if (s->broadcasts_len == s->broadcasts_cap) {
        s->broadcasts_cap = s->broadcasts_cap ? s->broadcasts_cap * 2 : 8;
        s->broadcasts = (aether_mesh_packet_t **)realloc(s->broadcasts, sizeof(*s->broadcasts) * (size_t)s->broadcasts_cap);
    }
    s->broadcasts[s->broadcasts_len++] = aether_packet_clone(packet);
    return 0;
}

static void fake_clear(fake_state_t *s) {
    for (int i = 0; i < s->broadcasts_len; i++) aether_packet_free(s->broadcasts[i]);
    free(s->broadcasts);
    for (int i = 0; i < s->unicasts_len; i++) {
        aether_packet_free(s->unicasts[i]);
        free(s->unicasts_next_hops[i]);
    }
    free(s->unicasts);
    free(s->unicasts_next_hops);
    free(s->block_recipient);
    memset(s, 0, sizeof(*s));
}

static aether_mesh_sender_t make_sender(fake_state_t *state) {
    aether_mesh_sender_t s = {0};
    s.local_uhid = LOCAL_UHID;
    s.local_geohash = NULL;
    s.send = fake_send;
    s.broadcast = fake_broadcast;
    s.user_data = state;
    return s;
}

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// ───── Tests ─────────────────────────────────────────────

static void create_bundle_attempts_direct_delivery(void) {
    fake_state_t s = {0};
    aether_mesh_sender_t sender = make_sender(&s);
    aether_dtn_service_t *svc = aether_dtn_service_new(&sender);

    uint8_t payload[] = {1, 2, 3};
    int rc = aether_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                                      AETHER_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(rc == 0);

    // FakeMeshSender's send always returns true → direct delivery succeeded → unicast recorded.
    int dtn_unicasts = 0;
    for (int i = 0; i < s.unicasts_len; i++) {
        if (s.unicasts[i]->type == AETHER_PACKET_TYPE_DTN_BUNDLE
            && strcmp(s.unicasts_next_hops[i], "recipient") == 0) dtn_unicasts++;
    }
    assert(dtn_unicasts == 1);

    aether_dtn_service_free(svc);
    fake_clear(&s);
}

static void create_bundle_with_failing_send_keeps_pending(void) {
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("recipient");
    aether_mesh_sender_t sender = make_sender(&s);
    aether_dtn_service_t *svc = aether_dtn_service_new(&sender);

    uint8_t payload[] = {1};
    int rc = aether_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                                      AETHER_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(rc == 0);
    // No unicast recorded because send was blocked
    assert(s.unicasts_len == 0);

    aether_dtn_service_free(svc);
    fake_clear(&s);
}

static void run_delivery_scan_retries_pending(void) {
    fake_state_t s = {0};
    s.fail_send_for_recipient = true;
    s.block_recipient = strdup("recipient");
    aether_mesh_sender_t sender = make_sender(&s);
    aether_dtn_service_t *svc = aether_dtn_service_new(&sender);

    uint8_t payload[] = {1};
    aether_dtn_create_bundle(svc, "recipient", payload, sizeof(payload),
                             AETHER_BUNDLE_PRIORITY_NORMAL, NULL);
    assert(s.unicasts_len == 0);

    // Unblock and re-scan
    s.fail_send_for_recipient = false;
    aether_dtn_run_delivery_scan(svc);

    int dtn_unicasts = 0;
    for (int i = 0; i < s.unicasts_len; i++) {
        if (s.unicasts[i]->type == AETHER_PACKET_TYPE_DTN_BUNDLE) dtn_unicasts++;
    }
    assert(dtn_unicasts >= 1);

    aether_dtn_service_free(svc);
    fake_clear(&s);
}

static void expire_stale_is_safe_with_no_bundles(void) {
    fake_state_t s = {0};
    aether_mesh_sender_t sender = make_sender(&s);
    aether_dtn_service_t *svc = aether_dtn_service_new(&sender);

    int n = aether_dtn_expire_stale(svc);
    assert(n == 0);

    aether_dtn_service_free(svc);
    fake_clear(&s);
}

static void bundle_lifecycle_helpers(void) {
    aether_dtn_bundle_t *b = aether_dtn_bundle_new();
    assert(b != NULL);
    assert(b->priority == AETHER_BUNDLE_PRIORITY_NORMAL);
    assert(b->status == AETHER_BUNDLE_STATUS_PENDING);
    assert(b->copy_count == 1);
    assert(b->max_copies == AETHER_DTN_MAX_COPIES);
    assert(!aether_dtn_bundle_is_expired(b));  // 72h in the future

    // Force an expired timestamp and re-check
    b->expires_at_ms = 0;
    assert(aether_dtn_bundle_is_expired(b));

    aether_dtn_bundle_free(b);
}

int main(void) {
    printf("Aether DTN Service — Unit Tests\n");
    printf("================================\n");

    RUN(create_bundle_attempts_direct_delivery);
    RUN(create_bundle_with_failing_send_keeps_pending);
    RUN(run_delivery_scan_retries_pending);
    RUN(expire_stale_is_safe_with_no_bundles);
    RUN(bundle_lifecycle_helpers);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
