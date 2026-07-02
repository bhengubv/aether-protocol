// SPDX-License-Identifier: MIT
// Aether presence WIRE binding (PacketType PresenceBeacon 21 / PresenceQuery 22). See aethernet/presence.h.
//
// Thin transport: broadcast a locally-built beacon or query (source -> "*") and surface inbound
// beacons/queries via callbacks. Mirrors the green C# PresenceService.
//
// The payloads are encoded with snprintf (byte-identical to the C# System.Text.Json output — beacon
// field order erid, geohash, capabilities, status, sent_at_ms; query field order query_id, geohash;
// strings interpolated verbatim, bare-int capabilities/status, sent_at_ms bare int64, lowercase-dashed
// UUID) and decoded on receive with the vendored cJSON, matching the SOS / channels / prekey approach.
// Byte-identity gate: fixtures/presence/vectors.json.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
// in their own mutex.

#include "aethernet/presence.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <cjson/cJSON.h>

// ─── Internal state ──────────────────────────────────────

struct aethernet_presence_service {
    aethernet_mesh_sender_t *sender;

    aethernet_presence_beacon_received_cb beacon_received_cb;
    void                                 *beacon_received_cb_user_data;

    aethernet_presence_query_received_cb  query_received_cb;
    void                                 *query_received_cb_user_data;
};

// ─── Helpers ─────────────────────────────────────────────

static char *dup_cstr_pr(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static int64_t now_ms_pr(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static void random_uuid_pr(uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    // Reference impl uses rand() seeded once (mirrors sos.c / channels.c / prekey.c random_uuid).
    // Hosts needing cryptographic randomness for query ids supply their own UUID source.
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)(now_ms_pr() & 0x7FFFFFFF)); seeded = 1; }
    for (int i = 0; i < AETHERNET_PACKET_ID_SIZE; i++) {
        out[i] = (uint8_t)(rand() & 0xFF);
    }
    out[6] = (uint8_t)((out[6] & 0x0F) | 0x40);  // RFC 4122 v4
    out[8] = (uint8_t)((out[8] & 0x3F) | 0x80);
}

// Format a 16-byte UUID into the canonical lowercase 8-4-4-4-12 dashed form. `out` >= 37 bytes.
static void canonical_uuid_pr(const uint8_t id[AETHERNET_PACKET_ID_SIZE], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        id[0], id[1], id[2], id[3], id[4], id[5], id[6], id[7],
        id[8], id[9], id[10], id[11], id[12], id[13], id[14], id[15]);
}

// Parse a canonical dashed UUID string (36 chars, dashes at 8/13/18/23) into 16 bytes. Accepts upper-
// or lowercase hex. Returns true on success. Mirrors parse_uuid in prekey.c.
static bool parse_uuid_pr(const char *s, uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    if (!s) return false;
    static const int8_t dash_pos[4] = {8, 13, 18, 23};
    int di = 0, byte = 0, nibble = 0, value = 0;
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
        if (nibble == 0) { nibble = 1; }
        else { out[byte++] = (uint8_t)value; value = 0; nibble = 0; }
    }
    return byte == AETHERNET_PACKET_ID_SIZE;
}

// ─── Serialization ───────────────────────────────────────

// Encode a PresenceBeacon payload as canonical JSON. Field order erid, geohash, capabilities, status,
// sent_at_ms; no whitespace; bare-int capabilities/status; sent_at_ms bare int64. The erid/geohash
// strings are interpolated verbatim (as sos.c / channels.c / prekey.c do) so ASCII ERIDs/geohashes
// reproduce byte-for-byte; a NULL geohash serializes as "". Byte-identity gate:
// fixtures/presence/vectors.json.
static bool encode_beacon_payload(const aethernet_presence_beacon_t *b,
                                  uint8_t **out_payload,
                                  uint32_t *out_len) {
    const char *erid    = b->erid    ? b->erid    : "";
    const char *geohash = b->geohash ? b->geohash : "";

    // Fixed frame `{"erid":"","geohash":"","capabilities":,"status":,"sent_at_ms":}` is ~62 bytes;
    // add the two variable strings, room for two int32 (<=11 each) + one int64 (<=20), + margin. 128
    // over the fixed frame gives comfortable headroom (mirrors space_breadcrumb.c's generous sizing).
    size_t cap = 128 + strlen(erid) + strlen(geohash);
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"erid\":\"%s\",\"geohash\":\"%s\","
        "\"capabilities\":%d,\"status\":%d,\"sent_at_ms\":%lld}",
        erid, geohash,
        (int)b->capabilities, (int)b->status, (long long)b->sent_at_ms);
    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

bool aethernet_presence_beacon_payload_serialize(const aethernet_presence_beacon_t *beacon,
                                                 uint8_t **out_json,
                                                 uint32_t *out_len) {
    if (!beacon || !out_json || !out_len) return false;
    return encode_beacon_payload(beacon, out_json, out_len);
}

// Encode a PresenceQuery payload {"query_id":"<uuid>","geohash":"<geohash>"} as canonical JSON. Field
// order query_id, geohash; no whitespace; lowercase-dashed UUID; geohash interpolated verbatim (NULL →
// ""). Byte-identity gate: fixtures/presence/vectors.json.
static bool encode_query_payload(const uint8_t query_id[AETHERNET_PACKET_ID_SIZE],
                                 const char *geohash,
                                 uint8_t **out_payload,
                                 uint32_t *out_len) {
    const char *gh = geohash ? geohash : "";
    char id_canonical[37];
    canonical_uuid_pr(query_id, id_canonical);

    // Fixed frame `{"query_id":"","geohash":""}` (28) + 36-char uuid + geohash + NUL. 96 over the
    // geohash length gives comfortable headroom over the 64-byte fixed frame.
    size_t cap = 96 + strlen(gh);
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"query_id\":\"%s\",\"geohash\":\"%s\"}",
        id_canonical, gh);
    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

bool aethernet_presence_query_payload_serialize(const uint8_t query_id[AETHERNET_PACKET_ID_SIZE],
                                                const char *geohash,
                                                uint8_t **out_json,
                                                uint32_t *out_len) {
    if (!query_id || !out_json || !out_len) return false;
    return encode_query_payload(query_id, geohash, out_json, out_len);
}

// ─── Broadcast ───────────────────────────────────────────

// Build and broadcast a presence packet of `type` carrying `body` (source local UHID, dest "*", default
// TTL) via sender->broadcast. Writes the fan-out count to *out_count (may be NULL). Returns false if the
// host wired no broadcast.
static bool broadcast_presence_packet(aethernet_presence_service_t *service,
                                      aethernet_packet_type_t type,
                                      const uint8_t *body,
                                      uint32_t body_len,
                                      int *out_count) {
    if (!service->sender->broadcast) return false;  // host wired no broadcast — cannot deliver

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) return false;
    pkt->type = (uint8_t)type;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, "*");
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);

    int delivered = service->sender->broadcast(service->sender, pkt);
    aethernet_packet_free(pkt);
    if (out_count) *out_count = delivered;
    return true;
}

// ─── Handlers ────────────────────────────────────────────

// Handle an inbound PresenceBeacon: decode the payload, and if it carries a non-empty erid, fire the
// beacon-received callback. Mirrors the C# PresenceBeacon case of HandleAsync.
static bool handle_beacon(aethernet_presence_service_t *service,
                          const aethernet_mesh_packet_t *packet) {
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;  // malformed → benign drop (C# swallows JsonException)

    const cJSON *jerid = cJSON_GetObjectItemCaseSensitive(body, "erid");
    const cJSON *jgeo  = cJSON_GetObjectItemCaseSensitive(body, "geohash");
    const cJSON *jcap  = cJSON_GetObjectItemCaseSensitive(body, "capabilities");
    const cJSON *jstat = cJSON_GetObjectItemCaseSensitive(body, "status");
    const cJSON *jsent = cJSON_GetObjectItemCaseSensitive(body, "sent_at_ms");

    // erid must be a present, non-empty string (mirrors the C# string.IsNullOrEmpty(beacon.Erid)
    // guard). Anything else → malformed → benign drop.
    if (!cJSON_IsString(jerid) || jerid->valuestring == NULL || jerid->valuestring[0] == '\0') {
        cJSON_Delete(body);
        return false;
    }

    // Copy erid + geohash (and the packet source) into owned buffers BEFORE cJSON_Delete — the cJSON
    // valuestring pointers are freed by cJSON_Delete(body), so using them afterwards would be a
    // use-after-free (crashes the Mac allocator even though Windows offline may pass by luck).
    aethernet_presence_beacon_t beacon;
    memset(&beacon, 0, sizeof(beacon));
    bool ok = true;

    char *erid_owned = dup_cstr_pr(jerid->valuestring);
    if (!erid_owned) ok = false;

    char *geo_owned = NULL;
    if (ok) {
        const char *geo = (cJSON_IsString(jgeo) && jgeo->valuestring) ? jgeo->valuestring : "";
        geo_owned = dup_cstr_pr(geo);
        if (!geo_owned) ok = false;
    }

    char *from_owned = NULL;
    if (ok) {
        from_owned = dup_cstr_pr(packet->source_uhid ? packet->source_uhid : "");
        if (!from_owned) ok = false;
    }

    if (ok) {
        beacon.erid    = erid_owned;
        beacon.geohash = geo_owned;
        // Numbers use valuedouble to preserve the full int64 range (cJSON's valueint is a 32-bit int,
        // too narrow for a Unix-ms timestamp). Missing/non-number fields default to 0, matching the C#
        // deserializer's default(int)/default(long) behaviour.
        beacon.capabilities = cJSON_IsNumber(jcap)  ? (int32_t)jcap->valuedouble  : 0;
        beacon.status       = cJSON_IsNumber(jstat) ? (int32_t)jstat->valuedouble : 0;
        beacon.sent_at_ms   = cJSON_IsNumber(jsent) ? (int64_t)jsent->valuedouble : 0;
    }

    cJSON_Delete(body);

    if (!ok) {
        free(erid_owned);
        free(geo_owned);
        free(from_owned);
        return false;
    }

    if (service->beacon_received_cb) {
        aethernet_presence_beacon_received_t evt;
        evt.beacon = &beacon;
        evt.from_uhid = from_owned;
        service->beacon_received_cb(&evt, service->beacon_received_cb_user_data);
    }

    free(erid_owned);
    free(geo_owned);
    free(from_owned);
    return true;
}

// Handle an inbound PresenceQuery: decode the payload and fire the query-received callback. The C#
// reference accepts any well-formed query payload (only null on a JsonException → false); a missing
// query_id deserializes to Guid.Empty. We mirror that: parse succeeds → callback fires, echoing the
// query id (all-zero if absent/unparseable) and the geohash. Mirrors the C# PresenceQuery case.
static bool handle_query(aethernet_presence_service_t *service,
                         const aethernet_mesh_packet_t *packet) {
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;  // malformed → benign drop

    const cJSON *jqid = cJSON_GetObjectItemCaseSensitive(body, "query_id");
    const cJSON *jgeo = cJSON_GetObjectItemCaseSensitive(body, "geohash");

    // query_id: parse the canonical dashed UUID if present; otherwise Guid.Empty (all-zero), matching
    // the C# default(Guid) when the field is missing/unparseable.
    uint8_t query_id[AETHERNET_PACKET_ID_SIZE];
    memset(query_id, 0, sizeof(query_id));
    if (cJSON_IsString(jqid) && jqid->valuestring) {
        (void)parse_uuid_pr(jqid->valuestring, query_id);  // leave all-zero on parse failure
    }

    // Copy geohash + packet source into owned buffers BEFORE cJSON_Delete (use-after-free otherwise).
    const char *geo = (cJSON_IsString(jgeo) && jgeo->valuestring) ? jgeo->valuestring : "";
    char *geo_owned = dup_cstr_pr(geo);
    char *from_owned = dup_cstr_pr(packet->source_uhid ? packet->source_uhid : "");

    cJSON_Delete(body);

    if (!geo_owned || !from_owned) {
        free(geo_owned);
        free(from_owned);
        return false;
    }

    if (service->query_received_cb) {
        aethernet_presence_query_received_t evt;
        memcpy(evt.query_id, query_id, AETHERNET_PACKET_ID_SIZE);
        evt.geohash = geo_owned;
        evt.from_uhid = from_owned;
        service->query_received_cb(&evt, service->query_received_cb_user_data);
    }

    free(geo_owned);
    free(from_owned);
    return true;
}

// ─── Public API ──────────────────────────────────────────

aethernet_presence_service_t *aethernet_presence_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_presence_service_t *svc =
        (aethernet_presence_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_presence_service_free(aethernet_presence_service_t *service) {
    if (!service) return;
    free(service);
}

bool aethernet_presence_broadcast_beacon(aethernet_presence_service_t *service,
                                         const aethernet_presence_beacon_t *beacon,
                                         int *out_count) {
    if (!service || !beacon) return false;

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_beacon_payload(beacon, &body, &body_len)) return false;

    bool ok = broadcast_presence_packet(service, AETHERNET_PACKET_TYPE_PRESENCE_BEACON,
                                        body, body_len, out_count);
    free(body);
    return ok;
}

bool aethernet_presence_query(aethernet_presence_service_t *service,
                              const char *geohash,
                              uint8_t out_query_id[AETHERNET_PACKET_ID_SIZE],
                              int *out_count) {
    if (!service) return false;

    uint8_t query_id[AETHERNET_PACKET_ID_SIZE];
    random_uuid_pr(query_id);

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_query_payload(query_id, geohash, &body, &body_len)) return false;

    bool ok = broadcast_presence_packet(service, AETHERNET_PACKET_TYPE_PRESENCE_QUERY,
                                        body, body_len, out_count);
    free(body);
    if (!ok) return false;

    if (out_query_id) memcpy(out_query_id, query_id, AETHERNET_PACKET_ID_SIZE);
    return true;
}

bool aethernet_presence_handle_packet(aethernet_presence_service_t *service,
                                      const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->payload == NULL || packet->payload_len == 0) return false;

    if (packet->type == AETHERNET_PACKET_TYPE_PRESENCE_BEACON)
        return handle_beacon(service, packet);
    if (packet->type == AETHERNET_PACKET_TYPE_PRESENCE_QUERY)
        return handle_query(service, packet);
    return false;  // wrong packet type
}

void aethernet_presence_set_beacon_received_cb(aethernet_presence_service_t *service,
                                               aethernet_presence_beacon_received_cb cb,
                                               void *user_data) {
    if (!service) return;
    service->beacon_received_cb = cb;
    service->beacon_received_cb_user_data = user_data;
}

void aethernet_presence_set_query_received_cb(aethernet_presence_service_t *service,
                                              aethernet_presence_query_received_cb cb,
                                              void *user_data) {
    if (!service) return;
    service->query_received_cb = cb;
    service->query_received_cb_user_data = user_data;
}
