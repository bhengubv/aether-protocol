// SPDX-License-Identifier: MIT
// Video call-control for the Aether mesh (PacketType 27).
//
// Single-threaded reference impl; hosts pumping packets from multiple threads
// must wrap the service in their own mutex. The VideoCallControlPayload is encoded
// with snprintf (byte-identical to the C# System.Text.Json SnakeCaseLower output —
// {"call_id":"<uuid>","action":"<verb>","sent_at_ms":M}, no whitespace, lowercase-dashed
// UUID) and decoded on receive with the vendored cJSON, matching the SOS / channels approach.
// This is the caller-intent layer (ring/accept/decline/hangup): directed unicast to a single
// peer via sender->send, distinct from the media plane (VideoSignaling / VideoFrame) handled by
// the streaming VideoCallService. ring() mints a call id and directed-sends "ring"; accept/decline/
// hangup echo the matching verb for a known call id; inbound signals surface via the callback.

#include "aethernet/videocall.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <cjson/cJSON.h>

// ─── Internal state ──────────────────────────────────────

struct aethernet_video_call_control_service {
    aethernet_mesh_sender_t *sender;

    aethernet_video_call_state_changed_cb state_changed_cb;
    void *state_changed_cb_user_data;
};

// ─── Helpers ─────────────────────────────────────────────

static int64_t now_ms_vcc(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static void random_uuid_vcc(uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    // Reference impl uses rand() seeded once (mirrors sos.c / channels.c random_uuid). Hosts that
    // need cryptographic randomness for call ids supply their own UUID source.
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)(now_ms_vcc() & 0x7FFFFFFF)); seeded = 1; }
    for (int i = 0; i < AETHERNET_PACKET_ID_SIZE; i++) {
        out[i] = (uint8_t)(rand() & 0xFF);
    }
    // Set RFC 4122 v4 marker bits
    out[6] = (uint8_t)((out[6] & 0x0F) | 0x40);
    out[8] = (uint8_t)((out[8] & 0x3F) | 0x80);
}

// Format a 16-byte UUID into the canonical lowercase 8-4-4-4-12 dashed form.
// `out` must hold at least 37 bytes (36 chars + null).
static void canonical_uuid_vcc(const uint8_t id[AETHERNET_PACKET_ID_SIZE], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        id[0], id[1], id[2], id[3], id[4], id[5], id[6], id[7],
        id[8], id[9], id[10], id[11], id[12], id[13], id[14], id[15]);
}

// Parse a canonical dashed UUID string (36 chars, dashes at 8/13/18/23) into 16 bytes. Accepts
// upper- or lowercase hex. Returns true on success. Mirrors sos.c / channels.c parse_uuid.
static bool parse_uuid_vcc(const char *s, uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
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

// Encode a VideoCallControlPayload as canonical JSON. snake_case keys, field order call_id, action,
// sent_at_ms, no whitespace, lowercase-dashed UUID — the byte-identity gate
// (fixtures/videocall/vectors.json). Mirrors the C# reference (System.Text.Json, SnakeCaseLower). The
// action string is interpolated verbatim (as sos.c / channels.c do for their string fields), so the
// ASCII vectors reproduce byte-for-byte. We format directly rather than via cJSON's printer so the
// bytes carry no printer-inserted spacing.
static bool encode_video_call_control_payload(const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                              const char *action,
                                              int64_t sent_at_ms,
                                              uint8_t **out_payload,
                                              uint32_t *out_len) {
    char id_canonical[37];  // 8-4-4-4-12 + null
    canonical_uuid_vcc(call_id, id_canonical);

    // 36-char uuid + int64 (<=20 digits, incl. sign) + the action string + fixed key/punctuation.
    // 96 covers the keys/uuid/int; add the action length.
    size_t cap = 96 + strlen(action);
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"call_id\":\"%s\",\"action\":\"%s\",\"sent_at_ms\":%lld}",
        id_canonical, action, (long long)sent_at_ms);

    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

// Public wrapper over encode_video_call_control_payload — see aethernet/videocall.h. Kept thin so the
// wire path (the ring/accept/decline/hangup senders) and the byte-identity gate exercise identical
// serialization.
bool aethernet_video_call_control_payload_serialize(const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                                    const char *action,
                                                    int64_t sent_at_ms,
                                                    uint8_t **out_json,
                                                    uint32_t *out_len) {
    if (!call_id || !action || !out_json || !out_len) return false;
    return encode_video_call_control_payload(call_id, action, sent_at_ms, out_json, out_len);
}

// Build and directed-send a VideoCall control packet carrying `action` for `call_id` to `peer_uhid`.
// Returns the delivery result from sender->send (false if the host wired no directed send). Mirrors
// the C# SendControlAsync.
static bool send_control_vcc(aethernet_video_call_control_service_t *service,
                             const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                             const char *peer_uhid,
                             const char *action) {
    if (!service->sender->send) return false;  // host wired no directed send — cannot deliver

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_video_call_control_payload(call_id, action, now_ms_vcc(), &body, &body_len)) {
        return false;
    }

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return false; }
    pkt->type = AETHERNET_PACKET_TYPE_VIDEO_CALL;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, peer_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);

    bool delivered = service->sender->send(service->sender, pkt, peer_uhid);
    aethernet_packet_free(pkt);
    return delivered;
}

// ─── Public API ──────────────────────────────────────────

aethernet_video_call_control_service_t *aethernet_video_call_control_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_video_call_control_service_t *svc =
        (aethernet_video_call_control_service_t *)calloc(1, sizeof(aethernet_video_call_control_service_t));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_video_call_control_service_free(aethernet_video_call_control_service_t *service) {
    if (!service) return;
    free(service);
}

bool aethernet_video_call_control_ring(aethernet_video_call_control_service_t *service,
                                       const char *peer_uhid,
                                       uint8_t out_call_id[AETHERNET_PACKET_ID_SIZE]) {
    if (!service || !peer_uhid || peer_uhid[0] == '\0' || !out_call_id) return false;

    uint8_t call_id[AETHERNET_PACKET_ID_SIZE];
    random_uuid_vcc(call_id);

    if (!send_control_vcc(service, call_id, peer_uhid, "ring")) return false;

    memcpy(out_call_id, call_id, AETHERNET_PACKET_ID_SIZE);
    return true;
}

bool aethernet_video_call_control_accept(aethernet_video_call_control_service_t *service,
                                         const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                         const char *peer_uhid) {
    if (!service || !call_id || !peer_uhid || peer_uhid[0] == '\0') return false;
    return send_control_vcc(service, call_id, peer_uhid, "accept");
}

bool aethernet_video_call_control_decline(aethernet_video_call_control_service_t *service,
                                          const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                          const char *peer_uhid) {
    if (!service || !call_id || !peer_uhid || peer_uhid[0] == '\0') return false;
    return send_control_vcc(service, call_id, peer_uhid, "decline");
}

bool aethernet_video_call_control_hangup(aethernet_video_call_control_service_t *service,
                                         const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                         const char *peer_uhid) {
    if (!service || !call_id || !peer_uhid || peer_uhid[0] == '\0') return false;
    return send_control_vcc(service, call_id, peer_uhid, "hangup");
}

bool aethernet_video_call_control_handle_packet(aethernet_video_call_control_service_t *service,
                                                const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_VIDEO_CALL) return false;
    if (packet->payload == NULL || packet->payload_len == 0) return false;

    // Decode the payload (call_id / action / sent_at_ms) via the vendored cJSON, mirroring the C#
    // HandleAsync which deserializes VideoCallControlPayload. Malformed → benign drop (C# swallows
    // JsonException and returns false).
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;

    const cJSON *jcall = cJSON_GetObjectItemCaseSensitive(body, "call_id");
    const cJSON *jact  = cJSON_GetObjectItemCaseSensitive(body, "action");

    // action must be a present, non-empty string (mirrors C# string.IsNullOrEmpty check); the call id
    // must parse. Otherwise the payload is malformed → benign drop.
    uint8_t call_id[AETHERNET_PACKET_ID_SIZE];
    if (!cJSON_IsString(jcall) || jcall->valuestring == NULL
        || !parse_uuid_vcc(jcall->valuestring, call_id)
        || !cJSON_IsString(jact) || jact->valuestring == NULL || jact->valuestring[0] == '\0') {
        cJSON_Delete(body);
        return false;
    }

    // Copy the action into an owned buffer BEFORE cJSON_Delete — the cJSON valuestring pointer is
    // freed by cJSON_Delete(body), so using it afterwards would be a use-after-free.
    char *action_owned = NULL;
    size_t action_n = strlen(jact->valuestring) + 1;
    action_owned = (char *)malloc(action_n);
    if (!action_owned) { cJSON_Delete(body); return false; }
    memcpy(action_owned, jact->valuestring, action_n);

    cJSON_Delete(body);

    if (service->state_changed_cb) {
        aethernet_video_call_state_changed_t evt = {0};
        memcpy(evt.call_id, call_id, AETHERNET_PACKET_ID_SIZE);
        evt.action = action_owned;                                     // owned copy, valid for the call
        evt.from_uhid = packet->source_uhid ? packet->source_uhid : (char *)"";  // borrowed from packet
        service->state_changed_cb(&evt, service->state_changed_cb_user_data);
    }

    free(action_owned);
    return true;
}

void aethernet_video_call_control_set_state_changed_cb(aethernet_video_call_control_service_t *service,
                                                       aethernet_video_call_state_changed_cb cb,
                                                       void *user_data) {
    if (!service) return;
    service->state_changed_cb = cb;
    service->state_changed_cb_user_data = user_data;
}
