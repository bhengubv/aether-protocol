// SPDX-License-Identifier: MIT
// aether-forge announce WIRE binding (PacketType ForgeAnnounce 41). See aethernet/forge_announce.h.
//
// Thin transport: broadcast a freshly-cached artifact announcement (source -> "*") and surface inbound
// announcements via a callback. Mirrors the green C# ForgeAnnounceService.
//
// The payload is encoded with snprintf (byte-identical to the C# System.Text.Json output — field order
// package_id, content_hash, size_bytes, announced_at_ms; size_bytes + announced_at_ms bare int64) and
// decoded on receive with the vendored cJSON. Byte-identity gate: fixtures/forge/vectors.json.

#include "aethernet/forge_announce.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <cjson/cJSON.h>

// ─── Internal state ──────────────────────────────────────

struct aethernet_forge_announce_service {
    aethernet_mesh_sender_t *sender;
    aethernet_forge_announce_received_cb received_cb;
    void                                *received_cb_user_data;
};

static char *dup_cstr_fa(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// ─── Serialization ───────────────────────────────────────

// Encode a ForgeAnnounce payload as canonical JSON. Field order package_id, content_hash, size_bytes,
// announced_at_ms; no whitespace; bare-int64 size_bytes/announced_at_ms. The string fields are
// interpolated verbatim (as sos.c / channels.c do) so ASCII ids/hashes reproduce byte-for-byte.
// content_hash NULL is emitted as "" (mirrors the C# `?? string.Empty`). Byte-identity gate:
// fixtures/forge/vectors.json.
static bool encode_announce_payload(const char *package_id,
                                    const char *content_hash,
                                    int64_t size_bytes,
                                    int64_t announced_at_ms,
                                    uint8_t **out_payload,
                                    uint32_t *out_len) {
    const char *ch = content_hash ? content_hash : "";

    // Size the buffer generously: the full fixed frame (keys + punctuation
    //   {"package_id":"","content_hash":"","size_bytes":,"announced_at_ms":}
    // ~68 bytes) + each variable string field + two int64 (<=20 digits incl. sign) + margin.
    size_t cap = 128
               + strlen(package_id)
               + strlen(ch)
               + 40   // two int64s
               + 64;  // margin
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"package_id\":\"%s\",\"content_hash\":\"%s\","
        "\"size_bytes\":%lld,\"announced_at_ms\":%lld}",
        package_id, ch, (long long)size_bytes, (long long)announced_at_ms);
    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

bool aethernet_forge_announce_payload_serialize(const char *package_id,
                                                const char *content_hash,
                                                int64_t size_bytes,
                                                int64_t announced_at_ms,
                                                uint8_t **out_json,
                                                uint32_t *out_len) {
    if (!package_id || !out_json || !out_len) return false;
    return encode_announce_payload(package_id, content_hash, size_bytes, announced_at_ms,
                                   out_json, out_len);
}

// ─── Public API ──────────────────────────────────────────

aethernet_forge_announce_service_t *aethernet_forge_announce_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_forge_announce_service_t *svc =
        (aethernet_forge_announce_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_forge_announce_service_free(aethernet_forge_announce_service_t *service) {
    if (!service) return;
    free(service);
}

void aethernet_forge_announce_set_received_cb(aethernet_forge_announce_service_t *service,
                                              aethernet_forge_announce_received_cb cb,
                                              void *user_data) {
    if (!service) return;
    service->received_cb = cb;
    service->received_cb_user_data = user_data;
}

bool aethernet_forge_announce_broadcast(aethernet_forge_announce_service_t *service,
                                        const char *package_id,
                                        const char *content_hash,
                                        int64_t size_bytes,
                                        int64_t announced_at_ms,
                                        int *out_count) {
    if (!service || !package_id || package_id[0] == '\0') return false;  // C# ThrowIfNullOrEmpty
    if (!service->sender->broadcast) return false;  // host wired no broadcast — cannot deliver

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_announce_payload(package_id, content_hash, size_bytes, announced_at_ms,
                                 &body, &body_len)) {
        return false;
    }

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return false; }
    pkt->type = (uint8_t)AETHERNET_PACKET_TYPE_FORGE_ANNOUNCE;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, "*");
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);
    free(body);

    int delivered = service->sender->broadcast(service->sender, pkt);
    aethernet_packet_free(pkt);
    if (out_count) *out_count = delivered;
    return true;
}

bool aethernet_forge_announce_handle_packet(aethernet_forge_announce_service_t *service,
                                            const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_FORGE_ANNOUNCE) return false;
    if (packet->payload == NULL || packet->payload_len == 0) return false;

    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;  // malformed → benign drop (C# swallows JsonException)

    const cJSON *jpkg = cJSON_GetObjectItemCaseSensitive(body, "package_id");
    const cJSON *jch  = cJSON_GetObjectItemCaseSensitive(body, "content_hash");
    const cJSON *jsz  = cJSON_GetObjectItemCaseSensitive(body, "size_bytes");
    const cJSON *jann = cJSON_GetObjectItemCaseSensitive(body, "announced_at_ms");

    // package_id must be a present, non-empty string (mirrors the C# string.IsNullOrEmpty guard).
    if (!cJSON_IsString(jpkg) || jpkg->valuestring == NULL || jpkg->valuestring[0] == '\0') {
        cJSON_Delete(body);
        return false;
    }

    // Copy every string we keep into owned buffers BEFORE cJSON_Delete — the cJSON valuestring pointers
    // are freed by cJSON_Delete(body), so using them afterwards would be a use-after-free (crashes the
    // Mac allocator even though Windows offline may pass by luck).
    char *package_id = dup_cstr_fa(jpkg->valuestring);
    const char *ch_src = (cJSON_IsString(jch) && jch->valuestring) ? jch->valuestring : "";
    char *content_hash = dup_cstr_fa(ch_src);
    // Numbers via valuedouble to preserve the full int64 range (cJSON's valueint is a 32-bit int, too
    // narrow for a byte-count / ms timestamp). Missing/non-number → 0 (matches the C# default(long)).
    int64_t size_bytes      = cJSON_IsNumber(jsz)  ? (int64_t)jsz->valuedouble  : 0;
    int64_t announced_at_ms = cJSON_IsNumber(jann) ? (int64_t)jann->valuedouble : 0;

    cJSON_Delete(body);

    if (!package_id || !content_hash) {
        free(package_id);
        free(content_hash);
        return false;
    }

    if (service->received_cb) {
        aethernet_forge_announce_t evt;
        evt.package_id = package_id;         // owned copy, valid for the call
        evt.content_hash = content_hash;     // owned copy, valid for the call
        evt.size_bytes = size_bytes;
        evt.announced_at_ms = announced_at_ms;
        service->received_cb(&evt, service->received_cb_user_data);
    }

    free(package_id);
    free(content_hash);
    return true;
}
