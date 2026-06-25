// SPDX-License-Identifier: MIT
// DTN store-and-forward implementation for the Aether mesh.
//
// NOTE: single-threaded; hosts that pump packets from multiple threads must wrap
// the service in their own mutex. The bundle, custody-ack, and delivery-receipt
// wire is the canonical binary DTN envelope (aethernet/dtn_envelope.h) — byte
// identical across all eight AetherNet SDKs and pinned by fixtures/dtn. Behaviour
// mirrors the Go/C# reference Service: a third-party bundle is accepted into
// custody, hop-counted, stored, and acked; a bundle for the local node is
// delivered (callback + delivery-receipt); the delivery scan re-attempts direct
// delivery then epidemic-replicates to peers chosen by GeohashEpidemicStrategy.

#include "aethernet/dtn.h"
#include "aethernet/dtn_envelope.h"
#include "aethernet/constants.h"
#include "aethernet/security.h"   // aethernet_random_bytes
#include "aethernet_reputation.h"

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
    aethernet_dtn_bundle_received_cb on_bundle_received;     // optional
    void *on_bundle_received_user_data;                       // opaque, caller-owned
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

// Count of bundles still held in an active state (Pending or InCustody) and not
// yet expired — the GetActiveCount() the at-capacity custody check keys off.
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

// Prepend `bundle` to the store; the service takes ownership.
static void store_bundle(aethernet_dtn_service_t *svc, aethernet_dtn_bundle_t *bundle) {
    bundle_node_t *node = (bundle_node_t *)calloc(1, sizeof(bundle_node_t));
    if (!node) { aethernet_dtn_bundle_free(bundle); return; }
    node->bundle = bundle;
    node->next = svc->bundles;
    svc->bundles = node;
}

static void save_custody(aethernet_dtn_service_t *svc, const uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE],
                         const char *from_uhid, const char *to_uhid, bool accepted) {
    custody_node_t *node = (custody_node_t *)calloc(1, sizeof(custody_node_t));
    if (!node) return;
    memcpy(node->bundle_id, bundle_id, AETHERNET_PACKET_ID_SIZE);
    node->from_uhid = str_dup_dtn(from_uhid);
    node->to_uhid = str_dup_dtn(to_uhid);
    node->accepted = accepted;
    node->transferred_at_ms = now_ms_dtn();
    node->next = svc->custody_records;
    svc->custody_records = node;
}

// Number of custody records held for a bundle id — the total_custody_transfers
// reported in a delivery receipt.
static int count_custody(aethernet_dtn_service_t *svc, const uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE]) {
    int n = 0;
    for (custody_node_t *c = svc->custody_records; c; c = c->next) {
        if (memcmp(c->bundle_id, bundle_id, AETHERNET_PACKET_ID_SIZE) == 0) n++;
    }
    return n;
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
    if (aethernet_dtn_bundle_encode(bundle, &body, &body_len)) {
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

// Emit a custody ack (binary envelope) back to the peer that offered the bundle.
static void send_custody_ack(aethernet_dtn_service_t *svc, const uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE],
                             const char *to_uhid, bool accepted) {
    if (!to_uhid || !*to_uhid) return;
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!aethernet_dtn_custody_ack_encode(bundle_id, accepted, &body, &body_len)) return;
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (pkt) {
        pkt->type = AETHERNET_PACKET_TYPE_DTN_CUSTODY_ACK;
        aethernet_packet_set_source_uhid(pkt, svc->sender->local_uhid);
        aethernet_packet_set_destination_uhid(pkt, to_uhid);
        pkt->ttl = AETHERNET_DEFAULT_TTL;
        aethernet_packet_set_payload(pkt, body, body_len);
        svc->sender->send(svc->sender, pkt, to_uhid);
        aethernet_packet_free(pkt);
    }
    free(body);
}

// Emit a delivery receipt back to the original sender once a bundle is delivered
// to the local node. Skipped when we are the original sender.
static void send_delivery_receipt(aethernet_dtn_service_t *svc, const aethernet_dtn_bundle_t *bundle) {
    if (!bundle->sender_uhid || !*bundle->sender_uhid) return;
    if (svc->sender->local_uhid && strcmp(bundle->sender_uhid, svc->sender->local_uhid) == 0) return;
    int custody = count_custody(svc, bundle->id);
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!aethernet_dtn_delivery_receipt_encode(bundle->id, bundle->recipient_uhid, bundle->hop_count,
                                               custody, now_ms_dtn(), &body, &body_len)) {
        return;
    }
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (pkt) {
        pkt->type = AETHERNET_PACKET_TYPE_DTN_DELIVERY_RECEIPT;
        aethernet_packet_set_source_uhid(pkt, svc->sender->local_uhid);
        aethernet_packet_set_destination_uhid(pkt, bundle->sender_uhid);
        pkt->ttl = AETHERNET_DEFAULT_TTL;
        aethernet_packet_set_payload(pkt, body, body_len);
        svc->sender->send(svc->sender, pkt, bundle->sender_uhid);
        aethernet_packet_free(pkt);
    }
    free(body);
}

// ─── GeohashEpidemicStrategy (mirrors rust/src/dtn/strategy.rs) ───

// Byte-length of the common prefix of two geohashes; 0 if `a` is NULL/empty.
static int shared_prefix_dtn(const char *a, const char *b) {
    if (!a || !*a || !b) return 0;
    int i = 0;
    while (a[i] && b[i] && a[i] == b[i]) i++;
    return i;
}

// Select up to `slots` replication targets for `bundle` among `peers`, writing
// owned uhid copies to out_targets[] (caller frees each). Returns the count.
// Matches GeohashEpidemicStrategy.select_targets: SOS fans out to the first
// eligible carriers; otherwise prefer peers at least as close to the recipient
// as we are (longer shared geohash prefix), ties broken by reliability.
static int select_replication_targets(const aethernet_dtn_bundle_t *bundle,
                                      const aethernet_peer_info_t *peers, int peer_count,
                                      const char *local_geohash, int slots,
                                      char **out_targets) {
    if (slots <= 0 || peer_count <= 0) return 0;

    int *elig = (int *)malloc(sizeof(int) * (size_t)peer_count);
    if (!elig) return 0;
    int elig_n = 0;
    for (int i = 0; i < peer_count; i++) {
        const aethernet_peer_info_t *p = &peers[i];
        bool not_sender = !bundle->sender_uhid || !p->uhid || strcmp(p->uhid, bundle->sender_uhid) != 0;
        if (p->uhid && *p->uhid && not_sender && !p->is_blocked
            && (p->capabilities & AETHERNET_CAP_DTN_CARRIER) != 0) {
            elig[elig_n++] = i;
        }
    }
    if (elig_n == 0) { free(elig); return 0; }

    int out_n = 0;

    if (bundle->priority == AETHERNET_BUNDLE_PRIORITY_SOS) {
        for (int k = 0; k < elig_n && out_n < slots; k++) {
            out_targets[out_n++] = str_dup_dtn(peers[elig[k]].uhid);
        }
        free(elig);
        return out_n;
    }

    bool *used = (bool *)calloc((size_t)elig_n, sizeof(bool));
    if (!used) { free(elig); return 0; }

    if (bundle->recipient_last_geohash && *bundle->recipient_last_geohash) {
        const char *rg = bundle->recipient_last_geohash;
        int local_prox = shared_prefix_dtn(local_geohash, rg);
        for (int pick = 0; pick < slots; pick++) {
            int best = -1, best_prox = -1, best_rel = 0;
            for (int k = 0; k < elig_n; k++) {
                if (used[k]) continue;
                const aethernet_peer_info_t *p = &peers[elig[k]];
                int prox = shared_prefix_dtn(p->geohash, rg);
                if (prox < local_prox) continue;  // not at least as close as us
                if (best < 0 || prox > best_prox
                    || (prox == best_prox && p->reliability_score > best_rel)) {
                    best = k; best_prox = prox; best_rel = p->reliability_score;
                }
            }
            if (best < 0) break;
            used[best] = true;
            out_targets[out_n++] = str_dup_dtn(peers[elig[best]].uhid);
        }
    } else {
        for (int pick = 0; pick < slots; pick++) {
            int best = -1, best_rel = 0;
            for (int k = 0; k < elig_n; k++) {
                if (used[k]) continue;
                if (best < 0 || peers[elig[k]].reliability_score > best_rel) {
                    best = k; best_rel = peers[elig[k]].reliability_score;
                }
            }
            if (best < 0) break;
            used[best] = true;
            out_targets[out_n++] = str_dup_dtn(peers[elig[best]].uhid);
        }
    }

    free(used);
    free(elig);
    return out_n;
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

void aethernet_dtn_set_bundle_received_callback(
    aethernet_dtn_service_t *service,
    aethernet_dtn_bundle_received_cb cb,
    void *user_data) {
    if (!service) return;
    service->on_bundle_received = cb;
    service->on_bundle_received_user_data = user_data;
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

    // Fresh random bundle id (RFC-4122 bytes) so every bundle is uniquely
    // addressable for custody dedup and relay — mirrors uuid.NewString() in the
    // Go/C# reference CreateBundle.
    aethernet_random_bytes(bundle->id, AETHERNET_PACKET_ID_SIZE);

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

    store_bundle(service, bundle);

    if (try_direct_delivery(service, bundle)) {
        bundle->status = AETHERNET_BUNDLE_STATUS_DELIVERED;
    }
    return 0;
}

// Handle an inbound DTN bundle: deliver to the local node, or accept custody and
// relay later (unless we are at capacity, in which case we refuse). Mirrors
// Service.handleBundle in go/dtn/service.go.
static void handle_bundle(aethernet_dtn_service_t *service, const aethernet_mesh_packet_t *packet) {
    aethernet_dtn_bundle_t *bundle = aethernet_dtn_bundle_decode(packet->payload, packet->payload_len);
    if (!bundle) return;  // malformed / wrong-version envelope — drop

    // Final recipient is the local node → deliver.
    if (service->sender->local_uhid && bundle->recipient_uhid
            && strcmp(bundle->recipient_uhid, service->sender->local_uhid) == 0) {
        bundle->status = AETHERNET_BUNDLE_STATUS_DELIVERED;
        if (service->reputation != NULL) {
            aethernet_reputation_record_delivery_success(service->reputation, packet->source_uhid, 0);
        }
        if (service->on_bundle_received != NULL) {
            aethernet_dtn_bundle_received_event_t evt;
            memcpy(evt.bundle_id, bundle->id, AETHERNET_PACKET_ID_SIZE);
            evt.sender_uhid = bundle->sender_uhid;
            evt.recipient_uhid = bundle->recipient_uhid;
            evt.encrypted_payload = bundle->encrypted_payload;
            evt.encrypted_payload_len = bundle->encrypted_payload_len;
            evt.priority = bundle->priority;
            evt.hop_count = bundle->hop_count;
            evt.received_at_ms = now_ms_dtn();
            service->on_bundle_received(&evt, service->on_bundle_received_user_data);
        }
        send_delivery_receipt(service, bundle);
        aethernet_dtn_bundle_free(bundle);
        return;
    }

    // In transit. Refuse custody if we are already at capacity.
    if (active_bundle_count(service) >= AETHERNET_DTN_MAX_BUNDLES_PER_NODE) {
        send_custody_ack(service, bundle->id, packet->source_uhid, false);
        aethernet_dtn_bundle_free(bundle);
        return;
    }

    // Accept custody: hold the bundle, hop-count it, record + ack the transfer.
    bundle->status = AETHERNET_BUNDLE_STATUS_IN_CUSTODY;
    bundle->hop_count += 1;
    save_custody(service, bundle->id, packet->source_uhid, service->sender->local_uhid, true);
    send_custody_ack(service, bundle->id, packet->source_uhid, true);
    store_bundle(service, bundle);  // service takes ownership
}

static void handle_custody_ack(aethernet_dtn_service_t *service, const aethernet_mesh_packet_t *packet) {
    uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE];
    bool accepted = false;
    if (!aethernet_dtn_custody_ack_decode(packet->payload, packet->payload_len, bundle_id, &accepted)) {
        return;
    }
    if (!accepted) {
        if (service->reputation != NULL) {
            aethernet_reputation_record_custody_refusal(service->reputation, packet->source_uhid);
        }
        return;
    }
    // Peer accepted a copy → one more confirmed copy of our bundle in the mesh.
    bundle_node_t *n = find_bundle_node(service, bundle_id);
    if (n && n->bundle) n->bundle->copy_count += 1;
}

static void handle_delivery_receipt(aethernet_dtn_service_t *service, const aethernet_mesh_packet_t *packet) {
    uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE];
    char *recipient_uhid = NULL;
    int32_t total_hops = 0, total_custody_transfers = 0;
    int64_t delivered_at_ms = 0;
    if (!aethernet_dtn_delivery_receipt_decode(packet->payload, packet->payload_len, bundle_id,
                                               &recipient_uhid, &total_hops,
                                               &total_custody_transfers, &delivered_at_ms)) {
        return;
    }
    free(recipient_uhid);  // C surface has no OnBundleDelivered callback
    bundle_node_t *n = find_bundle_node(service, bundle_id);
    if (n && n->bundle) n->bundle->status = AETHERNET_BUNDLE_STATUS_DELIVERED;
}

void aethernet_dtn_handle_packet(aethernet_dtn_service_t *service, const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return;
    switch (packet->type) {
        case AETHERNET_PACKET_TYPE_DTN_BUNDLE:
            handle_bundle(service, packet);
            break;
        case AETHERNET_PACKET_TYPE_DTN_CUSTODY_ACK:
            handle_custody_ack(service, packet);
            break;
        case AETHERNET_PACKET_TYPE_DTN_DELIVERY_RECEIPT:
            handle_delivery_receipt(service, packet);
            break;
        default:
            break;
    }
}

void aethernet_dtn_run_delivery_scan(aethernet_dtn_service_t *service) {
    if (!service) return;
    int64_t now = now_ms_dtn();

    // Snapshot connected peers once for this pass (host may not support it).
    aethernet_peer_info_t peers[32];
    int peer_count = 0;
    if (service->sender->connected_peers) {
        peer_count = service->sender->connected_peers(service->sender, peers, 32);
        if (peer_count < 0) peer_count = 0;
        if (peer_count > 32) peer_count = 32;
    }
    const char *local_geohash = service->sender->local_geohash;

    for (bundle_node_t *n = service->bundles; n; n = n->next) {
        aethernet_dtn_bundle_t *b = n->bundle;
        if (!b) continue;
        if (b->status == AETHERNET_BUNDLE_STATUS_DELIVERED) continue;
        if (b->expires_at_ms <= now) continue;

        // First try to hand the bundle straight to its recipient.
        if (try_direct_delivery(service, b)) {
            b->status = AETHERNET_BUNDLE_STATUS_DELIVERED;
            continue;
        }

        // Otherwise epidemic-replicate to strategy-chosen carriers.
        if (peer_count == 0 || b->copy_count >= b->max_copies) continue;
        int slots = b->max_copies - b->copy_count;
        char *targets[32];
        int nt = select_replication_targets(b, peers, peer_count, local_geohash, slots, targets);
        for (int i = 0; i < nt; i++) {
            if (b->copy_count < b->max_copies) {
                aethernet_mesh_packet_t *pkt = build_bundle_packet(service, b);
                if (pkt) {
                    if (service->sender->send(service->sender, pkt, targets[i])) {
                        b->copy_count += 1;
                    }
                    aethernet_packet_free(pkt);
                }
            }
            free(targets[i]);
        }
    }
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
