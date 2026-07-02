// SPDX-License-Identifier: MIT
// SOS broadcast implementation for the Aether mesh.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads
// must wrap the service in their own mutex. The SOS envelope is encoded with
// snprintf and decoded on receive with the vendored cJSON (broadcast_type /
// message / latitude / longitude / geohash), matching the C# reference.

#include "aethernet/sos.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <cjson/cJSON.h>

// ─── Internal state ──────────────────────────────────────

typedef struct seen_node {
    uint8_t id[AETHERNET_PACKET_ID_SIZE];
    int64_t seen_at_ms;
    struct seen_node *next;
} seen_node_t;

typedef struct origin_node {
    int64_t at_ms;
    struct origin_node *next;
} origin_node_t;

typedef struct active_node {
    aethernet_sos_alert_t *alert;
    struct active_node *next;
} active_node_t;

struct aethernet_sos_service {
    aethernet_mesh_sender_t *sender;
    seen_node_t *seen;
    origin_node_t *recent_origins;
    int recent_origin_count;
    active_node_t *active_alerts;

    aethernet_sos_received_cb received_cb;
    void *received_cb_user_data;

    aethernet_sos_acknowledged_cb acknowledged_cb;
    void *acknowledged_cb_user_data;
};

// ─── Helpers ─────────────────────────────────────────────

static int64_t now_ms_sos(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *str_dup_sos(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static void prune_old_origins(aethernet_sos_service_t *svc) {
    int64_t cutoff = now_ms_sos() - (int64_t)3600 * 1000;
    while (svc->recent_origins && svc->recent_origins->at_ms < cutoff) {
        origin_node_t *next = svc->recent_origins->next;
        free(svc->recent_origins);
        svc->recent_origins = next;
        svc->recent_origin_count--;
    }
}

static bool seen_contains(aethernet_sos_service_t *svc, const uint8_t id[AETHERNET_PACKET_ID_SIZE]) {
    for (seen_node_t *n = svc->seen; n; n = n->next) {
        if (memcmp(n->id, id, AETHERNET_PACKET_ID_SIZE) == 0) return true;
    }
    return false;
}

static void seen_add(aethernet_sos_service_t *svc, const uint8_t id[AETHERNET_PACKET_ID_SIZE]) {
    seen_node_t *node = (seen_node_t *)malloc(sizeof(seen_node_t));
    if (!node) return;
    memcpy(node->id, id, AETHERNET_PACKET_ID_SIZE);
    node->seen_at_ms = now_ms_sos();
    node->next = svc->seen;
    svc->seen = node;
}

static void random_uuid(uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    // Reference impl uses rand() seeded once. Hosts that need cryptographic
    // randomness for SOS broadcast IDs supply their own UUID source.
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)(now_ms_sos() & 0x7FFFFFFF)); seeded = 1; }
    for (int i = 0; i < AETHERNET_PACKET_ID_SIZE; i++) {
        out[i] = (uint8_t)(rand() & 0xFF);
    }
    // Set RFC 4122 v4 marker bits
    out[6] = (uint8_t)((out[6] & 0x0F) | 0x40);
    out[8] = (uint8_t)((out[8] & 0x3F) | 0x80);
}

// Format a 16-byte UUID into the canonical lowercase 8-4-4-4-12 dashed form.
// `out` must hold at least 37 bytes (36 chars + null).
static void canonical_uuid(const uint8_t id[AETHERNET_PACKET_ID_SIZE], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        id[0], id[1], id[2], id[3], id[4], id[5], id[6], id[7],
        id[8], id[9], id[10], id[11], id[12], id[13], id[14], id[15]);
}

// Parse a canonical dashed UUID string (36 chars, dashes at 8/13/18/23) into 16 bytes.
// Accepts upper- or lowercase hex. Returns true on success. `s` need not be null-terminated
// beyond the 36 chars, but must be at least 36 bytes readable.
static bool parse_uuid(const char *s, uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    if (!s) return false;
    static const int8_t dash_pos[4] = {8, 13, 18, 23};
    int di = 0;
    int byte = 0;
    int nibble = 0;
    int value = 0;
    for (int i = 0; i < 36; i++) {
        char c = s[i];
        bool is_dash_slot = (di < 4 && i == dash_pos[di]);
        if (is_dash_slot) {
            if (c != '-') return false;
            di++;
            continue;
        }
        int hex;
        if (c >= '0' && c <= '9') hex = c - '0';
        else if (c >= 'a' && c <= 'f') hex = c - 'a' + 10;
        else if (c >= 'A' && c <= 'F') hex = c - 'A' + 10;
        else return false;
        value = (value << 4) | hex;
        if (nibble == 0) {
            nibble = 1;
        } else {
            out[byte++] = (uint8_t)value;
            value = 0;
            nibble = 0;
        }
    }
    return byte == AETHERNET_PACKET_ID_SIZE;
}

// Encode a SosAckPayload as canonical JSON: {"broadcast_id":"<uuid>","received_at_ms":<int>}.
// snake_case keys, field order broadcast_id then received_at_ms, no whitespace, lowercase-dashed
// UUID — the byte-identity gate (fixtures/sos/vectors.json). Mirrors the C# reference, which uses
// System.Text.Json with SnakeCaseLower. We format directly (as encode_sos_payload does) rather than
// via cJSON's printer so the bytes are exactly the canonical form with no printer-inserted spacing.
static bool encode_sos_ack_payload(const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE],
                                   int64_t received_at_ms,
                                   uint8_t **out_payload,
                                   uint32_t *out_len) {
    char id_canonical[37];
    canonical_uuid(broadcast_id, id_canonical);

    // 36-char uuid + int64 (<=20 digits, incl. sign) + fixed key/punctuation. 128 is ample.
    size_t cap = 128;
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"broadcast_id\":\"%s\",\"received_at_ms\":%lld}",
        id_canonical, (long long)received_at_ms);

    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

// Public wrapper over encode_sos_ack_payload — see aethernet/sos.h. Kept thin so the wire path
// (send_sos_ack) and the byte-identity gate exercise identical serialization.
bool aethernet_sos_ack_payload_serialize(const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE],
                                      int64_t received_at_ms,
                                      uint8_t **out_json,
                                      uint32_t *out_len) {
    if (!broadcast_id || !out_json || !out_len) return false;
    return encode_sos_ack_payload(broadcast_id, received_at_ms, out_json, out_len);
}

static bool encode_sos_payload(const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE],
                               const char *broadcast_type,
                               const char *message,
                               double latitude,
                               double longitude,
                               const char *geohash,
                               uint8_t **out_payload,
                               uint32_t *out_len) {
    char id_canonical[37];  // 8-4-4-4-12 + null
    snprintf(id_canonical, sizeof(id_canonical),
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        broadcast_id[0], broadcast_id[1], broadcast_id[2], broadcast_id[3],
        broadcast_id[4], broadcast_id[5], broadcast_id[6], broadcast_id[7],
        broadcast_id[8], broadcast_id[9], broadcast_id[10], broadcast_id[11],
        broadcast_id[12], broadcast_id[13], broadcast_id[14], broadcast_id[15]);

    size_t cap = 256 + (broadcast_type ? strlen(broadcast_type) : 0)
                 + (message ? strlen(message) : 0)
                 + (geohash ? strlen(geohash) : 0);
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"broadcast_id\":\"%s\",\"broadcast_type\":\"%s\",\"message\":%s%s%s,"
        "\"latitude\":%.6f,\"longitude\":%.6f,\"geohash\":%s%s%s}",
        id_canonical,
        broadcast_type ? broadcast_type : "sos",
        message ? "\"" : "null", message ? message : "", message ? "\"" : "",
        latitude, longitude,
        geohash ? "\"" : "null", geohash ? geohash : "", geohash ? "\"" : "");

    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

// ─── Public API ──────────────────────────────────────────

aethernet_sos_alert_t *aethernet_sos_alert_new(void) {
    aethernet_sos_alert_t *a = (aethernet_sos_alert_t *)calloc(1, sizeof(aethernet_sos_alert_t));
    if (!a) return NULL;
    random_uuid(a->id);
    a->received_at_ms = now_ms_sos();
    return a;
}

void aethernet_sos_alert_free(aethernet_sos_alert_t *alert) {
    if (!alert) return;
    free(alert->sender_uhid);
    free(alert->broadcast_type);
    free(alert->message);
    free(alert->geohash);
    for (int i = 0; i < alert->acknowledged_by_count; i++) {
        free(alert->acknowledged_by[i]);
    }
    free(alert->acknowledged_by);
    free(alert);
}

aethernet_sos_service_t *aethernet_sos_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_sos_service_t *svc = (aethernet_sos_service_t *)calloc(1, sizeof(aethernet_sos_service_t));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_sos_service_free(aethernet_sos_service_t *service) {
    if (!service) return;
    while (service->seen) {
        seen_node_t *next = service->seen->next;
        free(service->seen);
        service->seen = next;
    }
    while (service->recent_origins) {
        origin_node_t *next = service->recent_origins->next;
        free(service->recent_origins);
        service->recent_origins = next;
    }
    while (service->active_alerts) {
        active_node_t *next = service->active_alerts->next;
        aethernet_sos_alert_free(service->active_alerts->alert);
        free(service->active_alerts);
        service->active_alerts = next;
    }
    free(service);
}

int aethernet_sos_broadcast(aethernet_sos_service_t *service,
                         const char *broadcast_type,
                         const char *message,
                         double latitude,
                         double longitude,
                         const char *geohash) {
    if (!service || !broadcast_type) return -1;

    prune_old_origins(service);
    if (service->recent_origin_count >= AETHERNET_MAX_SOS_BROADCASTS_PER_HOUR) return 1;

    origin_node_t *origin = (origin_node_t *)malloc(sizeof(origin_node_t));
    if (!origin) return -1;
    origin->at_ms = now_ms_sos();
    origin->next = service->recent_origins;
    service->recent_origins = origin;
    service->recent_origin_count++;

    aethernet_sos_alert_t *alert = aethernet_sos_alert_new();
    if (!alert) return -1;
    alert->sender_uhid = str_dup_sos(service->sender->local_uhid);
    alert->broadcast_type = str_dup_sos(broadcast_type);
    alert->message = str_dup_sos(message);
    alert->latitude = latitude;
    alert->longitude = longitude;
    alert->geohash = str_dup_sos(geohash);

    active_node_t *node = (active_node_t *)calloc(1, sizeof(active_node_t));
    if (!node) { aethernet_sos_alert_free(alert); return -1; }
    node->alert = alert;
    node->next = service->active_alerts;
    service->active_alerts = node;

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_sos_payload(alert->id, broadcast_type, message, latitude, longitude, geohash,
                            &body, &body_len)) {
        return -1;
    }

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return -1; }
    pkt->type = AETHERNET_PACKET_TYPE_SOS_BROADCAST;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    pkt->ttl = AETHERNET_SOS_TTL;
    pkt->priority = AETHERNET_SOS_PRIORITY;
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);
    seen_add(service, pkt->packet_id);

    service->sender->broadcast(service->sender, pkt);
    aethernet_packet_free(pkt);
    return 0;
}

// Send a directed SosAck back to the alert originator so the sender learns their emergency reached
// this device. Best-effort: delivered via the sender's directed send (next-hop unicast), NOT the
// broadcast. Mirrors the C# SendSosAckAsync — guards an empty originator and never acks our own SOS.
static void send_sos_ack(aethernet_sos_service_t *service,
                         const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE],
                         const char *originator_uhid) {
    if (!originator_uhid || originator_uhid[0] == '\0') return;
    if (service->sender->local_uhid
        && strcmp(originator_uhid, service->sender->local_uhid) == 0) return;
    if (!service->sender->send) return;  // host wired no directed send — best-effort no-op

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_sos_ack_payload(broadcast_id, now_ms_sos(), &body, &body_len)) return;

    aethernet_mesh_packet_t *ack = aethernet_packet_new();
    if (!ack) { free(body); return; }
    ack->type = AETHERNET_PACKET_TYPE_SOS_ACK;
    aethernet_packet_set_source_uhid(ack, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(ack, originator_uhid);
    ack->ttl = AETHERNET_SOS_TTL;
    ack->priority = AETHERNET_SOS_PRIORITY;
    aethernet_packet_set_payload(ack, body, body_len);
    free(body);

    service->sender->send(service->sender, ack, originator_uhid);
    aethernet_packet_free(ack);
}

void aethernet_sos_handle_packet(aethernet_sos_service_t *service, aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return;
    if (packet->type != AETHERNET_PACKET_TYPE_SOS_BROADCAST) return;
    if (seen_contains(service, packet->packet_id)) return;
    seen_add(service, packet->packet_id);

    if (packet->source_uhid && service->sender->local_uhid
        && strcmp(packet->source_uhid, service->sender->local_uhid) == 0) return;

    // Surface the alert, decoding the cleartext SOS envelope from the packet
    // payload (broadcast_id / broadcast_type / message / latitude / longitude / geohash) via cJSON.
    uint8_t ack_bid[AETHERNET_PACKET_ID_SIZE];
    bool have_bid = false;  // did we recover the originator's broadcast id? (gate the directed ack)
    aethernet_sos_alert_t *alert = aethernet_sos_alert_new();
    if (alert) {
        alert->sender_uhid = str_dup_sos(packet->source_uhid);
        // Decode the envelope — matches the C# reference, which deserializes the
        // full SOS payload. broadcast_type stays "sos" only if the payload is not
        // the JSON envelope (e.g. a malformed or probe packet).
        alert->broadcast_type = str_dup_sos("sos");
        if (packet->payload != NULL && packet->payload_len > 0) {
            cJSON *env = cJSON_ParseWithLength((const char *)packet->payload,
                                               packet->payload_len);
            if (env != NULL) {
                const cJSON *jbid  = cJSON_GetObjectItemCaseSensitive(env, "broadcast_id");
                const cJSON *jtype = cJSON_GetObjectItemCaseSensitive(env, "broadcast_type");
                const cJSON *jmsg  = cJSON_GetObjectItemCaseSensitive(env, "message");
                const cJSON *jlat  = cJSON_GetObjectItemCaseSensitive(env, "latitude");
                const cJSON *jlon  = cJSON_GetObjectItemCaseSensitive(env, "longitude");
                const cJSON *jgeo  = cJSON_GetObjectItemCaseSensitive(env, "geohash");
                // Adopt the originator's broadcast id (mirrors C# alert.Id = body.BroadcastId) so the
                // directed ack we send carries the id the originator holds in its active-alert set.
                if (cJSON_IsString(jbid) && jbid->valuestring != NULL
                    && parse_uuid(jbid->valuestring, ack_bid)) {
                    memcpy(alert->id, ack_bid, AETHERNET_PACKET_ID_SIZE);
                    have_bid = true;
                }
                if (cJSON_IsString(jtype) && jtype->valuestring != NULL) {
                    free(alert->broadcast_type);
                    alert->broadcast_type = str_dup_sos(jtype->valuestring);
                }
                if (cJSON_IsString(jmsg) && jmsg->valuestring != NULL) {
                    alert->message = str_dup_sos(jmsg->valuestring);
                }
                if (cJSON_IsNumber(jlat)) alert->latitude = jlat->valuedouble;
                if (cJSON_IsNumber(jlon)) alert->longitude = jlon->valuedouble;
                if (cJSON_IsString(jgeo) && jgeo->valuestring != NULL) {
                    alert->geohash = str_dup_sos(jgeo->valuestring);
                }
                cJSON_Delete(env);
            }
        }
        active_node_t *node = (active_node_t *)calloc(1, sizeof(active_node_t));
        if (node) {
            node->alert = alert;
            node->next = service->active_alerts;
            service->active_alerts = node;
            if (service->received_cb) service->received_cb(alert, service->received_cb_user_data);
        } else {
            aethernet_sos_alert_free(alert);
        }
    }

    // Acknowledge back to the originator so the sender learns their SOS reached a device.
    // Directed (unicast) to the SOS source; best-effort. Mirrors the C# HandleAsync ordering
    // (surface → ack → re-flood).
    if (have_bid) {
        send_sos_ack(service, ack_bid, packet->source_uhid);
    }

    if (packet->ttl > 1) {
        packet->ttl--;
        service->sender->broadcast(service->sender, packet);
    }
}

void aethernet_sos_resolve(aethernet_sos_service_t *service, const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE]) {
    if (!service) return;
    active_node_t **prev = &service->active_alerts;
    while (*prev) {
        active_node_t *node = *prev;
        if (node->alert && memcmp(node->alert->id, broadcast_id, AETHERNET_PACKET_ID_SIZE) == 0) {
            *prev = node->next;
            aethernet_sos_alert_free(node->alert);
            free(node);
            return;
        }
        prev = &node->next;
    }
}

// Look up the active alert whose id matches `broadcast_id`, or NULL. Only the ORIGINATOR holds the
// alert it created in its active set, so every other node returns NULL here and no-ops the ack.
static aethernet_sos_alert_t *find_active_alert(aethernet_sos_service_t *service,
                                                const uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE]) {
    for (active_node_t *n = service->active_alerts; n; n = n->next) {
        if (n->alert && memcmp(n->alert->id, broadcast_id, AETHERNET_PACKET_ID_SIZE) == 0) {
            return n->alert;
        }
    }
    return NULL;
}

// Add `responder` to the alert's acknowledged-by set unless already present. Returns the new
// distinct count on a fresh add, or 0 if the responder was already counted (dedup — no change).
static int alert_add_responder(aethernet_sos_alert_t *alert, const char *responder) {
    for (int i = 0; i < alert->acknowledged_by_count; i++) {
        if (strcmp(alert->acknowledged_by[i], responder) == 0) return 0;  // already counted
    }
    char **grown = (char **)realloc(alert->acknowledged_by,
                                    sizeof(char *) * (size_t)(alert->acknowledged_by_count + 1));
    if (!grown) return 0;  // OOM — treat as no-op (best-effort, matches the ack's best-effort nature)
    alert->acknowledged_by = grown;
    char *copy = str_dup_sos(responder);
    if (!copy) return 0;
    alert->acknowledged_by[alert->acknowledged_by_count++] = copy;
    return alert->acknowledged_by_count;
}

int aethernet_sos_handle_ack(aethernet_sos_service_t *service, const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return -1;
    if (packet->type != AETHERNET_PACKET_TYPE_SOS_ACK) return -1;
    if (packet->payload == NULL || packet->payload_len == 0) return 0;

    // Parse the ack payload (broadcast_id / received_at_ms) via the vendored cJSON, mirroring the
    // C# HandleAckAsync which deserializes SosAckPayload. received_at_ms is not retained locally.
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return 0;  // malformed payload — benign no-op (C# swallows JsonException)

    uint8_t broadcast_id[AETHERNET_PACKET_ID_SIZE];
    bool parsed = false;
    const cJSON *jbid = cJSON_GetObjectItemCaseSensitive(body, "broadcast_id");
    if (cJSON_IsString(jbid) && jbid->valuestring != NULL) {
        parsed = parse_uuid(jbid->valuestring, broadcast_id);
    }
    cJSON_Delete(body);
    if (!parsed) return 0;

    // Only the ORIGINATOR holds this alert in its active set; every other node ignores the ack.
    aethernet_sos_alert_t *alert = find_active_alert(service, broadcast_id);
    if (!alert) return 0;

    const char *responder = packet->source_uhid;
    if (!responder || responder[0] == '\0') return 0;
    if (service->sender->local_uhid
        && strcmp(responder, service->sender->local_uhid) == 0) return 0;  // our own ack echoed back

    int total = alert_add_responder(alert, responder);
    if (total == 0) return 0;  // already counted this responder — dedup, no callback

    if (service->acknowledged_cb) {
        service->acknowledged_cb(alert->id, responder, total, service->acknowledged_cb_user_data);
    }
    return 0;
}

void aethernet_sos_set_received_cb(aethernet_sos_service_t *service,
                                aethernet_sos_received_cb cb,
                                void *user_data) {
    if (!service) return;
    service->received_cb = cb;
    service->received_cb_user_data = user_data;
}

void aethernet_sos_set_acknowledged_cb(aethernet_sos_service_t *service,
                                    aethernet_sos_acknowledged_cb cb,
                                    void *user_data) {
    if (!service) return;
    service->acknowledged_cb = cb;
    service->acknowledged_cb_user_data = user_data;
}
