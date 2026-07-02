// SPDX-License-Identifier: MIT
// Heartbeat liveness beacons for the Aether mesh (PacketType 10).
//
// Single-threaded reference impl; hosts pumping packets from multiple threads
// must wrap the service in their own mutex. The HeartbeatPayload is encoded with
// snprintf (byte-identical to the C# System.Text.Json SnakeCaseLower output —
// {"sequence":N,"sent_at_ms":M}, no whitespace) and decoded on receive with the
// vendored cJSON, matching the SOS approach in sos.c. Heartbeats are single-hop
// (TTL 1): the receiver refreshes per-peer liveness but never re-broadcasts.

#include "aethernet/heartbeat.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <cjson/cJSON.h>

// ─── Internal state ──────────────────────────────────────

// Per-peer liveness node, keyed by source UHID. Upserted on each received heartbeat, mirroring the
// C# ConcurrentDictionary<string, PeerLiveness>.
typedef struct peer_node {
    aethernet_peer_liveness_t liveness;  // owns liveness.uhid
    struct peer_node *next;
} peer_node_t;

struct aethernet_heartbeat_service {
    aethernet_mesh_sender_t *sender;
    int32_t sequence;          // outgoing monotonic sequence; ++ per send (first beat is 1)
    peer_node_t *peers;        // linked list of known peers (upsert by uhid)

    aethernet_heartbeat_peer_seen_cb peer_seen_cb;
    void *peer_seen_cb_user_data;
};

// ─── Helpers ─────────────────────────────────────────────

static int64_t now_ms_hb(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *str_dup_hb(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// Locate the peer node for `uhid`, or NULL.
static peer_node_t *peer_find(aethernet_heartbeat_service_t *svc, const char *uhid) {
    for (peer_node_t *n = svc->peers; n; n = n->next) {
        if (n->liveness.uhid && strcmp(n->liveness.uhid, uhid) == 0) return n;
    }
    return NULL;
}

// Encode a HeartbeatPayload as canonical JSON: {"sequence":<int>,"sent_at_ms":<int>}. snake_case
// keys, field order sequence then sent_at_ms, no whitespace — the byte-identity gate
// (fixtures/heartbeat/vectors.json). Mirrors the C# reference, which uses System.Text.Json with
// SnakeCaseLower. We format directly (as sos.c's encode_sos_ack_payload does) rather than via
// cJSON's printer so the bytes are exactly the canonical form with no printer-inserted spacing.
static bool encode_heartbeat_payload(int32_t sequence,
                                     int64_t sent_at_ms,
                                     uint8_t **out_payload,
                                     uint32_t *out_len) {
    // int32 (<=11 chars incl. sign) + int64 (<=20 chars incl. sign) + fixed key/punctuation.
    // 64 is ample; sos.c uses 128 for the larger ack payload.
    size_t cap = 64;
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"sequence\":%ld,\"sent_at_ms\":%lld}",
        (long)sequence, (long long)sent_at_ms);

    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

// Public wrapper over encode_heartbeat_payload — see aethernet/heartbeat.h. Kept thin so the wire
// path (aethernet_heartbeat_send) and the byte-identity gate exercise identical serialization.
bool aethernet_heartbeat_payload_serialize(int32_t sequence,
                                           int64_t sent_at_ms,
                                           uint8_t **out_json,
                                           uint32_t *out_len) {
    if (!out_json || !out_len) return false;
    return encode_heartbeat_payload(sequence, sent_at_ms, out_json, out_len);
}

// Deep-copy the service's peer list (filtered by `predicate`, or all if NULL) into a freshly
// allocated array. Shared by get_known_peers / get_live_peers. Returns the count (>=0), or -1 on
// allocation failure. cutoff_ms is only consulted when live_only is true.
static int snapshot_peers(const aethernet_heartbeat_service_t *svc,
                          bool live_only,
                          int64_t cutoff_ms,
                          aethernet_peer_liveness_t **out_peers,
                          int *out_count) {
    int count = 0;
    for (peer_node_t *n = svc->peers; n; n = n->next) {
        if (!live_only || n->liveness.received_at_ms >= cutoff_ms) count++;
    }
    if (count == 0) {
        *out_peers = NULL;
        *out_count = 0;
        return 0;
    }

    aethernet_peer_liveness_t *arr =
        (aethernet_peer_liveness_t *)calloc((size_t)count, sizeof(*arr));
    if (!arr) return -1;

    int i = 0;
    for (peer_node_t *n = svc->peers; n && i < count; n = n->next) {
        if (live_only && n->liveness.received_at_ms < cutoff_ms) continue;
        arr[i] = n->liveness;                       // shallow copy of scalars + uhid pointer
        arr[i].uhid = str_dup_hb(n->liveness.uhid); // then deep-copy uhid so the array owns it
        i++;
    }
    *out_peers = arr;
    *out_count = count;
    return count;
}

// ─── Public API ──────────────────────────────────────────

aethernet_heartbeat_service_t *aethernet_heartbeat_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_heartbeat_service_t *svc =
        (aethernet_heartbeat_service_t *)calloc(1, sizeof(aethernet_heartbeat_service_t));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_heartbeat_service_free(aethernet_heartbeat_service_t *service) {
    if (!service) return;
    while (service->peers) {
        peer_node_t *next = service->peers->next;
        free(service->peers->liveness.uhid);
        free(service->peers);
        service->peers = next;
    }
    free(service);
}

int aethernet_heartbeat_send(aethernet_heartbeat_service_t *service) {
    if (!service) return -1;

    int32_t seq = ++service->sequence;  // first beat is 1, mirroring Interlocked.Increment from 0

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_heartbeat_payload(seq, now_ms_hb(), &body, &body_len)) return -1;

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return -1; }
    pkt->type = AETHERNET_PACKET_TYPE_HEARTBEAT;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, "*");
    pkt->ttl = 1;  // heartbeats are single-hop: liveness of DIRECT neighbours only
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);

    int delivered = service->sender->broadcast(service->sender, pkt);
    aethernet_packet_free(pkt);
    return delivered;
}

bool aethernet_heartbeat_handle_packet(aethernet_heartbeat_service_t *service,
                                       const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_HEARTBEAT) return false;

    // Ignore our own heartbeat echoed back.
    if (packet->source_uhid && service->sender->local_uhid
        && strcmp(packet->source_uhid, service->sender->local_uhid) == 0) return false;
    if (!packet->source_uhid) return false;  // need a key for the liveness record

    if (packet->payload == NULL || packet->payload_len == 0) return false;

    // Decode the payload (sequence / sent_at_ms) via the vendored cJSON, mirroring the C#
    // HandleAsync which deserializes HeartbeatPayload. Malformed → benign drop (C# swallows
    // JsonException and returns false).
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;

    const cJSON *jseq = cJSON_GetObjectItemCaseSensitive(body, "sequence");
    const cJSON *jsent = cJSON_GetObjectItemCaseSensitive(body, "sent_at_ms");
    int32_t sequence = cJSON_IsNumber(jseq) ? (int32_t)jseq->valuedouble : 0;
    int64_t sent_at_ms = cJSON_IsNumber(jsent) ? (int64_t)jsent->valuedouble : 0;
    cJSON_Delete(body);

    int64_t received_at_ms = now_ms_hb();

    // Upsert the sender's liveness record, keyed by source UHID (mirrors the C# dictionary
    // assignment _peers[SourceUhid] = liveness).
    peer_node_t *node = peer_find(service, packet->source_uhid);
    if (node == NULL) {
        node = (peer_node_t *)calloc(1, sizeof(peer_node_t));
        if (!node) return false;
        node->liveness.uhid = str_dup_hb(packet->source_uhid);
        if (!node->liveness.uhid) { free(node); return false; }
        node->next = service->peers;
        service->peers = node;
    }
    node->liveness.last_sequence = sequence;
    node->liveness.last_sent_at_ms = sent_at_ms;
    node->liveness.received_at_ms = received_at_ms;

    if (service->peer_seen_cb) {
        service->peer_seen_cb(&node->liveness, service->peer_seen_cb_user_data);
    }
    return true;
}

int aethernet_heartbeat_get_known_peers(const aethernet_heartbeat_service_t *service,
                                        aethernet_peer_liveness_t **out_peers,
                                        int *out_count) {
    if (!service || !out_peers || !out_count) return -1;
    return snapshot_peers(service, false, 0, out_peers, out_count);
}

int aethernet_heartbeat_get_live_peers(const aethernet_heartbeat_service_t *service,
                                       int within_seconds,
                                       aethernet_peer_liveness_t **out_peers,
                                       int *out_count) {
    if (!service || !out_peers || !out_count) return -1;
    // cutoff = now - within_seconds*1000. A negative within_seconds pushes the cutoff into the
    // future, excluding even a just-seen peer (matches the C# GetLivePeers(-1) proof).
    int64_t cutoff = now_ms_hb() - (int64_t)within_seconds * 1000;
    return snapshot_peers(service, true, cutoff, out_peers, out_count);
}

void aethernet_peer_liveness_list_free(aethernet_peer_liveness_t *peers, int count) {
    if (!peers) return;
    for (int i = 0; i < count; i++) free(peers[i].uhid);
    free(peers);
}

void aethernet_heartbeat_set_peer_seen_cb(aethernet_heartbeat_service_t *service,
                                          aethernet_heartbeat_peer_seen_cb cb,
                                          void *user_data) {
    if (!service) return;
    service->peer_seen_cb = cb;
    service->peer_seen_cb_user_data = user_data;
}
