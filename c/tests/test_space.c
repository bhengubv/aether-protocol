// SPDX-License-Identifier: MIT
//
// Behavioural test for the in-memory aether-space breadcrumb noticeboard: drop
// (TTL clamp + emergency override + received callback), geohash-prefix scan,
// creator-only delete, and prune.

#include <stdio.h>

#include "aethernet/space.h"

static int g_failures = 0;

#define CHECK(cond, msg)                                                      \
    do {                                                                      \
        if (!(cond)) {                                                        \
            fprintf(stderr, "FAIL: %s (%s:%d)\n", (msg), __FILE__, __LINE__); \
            g_failures++;                                                     \
        }                                                                     \
    } while (0)

static int g_received = 0;
static void on_received(const aethernet_space_breadcrumb_t *crumb, void *ud) {
    (void)crumb;
    (void)ud;
    g_received++;
}

int main(void) {
    aethernet_space_service_t *svc = aethernet_space_service_new();
    CHECK(svc != NULL, "space_service_new");
    aethernet_space_set_received_callback(svc, on_received, NULL);

    const aethernet_space_breadcrumb_t *a =
        aethernet_space_drop(svc, "k3vf9z", "hashA", "anchor1", AETHERNET_BREADCRUMB_NOTICE, 24);
    CHECK(a != NULL && a->ttl_hours == 24, "notice ttl 24");
    CHECK(g_received == 1, "received callback fired once");

    // Emergency breadcrumbs get the fixed 720h TTL.
    const aethernet_space_breadcrumb_t *e =
        aethernet_space_drop(svc, "k3vf9z", "hashE", "anchor1", AETHERNET_BREADCRUMB_EMERGENCY, 1);
    CHECK(e != NULL && e->ttl_hours == 720, "emergency ttl 720");

    // Scan: prefix-proximity hit vs a far cell.
    const aethernet_space_breadcrumb_t *hits[8];
    CHECK(aethernet_space_scan(svc, "k3vf9z", 1, hits, 8) == 2, "scan near returns 2");
    CHECK(aethernet_space_scan(svc, "xxxxxx", 1, hits, 8) == 0, "scan far returns 0");

    // Creator-only delete (delete by content hash).
    CHECK(!aethernet_space_delete(svc, "hashA", "wrong"), "non-anchor delete refused");
    CHECK(aethernet_space_delete(svc, "hashA", "anchor1"), "anchor delete succeeds");
    CHECK(aethernet_space_scan(svc, "k3vf9z", 1, hits, 8) == 1, "after delete scan returns 1");

    // Nothing is past its TTL yet.
    CHECK(aethernet_space_prune_expired(svc) == 0, "nothing expired");

    aethernet_space_service_free(svc);

    if (g_failures == 0) {
        printf("test_space: all checks passed\n");
        return 0;
    }
    fprintf(stderr, "test_space: %d check(s) failed\n", g_failures);
    return 1;
}
