// SPDX-License-Identifier: MIT
// aether-space breadcrumb WIRE binding (PacketType SpaceBreadcrumb 40). See aethernet/space_breadcrumb.h.
//
// Thin transport: broadcast a locally-dropped breadcrumb (source -> "*") and surface inbound
// breadcrumbs via a callback. Mirrors the green C# SpaceBreadcrumbService.
//
// The payload is encoded with snprintf (byte-identical to the C# System.Text.Json output — field order
// content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature; created_at_ms bare
// int64; ttl_hours + type bare ints; signature STANDARD base64, "" when empty) and decoded on receive
// with the vendored cJSON. Byte-identity gate: fixtures/space/vectors.json.

#include "aethernet/space_breadcrumb.h"
#include "aethernet/constants.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <cjson/cJSON.h>

// ─── Base64 (RFC 4648 §4, standard '+/' alphabet, '=' padding) ───────────────
// Self-contained encode + decode — the signature byte array goes out as STANDARD base64 and inbound
// signatures are decoded back the same way. Mirrors the local pair in prekey.c (each wire path carries
// its own copy rather than exporting a shared symbol).

static const char s_b64_chars_sb[] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

// Encode data[0..len) into out (must hold ((len+2)/3)*4 + 1 bytes incl. NUL). Null-terminates. A zero
// length yields the empty string "" (out[0] = '\0').
static void b64_encode_into_sb(const uint8_t *data, size_t len, char *out) {
    size_t olen = ((len + 2) / 3) * 4;
    size_t i = 0, j = 0;
    while (i < len) {
        uint32_t a = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t b = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t c = (uint32_t)(i < len ? data[i++] : 0);
        uint32_t triple = (a << 16) | (b << 8) | c;
        out[j++] = s_b64_chars_sb[(triple >> 18) & 0x3F];
        out[j++] = s_b64_chars_sb[(triple >> 12) & 0x3F];
        out[j++] = s_b64_chars_sb[(triple >>  6) & 0x3F];
        out[j++] = s_b64_chars_sb[ triple        & 0x3F];
    }
    size_t pad = (len % 3) ? (3 - (len % 3)) : 0;
    for (size_t p = 0; p < pad; p++)
        out[olen - 1 - p] = '=';
    out[olen] = '\0';
}

// The base64 length (excluding NUL) of `n` raw bytes.
static size_t b64_len_sb(size_t n) { return ((n + 2) / 3) * 4; }

// Map a single base64 character to its 6-bit value, or -1 if not a base64 digit.
static int b64_val_sb(char c) {
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

// Decode a NUL-terminated STANDARD base64 string into a freshly malloc'd buffer. On success writes the
// buffer to *out (caller frees) and the byte count to *out_len, returns true. The empty string decodes
// to a zero-length result (*out is a 1-byte allocation, *out_len 0). Returns false on malformed input
// (bad length, stray characters, misplaced padding) or OOM. `s` must be non-NULL.
static bool b64_decode_alloc_sb(const char *s, uint8_t **out, uint32_t *out_len) {
    size_t slen = strlen(s);
    if (slen % 4 != 0) return false;  // standard base64 is always a multiple of 4
    if (slen == 0) {
        uint8_t *buf = (uint8_t *)malloc(1);
        if (!buf) return false;
        *out = buf;
        *out_len = 0;
        return true;
    }

    size_t pad = 0;
    if (s[slen - 1] == '=') pad++;
    if (slen >= 2 && s[slen - 2] == '=') pad++;
    size_t decoded_len = (slen / 4) * 3 - pad;

    uint8_t *buf = (uint8_t *)malloc(decoded_len ? decoded_len : 1);
    if (!buf) return false;

    size_t oi = 0;
    for (size_t i = 0; i < slen; i += 4) {
        int q0 = b64_val_sb(s[i]);
        int q1 = b64_val_sb(s[i + 1]);
        if (q0 < 0 || q1 < 0) { free(buf); return false; }

        bool p2 = (s[i + 2] == '=');
        bool p3 = (s[i + 3] == '=');
        // Padding may only occur in the final quartet, and '=' cannot precede a non-'='.
        if ((p2 || p3) && i + 4 != slen) { free(buf); return false; }
        if (p2 && !p3) { free(buf); return false; }

        int q2 = p2 ? 0 : b64_val_sb(s[i + 2]);
        int q3 = p3 ? 0 : b64_val_sb(s[i + 3]);
        if ((!p2 && q2 < 0) || (!p3 && q3 < 0)) { free(buf); return false; }

        uint32_t triple = ((uint32_t)q0 << 18) | ((uint32_t)q1 << 12) |
                          ((uint32_t)q2 << 6)  |  (uint32_t)q3;
        if (oi < decoded_len) buf[oi++] = (uint8_t)((triple >> 16) & 0xFF);
        if (!p2 && oi < decoded_len) buf[oi++] = (uint8_t)((triple >> 8) & 0xFF);
        if (!p3 && oi < decoded_len) buf[oi++] = (uint8_t)(triple & 0xFF);
    }
    if (oi != decoded_len) { free(buf); return false; }
    *out = buf;
    *out_len = (uint32_t)decoded_len;
    return true;
}

// ─── Internal state ──────────────────────────────────────

struct aethernet_space_breadcrumb_service {
    aethernet_mesh_sender_t *sender;
    aethernet_space_breadcrumb_received_cb received_cb;
    void                                  *received_cb_user_data;
};

static char *dup_cstr_sb(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// Free the owned heap fields of a STACK breadcrumb built locally in handle_packet and zero it. We
// deliberately do NOT call aethernet_space_breadcrumb_free() here: that public helper ends with
// free(crumb), which is only valid for a heap-allocated breadcrumb — calling it on a stack struct is a
// fatal free of a non-heap pointer. This frees just the members.
static void free_crumb_fields_sb(aethernet_space_breadcrumb_t *c) {
    if (!c) return;
    free(c->content_hash);
    free(c->geo_hash);
    free(c->anchor_uhid);
    free(c->signature);
    memset(c, 0, sizeof(*c));
}

// ─── Serialization ───────────────────────────────────────

// Encode a SpaceBreadcrumb payload as canonical JSON. Field order content_hash, geo_hash, anchor_uhid,
// created_at_ms, ttl_hours, type, signature; no whitespace; bare-int created_at_ms/ttl_hours/type;
// signature STANDARD base64 (empty "" when unsigned). The string fields are interpolated verbatim (as
// sos.c / channels.c do) so ASCII hashes/uhids reproduce byte-for-byte. Byte-identity gate:
// fixtures/space/vectors.json.
static bool encode_breadcrumb_payload(const aethernet_space_breadcrumb_t *b,
                                      uint8_t **out_payload,
                                      uint32_t *out_len) {
    const char *content_hash = b->content_hash ? b->content_hash : "";
    const char *geo_hash     = b->geo_hash     ? b->geo_hash     : "";
    const char *anchor_uhid  = b->anchor_uhid  ? b->anchor_uhid  : "";

    // Encode the signature to STANDARD base64 (empty string when unsigned). Allocate the exact size
    // for the b64 text so an arbitrarily long signature is handled without a fixed cap.
    size_t sig_raw = (b->signature && b->signature_len) ? (size_t)b->signature_len : 0;
    size_t sig_b64_len = b64_len_sb(sig_raw);            // 0 for an empty signature
    char *sig_b64 = (char *)malloc(sig_b64_len + 1);
    if (!sig_b64) return false;
    if (sig_raw) {
        b64_encode_into_sb(b->signature, sig_raw, sig_b64);
    } else {
        sig_b64[0] = '\0';
    }

    // Size the buffer generously: the full fixed frame (keys + punctuation) + each variable string
    // field + the base64 signature text + int32/int64 room + margin. The fixed frame
    //   {"content_hash":"","geo_hash":"","anchor_uhid":"","created_at_ms":,"ttl_hours":,"type":,"signature":""}
    // is ~104 bytes; 20 (int64) + 11 (int) + 11 (int) covers the numbers; +64 margin. This is the
    // buffer a prior C port under-sized — hence the explicit strlen of every variable field below.
    size_t cap = 192
               + strlen(content_hash)
               + strlen(geo_hash)
               + strlen(anchor_uhid)
               + sig_b64_len
               + 64;
    char *buf = (char *)malloc(cap);
    if (!buf) { free(sig_b64); return false; }

    int n = snprintf(buf, cap,
        "{\"content_hash\":\"%s\",\"geo_hash\":\"%s\",\"anchor_uhid\":\"%s\","
        "\"created_at_ms\":%lld,\"ttl_hours\":%d,\"type\":%d,\"signature\":\"%s\"}",
        content_hash, geo_hash, anchor_uhid,
        (long long)b->created_at_ms, (int)b->ttl_hours, (int)b->type,
        sig_b64);
    free(sig_b64);
    if (n < 0 || (size_t)n >= cap) { free(buf); return false; }

    *out_payload = (uint8_t *)buf;
    *out_len = (uint32_t)n;
    return true;
}

bool aethernet_space_breadcrumb_payload_serialize(const aethernet_space_breadcrumb_t *breadcrumb,
                                                  uint8_t **out_json,
                                                  uint32_t *out_len) {
    if (!breadcrumb || !out_json || !out_len) return false;
    return encode_breadcrumb_payload(breadcrumb, out_json, out_len);
}

// ─── Public API ──────────────────────────────────────────

aethernet_space_breadcrumb_service_t *aethernet_space_breadcrumb_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_space_breadcrumb_service_t *svc =
        (aethernet_space_breadcrumb_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_space_breadcrumb_service_free(aethernet_space_breadcrumb_service_t *service) {
    if (!service) return;
    free(service);
}

void aethernet_space_breadcrumb_set_received_cb(aethernet_space_breadcrumb_service_t *service,
                                                aethernet_space_breadcrumb_received_cb cb,
                                                void *user_data) {
    if (!service) return;
    service->received_cb = cb;
    service->received_cb_user_data = user_data;
}

bool aethernet_space_breadcrumb_broadcast(aethernet_space_breadcrumb_service_t *service,
                                          const aethernet_space_breadcrumb_t *breadcrumb,
                                          int *out_count) {
    if (!service || !breadcrumb) return false;
    if (!service->sender->broadcast) return false;  // host wired no broadcast — cannot deliver

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!encode_breadcrumb_payload(breadcrumb, &body, &body_len)) return false;

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return false; }
    pkt->type = (uint8_t)AETHERNET_PACKET_TYPE_SPACE_BREADCRUMB;
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

bool aethernet_space_breadcrumb_handle_packet(aethernet_space_breadcrumb_service_t *service,
                                              const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_SPACE_BREADCRUMB) return false;
    if (packet->payload == NULL || packet->payload_len == 0) return false;

    cJSON *body = cJSON_ParseWithLength((const char *)packet->payload, packet->payload_len);
    if (body == NULL) return false;  // malformed → benign drop (C# swallows JsonException)

    const cJSON *jch  = cJSON_GetObjectItemCaseSensitive(body, "content_hash");
    const cJSON *jgeo = cJSON_GetObjectItemCaseSensitive(body, "geo_hash");
    const cJSON *janc = cJSON_GetObjectItemCaseSensitive(body, "anchor_uhid");
    const cJSON *jcre = cJSON_GetObjectItemCaseSensitive(body, "created_at_ms");
    const cJSON *jttl = cJSON_GetObjectItemCaseSensitive(body, "ttl_hours");
    const cJSON *jtyp = cJSON_GetObjectItemCaseSensitive(body, "type");
    const cJSON *jsig = cJSON_GetObjectItemCaseSensitive(body, "signature");

    // content_hash must be a present, non-empty string (mirrors the C# string.IsNullOrEmpty guard).
    // Anything else → malformed → benign drop.
    if (!cJSON_IsString(jch) || jch->valuestring == NULL || jch->valuestring[0] == '\0') {
        cJSON_Delete(body);
        return false;
    }

    // Copy every string we keep, and decode the signature, into owned buffers BEFORE cJSON_Delete —
    // the cJSON valuestring pointers are freed by cJSON_Delete(body), so using them afterwards would be
    // a use-after-free (crashes the Mac allocator even though Windows offline may pass by luck).
    aethernet_space_breadcrumb_t crumb;
    memset(&crumb, 0, sizeof(crumb));
    bool ok = true;

    crumb.content_hash = dup_cstr_sb(jch->valuestring);
    if (!crumb.content_hash) ok = false;

    if (ok) {
        const char *geo = (cJSON_IsString(jgeo) && jgeo->valuestring) ? jgeo->valuestring : "";
        crumb.geo_hash = dup_cstr_sb(geo);
        if (!crumb.geo_hash) ok = false;
    }
    if (ok) {
        const char *anc = (cJSON_IsString(janc) && janc->valuestring) ? janc->valuestring : "";
        crumb.anchor_uhid = dup_cstr_sb(anc);
        if (!crumb.anchor_uhid) ok = false;
    }
    if (ok) {
        // Numbers use valuedouble to preserve the full int64 range (cJSON's valueint is a 32-bit int,
        // too narrow for a Unix-ms timestamp). Missing/non-number fields default to 0, matching the C#
        // deserializer's default(long)/default(int) behaviour.
        crumb.created_at_ms = cJSON_IsNumber(jcre) ? (int64_t)jcre->valuedouble : 0;
        crumb.ttl_hours     = cJSON_IsNumber(jttl) ? (int32_t)jttl->valuedouble : 0;
        crumb.type          = cJSON_IsNumber(jtyp) ? (uint8_t)(int)jtyp->valuedouble : 0;

        // signature: STANDARD base64 → raw bytes; empty string / missing → NULL, len 0 (unsigned).
        if (cJSON_IsString(jsig) && jsig->valuestring && jsig->valuestring[0] != '\0') {
            uint8_t *sig = NULL;
            uint32_t sig_len = 0;
            if (b64_decode_alloc_sb(jsig->valuestring, &sig, &sig_len)) {
                crumb.signature = sig;
                crumb.signature_len = sig_len;
            } else {
                ok = false;  // malformed base64 → benign drop
            }
        }
    }

    cJSON_Delete(body);

    if (!ok) {
        free_crumb_fields_sb(&crumb);
        return false;
    }

    if (service->received_cb) {
        service->received_cb(&crumb, service->received_cb_user_data);
    }

    free_crumb_fields_sb(&crumb);
    return true;
}
