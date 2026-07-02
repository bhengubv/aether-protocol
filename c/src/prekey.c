// SPDX-License-Identifier: MIT
// Mesh pre-key exchange for the Aether mesh (PacketType PreKeyRequest 25 / PreKeyResponse 26).
//
// Directed request/response — never broadcast — so bundle requests do not leak identity-interest to
// the whole mesh. Transport only: the host wires the published bundle in (set_local_bundle) and
// consumes received bundles out (the bundle-received callback) via the Signal service. Mirrors the
// green C# PreKeyExchangeService.
//
// The request/response payloads are encoded with snprintf (byte-identical to the C# System.Text.Json
// output — request_id/requester_uhid for the request; request_id/uhid/identity_key/… for the
// response, every byte[] field STANDARD base64, no whitespace, lowercase-dashed UUID, bare-int ids)
// and decoded on receive with the vendored cJSON, matching the SOS / channels / videocall approach.
// Byte-identity gate: fixtures/prekey/vectors.json.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
// in their own mutex.

#include "aethernet/prekey.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <cjson/cJSON.h>

// ─── Base64 (RFC 4648 §4, standard '+/' alphabet, '=' padding) ───────────────
// Self-contained encode + decode. The bundle byte arrays go out as STANDARD base64 and inbound
// arrays are decoded back the same way. chunk_bitmap.c has a file-local encoder but no decoder and
// no export, so the prekey path carries its own pair (mirrors videocall keeping helpers local).

static const char s_b64_chars[] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

// Encode data[0..len) into out (must hold ((len+2)/3)*4 + 1 bytes incl. NUL). Null-terminates.
static void b64_encode_into(const uint8_t *data, size_t len, char *out) {
    size_t olen = ((len + 2) / 3) * 4;
    size_t i = 0, j = 0;
    while (i < len) {
        uint32_t a = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t b = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t c = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t triple = (a << 16) | (b << 8) | c;
        out[j++] = s_b64_chars[(triple >> 18) & 0x3F];
        out[j++] = s_b64_chars[(triple >> 12) & 0x3F];
        out[j++] = s_b64_chars[(triple >>  6) & 0x3F];
        out[j++] = s_b64_chars[ triple        & 0x3F];
    }
    size_t pad = (3 - (len % 3)) % 3;
    for (size_t p = 0; p < pad; p++)
        out[olen - 1 - p] = '=';
    out[olen] = '\0';
}

// Map a single base64 character to its 6-bit value, or -1 if not a base64 digit.
static int b64_val(char c) {
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

// Decode a NUL-terminated STANDARD base64 string into exactly `expected` bytes. Returns true only if
// the input is well-formed (correct '=' padding, no stray characters) AND decodes to exactly
// `expected` bytes — a length mismatch (e.g. a swapped/truncated field) is rejected. `s` must be
// non-NULL.
static bool b64_decode_exact(const char *s, uint8_t *out, size_t expected) {
    size_t slen = strlen(s);
    if (slen % 4 != 0) return false;                 // standard base64 is always a multiple of 4
    if (slen == 0) return expected == 0;

    size_t pad = 0;
    if (s[slen - 1] == '=') pad++;
    if (slen >= 2 && s[slen - 2] == '=') pad++;
    size_t decoded_len = (slen / 4) * 3 - pad;
    if (decoded_len != expected) return false;

    size_t oi = 0;
    for (size_t i = 0; i < slen; i += 4) {
        int q0 = b64_val(s[i]);
        int q1 = b64_val(s[i + 1]);
        if (q0 < 0 || q1 < 0) return false;

        bool p2 = (s[i + 2] == '=');
        bool p3 = (s[i + 3] == '=');
        // Padding may only occur in the final quartet, and '=' cannot precede a non-'='.
        if ((p2 || p3) && i + 4 != slen) return false;
        if (p2 && !p3) return false;

        int q2 = p2 ? 0 : b64_val(s[i + 2]);
        int q3 = p3 ? 0 : b64_val(s[i + 3]);
        if ((!p2 && q2 < 0) || (!p3 && q3 < 0)) return false;

        uint32_t triple = ((uint32_t)q0 << 18) | ((uint32_t)q1 << 12) |
                          ((uint32_t)q2 << 6)  |  (uint32_t)q3;
        if (oi < expected) out[oi++] = (uint8_t)((triple >> 16) & 0xFF);
        if (!p2 && oi < expected) out[oi++] = (uint8_t)((triple >> 8) & 0xFF);
        if (!p3 && oi < expected) out[oi++] = (uint8_t)(triple & 0xFF);
    }
    return oi == expected;
}

// ─── Internal state ──────────────────────────────────────

// One cached received bundle. The cache mirrors the C# ConcurrentDictionary<string, PreKeyBundle>:
// keyed by uhid, latest response replaces any prior entry for that uhid.
typedef struct {
    aethernet_pre_key_exchange_bundle_t bundle;  // owns bundle.uhid
} received_entry_t;

struct aethernet_pre_key_exchange_service {
    aethernet_mesh_sender_t *sender;

    bool                                has_local;
    aethernet_pre_key_exchange_bundle_t local;   // owns local.uhid when has_local

    received_entry_t *received;   // dynamic array of cached received bundles
    int               received_len;
    int               received_cap;

    aethernet_pre_key_bundle_received_cb bundle_received_cb;
    void                                *bundle_received_cb_user_data;
};

// ─── Helpers ─────────────────────────────────────────────

static int64_t now_ms_pk(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static void random_uuid_pk(uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
    // Reference impl uses rand() seeded once (mirrors sos.c / channels.c / videocall random_uuid).
    // Hosts needing cryptographic randomness for request ids supply their own UUID source.
    static int seeded = 0;
    if (!seeded) { srand((unsigned int)(now_ms_pk() & 0x7FFFFFFF)); seeded = 1; }
    for (int i = 0; i < AETHERNET_PACKET_ID_SIZE; i++) {
        out[i] = (uint8_t)(rand() & 0xFF);
    }
    out[6] = (uint8_t)((out[6] & 0x0F) | 0x40);  // RFC 4122 v4
    out[8] = (uint8_t)((out[8] & 0x3F) | 0x80);
}

// Format a 16-byte UUID into the canonical lowercase 8-4-4-4-12 dashed form. `out` >= 37 bytes.
static void canonical_uuid_pk(const uint8_t id[AETHERNET_PACKET_ID_SIZE], char out[37]) {
    snprintf(out, 37,
        "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
        id[0], id[1], id[2], id[3], id[4], id[5], id[6], id[7],
        id[8], id[9], id[10], id[11], id[12], id[13], id[14], id[15]);
}

// Parse a canonical dashed UUID string (36 chars, dashes at 8/13/18/23) into 16 bytes. Accepts
// upper- or lowercase hex. Returns true on success. Mirrors videocall parse_uuid.
static bool parse_uuid_pk(const char *s, uint8_t out[AETHERNET_PACKET_ID_SIZE]) {
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

static char *dup_cstr_pk(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// Deep-copy `src` into `dst` (dst->uhid becomes an owned copy). Returns false on allocation failure;
// on failure dst is left zeroed. dst must be uninitialised/zeroed on entry.
static bool bundle_copy(aethernet_pre_key_exchange_bundle_t *dst,
                        const aethernet_pre_key_exchange_bundle_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->uhid = dup_cstr_pk(src->uhid);
    if (!dst->uhid) return false;
    memcpy(dst->identity_key, src->identity_key, 32);
    memcpy(dst->identity_key_x25519, src->identity_key_x25519, 32);
    dst->pre_key_id = src->pre_key_id;
    memcpy(dst->pre_key, src->pre_key, 32);
    dst->signed_pre_key_id = src->signed_pre_key_id;
    memcpy(dst->signed_pre_key, src->signed_pre_key, 32);
    memcpy(dst->signed_pre_key_signature, src->signed_pre_key_signature, 64);
    return true;
}

void aethernet_pre_key_bundle_free(aethernet_pre_key_exchange_bundle_t *bundle) {
    if (!bundle) return;
    free(bundle->uhid);
    memset(bundle, 0, sizeof(*bundle));
}

// ─── Serialization ───────────────────────────────────────

// Encode a PreKeyRequest payload {"request_id":"<uuid>","requester_uhid":"<string>"} as canonical
// JSON. Field order request_id, requester_uhid, no whitespace, lowercase-dashed UUID. The
// requester_uhid is interpolated verbatim (as sos.c / channels.c / videocall do for their string
// fields), so ASCII UHIDs reproduce byte-for-byte. Byte-identity gate: fixtures/prekey/vectors.json.
static bool encode_request_payload(const uint8_t request_id[AETHERNET_PACKET_ID_SIZE],
                                   const char *requester_uhid,
                                   uint8_t **out_payload,
                                   uint32_t *out_len) {
    char id_canonical[37];
    canonical_uuid_pk(request_id, id_canonical);

    // Fixed keys/punctuation `{"request_id":"","requester_uhid":""}` (37) + 36-char uuid (73) + the
    // requester string + NUL. 96 gives comfortable headroom over the 73-byte fixed frame.
    size_t cap = 96 + strlen(requester_uhid);
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"request_id\":\"%s\",\"requester_uhid\":\"%s\"}",
        id_canonical, requester_uhid);
    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

bool aethernet_pre_key_request_payload_serialize(const uint8_t request_id[AETHERNET_PACKET_ID_SIZE],
                                                 const char *requester_uhid,
                                                 uint8_t **out_json,
                                                 uint32_t *out_len) {
    if (!request_id || !requester_uhid || !out_json || !out_len) return false;
    return encode_request_payload(request_id, requester_uhid, out_json, out_len);
}

// Encode a PreKeyResponse payload as canonical JSON. Field order request_id, uhid, identity_key,
// identity_key_x25519, pre_key_id, pre_key, signed_pre_key_id, signed_pre_key,
// signed_pre_key_signature; byte[] fields STANDARD base64, bare-int ids, no whitespace,
// lowercase-dashed UUID. Byte-identity gate: fixtures/prekey/vectors.json. We format directly
// (not via cJSON's printer) so the bytes carry no printer-inserted spacing.
static bool encode_response_payload(const uint8_t request_id[AETHERNET_PACKET_ID_SIZE],
                                    const aethernet_pre_key_exchange_bundle_t *bundle,
                                    uint8_t **out_payload,
                                    uint32_t *out_len) {
    char id_canonical[37];
    canonical_uuid_pk(request_id, id_canonical);

    // base64 of a 32-byte field = 44 chars (+NUL); of a 64-byte field = 88 chars (+NUL).
    char ik_b64[45], ikx_b64[45], pk_b64[45], spk_b64[45], sig_b64[89];
    b64_encode_into(bundle->identity_key, 32, ik_b64);
    b64_encode_into(bundle->identity_key_x25519, 32, ikx_b64);
    b64_encode_into(bundle->pre_key, 32, pk_b64);
    b64_encode_into(bundle->signed_pre_key, 32, spk_b64);
    b64_encode_into(bundle->signed_pre_key_signature, 64, sig_b64);

    // Fixed keys/punctuation + 36-char uuid + uhid + four 44-char b64 + one 88-char b64 + two int32
    // (<=11 chars each incl. sign). ~360 fixed; add uhid length and pad generously.
    size_t cap = 512 + strlen(bundle->uhid);
    char *buf = (char *)malloc(cap);
    if (!buf) return false;

    int n = snprintf(buf, cap,
        "{\"request_id\":\"%s\",\"uhid\":\"%s\","
        "\"identity_key\":\"%s\","
        "\"identity_key_x25519\":\"%s\","
        "\"pre_key_id\":%d,\"pre_key\":\"%s\","
        "\"signed_pre_key_id\":%d,\"signed_pre_key\":\"%s\","
        "\"signed_pre_key_signature\":\"%s\"}",
        id_canonical, bundle->uhid,
        ik_b64,
        ikx_b64,
        (int)bundle->pre_key_id, pk_b64,
        (int)bundle->signed_pre_key_id, spk_b64,
        sig_b64);
    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

bool aethernet_pre_key_response_payload_serialize(const uint8_t request_id[AETHERNET_PACKET_ID_SIZE],
                                                  const aethernet_pre_key_exchange_bundle_t *bundle,
                                                  uint8_t **out_json,
                                                  uint32_t *out_len) {
    if (!request_id || !bundle || !bundle->uhid || !out_json || !out_len) return false;
    return encode_response_payload(request_id, bundle, out_json, out_len);
}

// ─── Directed send ───────────────────────────────────────

// Build and directed-send a pre-key packet of `type` carrying `body` to `peer_uhid`. Takes ownership
// of nothing (copies body into the packet). Returns the delivery result from sender->send (false if
// the host wired no directed send).
static bool send_pre_key_packet(aethernet_pre_key_exchange_service_t *service,
                                aethernet_packet_type_t type,
                                const char *peer_uhid,
                                const uint8_t *body,
                                uint32_t body_len) {
    if (!service->sender->send) return false;  // host wired no directed send — cannot deliver

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) return false;
    pkt->type = (uint8_t)type;
    aethernet_packet_set_source_uhid(pkt, service->sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, peer_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);

    bool delivered = service->sender->send(service->sender, pkt, peer_uhid);
    aethernet_packet_free(pkt);
    return delivered;
}

// ─── Received-bundle cache ───────────────────────────────

// Upsert `bundle` (deep-copied) into the received cache keyed by uhid — replacing any prior entry for
// the same uhid (mirrors the C# ConcurrentDictionary indexer assignment). Returns false on
// allocation failure.
static bool cache_received(aethernet_pre_key_exchange_service_t *service,
                           const aethernet_pre_key_exchange_bundle_t *bundle) {
    for (int i = 0; i < service->received_len; i++) {
        if (strcmp(service->received[i].bundle.uhid, bundle->uhid) == 0) {
            aethernet_pre_key_exchange_bundle_t copy;
            if (!bundle_copy(&copy, bundle)) return false;
            aethernet_pre_key_bundle_free(&service->received[i].bundle);
            service->received[i].bundle = copy;
            return true;
        }
    }
    if (service->received_len == service->received_cap) {
        int new_cap = service->received_cap ? service->received_cap * 2 : 8;
        received_entry_t *grown =
            (received_entry_t *)realloc(service->received, sizeof(*grown) * (size_t)new_cap);
        if (!grown) return false;
        service->received = grown;
        service->received_cap = new_cap;
    }
    aethernet_pre_key_exchange_bundle_t copy;
    if (!bundle_copy(&copy, bundle)) return false;
    service->received[service->received_len].bundle = copy;
    service->received_len++;
    return true;
}

// ─── Handlers ────────────────────────────────────────────

// Handle an inbound PreKeyRequest: if a local bundle is set, directed-send a PreKeyResponse carrying
// it back to the requester. Mirrors the C# HandleRequestAsync.
static bool handle_request(aethernet_pre_key_exchange_service_t *service,
                           const aethernet_mesh_packet_t *packet) {
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;  // malformed → benign drop (C# swallows JsonException)

    const cJSON *jreq = cJSON_GetObjectItemCaseSensitive(body, "request_id");
    const cJSON *jrequester = cJSON_GetObjectItemCaseSensitive(body, "requester_uhid");

    uint8_t request_id[AETHERNET_PACKET_ID_SIZE];
    if (!cJSON_IsString(jreq) || jreq->valuestring == NULL
        || !parse_uuid_pk(jreq->valuestring, request_id)) {
        cJSON_Delete(body);
        return false;
    }

    // No local bundle set → C# returns false and sends nothing.
    if (!service->has_local) {
        cJSON_Delete(body);
        return false;
    }

    // Reply to the payload's requester_uhid if present and non-empty, else the packet source. Copy
    // whichever we choose into an owned buffer BEFORE cJSON_Delete — the cJSON valuestring pointer is
    // freed by cJSON_Delete(body), so using it afterwards would be a use-after-free (crashes the Mac
    // allocator even though Windows offline may pass by luck).
    const char *reply_src =
        (cJSON_IsString(jrequester) && jrequester->valuestring && jrequester->valuestring[0] != '\0')
            ? jrequester->valuestring
            : (packet->source_uhid ? packet->source_uhid : "");
    char *reply_to = dup_cstr_pk(reply_src);
    cJSON_Delete(body);
    if (!reply_to) return false;

    uint8_t *out = NULL;
    uint32_t out_len = 0;
    if (!encode_response_payload(request_id, &service->local, &out, &out_len)) {
        free(reply_to);
        return false;
    }

    bool delivered = send_pre_key_packet(service, AETHERNET_PACKET_TYPE_PREKEY_RESPONSE,
                                         reply_to, out, out_len);
    (void)delivered;  // C# returns true once the reply is dispatched, regardless of delivery result
    free(out);
    free(reply_to);
    return true;
}

// Handle an inbound PreKeyResponse: decode the bundle, cache it by uhid, and fire the callback.
// Mirrors the C# HandleResponse.
static bool handle_response(aethernet_pre_key_exchange_service_t *service,
                            const aethernet_mesh_packet_t *packet) {
    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;  // malformed → benign drop

    const cJSON *jreq  = cJSON_GetObjectItemCaseSensitive(body, "request_id");
    const cJSON *juhid = cJSON_GetObjectItemCaseSensitive(body, "uhid");
    const cJSON *jik   = cJSON_GetObjectItemCaseSensitive(body, "identity_key");
    const cJSON *jikx  = cJSON_GetObjectItemCaseSensitive(body, "identity_key_x25519");
    const cJSON *jpkid = cJSON_GetObjectItemCaseSensitive(body, "pre_key_id");
    const cJSON *jpk   = cJSON_GetObjectItemCaseSensitive(body, "pre_key");
    const cJSON *jspkid= cJSON_GetObjectItemCaseSensitive(body, "signed_pre_key_id");
    const cJSON *jspk  = cJSON_GetObjectItemCaseSensitive(body, "signed_pre_key");
    const cJSON *jsig  = cJSON_GetObjectItemCaseSensitive(body, "signed_pre_key_signature");

    // request_id must parse; uhid must be a present, non-empty string (mirrors the C#
    // string.IsNullOrEmpty(body.Uhid) guard); ids must be numbers; every byte[] field must be a
    // base64 string that decodes to its exact length. Anything else → malformed → benign drop.
    uint8_t request_id[AETHERNET_PACKET_ID_SIZE];
    if (!cJSON_IsString(jreq) || jreq->valuestring == NULL
        || !parse_uuid_pk(jreq->valuestring, request_id)
        || !cJSON_IsString(juhid) || juhid->valuestring == NULL || juhid->valuestring[0] == '\0'
        || !cJSON_IsNumber(jpkid) || !cJSON_IsNumber(jspkid)
        || !cJSON_IsString(jik)  || jik->valuestring  == NULL
        || !cJSON_IsString(jikx) || jikx->valuestring == NULL
        || !cJSON_IsString(jpk)  || jpk->valuestring  == NULL
        || !cJSON_IsString(jspk) || jspk->valuestring == NULL
        || !cJSON_IsString(jsig) || jsig->valuestring == NULL) {
        cJSON_Delete(body);
        return false;
    }

    // Decode all byte arrays into an owned bundle BEFORE cJSON_Delete. Copy uhid into the bundle too
    // — every string/array we keep must be owned before the parse tree is freed (use-after-free
    // otherwise: the Mac allocator crashes on a freed cJSON valuestring even though Windows may pass).
    aethernet_pre_key_exchange_bundle_t bundle;
    memset(&bundle, 0, sizeof(bundle));
    bundle.pre_key_id = jpkid->valueint;
    bundle.signed_pre_key_id = jspkid->valueint;

    bool ok =
        b64_decode_exact(jik->valuestring,  bundle.identity_key, 32) &&
        b64_decode_exact(jikx->valuestring, bundle.identity_key_x25519, 32) &&
        b64_decode_exact(jpk->valuestring,  bundle.pre_key, 32) &&
        b64_decode_exact(jspk->valuestring, bundle.signed_pre_key, 32) &&
        b64_decode_exact(jsig->valuestring, bundle.signed_pre_key_signature, 64);
    if (ok) {
        bundle.uhid = dup_cstr_pk(juhid->valuestring);
        if (!bundle.uhid) ok = false;
    }

    // Copy the packet source into an owned buffer for the callback's from_uhid, again before delete.
    char *from_uhid = ok ? dup_cstr_pk(packet->source_uhid ? packet->source_uhid : "") : NULL;
    if (ok && !from_uhid) ok = false;

    cJSON_Delete(body);

    if (!ok) {
        aethernet_pre_key_bundle_free(&bundle);
        free(from_uhid);
        return false;
    }

    // Cache by uhid (latest replaces), then fire the callback with borrowed pointers.
    if (!cache_received(service, &bundle)) {
        aethernet_pre_key_bundle_free(&bundle);
        free(from_uhid);
        return false;
    }

    if (service->bundle_received_cb) {
        aethernet_pre_key_bundle_received_t evt;
        memcpy(evt.request_id, request_id, AETHERNET_PACKET_ID_SIZE);
        evt.from_uhid = from_uhid;   // owned copy, valid for the call
        evt.bundle = &bundle;        // owned copy, valid for the call
        service->bundle_received_cb(&evt, service->bundle_received_cb_user_data);
    }

    aethernet_pre_key_bundle_free(&bundle);
    free(from_uhid);
    return true;
}

// ─── Public API ──────────────────────────────────────────

aethernet_pre_key_exchange_service_t *aethernet_pre_key_exchange_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_pre_key_exchange_service_t *svc =
        (aethernet_pre_key_exchange_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_pre_key_exchange_service_free(aethernet_pre_key_exchange_service_t *service) {
    if (!service) return;
    if (service->has_local) aethernet_pre_key_bundle_free(&service->local);
    for (int i = 0; i < service->received_len; i++)
        aethernet_pre_key_bundle_free(&service->received[i].bundle);
    free(service->received);
    free(service);
}

bool aethernet_pre_key_exchange_set_local_bundle(aethernet_pre_key_exchange_service_t *service,
                                                 const aethernet_pre_key_exchange_bundle_t *bundle) {
    if (!service || !bundle || !bundle->uhid) return false;
    aethernet_pre_key_exchange_bundle_t copy;
    if (!bundle_copy(&copy, bundle)) return false;
    if (service->has_local) aethernet_pre_key_bundle_free(&service->local);
    service->local = copy;
    service->has_local = true;
    return true;
}

bool aethernet_pre_key_exchange_get_local_bundle(const aethernet_pre_key_exchange_service_t *service,
                                                 aethernet_pre_key_exchange_bundle_t *out) {
    if (!service || !out || !service->has_local) return false;
    return bundle_copy(out, &service->local);
}

bool aethernet_pre_key_exchange_request_bundle(aethernet_pre_key_exchange_service_t *service,
                                               const char *peer_uhid,
                                               uint8_t out_request_id[AETHERNET_PACKET_ID_SIZE]) {
    if (!service || !peer_uhid || peer_uhid[0] == '\0' || !out_request_id) return false;

    uint8_t request_id[AETHERNET_PACKET_ID_SIZE];
    random_uuid_pk(request_id);

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_request_payload(request_id, service->sender->local_uhid, &body, &body_len)) {
        return false;
    }

    bool delivered = send_pre_key_packet(service, AETHERNET_PACKET_TYPE_PREKEY_REQUEST,
                                         peer_uhid, body, body_len);
    free(body);
    if (!delivered) return false;

    memcpy(out_request_id, request_id, AETHERNET_PACKET_ID_SIZE);
    return true;
}

bool aethernet_pre_key_exchange_handle_packet(aethernet_pre_key_exchange_service_t *service,
                                              const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->payload == NULL || packet->payload_len == 0) return false;

    if (packet->type == AETHERNET_PACKET_TYPE_PREKEY_REQUEST)
        return handle_request(service, packet);
    if (packet->type == AETHERNET_PACKET_TYPE_PREKEY_RESPONSE)
        return handle_response(service, packet);
    return false;  // wrong packet type
}

bool aethernet_pre_key_exchange_get_received_bundle(const aethernet_pre_key_exchange_service_t *service,
                                                    const char *uhid,
                                                    aethernet_pre_key_exchange_bundle_t *out) {
    if (!service || !uhid || !out) return false;
    for (int i = 0; i < service->received_len; i++) {
        if (strcmp(service->received[i].bundle.uhid, uhid) == 0)
            return bundle_copy(out, &service->received[i].bundle);
    }
    return false;
}

void aethernet_pre_key_exchange_set_bundle_received_cb(aethernet_pre_key_exchange_service_t *service,
                                                       aethernet_pre_key_bundle_received_cb cb,
                                                       void *user_data) {
    if (!service) return;
    service->bundle_received_cb = cb;
    service->bundle_received_cb_user_data = user_data;
}
