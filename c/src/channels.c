// SPDX-License-Identifier: MIT
// Named-channel pub/sub for the Aether mesh (PacketType 7).
//
// Single-threaded reference impl; hosts pumping packets from multiple threads
// must wrap the service in their own mutex. The ChannelMessagePayload is encoded
// with snprintf (byte-identical to the C# System.Text.Json SnakeCaseLower output —
// {"channel_id":...,"message_id":"<uuid>","sender_uhid":...,"content":...,"sent_at_ms":M},
// no whitespace, lowercase-dashed UUID) and decoded on receive with the vendored
// cJSON, matching the SOS / heartbeat approach. Publishing floods a ChannelMessage
// (dest "*", TTL default); receivers de-dup by message id, surface messages for
// subscribed channels (not their own), and re-flood while TTL allows.

#include "aethernet/channels.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <cjson/cJSON.h>

// ─── Internal state ──────────────────────────────────────

// A subscribed channel id, held in a singly-linked list (mirrors the C#
// ConcurrentDictionary<string, byte> _subscriptions keyed by channel id).
typedef struct sub_node {
    char *channel_id;      // owned
    struct sub_node *next;
} sub_node_t;

// A seen message id, for flood de-duplication (mirrors _seen keyed by message id).
typedef struct seen_node {
    uint8_t id[AETHERNET_PACKET_ID_SIZE];
    struct seen_node *next;
} seen_node_t;

struct aethernet_channel_service {
    aethernet_mesh_sender_t *sender;
    sub_node_t *subscriptions;
    seen_node_t *seen;

    aethernet_channel_message_received_cb received_cb;
    void *received_cb_user_data;
};

// ─── Helpers ─────────────────────────────────────────────

static int64_t now_ms_chan(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *str_dup_chan(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static void random_uuid_chan(uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    // Reference impl uses rand() seeded once (mirrors sos.c random_uuid). Hosts that need
    // cryptographic randomness for message ids supply their own UUID source.
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)(now_ms_chan() & 0x7FFFFFFF)); seeded = 1; }
    for (int i = 0; i < AETHERNET_PACKET_ID_SIZE; i++) {
        out[i] = (uint8_t)(rand() & 0xFF);
    }
    // Set RFC 4122 v4 marker bits
    out[6] = (uint8_t)((out[6] & 0x0F) | 0x40);
    out[8] = (uint8_t)((out[8] & 0x3F) | 0x80);
}

// Format a 16-byte UUID into the canonical lowercase 8-4-4-4-12 dashed form.
// `out` must hold at least 37 bytes (36 chars + null).
static void canonical_uuid_chan(const uint8_t id[AETHERNET_PACKET_ID_SIZE], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        id[0], id[1], id[2], id[3], id[4], id[5], id[6], id[7],
        id[8], id[9], id[10], id[11], id[12], id[13], id[14], id[15]);
}

// Parse a canonical dashed UUID string (36 chars, dashes at 8/13/18/23) into 16 bytes. Accepts
// upper- or lowercase hex. Returns true on success. Mirrors sos.c parse_uuid.
static bool parse_uuid_chan(const char *s, uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
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

static bool seen_contains(aethernet_channel_service_t *svc, const uint8_t id[AETHERNET_PACKET_ID_SIZE]) {
    for (seen_node_t *n = svc->seen; n; n = n->next) {
        if (memcmp(n->id, id, AETHERNET_PACKET_ID_SIZE) == 0) return true;
    }
    return false;
}

// Add `id` to the seen set unless already present. Returns true if newly added, false if it was
// already present (dedup — mirrors ConcurrentDictionary.TryAdd's bool contract).
static bool seen_add(aethernet_channel_service_t *svc, const uint8_t id[AETHERNET_PACKET_ID_SIZE]) {
    if (seen_contains(svc, id)) return false;
    seen_node_t *node = (seen_node_t *)malloc(sizeof(seen_node_t));
    if (!node) return false;
    memcpy(node->id, id, AETHERNET_PACKET_ID_SIZE);
    node->next = svc->seen;
    svc->seen = node;
    return true;
}

static sub_node_t *sub_find(aethernet_channel_service_t *svc, const char *channel_id) {
    for (sub_node_t *n = svc->subscriptions; n; n = n->next) {
        if (n->channel_id && strcmp(n->channel_id, channel_id) == 0) return n;
    }
    return NULL;
}

// Encode a ChannelMessagePayload as canonical JSON. snake_case keys, field order channel_id,
// message_id, sender_uhid, content, sent_at_ms, no whitespace, lowercase-dashed UUID — the
// byte-identity gate (fixtures/channels/vectors.json). Mirrors the C# reference (System.Text.Json,
// SnakeCaseLower). String fields are interpolated verbatim (as sos.c does for message/geohash), so
// the ASCII vectors reproduce byte-for-byte. We format directly rather than via cJSON's printer so
// the bytes carry no printer-inserted spacing.
static bool encode_channel_payload(const char *channel_id,
                                   const uint8_t message_id[AETHERNET_PACKET_ID_SIZE],
                                   const char *sender_uhid,
                                   const char *content,
                                   int64_t sent_at_ms,
                                   uint8_t **out_payload,
                                   uint32_t *out_len) {
    char id_canonical[37];  // 8-4-4-4-12 + null
    canonical_uuid_chan(message_id, id_canonical);

    // 36-char uuid + int64 (<=20 digits, incl. sign) + the three variable strings + fixed
    // key/punctuation. 128 covers the keys/uuid/int; add the string lengths.
    size_t cap = 128 + strlen(channel_id) + strlen(sender_uhid) + strlen(content);
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"channel_id\":\"%s\",\"message_id\":\"%s\",\"sender_uhid\":\"%s\","
        "\"content\":\"%s\",\"sent_at_ms\":%lld}",
        channel_id, id_canonical, sender_uhid, content, (long long)sent_at_ms);

    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

// Public wrapper over encode_channel_payload — see aethernet/channels.h. Kept thin so the wire path
// (aethernet_channel_publish) and the byte-identity gate exercise identical serialization.
bool aethernet_channel_message_payload_serialize(const char *channel_id,
                                                 const uint8_t message_id[AETHERNET_PACKET_ID_SIZE],
                                                 const char *sender_uhid,
                                                 const char *content,
                                                 int64_t sent_at_ms,
                                                 uint8_t **out_json,
                                                 uint32_t *out_len) {
    if (!channel_id || !message_id || !sender_uhid || !content || !out_json || !out_len) return false;
    return encode_channel_payload(channel_id, message_id, sender_uhid, content, sent_at_ms,
                                  out_json, out_len);
}

// ─── Public API ──────────────────────────────────────────

aethernet_channel_service_t *aethernet_channel_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_channel_service_t *svc =
        (aethernet_channel_service_t *)calloc(1, sizeof(aethernet_channel_service_t));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_channel_service_free(aethernet_channel_service_t *service) {
    if (!service) return;
    while (service->subscriptions) {
        sub_node_t *next = service->subscriptions->next;
        free(service->subscriptions->channel_id);
        free(service->subscriptions);
        service->subscriptions = next;
    }
    while (service->seen) {
        seen_node_t *next = service->seen->next;
        free(service->seen);
        service->seen = next;
    }
    free(service);
}

void aethernet_channel_subscribe(aethernet_channel_service_t *service, const char *channel_id) {
    if (!service || !channel_id || channel_id[0] == '\0') return;
    if (sub_find(service, channel_id)) return;  // idempotent
    sub_node_t *node = (sub_node_t *)malloc(sizeof(sub_node_t));
    if (!node) return;
    node->channel_id = str_dup_chan(channel_id);
    if (!node->channel_id) { free(node); return; }
    node->next = service->subscriptions;
    service->subscriptions = node;
}

void aethernet_channel_unsubscribe(aethernet_channel_service_t *service, const char *channel_id) {
    if (!service || !channel_id) return;
    sub_node_t **prev = &service->subscriptions;
    while (*prev) {
        sub_node_t *node = *prev;
        if (node->channel_id && strcmp(node->channel_id, channel_id) == 0) {
            *prev = node->next;
            free(node->channel_id);
            free(node);
            return;
        }
        prev = &node->next;
    }
}

int aethernet_channel_get_subscriptions(const aethernet_channel_service_t *service,
                                        char ***out_channels,
                                        int *out_count) {
    if (!service || !out_channels || !out_count) return -1;

    int count = 0;
    for (sub_node_t *n = service->subscriptions; n; n = n->next) count++;
    if (count == 0) {
        *out_channels = NULL;
        *out_count = 0;
        return 0;
    }

    char **arr = (char **)calloc((size_t)count, sizeof(char *));
    if (!arr) return -1;

    int i = 0;
    for (sub_node_t *n = service->subscriptions; n && i < count; n = n->next) {
        arr[i] = str_dup_chan(n->channel_id);
        if (!arr[i]) {  // OOM mid-copy — unwind
            for (int j = 0; j < i; j++) free(arr[j]);
            free(arr);
            return -1;
        }
        i++;
    }
    *out_channels = arr;
    *out_count = count;
    return count;
}

void aethernet_channel_subscriptions_free(char **channels, int count) {
    if (!channels) return;
    for (int i = 0; i < count; i++) free(channels[i]);
    free(channels);
}

int aethernet_channel_publish(aethernet_channel_service_t *service,
                             const char *channel_id,
                             const char *content) {
    if (!service || !channel_id || channel_id[0] == '\0' || !content) return -1;

    uint8_t message_id[AETHERNET_PACKET_ID_SIZE];
    random_uuid_chan(message_id);
    seen_add(service, message_id);  // never re-handle our own message when it floods back

    const char *sender_uhid = service->sender->local_uhid ? service->sender->local_uhid : "";

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_channel_payload(channel_id, message_id, sender_uhid, content,
                                now_ms_chan(), &body, &body_len)) {
        return -1;
    }

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return -1; }
    pkt->type = AETHERNET_PACKET_TYPE_CHANNEL_MESSAGE;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, "*");
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);

    int delivered = service->sender->broadcast(service->sender, pkt);
    aethernet_packet_free(pkt);
    return delivered;
}

bool aethernet_channel_handle_packet(aethernet_channel_service_t *service,
                                     aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_CHANNEL_MESSAGE) return false;
    if (packet->payload == NULL || packet->payload_len == 0) return false;

    // Decode the payload (channel_id / message_id / sender_uhid / content / sent_at_ms) via the
    // vendored cJSON, mirroring the C# HandleAsync which deserializes ChannelMessagePayload.
    // Malformed → benign drop (C# swallows JsonException and returns false).
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;

    const cJSON *jchan = cJSON_GetObjectItemCaseSensitive(body, "channel_id");
    const cJSON *jmid  = cJSON_GetObjectItemCaseSensitive(body, "message_id");
    const cJSON *jsend = cJSON_GetObjectItemCaseSensitive(body, "sender_uhid");
    const cJSON *jcont = cJSON_GetObjectItemCaseSensitive(body, "content");
    const cJSON *jsent = cJSON_GetObjectItemCaseSensitive(body, "sent_at_ms");

    // channel_id must be a present, non-empty string (mirrors C# string.IsNullOrEmpty check); the
    // message id must parse. Otherwise the payload is malformed → benign drop.
    uint8_t message_id[AETHERNET_PACKET_ID_SIZE];
    if (!cJSON_IsString(jchan) || jchan->valuestring == NULL || jchan->valuestring[0] == '\0'
        || !cJSON_IsString(jmid) || jmid->valuestring == NULL
        || !parse_uuid_chan(jmid->valuestring, message_id)) {
        cJSON_Delete(body);
        return false;
    }

    // Flood de-duplication: only the first copy of a given message id is processed.
    if (!seen_add(service, message_id)) {
        cJSON_Delete(body);
        return false;
    }

    const char *channel_id = jchan->valuestring;
    const char *sender_uhid = (cJSON_IsString(jsend) && jsend->valuestring) ? jsend->valuestring : "";
    const char *content = (cJSON_IsString(jcont) && jcont->valuestring) ? jcont->valuestring : "";
    int64_t sent_at_ms = cJSON_IsNumber(jsent) ? (int64_t)jsent->valuedouble : 0;

    bool is_own = service->sender->local_uhid
        && strcmp(sender_uhid, service->sender->local_uhid) == 0;

    if (!is_own && sub_find(service, channel_id) && service->received_cb) {
        aethernet_channel_message_t msg = {0};
        msg.channel_id = (char *)channel_id;   // borrowed for the callback duration
        memcpy(msg.message_id, message_id, AETHERNET_PACKET_ID_SIZE);
        msg.sender_uhid = (char *)sender_uhid;  // borrowed
        msg.content = (char *)content;          // borrowed
        msg.sent_at_ms = sent_at_ms;
        service->received_cb(&msg, service->received_cb_user_data);
    }

    cJSON_Delete(body);

    // Re-flood so subscribers further out receive it — even if WE aren't subscribed (pure relay).
    // Never re-flood our own message (mirrors the C# `packet.Ttl > 1 && !isOwn` guard).
    if (packet->ttl > 1 && !is_own) {
        packet->ttl--;
        service->sender->broadcast(service->sender, packet);
    }

    return true;
}

void aethernet_channel_set_message_received_cb(aethernet_channel_service_t *service,
                                               aethernet_channel_message_received_cb cb,
                                               void *user_data) {
    if (!service) return;
    service->received_cb = cb;
    service->received_cb_user_data = user_data;
}
