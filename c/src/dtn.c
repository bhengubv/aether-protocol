// SPDX-License-Identifier: MIT
// DTN store-and-forward implementation for the Aether mesh.
//
// NOTE: This implementation is single-threaded; hosts that pump packets from
// multiple threads must wrap the service in their own mutex. JSON encoding /
// decoding of bundle payloads is intentionally out of scope here — the wire
// format is documented in c/include/aether/dtn.h, and hosts plug in cJSON or
// json-c on their side. Encoding helpers below produce minimal valid JSON
// suitable for cross-language round-trip; decoding is left as a TODO so the
// service compiles cleanly without a JSON dep.

#include "aether/dtn.h"
#include "aether/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

// ─── Internal store nodes ────────────────────────────────

typedef struct bundle_node {
    aether_dtn_bundle_t *bundle;
    struct bundle_node *next;
} bundle_node_t;

typedef struct custody_node {
    uint8_t bundle_id[AETHER_PACKET_ID_SIZE];
    char *from_uhid;     // owned
    char *to_uhid;       // owned
    bool accepted;
    int64_t transferred_at_ms;
    struct custody_node *next;
} custody_node_t;

struct aether_dtn_service {
    aether_mesh_sender_t *sender;
    bundle_node_t *bundles;
    custody_node_t *custody_records;
};

// ─── Helpers ─────────────────────────────────────────────

static int64_t now_ms_dtn(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *str_dup_dtn(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static int active_bundle_count(aether_dtn_service_t *svc) {
    int count = 0;
    int64_t now = now_ms_dtn();
    for (bundle_node_t *n = svc->bundles; n; n = n->next) {
        const aether_dtn_bundle_t *b = n->bundle;
        if (!b) continue;
        if (b->expires_at_ms <= now) continue;
        if (b->status == AETHER_BUNDLE_STATUS_PENDING
            || b->status == AETHER_BUNDLE_STATUS_IN_CUSTODY) count++;
    }
    return count;
}

static bundle_node_t *find_bundle_node(aether_dtn_service_t *svc, const uint8_t id[AETHER_PACKET_ID_SIZE]) {
    for (bundle_node_t *n = svc->bundles; n; n = n->next) {
        if (n->bundle && memcmp(n->bundle->id, id, AETHER_PACKET_ID_SIZE) == 0) return n;
    }
    return NULL;
}

static bool bundle_to_packet_payload(const aether_dtn_bundle_t *b, uint8_t **out_payload, uint32_t *out_len) {
    // Minimal JSON encode. Cross-language stable shape; see DtnService.cs / dtn.go for canonical reference.
    // Format: {"id":"<hex32>","sender_uhid":"...","recipient_uhid":"...","encrypted_payload":[...],
    //          "priority":<int>,"status":<int>,"copy_count":<int>,"max_copies":<int>,
    //          "sender_geohash":<str|null>,"recipient_last_geohash":<str|null>,
    //          "hop_count":<int>,"created_at_ms":<int>,"expires_at_ms":<int>}
    // For brevity the reference impl writes a compact form; hosts should re-encode via cJSON for production.
    char id_hex[33];
    for (int i = 0; i < 16; i++) {
        static const char hex[] = "0123456789abcdef";
        id_hex[i * 2]     = hex[(b->id[i] >> 4) & 0xF];
        id_hex[i * 2 + 1] = hex[b->id[i] & 0xF];
    }
    id_hex[32] = 0;

    // Allocate generously; payload bytes serialized as decimal numbers (~4 chars each).
    size_t cap = 256 + (size_t)b->encrypted_payload_len * 5
                 + (b->sender_uhid ? strlen(b->sender_uhid) : 0) * 2
                 + (b->recipient_uhid ? strlen(b->recipient_uhid) : 0) * 2;
    char *buf = (char *)malloc(cap);
    if (!buf) return false;
    size_t off = 0;

    off += (size_t)snprintf(buf + off, cap - off,
        "{\"id\":\"%s\",\"sender_uhid\":\"%s\",\"recipient_uhid\":\"%s\",\"encrypted_payload\":[",
        id_hex,
        b->sender_uhid ? b->sender_uhid : "",
        b->recipient_uhid ? b->recipient_uhid : "");

    for (uint32_t i = 0; i < b->encrypted_payload_len; i++) {
        if (off + 8 >= cap) break;
        off += (size_t)snprintf(buf + off, cap - off, "%s%u", (i == 0 ? "" : ","), b->encrypted_payload[i]);
    }

    off += (size_t)snprintf(buf + off, cap - off,
        "],\"priority\":%u,\"status\":%u,\"copy_count\":%d,\"max_copies\":%d,"
        "\"sender_geohash\":%s%s%s,\"recipient_last_geohash\":%s%s%s,"
        "\"hop_count\":%d,\"created_at_ms\":%lld,\"expires_at_ms\":%lld}",
        b->priority, b->status, b->copy_count, b->max_copies,
        b->sender_geohash ? "\"" : "null", b->sender_geohash ? b->sender_geohash : "", b->sender_geohash ? "\"" : "",
        b->recipient_last_geohash ? "\"" : "null", b->recipient_last_geohash ? b->recipient_last_geohash : "", b->recipient_last_geohash ? "\"" : "",
        b->hop_count, (long long)b->created_at_ms, (long long)b->expires_at_ms);

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)off;
    return true;
}

static aether_mesh_packet_t *build_bundle_packet(aether_dtn_service_t *svc, const aether_dtn_bundle_t *bundle) {
    aether_mesh_packet_t *pkt = aether_packet_new();
    if (!pkt) return NULL;
    memcpy(pkt->packet_id, bundle->id, AETHER_PACKET_ID_SIZE);
    pkt->type = AETHER_PACKET_TYPE_DTN_BUNDLE;
    aether_packet_set_source_uhid(pkt, svc->sender->local_uhid);
    aether_packet_set_destination_uhid(pkt, bundle->recipient_uhid);
    pkt->ttl = AETHER_DTN_TTL;
    pkt->priority = bundle->priority;

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (bundle_to_packet_payload(bundle, &body, &body_len)) {
        aether_packet_set_payload(pkt, body, body_len);
        free(body);
    }
    return pkt;
}

static bool try_direct_delivery(aether_dtn_service_t *svc, const aether_dtn_bundle_t *bundle) {
    aether_mesh_packet_t *pkt = build_bundle_packet(svc, bundle);
    if (!pkt) return false;
    bool delivered = svc->sender->send(svc->sender, pkt, bundle->recipient_uhid);
    aether_packet_free(pkt);
    return delivered;
}

// ─── Public API ──────────────────────────────────────────

aether_dtn_bundle_t *aether_dtn_bundle_new(void) {
    aether_dtn_bundle_t *b = (aether_dtn_bundle_t *)calloc(1, sizeof(aether_dtn_bundle_t));
    if (!b) return NULL;
    b->priority = AETHER_BUNDLE_PRIORITY_NORMAL;
    b->status = AETHER_BUNDLE_STATUS_PENDING;
    b->copy_count = 1;
    b->max_copies = AETHER_DTN_MAX_COPIES;
    b->created_at_ms = now_ms_dtn();
    b->expires_at_ms = b->created_at_ms + (int64_t)AETHER_DTN_BUNDLE_TTL_HOURS * 3600 * 1000;
    return b;
}

void aether_dtn_bundle_free(aether_dtn_bundle_t *bundle) {
    if (!bundle) return;
    free(bundle->sender_uhid);
    free(bundle->recipient_uhid);
    free(bundle->encrypted_payload);
    free(bundle->sender_geohash);
    free(bundle->recipient_last_geohash);
    free(bundle);
}

bool aether_dtn_bundle_is_expired(const aether_dtn_bundle_t *bundle) {
    if (!bundle) return true;
    return now_ms_dtn() >= bundle->expires_at_ms;
}

aether_dtn_service_t *aether_dtn_service_new(aether_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aether_dtn_service_t *svc = (aether_dtn_service_t *)calloc(1, sizeof(aether_dtn_service_t));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aether_dtn_service_free(aether_dtn_service_t *service) {
    if (!service) return;
    while (service->bundles) {
        bundle_node_t *next = service->bundles->next;
        aether_dtn_bundle_free(service->bundles->bundle);
        free(service->bundles);
        service->bundles = next;
    }
    while (service->custody_records) {
        custody_node_t *next = service->custody_records->next;
        free(service->custody_records->from_uhid);
        free(service->custody_records->to_uhid);
        free(service->custody_records);
        service->custody_records = next;
    }
    free(service);
}

int aether_dtn_create_bundle(aether_dtn_service_t *service,
                             const char *recipient_uhid,
                             const uint8_t *encrypted_payload,
                             uint32_t encrypted_payload_len,
                             aether_bundle_priority_t priority,
                             const char *recipient_last_geohash) {
    if (!service || !recipient_uhid) return -1;
    aether_dtn_bundle_t *bundle = aether_dtn_bundle_new();
    if (!bundle) return -1;

    bundle->sender_uhid = str_dup_dtn(service->sender->local_uhid);
    bundle->recipient_uhid = str_dup_dtn(recipient_uhid);
    if (encrypted_payload && encrypted_payload_len) {
        bundle->encrypted_payload = (uint8_t *)malloc(encrypted_payload_len);
        if (!bundle->encrypted_payload) { aether_dtn_bundle_free(bundle); return -1; }
        memcpy(bundle->encrypted_payload, encrypted_payload, encrypted_payload_len);
        bundle->encrypted_payload_len = encrypted_payload_len;
    }
    bundle->priority = (uint8_t)priority;
    bundle->sender_geohash = str_dup_dtn(service->sender->local_geohash);
    bundle->recipient_last_geohash = str_dup_dtn(recipient_last_geohash);

    bundle_node_t *node = (bundle_node_t *)calloc(1, sizeof(bundle_node_t));
    if (!node) { aether_dtn_bundle_free(bundle); return -1; }
    node->bundle = bundle;
    node->next = service->bundles;
    service->bundles = node;

    if (try_direct_delivery(service, bundle)) {
        bundle->status = AETHER_BUNDLE_STATUS_DELIVERED;
    }
    return 0;
}

void aether_dtn_handle_packet(aether_dtn_service_t *service, const aether_mesh_packet_t *packet) {
    if (!service || !packet) return;
    // The full handler decodes bundle / custody-ack / delivery-receipt JSON
    // payloads. See header note: hosts wire up a JSON library on receive side
    // for production. The reference impl ships a placeholder so the service
    // compiles cleanly without a JSON dep.
    (void)packet;
}

void aether_dtn_run_delivery_scan(aether_dtn_service_t *service) {
    if (!service) return;
    int64_t now = now_ms_dtn();
    for (bundle_node_t *n = service->bundles; n; n = n->next) {
        aether_dtn_bundle_t *b = n->bundle;
        if (!b) continue;
        if (b->status == AETHER_BUNDLE_STATUS_DELIVERED) continue;
        if (b->expires_at_ms <= now) continue;
        if (try_direct_delivery(service, b)) {
            b->status = AETHER_BUNDLE_STATUS_DELIVERED;
        }
    }
    (void)active_bundle_count;  // referenced by future replication logic
}

int aether_dtn_expire_stale(aether_dtn_service_t *service) {
    if (!service) return 0;
    int expired = 0;
    int64_t now = now_ms_dtn();
    for (bundle_node_t *n = service->bundles; n; n = n->next) {
        aether_dtn_bundle_t *b = n->bundle;
        if (!b) continue;
        if (b->expires_at_ms <= now && b->status != AETHER_BUNDLE_STATUS_EXPIRED) {
            b->status = AETHER_BUNDLE_STATUS_EXPIRED;
            expired++;
        }
    }
    return expired;
}
