// SPDX-License-Identifier: MIT
// AODV-inspired routing implementation for the Aether mesh.

#include "aether/routing.h"
#include "aether/constants.h"
#include "aether_reputation.h"

#include <stdlib.h>
#include <string.h>
#include <time.h>

// ─── Internal route table node ───────────────────────────

typedef struct route_node {
    aether_route_entry_t entry;
    struct route_node *next;
} route_node_t;

// ─── Internal RREQ dedup entry ───────────────────────────

typedef struct rreq_seen {
    uint8_t id[AETHER_PACKET_ID_SIZE];
    int64_t seen_at_ms;
    struct rreq_seen *next;
} rreq_seen_t;

// ─── Per-source RREQ rate-limit entry ────────────────────
#define RREQ_SOURCE_TS_CAP 32   /* ring-buffer capacity per source */

typedef struct rreq_source_ts {
    char     uhid[256];
    int64_t  timestamps[RREQ_SOURCE_TS_CAP];
    int      count;
    struct rreq_source_ts *next;
} rreq_source_ts_t;

struct aether_routing_service {
    aether_mesh_sender_t         *sender;
    route_node_t                 *routes;         // singly-linked list
    rreq_seen_t                  *seen_rreqs;     // singly-linked list
    int                           seen_count;
    rreq_source_ts_t             *rreq_sources;   /* per-source timestamps */
    AetherNodeReputationService  *reputation;     /* optional, may be NULL */
};

// ─── Helpers ─────────────────────────────────────────────

static int64_t now_ms(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *str_dup_safe(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static void route_entry_clear(aether_route_entry_t *e) {
    if (!e) return;
    free(e->destination_uhid);
    free(e->next_hop_uhid);
    e->destination_uhid = NULL;
    e->next_hop_uhid = NULL;
}

static aether_route_entry_t *route_entry_clone(const aether_route_entry_t *src) {
    aether_route_entry_t *out = (aether_route_entry_t *)calloc(1, sizeof(aether_route_entry_t));
    if (!out) return NULL;
    out->destination_uhid = str_dup_safe(src->destination_uhid);
    out->next_hop_uhid = str_dup_safe(src->next_hop_uhid);
    out->hop_count = src->hop_count;
    out->quality_score = src->quality_score;
    out->expires_at_ms = src->expires_at_ms;
    if ((!out->destination_uhid && src->destination_uhid)
        || (!out->next_hop_uhid && src->next_hop_uhid)) {
        aether_route_entry_free(out);
        return NULL;
    }
    return out;
}

static route_node_t *find_route_node(aether_routing_service_t *svc, const char *destination) {
    if (!destination) return NULL;
    for (route_node_t *n = svc->routes; n; n = n->next) {
        if (n->entry.destination_uhid && strcmp(n->entry.destination_uhid, destination) == 0) return n;
    }
    return NULL;
}

static bool rreq_seen_contains(aether_routing_service_t *svc, const uint8_t id[AETHER_PACKET_ID_SIZE]) {
    for (rreq_seen_t *r = svc->seen_rreqs; r; r = r->next) {
        if (memcmp(r->id, id, AETHER_PACKET_ID_SIZE) == 0) return true;
    }
    return false;
}

static void rreq_seen_add(aether_routing_service_t *svc, const uint8_t id[AETHER_PACKET_ID_SIZE]) {
    rreq_seen_t *r = (rreq_seen_t *)malloc(sizeof(rreq_seen_t));
    if (!r) return;
    memcpy(r->id, id, AETHER_PACKET_ID_SIZE);
    r->seen_at_ms = now_ms();
    r->next = svc->seen_rreqs;
    svc->seen_rreqs = r;
    svc->seen_count++;
}

// ─── Per-source RREQ rate-limit helpers ──────────────────

static rreq_source_ts_t *find_source_ts(aether_routing_service_t *svc, const char *uhid) {
    for (rreq_source_ts_t *s = svc->rreq_sources; s; s = s->next) {
        if (strncmp(s->uhid, uhid, sizeof(s->uhid) - 1) == 0) return s;
    }
    return NULL;
}

static rreq_source_ts_t *get_or_create_source_ts(aether_routing_service_t *svc, const char *uhid) {
    rreq_source_ts_t *s = find_source_ts(svc, uhid);
    if (!s) {
        s = (rreq_source_ts_t *)calloc(1, sizeof(rreq_source_ts_t));
        if (!s) return NULL;
        strncpy(s->uhid, uhid, sizeof(s->uhid) - 1);
        s->next = svc->rreq_sources;
        svc->rreq_sources = s;
    }
    return s;
}

/* Returns true iff the source has exceeded the per-window RREQ limit.
 * Also prunes stale timestamps and (if not flooded) appends now_s. */
static bool rreq_rate_limit_check_and_record(aether_routing_service_t *svc,
                                              const char *uhid, int64_t now_s) {
    rreq_source_ts_t *src = get_or_create_source_ts(svc, uhid);
    if (!src) return false;  /* allocation failure — permit packet */

    int64_t window_start = now_s - AETHER_RREQ_RATE_LIMIT_WINDOW_S;

    /* Compact: remove timestamps outside the window. */
    int out = 0;
    for (int i = 0; i < src->count; i++) {
        if (src->timestamps[i] > window_start) {
            src->timestamps[out++] = src->timestamps[i];
        }
    }
    src->count = out;

    if (src->count >= AETHER_RREQ_RATE_LIMIT_MAX) {
        return true;  /* flood: caller drops packet + records reputation */
    }

    /* Append current timestamp (guard against ring overflow). */
    if (src->count < RREQ_SOURCE_TS_CAP) {
        src->timestamps[src->count++] = now_s;
    }
    return false;
}

static void install_route(aether_routing_service_t *svc,
                          const char *destination,
                          const char *next_hop,
                          int32_t hop_count) {
    route_node_t *existing = find_route_node(svc, destination);
    if (existing) {
        free(existing->entry.next_hop_uhid);
        existing->entry.next_hop_uhid = str_dup_safe(next_hop);
        existing->entry.hop_count = hop_count;
        existing->entry.quality_score = 50;
        existing->entry.expires_at_ms = now_ms() + (int64_t)AETHER_ROUTE_EXPIRY_SECONDS * 1000;
        return;
    }
    route_node_t *node = (route_node_t *)calloc(1, sizeof(route_node_t));
    if (!node) return;
    node->entry.destination_uhid = str_dup_safe(destination);
    node->entry.next_hop_uhid = str_dup_safe(next_hop);
    node->entry.hop_count = hop_count;
    node->entry.quality_score = 50;
    node->entry.expires_at_ms = now_ms() + (int64_t)AETHER_ROUTE_EXPIRY_SECONDS * 1000;
    node->next = svc->routes;
    svc->routes = node;
}

// ─── Public API ──────────────────────────────────────────

aether_routing_service_t *aether_routing_service_new(aether_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aether_routing_service_t *svc = (aether_routing_service_t *)calloc(1, sizeof(aether_routing_service_t));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aether_routing_service_free(aether_routing_service_t *service) {
    if (!service) return;
    while (service->routes) {
        route_node_t *next = service->routes->next;
        route_entry_clear(&service->routes->entry);
        free(service->routes);
        service->routes = next;
    }
    while (service->seen_rreqs) {
        rreq_seen_t *next = service->seen_rreqs->next;
        free(service->seen_rreqs);
        service->seen_rreqs = next;
    }
    while (service->rreq_sources) {
        rreq_source_ts_t *next = service->rreq_sources->next;
        free(service->rreq_sources);
        service->rreq_sources = next;
    }
    free(service);
}

void aether_routing_set_reputation(aether_routing_service_t *service,
                                   AetherNodeReputationService *reputation) {
    if (!service) return;
    service->reputation = reputation;
}

bool aether_routing_find_cached(aether_routing_service_t *service,
                                const char *destination_uhid,
                                aether_route_entry_t **out_route) {
    if (!service || !destination_uhid || !out_route) return false;
    route_node_t *n = find_route_node(service, destination_uhid);
    if (!n || n->entry.expires_at_ms <= now_ms()) return false;
    *out_route = route_entry_clone(&n->entry);
    return *out_route != NULL;
}

int aether_routing_discover(aether_routing_service_t *service, const char *destination_uhid) {
    if (!service || !destination_uhid) return -1;
    route_node_t *cached = find_route_node(service, destination_uhid);
    if (cached && cached->entry.expires_at_ms > now_ms()) return 1;

    aether_mesh_packet_t *rreq = aether_packet_new();
    if (!rreq) return -1;
    rreq->type = AETHER_PACKET_TYPE_ROUTE_REQUEST;
    aether_packet_set_source_uhid(rreq, service->sender->local_uhid);
    aether_packet_set_destination_uhid(rreq, destination_uhid);
    rreq->ttl = AETHER_DEFAULT_TTL;
    rreq->priority = 0;

    int fanout = service->sender->broadcast(service->sender, rreq);
    aether_packet_free(rreq);
    return (fanout > 0) ? 0 : -1;
}

void aether_routing_handle_rreq(aether_routing_service_t *service, aether_mesh_packet_t *rreq) {
    if (!service || !rreq || rreq->type != AETHER_PACKET_TYPE_ROUTE_REQUEST) return;
    if (rreq_seen_contains(service, rreq->packet_id)) return;
    /* Per-source RREQ rate limiting — mirrors Go/Rust RoutingService. */
    if (rreq->source_uhid) {
        int64_t now_s = now_ms() / 1000;
        if (rreq_rate_limit_check_and_record(service, rreq->source_uhid, now_s)) {
            if (service->reputation) {
                aether_reputation_record_rreq_flood(service->reputation, rreq->source_uhid);
            }
            return;  /* silently drop: source is flooding unique RREQs */
        }
    }
    rreq_seen_add(service, rreq->packet_id);

    const char *local = service->sender->local_uhid;
    if (!rreq->source_uhid || (local && strcmp(rreq->source_uhid, local) == 0)) return;

    int32_t hop_count = AETHER_DEFAULT_TTL - rreq->ttl + 1;
    if (hop_count < 1) hop_count = 1;
    install_route(service, rreq->source_uhid, rreq->source_uhid, hop_count);

    bool we_are_destination = local && rreq->destination_uhid
        && strcmp(rreq->destination_uhid, local) == 0;
    bool we_know_destination = false;
    if (!we_are_destination && rreq->destination_uhid) {
        route_node_t *known = find_route_node(service, rreq->destination_uhid);
        we_know_destination = known && known->entry.expires_at_ms > now_ms();
    }

    if (we_are_destination || we_know_destination) {
        aether_mesh_packet_t *rrep = aether_packet_new();
        if (rrep) {
            rrep->type = AETHER_PACKET_TYPE_ROUTE_REPLY;
            aether_packet_set_source_uhid(rrep,
                we_are_destination ? local : rreq->destination_uhid);
            aether_packet_set_destination_uhid(rrep, rreq->source_uhid);
            rrep->ttl = AETHER_DEFAULT_TTL;
            aether_packet_set_payload(rrep, rreq->payload, rreq->payload_len);
            // Send via reverse route's next-hop, or broadcast as fallback.
            route_node_t *reverse = find_route_node(service, rreq->source_uhid);
            if (reverse && reverse->entry.expires_at_ms > now_ms() && reverse->entry.next_hop_uhid) {
                service->sender->send(service->sender, rrep, reverse->entry.next_hop_uhid);
            } else {
                service->sender->broadcast(service->sender, rrep);
            }
            aether_packet_free(rrep);
        }
        return;
    }

    if (rreq->ttl > 1) {
        rreq->ttl--;
        service->sender->broadcast(service->sender, rreq);
    }
}

void aether_routing_handle_rrep(aether_routing_service_t *service, aether_mesh_packet_t *rrep) {
    if (!service || !rrep || rrep->type != AETHER_PACKET_TYPE_ROUTE_REPLY) return;
    const char *local = service->sender->local_uhid;
    if (!rrep->source_uhid || (local && strcmp(rrep->source_uhid, local) == 0)) return;

    int32_t hop_count = AETHER_DEFAULT_TTL - rrep->ttl + 1;
    if (hop_count < 1) hop_count = 1;
    install_route(service, rrep->source_uhid, rrep->source_uhid, hop_count);

    bool we_are_target = local && rrep->destination_uhid
        && strcmp(rrep->destination_uhid, local) == 0;
    if (we_are_target) return;

    if (rrep->ttl <= 1 || !rrep->destination_uhid) return;
    route_node_t *next = find_route_node(service, rrep->destination_uhid);
    if (next && next->entry.expires_at_ms > now_ms() && next->entry.next_hop_uhid) {
        rrep->ttl--;
        service->sender->send(service->sender, rrep, next->entry.next_hop_uhid);
    }
}

int aether_routing_prune(aether_routing_service_t *service) {
    if (!service) return 0;
    int evicted = 0;
    int64_t cutoff = now_ms();
    route_node_t **prev = &service->routes;
    while (*prev) {
        route_node_t *node = *prev;
        if (node->entry.expires_at_ms <= cutoff) {
            *prev = node->next;
            route_entry_clear(&node->entry);
            free(node);
            evicted++;
        } else {
            prev = &node->next;
        }
    }
    if (service->seen_count > 10000) {
        while (service->seen_rreqs) {
            rreq_seen_t *next = service->seen_rreqs->next;
            free(service->seen_rreqs);
            service->seen_rreqs = next;
        }
        service->seen_count = 0;
    }
    return evicted;
}

void aether_route_entry_free(aether_route_entry_t *route) {
    if (!route) return;
    route_entry_clear(route);
    free(route);
}
