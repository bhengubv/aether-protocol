// SPDX-License-Identifier: MIT
// AetherNet Incentive — generic relay-tip envelope (TipPacket = 24).
//
// C port of AetherNet.Incentive.TipPacketPayload + MeshTipService. See
// include/aethernet/tip_packet.h for the contract. Byte-identical to the C#
// reference and every other language implementation, proven against
// fixtures/tipping/tip_packet_basic.json.

#include "aethernet/tip_packet.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <cjson/cJSON.h>

#include "aethernet/security.h"   /* aethernet_ed25519_sign / _verify */
/* AETHERNET_DEFAULT_TTL / AETHERNET_MAX_UHID_LEN arrive via aethernet/protocol.h
   -> constants.h (the project's transitive-include convention). */

// ── small helpers ─────────────────────────────────────────────────────────

// Duplicate a NUL-terminated string into a fresh malloc'd buffer. NULL input
// duplicates the empty string "" (so canonical building never dereferences NULL).
static char *dup_str(const char *s) {
    if (!s) s = "";
    size_t n = strlen(s);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, s, n + 1);
    return out;
}

// Replace *field with a fresh copy of value; returns false on OOM (leaving the old
// value intact).
static bool set_field(char **field, const char *value) {
    char *copy = dup_str(value);
    if (!copy) return false;
    free(*field);
    *field = copy;
    return true;
}

// Little-endian writers.
static void put_u32_le(uint8_t *p, uint32_t v) {
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
    p[2] = (uint8_t)((v >> 16) & 0xFF);
    p[3] = (uint8_t)((v >> 24) & 0xFF);
}

static void put_i64_le(uint8_t *p, int64_t v) {
    uint64_t u = (uint64_t)v;
    for (int i = 0; i < 8; i++) p[i] = (uint8_t)((u >> (8 * i)) & 0xFF);
}

static int hex_nibble(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Parse two hex chars into a byte; returns -1 on a non-hex char.
static int hex_byte(const char *s) {
    int hi = hex_nibble(s[0]);
    int lo = hex_nibble(s[1]);
    if (hi < 0 || lo < 0) return -1;
    return (hi << 4) | lo;
}

// Extract the integer value of a top-level "key": <int> from raw JSON text, with
// FULL int64 precision (cJSON stores numbers as double, which silently truncates
// values above 2^53). Returns true on success.
static bool extract_i64_from_json(const char *json, const char *key, int64_t *out) {
    char needle[128];
    int needle_n = snprintf(needle, sizeof(needle), "\"%s\"", key);
    const char *p = strstr(json, needle);
    if (!p) return false;
    p += needle_n;
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    if (*p != ':') return false;
    p++;
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    char *end = NULL;
    long long v = strtoll(p, &end, 10);
    if (end == p) return false;
    *out = (int64_t)v;
    return true;
}

// ── .NET GUID <-> dashed-string conversion (mixed-endian in-memory order) ───
//
// System.Guid.TryWriteBytes / new Guid(string) store the first three groups
// little-endian: Data1 (4 bytes, reversed), Data2 (2 bytes, reversed), Data3
// (2 bytes, reversed); the final 8 bytes (Data4) are stored as-is. The textual
// form "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" lists the groups big-endian, so the
// byte at text position k maps to the in-memory layout below.

// Parse a 36-char dashed GUID into 16 bytes in .NET in-memory order. Returns true
// on success. out must be 16 bytes.
static bool guid_parse_dotnet(const char *guid, uint8_t out[16]) {
    if (!guid) return false;
    if (strlen(guid) != 36) return false;
    if (guid[8] != '-' || guid[13] != '-' || guid[18] != '-' || guid[23] != '-') return false;

    // Collect the 32 hex digits (skipping dashes) into 16 big-endian bytes first.
    uint8_t be[16];
    int bi = 0;
    for (int i = 0; i < 36; ) {
        if (guid[i] == '-') { i++; continue; }
        int b = hex_byte(guid + i);
        if (b < 0) return false;
        if (bi >= 16) return false;
        be[bi++] = (uint8_t)b;
        i += 2;
    }
    if (bi != 16) return false;

    // Reorder big-endian text bytes -> .NET in-memory layout.
    // Data1: be[0..3] reversed.
    out[0] = be[3]; out[1] = be[2]; out[2] = be[1]; out[3] = be[0];
    // Data2: be[4..5] reversed.
    out[4] = be[5]; out[5] = be[4];
    // Data3: be[6..7] reversed.
    out[6] = be[7]; out[7] = be[6];
    // Data4: be[8..15] as-is.
    for (int i = 8; i < 16; i++) out[i] = be[i];
    return true;
}

// Format 16 .NET in-memory bytes back into a 36-char dashed GUID string + NUL
// (caller-allocated, >= 37 bytes).
static void guid_format_dotnet(const uint8_t in[16], char out[37]) {
    static const char hexd[] = "0123456789abcdef";
    // Reconstruct the big-endian text byte order from the in-memory layout.
    uint8_t be[16];
    be[0] = in[3]; be[1] = in[2]; be[2] = in[1]; be[3] = in[0];
    be[4] = in[5]; be[5] = in[4];
    be[6] = in[7]; be[7] = in[6];
    for (int i = 8; i < 16; i++) be[i] = in[i];

    int o = 0;
    for (int i = 0; i < 16; i++) {
        if (i == 4 || i == 6 || i == 8 || i == 10) out[o++] = '-';
        out[o++] = hexd[(be[i] >> 4) & 0xF];
        out[o++] = hexd[be[i] & 0xF];
    }
    out[o] = '\0';
}

// ── lifecycle ───────────────────────────────────────────────────────────────

void aethernet_tip_packet_init(aethernet_tip_packet_t *tip) {
    if (!tip) return;
    memset(tip, 0, sizeof(*tip));
}

void aethernet_tip_packet_free_fields(aethernet_tip_packet_t *tip) {
    if (!tip) return;
    free(tip->tipper_uhid);
    free(tip->recipient_uhid);
    free(tip->amount);
    free(tip->traffic_type);
    memset(tip, 0, sizeof(*tip));
}

bool aethernet_tip_packet_set_tipper(aethernet_tip_packet_t *tip, const char *uhid) {
    if (!tip) return false;
    return set_field(&tip->tipper_uhid, uhid);
}

bool aethernet_tip_packet_set_recipient(aethernet_tip_packet_t *tip, const char *uhid) {
    if (!tip) return false;
    return set_field(&tip->recipient_uhid, uhid);
}

bool aethernet_tip_packet_set_amount(aethernet_tip_packet_t *tip, const char *amount) {
    if (!tip) return false;
    return set_field(&tip->amount, amount);
}

bool aethernet_tip_packet_set_traffic_type(aethernet_tip_packet_t *tip, const char *traffic_type) {
    if (!tip) return false;
    return set_field(&tip->traffic_type, traffic_type);
}

void aethernet_tip_packet_set_reference_id(aethernet_tip_packet_t *tip, const uint8_t reference_id[16]) {
    if (!tip || !reference_id) return;
    memcpy(tip->reference_id, reference_id, AETHERNET_TIP_REFERENCE_ID_SIZE);
    tip->has_reference_id = true;
}

bool aethernet_tip_packet_set_reference_id_guid(aethernet_tip_packet_t *tip, const char *guid) {
    if (!tip) return false;
    uint8_t buf[16];
    if (!guid_parse_dotnet(guid, buf)) return false;
    memcpy(tip->reference_id, buf, AETHERNET_TIP_REFERENCE_ID_SIZE);
    tip->has_reference_id = true;
    return true;
}

// ── canonical bytes ─────────────────────────────────────────────────────────

uint8_t *aethernet_tip_packet_build_canonical(const aethernet_tip_packet_t *tip, size_t *out_len) {
    if (!tip || !out_len) return NULL;

    const char *tipper    = tip->tipper_uhid    ? tip->tipper_uhid    : "";
    const char *recipient = tip->recipient_uhid ? tip->recipient_uhid : "";
    const char *amount    = tip->amount         ? tip->amount         : "";
    const char *traffic   = tip->traffic_type   ? tip->traffic_type   : "";

    size_t tipper_len    = strlen(tipper);
    size_t recipient_len = strlen(recipient);
    size_t amount_len    = strlen(amount);
    size_t traffic_len   = strlen(traffic);

    size_t total = 4 + tipper_len
                 + 4 + recipient_len
                 + 4 + amount_len
                 + 4 + traffic_len
                 + AETHERNET_TIP_REFERENCE_ID_SIZE
                 + 8;

    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) return NULL;

    size_t off = 0;

    put_u32_le(buf + off, (uint32_t)tipper_len);    off += 4;
    memcpy(buf + off, tipper, tipper_len);          off += tipper_len;

    put_u32_le(buf + off, (uint32_t)recipient_len); off += 4;
    memcpy(buf + off, recipient, recipient_len);    off += recipient_len;

    put_u32_le(buf + off, (uint32_t)amount_len);    off += 4;
    memcpy(buf + off, amount, amount_len);          off += amount_len;

    put_u32_le(buf + off, (uint32_t)traffic_len);   off += 4;
    memcpy(buf + off, traffic, traffic_len);        off += traffic_len;

    // ReferenceId — 16 bytes, all-zero when absent, .NET GUID byte order otherwise.
    if (tip->has_reference_id) {
        memcpy(buf + off, tip->reference_id, AETHERNET_TIP_REFERENCE_ID_SIZE);
    } else {
        memset(buf + off, 0, AETHERNET_TIP_REFERENCE_ID_SIZE);
    }
    off += AETHERNET_TIP_REFERENCE_ID_SIZE;

    // Timestamp — Unix milliseconds, little-endian int64.
    put_i64_le(buf + off, tip->timestamp_unix_ms); off += 8;

    *out_len = off;
    return buf;
}

// ── sign / verify ───────────────────────────────────────────────────────────

bool aethernet_tip_packet_sign(aethernet_tip_packet_t *tip, const uint8_t *private_key) {
    if (!tip || !private_key) return false;

    size_t len = 0;
    uint8_t *canonical = aethernet_tip_packet_build_canonical(tip, &len);
    if (!canonical) return false;

    uint8_t sig[AETHERNET_TIP_SIGNATURE_SIZE];
    bool ok = aethernet_ed25519_sign(private_key, canonical, len, sig);

    aethernet_zeroize(canonical, len);
    free(canonical);
    if (!ok) return false;

    memcpy(tip->signature, sig, AETHERNET_TIP_SIGNATURE_SIZE);
    tip->signature_len = AETHERNET_TIP_SIGNATURE_SIZE;
    return true;
}

bool aethernet_tip_packet_verify(const aethernet_tip_packet_t *tip, const uint8_t *public_key) {
    if (!tip || !public_key) return false;
    if (tip->signature_len != AETHERNET_TIP_SIGNATURE_SIZE) return false;

    size_t len = 0;
    uint8_t *canonical = aethernet_tip_packet_build_canonical(tip, &len);
    if (!canonical) return false;

    bool ok = aethernet_ed25519_verify(public_key, canonical, len, tip->signature);
    free(canonical);
    return ok;
}

// ── JSON wire form ──────────────────────────────────────────────────────────

char *aethernet_tip_packet_to_json(const aethernet_tip_packet_t *tip) {
    if (!tip) return NULL;

    cJSON *obj = cJSON_CreateObject();
    if (!obj) return NULL;

    cJSON_AddStringToObject(obj, "tipper_uhid",    tip->tipper_uhid    ? tip->tipper_uhid    : "");
    cJSON_AddStringToObject(obj, "recipient_uhid", tip->recipient_uhid ? tip->recipient_uhid : "");
    cJSON_AddStringToObject(obj, "amount",         tip->amount         ? tip->amount         : "");
    cJSON_AddStringToObject(obj, "traffic_type",   tip->traffic_type   ? tip->traffic_type   : "");

    if (tip->has_reference_id) {
        char guid[37];
        guid_format_dotnet(tip->reference_id, guid);
        cJSON_AddStringToObject(obj, "reference_id", guid);
    } else {
        cJSON_AddNullToObject(obj, "reference_id");
    }

    // timestamp is a Unix-ms i64 — emit as a raw, exact integer token (cJSON's
    // double number type would round-trip large values lossily; bare-integer also
    // matches the Go/C# wire form).
    {
        char ts_str[24];
        snprintf(ts_str, sizeof(ts_str), "%lld", (long long)tip->timestamp_unix_ms);
        cJSON_AddRawToObject(obj, "timestamp", ts_str);
    }

    if (tip->signature_len == AETHERNET_TIP_SIGNATURE_SIZE) {
        static const char hexd[] = "0123456789abcdef";
        char hex[AETHERNET_TIP_SIGNATURE_SIZE * 2 + 1];
        for (size_t i = 0; i < AETHERNET_TIP_SIGNATURE_SIZE; i++) {
            hex[i * 2]     = hexd[(tip->signature[i] >> 4) & 0xF];
            hex[i * 2 + 1] = hexd[tip->signature[i] & 0xF];
        }
        hex[AETHERNET_TIP_SIGNATURE_SIZE * 2] = '\0';
        cJSON_AddStringToObject(obj, "signature", hex);
    }

    char *out = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    return out;
}

bool aethernet_tip_packet_from_json(const char *json, size_t json_len, aethernet_tip_packet_t *out_tip) {
    if (!json || !out_tip) return false;

    cJSON *obj = cJSON_ParseWithLength(json, json_len);
    if (!obj) return false;

    aethernet_tip_packet_init(out_tip);
    bool ok = true;

    const cJSON *jtipper    = cJSON_GetObjectItemCaseSensitive(obj, "tipper_uhid");
    const cJSON *jrecipient = cJSON_GetObjectItemCaseSensitive(obj, "recipient_uhid");
    const cJSON *jamount    = cJSON_GetObjectItemCaseSensitive(obj, "amount");
    const cJSON *jtraffic   = cJSON_GetObjectItemCaseSensitive(obj, "traffic_type");
    const cJSON *jref       = cJSON_GetObjectItemCaseSensitive(obj, "reference_id");
    const cJSON *jts        = cJSON_GetObjectItemCaseSensitive(obj, "timestamp");
    const cJSON *jsig       = cJSON_GetObjectItemCaseSensitive(obj, "signature");

    if (!cJSON_IsString(jtipper) || !cJSON_IsString(jrecipient)) {
        ok = false; // required fields
    }

    if (ok && !aethernet_tip_packet_set_tipper(out_tip, jtipper->valuestring))       ok = false;
    if (ok && !aethernet_tip_packet_set_recipient(out_tip, jrecipient->valuestring)) ok = false;
    if (ok) {
        const char *amt = cJSON_IsString(jamount) ? jamount->valuestring : "";
        if (!aethernet_tip_packet_set_amount(out_tip, amt)) ok = false;
    }
    if (ok) {
        const char *tt = cJSON_IsString(jtraffic) ? jtraffic->valuestring : "";
        if (!aethernet_tip_packet_set_traffic_type(out_tip, tt)) ok = false;
    }

    if (ok && cJSON_IsString(jref)) {
        // A malformed GUID string is treated as "no reference id" rather than a hard
        // parse failure (matches the lenient cross-language deserialisers).
        aethernet_tip_packet_set_reference_id_guid(out_tip, jref->valuestring);
    }

    if (ok) {
        // Parse timestamp from the raw text with full i64 precision (cJSON's double
        // would truncate large values). Falls back to the parsed number otherwise.
        int64_t ts = 0;
        if (extract_i64_from_json(json, "timestamp", &ts)) {
            out_tip->timestamp_unix_ms = ts;
        } else if (cJSON_IsNumber(jts)) {
            out_tip->timestamp_unix_ms = (int64_t)jts->valuedouble;
        }
    }

    if (ok && cJSON_IsString(jsig)) {
        const char *h = jsig->valuestring;
        size_t hn = strlen(h);
        if (hn == AETHERNET_TIP_SIGNATURE_SIZE * 2) {
            bool sig_ok = true;
            for (size_t i = 0; i < AETHERNET_TIP_SIGNATURE_SIZE; i++) {
                int b = hex_byte(h + i * 2);
                if (b < 0) { sig_ok = false; break; }
                out_tip->signature[i] = (uint8_t)b;
            }
            if (sig_ok) out_tip->signature_len = AETHERNET_TIP_SIGNATURE_SIZE;
        }
    }

    cJSON_Delete(obj);
    if (!ok) aethernet_tip_packet_free_fields(out_tip);
    return ok;
}

// ── MeshTipService ──────────────────────────────────────────────────────────

void aethernet_mesh_tip_service_init(aethernet_mesh_tip_service_t *svc, void *user_data) {
    if (!svc) return;
    memset(svc, 0, sizeof(*svc));
    svc->user_data   = user_data;
    svc->default_ttl = AETHERNET_DEFAULT_TTL; // 7
}

bool aethernet_mesh_tip_service_send(aethernet_mesh_tip_service_t *svc,
                                     const char *recipient_uhid,
                                     const char *amount,
                                     const char *traffic_type,
                                     const uint8_t *reference_id,
                                     int64_t timestamp_unix_ms,
                                     aethernet_mesh_packet_t **out_packet) {
    if (!svc || !svc->local_uhid || !svc->identity_sign || !svc->sign_packet ||
        !svc->send || !svc->broadcast || !recipient_uhid) {
        return false;
    }

    const char *local = svc->local_uhid(svc->user_data);
    if (!local) return false;

    // Build the payload.
    aethernet_tip_packet_t payload;
    aethernet_tip_packet_init(&payload);
    bool built = aethernet_tip_packet_set_tipper(&payload, local) &&
                 aethernet_tip_packet_set_recipient(&payload, recipient_uhid) &&
                 aethernet_tip_packet_set_amount(&payload, amount) &&
                 aethernet_tip_packet_set_traffic_type(&payload, traffic_type);
    if (!built) { aethernet_tip_packet_free_fields(&payload); return false; }

    payload.timestamp_unix_ms = timestamp_unix_ms;
    if (reference_id) aethernet_tip_packet_set_reference_id(&payload, reference_id);

    // Sign the payload's canonical bytes with the local identity key (real Ed25519).
    size_t canon_len = 0;
    uint8_t *canon = aethernet_tip_packet_build_canonical(&payload, &canon_len);
    if (!canon) { aethernet_tip_packet_free_fields(&payload); return false; }

    bool signed_ok = svc->identity_sign(svc->user_data, canon, canon_len, payload.signature);
    aethernet_zeroize(canon, canon_len);
    free(canon);
    if (!signed_ok) { aethernet_tip_packet_free_fields(&payload); return false; }
    payload.signature_len = AETHERNET_TIP_SIGNATURE_SIZE;

    // Serialise the body.
    char *body = aethernet_tip_packet_to_json(&payload);
    aethernet_tip_packet_free_fields(&payload);
    if (!body) return false;

    // Wrap in a MeshPacket.
    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) { free(body); return false; }
    pkt->type     = (uint8_t)AETHERNET_PACKET_TYPE_TIP_PACKET;
    pkt->ttl      = svc->default_ttl;
    pkt->priority = 0;

    bool wrapped = aethernet_packet_set_source_uhid(pkt, local) &&
                   aethernet_packet_set_destination_uhid(pkt, recipient_uhid) &&
                   aethernet_packet_set_payload(pkt, (const uint8_t *)body, strlen(body));
    free(body);
    if (!wrapped) { aethernet_packet_free(pkt); return false; }

    // Sign the enclosing MeshPacket (fills nonce/timestamp + envelope signature).
    if (!svc->sign_packet(svc->user_data, pkt)) { aethernet_packet_free(pkt); return false; }

    // Route toward the recipient: unicast over a discovered route, else broadcast.
    bool sent_ok;
    if (svc->find_next_hop) {
        char next_hop[AETHERNET_MAX_UHID_LEN + 1];
        if (svc->find_next_hop(svc->user_data, recipient_uhid, next_hop, sizeof(next_hop))) {
            sent_ok = svc->send(svc->user_data, pkt, next_hop);
            if (!sent_ok) { aethernet_packet_free(pkt); return false; }
            if (out_packet) *out_packet = pkt; else aethernet_packet_free(pkt);
            return true;
        }
    }

    (void)svc->broadcast(svc->user_data, pkt);
    if (out_packet) *out_packet = pkt; else aethernet_packet_free(pkt);
    return true;
}

bool aethernet_mesh_tip_service_handle(aethernet_mesh_tip_service_t *svc,
                                       const aethernet_mesh_packet_t *packet) {
    if (!svc || !packet) return false;
    if (!svc->local_uhid || !svc->send || !svc->broadcast) return false;
    if (packet->type != (uint8_t)AETHERNET_PACKET_TYPE_TIP_PACKET) return false;

    // 1. Deserialise the payload. A malformed payload is dropped.
    aethernet_tip_packet_t payload;
    if (!packet->payload || packet->payload_len == 0) return false;
    if (!aethernet_tip_packet_from_json((const char *)packet->payload, packet->payload_len, &payload)) {
        return false;
    }
    if (!payload.tipper_uhid || payload.tipper_uhid[0] == '\0' ||
        !payload.recipient_uhid || payload.recipient_uhid[0] == '\0') {
        aethernet_tip_packet_free_fields(&payload);
        return false;
    }

    // 2. Best-effort signature check: an Ed25519 signature is exactly 64 bytes.
    if (payload.signature_len != AETHERNET_TIP_SIGNATURE_SIZE) {
        aethernet_tip_packet_free_fields(&payload);
        return false;
    }

    // 3. Hand to the host's settlement provider (default no-op settles nothing). A
    //    settlement error is ignored here — it must not break relaying.
    if (svc->settle) {
        (void)svc->settle(svc->user_data, &payload);
    }

    aethernet_tip_packet_free_fields(&payload);

    // 4. Relay onward toward the addressed recipient if this node is not the
    //    destination and the packet may still be forwarded.
    const char *local = svc->local_uhid(svc->user_data);
    const char *dest  = packet->destination_uhid;
    bool is_dest = (local && dest && strcmp(dest, local) == 0);

    if (!is_dest && aethernet_packet_can_forward(packet) && dest) {
        if (svc->find_next_hop) {
            char next_hop[AETHERNET_MAX_UHID_LEN + 1];
            if (svc->find_next_hop(svc->user_data, dest, next_hop, sizeof(next_hop))) {
                if (!svc->send(svc->user_data, packet, next_hop)) return false;
                return true;
            }
        }
        (void)svc->broadcast(svc->user_data, packet);
    }

    return true;
}
