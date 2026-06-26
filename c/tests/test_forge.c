// SPDX-License-Identifier: MIT
//
// Behavioural test for the in-memory aether-forge package cache: cache (with the
// new-entry announcement + idempotent first-write-wins), query hit/miss, the fetch
// download-count increment, and aggregate stats.

#include <stdio.h>
#include <string.h>

#include "aethernet/forge.h"

static int g_failures = 0;

#define CHECK(cond, msg)                                                      \
    do {                                                                      \
        if (!(cond)) {                                                        \
            fprintf(stderr, "FAIL: %s (%s:%d)\n", (msg), __FILE__, __LINE__); \
            g_failures++;                                                     \
        }                                                                     \
    } while (0)

static int g_fired = 0;
static void on_new(const aethernet_forge_entry_t *entry, void *ud) {
    (void)entry;
    (void)ud;
    g_fired++;
}

int main(void) {
    aethernet_forge_service_t *svc = aethernet_forge_service_new();
    CHECK(svc != NULL, "forge_service_new");
    aethernet_forge_set_new_entry_callback(svc, on_new, NULL);

    const aethernet_forge_entry_t *e = aethernet_forge_cache(svc, "npm:react@18.2.0", "hash1", 1000);
    CHECK(e != NULL && e->download_count == 0, "cache new entry");
    CHECK(g_fired == 1, "new-entry callback fired once");

    // Idempotent re-cache: first write wins, no second announcement.
    const aethernet_forge_entry_t *e2 = aethernet_forge_cache(svc, "npm:react@18.2.0", "hash2", 9999);
    CHECK(e2 != NULL && strcmp(e2->content_hash, "hash1") == 0, "re-cache idempotent (first write wins)");
    CHECK(g_fired == 1, "re-cache did not re-announce");

    // Query hit + miss.
    CHECK(aethernet_forge_query(svc, "npm:react@18.2.0") != NULL, "query hit");
    CHECK(aethernet_forge_query(svc, "missing") == NULL, "query miss is NULL");

    // Fetch increments the download counter; miss returns NULL.
    const aethernet_forge_entry_t *f1 = aethernet_forge_fetch(svc, "npm:react@18.2.0");
    CHECK(f1 != NULL && f1->download_count == 1, "fetch increments download count");
    aethernet_forge_fetch(svc, "npm:react@18.2.0");
    CHECK(aethernet_forge_fetch(svc, "missing") == NULL, "fetch miss is NULL");

    // Stats: bytes-saved = downloads * size; one entry catalogued.
    aethernet_forge_stats_t st;
    aethernet_forge_get_stats(svc, &st);
    CHECK(st.catalogue_size == 1, "catalogue size 1");
    CHECK(st.total_bytes_saved == 2000, "total bytes saved 2000"); // 2 downloads * 1000 bytes

    aethernet_forge_service_free(svc);

    if (g_failures == 0) {
        printf("test_forge: all checks passed\n");
        return 0;
    }
    fprintf(stderr, "test_forge: %d check(s) failed\n", g_failures);
    return 1;
}
