// SPDX-License-Identifier: MIT
// aether-vault shard-request WIRE binding (PacketType VaultShardRequest 42). See aethernet/vaultshard.h.
//
// Thin transport: broadcast a request for an erasure-coded shard (source -> "*", requester = local
// UHID) and surface inbound shard requests via a callback. Mirrors the green C#
// VaultShardRequestService.
//
// The payload is encoded with snprintf (byte-identical to the C# System.Text.Json output — field order
// shard_hash, requester_uhid) and decoded on receive with the vendored cJSON. Byte-identity gate:
// fixtures/vaultshard/vectors.json.

#include "aethernet/vaultshard.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <cjson/cJSON.h>

// ─── Internal state ──────────────────────────────────────

struct aethernet_vault_shard_request_service {
    aethernet_mesh_sender_t *sender;
    aethernet_vault_shard_requested_cb requested_cb;
    void                              *requested_cb_user_data;
};

static char *dup_cstr_vs(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// ─── Serialization ───────────────────────────────────────

// Encode a VaultShardRequest payload as canonical JSON. Field order shard_hash, requester_uhid; no
// whitespace. The string fields are interpolated verbatim (as sos.c / channels.c do) so ASCII
// hashes/uhids reproduce byte-for-byte. Byte-identity gate: fixtures/vaultshard/vectors.json.
static bool encode_shard_request_payload(const char *shard_hash,
                                         const char *requester_uhid,
                                         uint8_t **out_payload,
                                         uint32_t *out_len) {
    const char *req = requester_uhid ? requester_uhid : "";

    // Size the buffer generously. The prior C port under-sized THIS request buffer and overflowed; so
    // budget the FULL fixed frame plus the strlen of every variable field plus margin. Fixed frame
    //   {"shard_hash":"","requester_uhid":""}
    // is 37 bytes; add both variable strings and a 64-byte margin.
    size_t cap = 64
               + strlen(shard_hash)
               + strlen(req)
               + 64;  // margin
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"shard_hash\":\"%s\",\"requester_uhid\":\"%s\"}",
        shard_hash, req);
    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

bool aethernet_vault_shard_request_payload_serialize(const char *shard_hash,
                                                     const char *requester_uhid,
                                                     uint8_t **out_json,
                                                     uint32_t *out_len) {
    if (!shard_hash || !out_json || !out_len) return false;
    return encode_shard_request_payload(shard_hash, requester_uhid, out_json, out_len);
}

// ─── Public API ──────────────────────────────────────────

aethernet_vault_shard_request_service_t *aethernet_vault_shard_request_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_vault_shard_request_service_t *svc =
        (aethernet_vault_shard_request_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_vault_shard_request_service_free(aethernet_vault_shard_request_service_t *service) {
    if (!service) return;
    free(service);
}

void aethernet_vault_shard_request_set_requested_cb(aethernet_vault_shard_request_service_t *service,
                                                    aethernet_vault_shard_requested_cb cb,
                                                    void *user_data) {
    if (!service) return;
    service->requested_cb = cb;
    service->requested_cb_user_data = user_data;
}

bool aethernet_vault_shard_request_request_shard(aethernet_vault_shard_request_service_t *service,
                                                 const char *shard_hash,
                                                 int *out_count) {
    if (!service || !shard_hash || shard_hash[0] == '\0') return false;  // C# ThrowIfNullOrEmpty
    if (!service->sender->broadcast) return false;  // host wired no broadcast — cannot deliver

    // requester_uhid = the sender's local UHID (mirrors the C# RequestShardAsync).
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_shard_request_payload(shard_hash, service->sender->local_uhid, &body, &body_len)) {
        return false;
    }

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return false; }
    pkt->type = (uint8_t)AETHERNET_PACKET_TYPE_VAULT_SHARD_REQUEST;
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

bool aethernet_vault_shard_request_handle_packet(aethernet_vault_shard_request_service_t *service,
                                                 const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_VAULT_SHARD_REQUEST) return false;
    if (packet->payload == NULL || packet->payload_len == 0) return false;

    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;  // malformed → benign drop (C# swallows JsonException)

    const cJSON *jsh  = cJSON_GetObjectItemCaseSensitive(body, "shard_hash");
    const cJSON *jreq = cJSON_GetObjectItemCaseSensitive(body, "requester_uhid");

    // shard_hash must be a present, non-empty string (mirrors the C# string.IsNullOrEmpty guard).
    if (!cJSON_IsString(jsh) || jsh->valuestring == NULL || jsh->valuestring[0] == '\0') {
        cJSON_Delete(body);
        return false;
    }

    // Copy every string we keep into owned buffers BEFORE cJSON_Delete — the cJSON valuestring pointers
    // are freed by cJSON_Delete(body), so using them afterwards would be a use-after-free (crashes the
    // Mac allocator even though Windows offline may pass by luck).
    char *shard_hash = dup_cstr_vs(jsh->valuestring);
    const char *req_src = (cJSON_IsString(jreq) && jreq->valuestring) ? jreq->valuestring : "";
    char *requester_uhid = dup_cstr_vs(req_src);

    cJSON_Delete(body);

    if (!shard_hash || !requester_uhid) {
        free(shard_hash);
        free(requester_uhid);
        return false;
    }

    if (service->requested_cb) {
        aethernet_vault_shard_request_t evt;
        evt.shard_hash = shard_hash;          // owned copy, valid for the call
        evt.requester_uhid = requester_uhid;  // owned copy, valid for the call
        service->requested_cb(&evt, service->requested_cb_user_data);
    }

    free(shard_hash);
    free(requester_uhid);
    return true;
}
