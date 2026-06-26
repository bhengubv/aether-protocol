// SPDX-License-Identifier: MIT
// aether-space in-memory breadcrumb store — see aethernet/space.h.

#include "aethernet/space.h"

#include <stdlib.h>
#include <string.h>
#include <time.h>

// ─── helpers ─────────────────────────────────────────────

static int64_t now_ms_space(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *str_dup_space(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static int32_t clamp_i32(int32_t v, int32_t lo, int32_t hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

static void free_crumb_fields(aethernet_space_breadcrumb_t *c) {
    if (!c) return;
    free(c->content_hash);
    free(c->geo_hash);
    free(c->anchor_uhid);
    free(c->signature);
    c->content_hash = NULL;
    c->geo_hash = NULL;
    c->anchor_uhid = NULL;
    c->signature = NULL;
    c->signature_len = 0;
}

void aethernet_space_breadcrumb_free(aethernet_space_breadcrumb_t *crumb) {
    if (!crumb) return;
    free_crumb_fields(crumb);
    free(crumb);
}

int64_t aethernet_space_breadcrumb_expires_at_ms(const aethernet_space_breadcrumb_t *crumb) {
    if (!crumb) return 0;
    int32_t ttl = crumb->ttl_hours < 0 ? 0 : crumb->ttl_hours;
    return crumb->created_at_ms + (int64_t)ttl * 3600 * 1000;
}

bool aethernet_space_breadcrumb_is_expired(const aethernet_space_breadcrumb_t *crumb) {
    if (!crumb) return true;
    return now_ms_space() >= aethernet_space_breadcrumb_expires_at_ms(crumb);
}

// Case-insensitive prefix test (ASCII geohash alphabet).
static bool starts_with_ci(const char *s, const char *prefix) {
    if (!s || !prefix) return false;
    for (size_t i = 0; prefix[i]; i++) {
        char a = s[i], b = prefix[i];
        if (a >= 'A' && a <= 'Z') a = (char)(a - 'A' + 'a');
        if (b >= 'A' && b <= 'Z') b = (char)(b - 'A' + 'a');
        if (a != b) return false;
    }
    return true;
}

// ─── store ───────────────────────────────────────────────

typedef struct breadcrumb_node {
    aethernet_space_breadcrumb_t crumb; // owned by-value (string fields owned)
    struct breadcrumb_node *next;
} breadcrumb_node_t;

struct aethernet_space_service {
    breadcrumb_node_t *head;
    aethernet_space_breadcrumb_cb on_received;
    void *on_received_user_data;
    aethernet_space_breadcrumb_cb on_expired;
    void *on_expired_user_data;
};

aethernet_space_service_t *aethernet_space_service_new(void) {
    return (aethernet_space_service_t *)calloc(1, sizeof(aethernet_space_service_t));
}

void aethernet_space_service_free(aethernet_space_service_t *service) {
    if (!service) return;
    breadcrumb_node_t *n = service->head;
    while (n) {
        breadcrumb_node_t *next = n->next;
        free_crumb_fields(&n->crumb);
        free(n);
        n = next;
    }
    free(service);
}

void aethernet_space_set_received_callback(aethernet_space_service_t *service, aethernet_space_breadcrumb_cb cb, void *user_data) {
    if (!service) return;
    service->on_received = cb;
    service->on_received_user_data = user_data;
}

void aethernet_space_set_expired_callback(aethernet_space_service_t *service, aethernet_space_breadcrumb_cb cb, void *user_data) {
    if (!service) return;
    service->on_expired = cb;
    service->on_expired_user_data = user_data;
}

// Find the node whose crumb.content_hash matches; NULL if absent.
static breadcrumb_node_t *find_node(aethernet_space_service_t *svc, const char *content_hash) {
    for (breadcrumb_node_t *n = svc->head; n; n = n->next) {
        if (n->crumb.content_hash && content_hash && strcmp(n->crumb.content_hash, content_hash) == 0) {
            return n;
        }
    }
    return NULL;
}

// Replace-or-insert a node keyed by content_hash; deep-copies the supplied fields.
// Returns a borrowed pointer to the stored crumb.
static aethernet_space_breadcrumb_t *upsert(
    aethernet_space_service_t *svc, const char *content_hash, const char *geo_hash,
    const char *anchor_uhid, int64_t created_at_ms, int32_t ttl_hours, uint8_t type,
    const uint8_t *signature, uint32_t signature_len) {
    breadcrumb_node_t *n = find_node(svc, content_hash);
    if (!n) {
        n = (breadcrumb_node_t *)calloc(1, sizeof(breadcrumb_node_t));
        if (!n) return NULL;
        n->next = svc->head;
        svc->head = n;
    } else {
        free_crumb_fields(&n->crumb);
    }
    n->crumb.content_hash = str_dup_space(content_hash);
    n->crumb.geo_hash = str_dup_space(geo_hash);
    n->crumb.anchor_uhid = str_dup_space(anchor_uhid);
    n->crumb.created_at_ms = created_at_ms;
    n->crumb.ttl_hours = ttl_hours;
    n->crumb.type = type;
    if (signature && signature_len) {
        n->crumb.signature = (uint8_t *)malloc(signature_len);
        if (n->crumb.signature) {
            memcpy(n->crumb.signature, signature, signature_len);
            n->crumb.signature_len = signature_len;
        }
    } else {
        n->crumb.signature = NULL;
        n->crumb.signature_len = 0;
    }
    return &n->crumb;
}

const aethernet_space_breadcrumb_t *aethernet_space_drop(
    aethernet_space_service_t *service,
    const char *geo_hash, const char *content_hash, const char *anchor_uhid,
    aethernet_breadcrumb_type_t type, int32_t ttl_hours) {
    if (!service || !content_hash) return NULL;
    int32_t effective_ttl = (type == AETHERNET_BREADCRUMB_EMERGENCY)
        ? AETHERNET_SPACE_EMERGENCY_TTL_HOURS
        : clamp_i32(ttl_hours, AETHERNET_SPACE_MIN_TTL_HOURS, AETHERNET_SPACE_MAX_TTL_HOURS);
    aethernet_space_breadcrumb_t *stored = upsert(service, content_hash, geo_hash, anchor_uhid,
                                                  now_ms_space(), effective_ttl, (uint8_t)type, NULL, 0);
    if (stored && service->on_received) {
        service->on_received(stored, service->on_received_user_data);
    }
    return stored;
}

int aethernet_space_scan(
    aethernet_space_service_t *service,
    const char *center_geo_hash, int32_t radius_cells,
    const aethernet_space_breadcrumb_t **out_results, int max) {
    if (!service || !out_results || max <= 0) return 0;
    int32_t prefix_len = clamp_i32(6 - radius_cells, 1, 6);
    char prefix[7];
    size_t clen = center_geo_hash ? strlen(center_geo_hash) : 0;
    size_t plen = ((size_t)prefix_len <= clen) ? (size_t)prefix_len : clen;
    if (plen > 6) plen = 6;
    for (size_t i = 0; i < plen; i++) prefix[i] = center_geo_hash[i];
    prefix[plen] = '\0';

    int count = 0;
    for (breadcrumb_node_t *n = service->head; n && count < max; n = n->next) {
        if (!aethernet_space_breadcrumb_is_expired(&n->crumb)
            && n->crumb.geo_hash && starts_with_ci(n->crumb.geo_hash, prefix)) {
            out_results[count++] = &n->crumb;
        }
    }
    return count;
}

void aethernet_space_pin(aethernet_space_service_t *service, const aethernet_space_breadcrumb_t *crumb) {
    if (!service || !crumb || !crumb->content_hash) return;
    aethernet_space_breadcrumb_t *stored = upsert(service, crumb->content_hash, crumb->geo_hash,
                                                  crumb->anchor_uhid, crumb->created_at_ms, crumb->ttl_hours,
                                                  crumb->type, crumb->signature, crumb->signature_len);
    if (stored && service->on_received) {
        service->on_received(stored, service->on_received_user_data);
    }
}

bool aethernet_space_delete(aethernet_space_service_t *service, const char *content_hash, const char *requestor_uhid) {
    if (!service || !content_hash) return false;
    breadcrumb_node_t *prev = NULL;
    for (breadcrumb_node_t *n = service->head; n; prev = n, n = n->next) {
        if (n->crumb.content_hash && strcmp(n->crumb.content_hash, content_hash) == 0) {
            if (!n->crumb.anchor_uhid || !requestor_uhid
                || strcmp(n->crumb.anchor_uhid, requestor_uhid) != 0) {
                return false; // creator-only delete
            }
            if (prev) prev->next = n->next; else service->head = n->next;
            free_crumb_fields(&n->crumb);
            free(n);
            return true;
        }
    }
    return false;
}

int aethernet_space_prune_expired(aethernet_space_service_t *service) {
    if (!service) return 0;
    int removed = 0;
    breadcrumb_node_t *prev = NULL;
    breadcrumb_node_t *n = service->head;
    while (n) {
        if (aethernet_space_breadcrumb_is_expired(&n->crumb)) {
            breadcrumb_node_t *next = n->next;
            if (service->on_expired) {
                service->on_expired(&n->crumb, service->on_expired_user_data);
            }
            if (prev) prev->next = next; else service->head = next;
            free_crumb_fields(&n->crumb);
            free(n);
            removed++;
            n = next;
        } else {
            prev = n;
            n = n->next;
        }
    }
    return removed;
}
