// SPDX-License-Identifier: MIT
// DTN store-and-forward implementation for the Aether mesh.
//
// NOTE: This implementation is single-threaded; hosts that pump packets from
// multiple threads must wrap the service in their own mutex. JSON encoding /
// decoding of bundle payloads is intentionally out of scope here — the wire
// format is documented in c/include/aethernet/dtn.h, and hosts plug in cJSON or
// json-c on their side. Encoding helpers below produce minimal valid JSON
// suitable for cross-language round-trip; decoding is left as a TODO so the
// service compiles cleanly without a JSON dep.

#include "aethernet/dtn.h"
#include "aethernet/constants.h"
#include "aethernet_reputation.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

// ─── Internal store nodes ────────────────────────────────

typedef struct bundle_node {
    aethernet_dtn_bundle_t *bundle;
    struct bundle_node *next;
} bundle_node_t;

typedef struct custody_node {
    uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE];
    char *from_uhid;     // owned
    char *to_uhid;       // owned
    bool accepted;
    int64_t transferred_at_ms;
    struct custody_node *next;
} custody_node_t;

struct aethernet_dtn_service {
    aethernet_mesh_sender_t *sender;
    bundle_node_t *bundles;
    custody_node_t *custody_records;
    AetherNetNodeReputationService *reputation; // optional, may be NULL
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

static int active_bundle_count(aethernet_dtn_service_t *svc) {
    int count = 0;
    int64_t now = now_ms_dtn();
    for (bundle_node_t *n = svc->bundles; n; n = n->next) {
        const aethernet_dtn_bundle_t *b = n->bundle;
        if (!b) continue;
        if (b->expires_at_ms <= now) continue;
        if (b->status == AETHERNET_BUNDLE_STATUS_PENDING
            || b->status == AETHERNET_BUNDLE_STATUS_IN_CUSTODY) count++;
    }
    return count;
}

static bundle_node_t *find_bundle_node(aethernet_dtn_service_t *svc, const uint8_t id[AETHERNET_PACKET_ID_SIZE]) {
    for (bundle_node_t *n = svc->bundles; n; n = n->next) {
        if (n->bundle && memcmp(n->bundle->id, id, AETHERNET_PACKET_ID_SIZE) == 0) return n;
    }
    return NULL;
}

static bool bundle_to_packet_payload(const aethernet_dtn_bundle_t *b, uint8_t **out_payload, uint32_t *out_len) {
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

static aethernet_mesh_packet_t *build_bundle_packet(aethernet_dtn_service_t *svc, const aethernet_dtn_bundle_t *bundle) {
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) return NULL;
    memcpy(pkt->packet_id, bundle->id, AETHERNET_PACKET_ID_SIZE);
    pkt->type = AETHERNET_PACKET_TYPE_DTN_BUNDLE;
    aethernet_packet_set_source_uhid(pkt, svc->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, bundle->recipient_uhid);
    pkt->ttl = AETHERNET_DTN_TTL;
    pkt->priority = bundle->priority;

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (bundle_to_packet_payload(bundle, &body, &body_len)) {
        aethernet_packet_set_payload(pkt, body, body_len);
        free(body);
    }
    return pkt;
}

static bool try_direct_delivery(aethernet_dtn_service_t *svc, const aethernet_dtn_bundle_t *bundle) {
    aethernet_mesh_packet_t *pkt = build_bundle_packet(svc, bundle);
    if (!pkt) return false;
    bool delivered = svc->sender->send(svc->sender, pkt, bundle->recipient_uhid);
    aethernet_packet_free(pkt);
    return delivered;
}

// ─── Public API ──────────────────────────────────────────

aethernet_dtn_bundle_t *aethernet_dtn_bundle_new(void) {
    aethernet_dtn_bundle_t *b = (aethernet_dtn_bundle_t *)calloc(1, sizeof(aethernet_dtn_bundle_t));
    if (!b) return NULL;
    b->priority = AETHERNET_BUNDLE_PRIORITY_NORMAL;
    b->status = AETHERNET_BUNDLE_STATUS_PENDING;
    b->copy_count = 1;
    b->max_copies = AETHERNET_DTN_MAX_COPIES;
    b->created_at_ms = now_ms_dtn();
    b->expires_at_ms = b->created_at_ms + (int64_t)AETHERNET_DTN_BUNDLE_TTL_HOURS * 3600 * 1000;
    return b;
}

void aethernet_dtn_bundle_free(aethernet_dtn_bundle_t *bundle) {
    if (!bundle) return;
    free(bundle->sender_uhid);
    free(bundle->recipient_uhid);
    free(bundle->encrypted_payload);
    free(bundle->sender_geohash);
    free(bundle->recipient_last_geohash);
    free(bundle);
}

bool aethernet_dtn_bundle_is_expired(const aethernet_dtn_bundle_t *bundle) {
    if (!bundle) return true;
    return now_ms_dtn() >= bundle->expires_at_ms;
}

aethernet_dtn_service_t *aethernet_dtn_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_dtn_service_t *svc = (aethernet_dtn_service_t *)calloc(1, sizeof(aethernet_dtn_service_t));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_dtn_service_free(aethernet_dtn_service_t *service) {
    if (!service) return;
    while (service->bundles) {
        bundle_node_t *next = service->bundles->next;
        aethernet_dtn_bundle_free(service->bundles->bundle);
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

void aethernet_dtn_set_reputation(aethernet_dtn_service_t *svc, AetherNetNodeReputationService *rep) {
    if (!svc) return;
    svc->reputation = rep;
}

int aethernet_dtn_create_bundle(aethernet_dtn_service_t *service,
                             const char *recipient_uhid,
                             const uint8_t *encrypted_payload,
                             uint32_t encrypted_payload_len,
                             aethernet_bundle_priority_t priority,
                             const char *recipient_last_geohash) {
    if (!service || !recipient_uhid) return -1;
    aethernet_dtn_bundle_t *bundle = aethernet_dtn_bundle_new();
    if (!bundle) return -1;

    bundle->sender_uhid = str_dup_dtn(service->sender->local_uhid);
    bundle->recipient_uhid = str_dup_dtn(recipient_uhid);
    if (encrypted_payload && encrypted_payload_len) {
        bundle->encrypted_payload = (uint8_t *)malloc(encrypted_payload_len);
        if (!bundle->encrypted_payload) { aethernet_dtn_bundle_free(bundle); return -1; }
        memcpy(bundle->encrypted_payload, encrypted_payload, encrypted_payload_len);
        bundle->encrypted_payload_len = encrypted_payload_len;
    }
    bundle->priority = (uint8_t)priority;
    bundle->sender_geohash = str_dup_dtn(service->sender->local_geohash);
    bundle->recipient_last_geohash = str_dup_dtn(recipient_last_geohash);

    bundle_node_t *node = (bundle_node_t *)calloc(1, sizeof(bundle_node_t));
    if (!node) { aethernet_dtn_bundle_free(bundle); return -1; }
    node->bundle = bundle;
    node->next = service->bundles;
    service->bundles = node;

    if (try_direct_delivery(service, bundle)) {
        bundle->status = AETHERNET_BUNDLE_STATUS_DELIVERED;
    }
    return 0;
}

void aethernet_dtn_handle_packet(aethernet_dtn_service_t *service, const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return;
    // The full handler decodes bundle / custody-ack / delivery-receipt JSON
    // payloads. See header note: hosts wire up a JSON library on receive side
    // for production. The reference impl ships a placeholder so the service
    // compiles cleanly without a JSON dep.

    if (packet->type == AETHERNET_PACKET_TYPE_DTN_BUNDLE) {
        // Bundle addressed to this node — record delivery success for the sender.
        if (service->sender->local_uhid
                && packet->destination_uhid
                && strcmp(packet->destination_uhid, service->sender->local_uhid) == 0) {
            if (service->reputation != NULL) {
                aethernet_reputation_record_delivery_success(service->reputation,
                                                         packet->source_uhid, 0);
            }
        }
    } else if (packet->type == AETHERNET_PACKET_TYPE_DTN_CUSTODY_ACK) {
        // Custody-ack refusal: hosts set payload[0] = 0 when the peer declines
        // custody (accepted == 0). JSON hosts encode this in the body; the
        // reference impl reads the first raw byte so it compiles without a JSON dep.
        if (packet->payload && packet->payload_len >= 1 && packet->payload[0] == 0) {
            if (service->reputation != NULL) {
                aethernet_reputation_record_custody_refusal(service->reputation,
                                                        packet->source_uhid);
            }
        }
    }
}

void aethernet_dtn_run_delivery_scan(aethernet_dtn_service_t *service) {
    if (!service) return;
    int64_t now = now_ms_dtn();
    for (bundle_node_t *n = service->bundles; n; n = n->next) {
        aethernet_dtn_bundle_t *b = n->bundle;
        if (!b) continue;
        if (b->status == AETHERNET_BUNDLE_STATUS_DELIVERED) continue;
        if (b->expires_at_ms <= now) continue;
        if (try_direct_delivery(service, b)) {
            b->status = AETHERNET_BUNDLE_STATUS_DELIVERED;
        }
    }
    (void)active_bundle_count;  // referenced by future replication logic
}

int aethernet_dtn_expire_stale(aethernet_dtn_service_t *service) {
    if (!service) return 0;
    int expired = 0;
    int64_t now = now_ms_dtn();
    for (bundle_node_t *n = service->bundles; n; n = n->next) {
        aethernet_dtn_bundle_t *b = n->bundle;
        if (!b) continue;
        if (b->expires_at_ms <= now && b->status != AETHERNET_BUNDLE_STATUS_EXPIRED) {
            b->status = AETHERNET_BUNDLE_STATUS_EXPIRED;
            expired++;
        }
    }
    return expired;
}
