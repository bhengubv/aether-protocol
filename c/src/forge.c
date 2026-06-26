// SPDX-License-Identifier: MIT
// aether-forge in-memory package cache — see aethernet/forge.h.

#include "aethernet/forge.h"

#include <stdlib.h>
#include <string.h>
#include <time.h>

static int64_t now_ms_forge(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *str_dup_forge(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

typedef struct forge_node {
    aethernet_forge_entry_t entry; // owned string fields
    struct forge_node *next;
} forge_node_t;

struct aethernet_forge_service {
    forge_node_t *head;
    aethernet_forge_new_entry_cb on_new_entry;
    void *on_new_entry_user_data;
};

aethernet_forge_service_t *aethernet_forge_service_new(void) {
    return (aethernet_forge_service_t *)calloc(1, sizeof(aethernet_forge_service_t));
}

void aethernet_forge_service_free(aethernet_forge_service_t *service) {
    if (!service) return;
    forge_node_t *n = service->head;
    while (n) {
        forge_node_t *next = n->next;
        free(n->entry.content_hash);
        free(n->entry.package_id);
        free(n);
        n = next;
    }
    free(service);
}

void aethernet_forge_set_new_entry_callback(aethernet_forge_service_t *service, aethernet_forge_new_entry_cb cb, void *user_data) {
    if (!service) return;
    service->on_new_entry = cb;
    service->on_new_entry_user_data = user_data;
}

static forge_node_t *find_forge_node(aethernet_forge_service_t *svc, const char *package_id) {
    for (forge_node_t *n = svc->head; n; n = n->next) {
        if (n->entry.package_id && package_id && strcmp(n->entry.package_id, package_id) == 0) {
            return n;
        }
    }
    return NULL;
}

const aethernet_forge_entry_t *aethernet_forge_query(aethernet_forge_service_t *service, const char *package_id) {
    if (!service || !package_id) return NULL;
    forge_node_t *n = find_forge_node(service, package_id);
    return n ? &n->entry : NULL;
}

const aethernet_forge_entry_t *aethernet_forge_cache(
    aethernet_forge_service_t *service, const char *package_id, const char *content_hash, int64_t size_bytes) {
    if (!service || !package_id) return NULL;
    forge_node_t *existing = find_forge_node(service, package_id);
    if (existing) return &existing->entry; // idempotent — first write wins

    forge_node_t *n = (forge_node_t *)calloc(1, sizeof(forge_node_t));
    if (!n) return NULL;
    n->entry.package_id = str_dup_forge(package_id);
    n->entry.content_hash = str_dup_forge(content_hash);
    n->entry.fetched_at_ms = now_ms_forge();
    n->entry.size_bytes = size_bytes;
    n->entry.download_count = 0;
    n->next = service->head;
    service->head = n;

    if (service->on_new_entry) {
        service->on_new_entry(&n->entry, service->on_new_entry_user_data);
    }
    return &n->entry;
}

const aethernet_forge_entry_t *aethernet_forge_fetch(aethernet_forge_service_t *service, const char *package_id) {
    if (!service || !package_id) return NULL;
    forge_node_t *n = find_forge_node(service, package_id);
    if (!n) return NULL;
    n->entry.download_count++;
    return &n->entry;
}

void aethernet_forge_get_stats(aethernet_forge_service_t *service, aethernet_forge_stats_t *out_stats) {
    if (!out_stats) return;
    out_stats->total_bytes_saved = 0;
    out_stats->total_peers_served = 0;
    out_stats->catalogue_size = 0;
    if (!service) return;
    for (forge_node_t *n = service->head; n; n = n->next) {
        out_stats->total_bytes_saved += (int64_t)n->entry.download_count * n->entry.size_bytes;
        out_stats->catalogue_size++;
    }
}

int aethernet_forge_top_packages(aethernet_forge_service_t *service, const aethernet_forge_entry_t **out_top, int max) {
    if (!service || !out_top || max <= 0) return 0;
    // Selection of the top-`max` by download_count desc, stable on insertion order
    // for ties (selection scan picks the first-seen highest each round).
    int total = 0;
    for (forge_node_t *n = service->head; n; n = n->next) total++;
    int want = total < max ? total : max;

    // Track which nodes are already selected via a small used-flag array.
    forge_node_t **nodes = (forge_node_t **)malloc(sizeof(forge_node_t *) * (size_t)(total > 0 ? total : 1));
    if (!nodes) return 0;
    int idx = 0;
    for (forge_node_t *n = service->head; n; n = n->next) nodes[idx++] = n;

    bool *used = (bool *)calloc((size_t)(total > 0 ? total : 1), sizeof(bool));
    if (!used) { free(nodes); return 0; }

    int out_n = 0;
    for (int pick = 0; pick < want; pick++) {
        int best = -1;
        for (int i = 0; i < total; i++) {
            if (used[i]) continue;
            if (best < 0 || nodes[i]->entry.download_count > nodes[best]->entry.download_count) {
                best = i;
            }
        }
        if (best < 0) break;
        used[best] = true;
        out_top[out_n++] = &nodes[best]->entry;
    }
    free(used);
    free(nodes);
    return out_n;
}
