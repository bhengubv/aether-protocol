// SPDX-License-Identifier: MIT
// Decentralised multi-device sync (no server): SyncRecord binary envelope,
// deterministic last-write-wins reconciliation, and signed DeviceLink
// membership. A faithful mirror of the C# reference under
// src/AetherNet.Security/Sync/ ({SyncRecord,SyncRecordSerializer,SyncReconciler,
// DeviceLink}.cs) — byte-identical wire, pinned by fixtures/sync/vectors.json.
//
// Wire conventions (shared with the rest of the SDK): integers little-endian;
// strings are a u16 byte-length + UTF-8; a SyncRecord's record_id is 16 bytes
// big-endian (the UUID exactly as written). DeviceLink signatures reuse the
// SDK's libsodium Ed25519 (aethernet_ed25519_sign / _verify, src/security.c),
// which is deterministic — so a link's serialized bytes match across SDKs.

#include "aethernet/sync.h"
#include "aethernet/security.h"   // aethernet_ed25519_sign / aethernet_ed25519_verify

#include <stdlib.h>
#include <string.h>

/* ─── Little-endian integer read/write helpers ──────────────────────────── */

static void put_u16_le(uint8_t *p, uint16_t v) {
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
}

static void put_i32_le(uint8_t *p, int32_t v) {
    uint32_t u = (uint32_t)v;
    p[0] = (uint8_t)(u & 0xFF);
    p[1] = (uint8_t)((u >> 8) & 0xFF);
    p[2] = (uint8_t)((u >> 16) & 0xFF);
    p[3] = (uint8_t)((u >> 24) & 0xFF);
}

static void put_i64_le(uint8_t *p, int64_t v) {
    uint64_t u = (uint64_t)v;
    for (int i = 0; i < 8; i++) p[i] = (uint8_t)((u >> (8 * i)) & 0xFF);
}

static uint16_t get_u16_le(const uint8_t *p) {
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static int32_t get_i32_le(const uint8_t *p) {
    uint32_t u = (uint32_t)p[0] | ((uint32_t)p[1] << 8)
               | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
    return (int32_t)u;
}

static int64_t get_i64_le(const uint8_t *p) {
    uint64_t u = 0;
    for (int i = 0; i < 8; i++) u |= (uint64_t)p[i] << (8 * i);
    return (int64_t)u;
}

static char *str_dup_or_empty(const char *s) {
    if (!s) s = "";
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

/* ─── SyncRecord ────────────────────────────────────────────────────────── */

bool aethernet_sync_record_serialize(const aethernet_sync_record_t *record,
                                     uint8_t **out, uint32_t *out_len) {
    if (!record || !out || !out_len) return false;

    const char *device = record->device_id ? record->device_id : "";
    const char *item = record->item_id ? record->item_id : "";
    size_t device_len = strlen(device);
    size_t item_len = strlen(item);
    if (device_len > 0xFFFF || item_len > 0xFFFF) return false;

    uint32_t payload_len = record->encrypted_payload ? record->encrypted_payload_len : 0;

    // version(1) + record_id(16) + op(1) + logical_clock(8) + created_at_ms(8)
    //   + device(2+len) + item(2+len) + payload(4+len)
    size_t total = 1 + 16 + 1 + 8 + 8 + 2 + device_len + 2 + item_len + 4 + payload_len;
    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) return false;

    size_t o = 0;
    buf[o++] = AETHERNET_SYNC_RECORD_FORMAT_VERSION;
    memcpy(buf + o, record->record_id, 16); o += 16;   // big-endian: bytes as-is
    buf[o++] = record->op;
    put_i64_le(buf + o, record->logical_clock); o += 8;
    put_i64_le(buf + o, record->created_at_ms); o += 8;

    put_u16_le(buf + o, (uint16_t)device_len); o += 2;
    memcpy(buf + o, device, device_len); o += device_len;
    put_u16_le(buf + o, (uint16_t)item_len); o += 2;
    memcpy(buf + o, item, item_len); o += item_len;

    put_i32_le(buf + o, (int32_t)payload_len); o += 4;
    if (payload_len) memcpy(buf + o, record->encrypted_payload, payload_len);

    *out = buf;
    *out_len = (uint32_t)total;
    return true;
}

bool aethernet_sync_record_deserialize(const uint8_t *data, uint32_t len,
                                       aethernet_sync_record_t **out_record) {
    if (!data || !out_record) return false;

    // Minimum: version + id + op + clock + created + 2 empty strings + payload len.
    if (len < 1 + 16 + 1 + 8 + 8 + 2 + 2 + 4) return false;

    size_t o = 0;
    if (data[o++] != AETHERNET_SYNC_RECORD_FORMAT_VERSION) return false;

    uint8_t record_id[16];
    memcpy(record_id, data + o, 16); o += 16;

    uint8_t op = data[o++];
    if (op > AETHERNET_SYNC_OP_READ) return false;

    int64_t logical_clock = get_i64_le(data + o); o += 8;
    int64_t created_at_ms = get_i64_le(data + o); o += 8;

    // device_id (u16 len + utf8)
    if (o + 2 > len) return false;
    uint16_t device_len = get_u16_le(data + o); o += 2;
    if (o + device_len > len) return false;
    const uint8_t *device_ptr = data + o; o += device_len;

    // item_id (u16 len + utf8)
    if (o + 2 > len) return false;
    uint16_t item_len = get_u16_le(data + o); o += 2;
    if (o + item_len > len) return false;
    const uint8_t *item_ptr = data + o; o += item_len;

    // encrypted_payload (i32 len + bytes)
    if (o + 4 > len) return false;
    int32_t payload_len = get_i32_le(data + o); o += 4;
    if (payload_len < 0 || o + (uint32_t)payload_len > len) return false;
    const uint8_t *payload_ptr = data + o;

    aethernet_sync_record_t *rec =
        (aethernet_sync_record_t *)calloc(1, sizeof(aethernet_sync_record_t));
    if (!rec) return false;

    memcpy(rec->record_id, record_id, 16);
    rec->op = op;
    rec->logical_clock = logical_clock;
    rec->created_at_ms = created_at_ms;

    rec->device_id = (char *)malloc((size_t)device_len + 1);
    rec->item_id = (char *)malloc((size_t)item_len + 1);
    if (!rec->device_id || !rec->item_id) { aethernet_sync_record_free(rec); return false; }
    memcpy(rec->device_id, device_ptr, device_len); rec->device_id[device_len] = '\0';
    memcpy(rec->item_id, item_ptr, item_len); rec->item_id[item_len] = '\0';

    if (payload_len > 0) {
        rec->encrypted_payload = (uint8_t *)malloc((size_t)payload_len);
        if (!rec->encrypted_payload) { aethernet_sync_record_free(rec); return false; }
        memcpy(rec->encrypted_payload, payload_ptr, (size_t)payload_len);
        rec->encrypted_payload_len = (uint32_t)payload_len;
    }

    *out_record = rec;
    return true;
}

void aethernet_sync_record_free(aethernet_sync_record_t *record) {
    if (!record) return;
    free(record->device_id);
    free(record->item_id);
    free(record->encrypted_payload);
    free(record);
}

/* ─── Reconcile (deterministic last-write-wins) ─────────────────────────── */

int aethernet_sync_compare(const aethernet_sync_record_t *a,
                           const aethernet_sync_record_t *b) {
    // created_at_ms, then logical_clock (later wins on both).
    if (a->created_at_ms != b->created_at_ms)
        return a->created_at_ms < b->created_at_ms ? -1 : 1;
    if (a->logical_clock != b->logical_clock)
        return a->logical_clock < b->logical_clock ? -1 : 1;

    // device_id ordinal (strcmp over the UTF-8 bytes, matching CompareOrdinal).
    int c = strcmp(a->device_id ? a->device_id : "", b->device_id ? b->device_id : "");
    if (c != 0) return c;

    // record_id bytes (big-endian memcmp).
    return memcmp(a->record_id, b->record_id, 16);
}

const aethernet_sync_record_t *aethernet_sync_winner(
    const aethernet_sync_record_t *records, size_t count) {
    if (!records || count == 0) return NULL;
    const aethernet_sync_record_t *best = &records[0];
    for (size_t i = 1; i < count; i++) {
        if (aethernet_sync_compare(&records[i], best) > 0) best = &records[i];
    }
    return best;
}

/* ─── DeviceLink (signed device membership) ─────────────────────────────── */

bool aethernet_device_link_signed_body(const char *device_id,
                                       const uint8_t *device_public_key,
                                       int64_t issued_at_ms,
                                       uint8_t **out, uint32_t *out_len) {
    if (!device_public_key || !out || !out_len) return false;
    if (!device_id) device_id = "";

    size_t id_len = strlen(device_id);
    if (id_len > 0xFFFF) return false;

    // version(1) + device_id(2+len) + device_public_key(32) + issued_at_ms(8)
    size_t total = 1 + 2 + id_len + AETHERNET_SYNC_DEVICE_KEY_SIZE + 8;
    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) return false;

    size_t o = 0;
    buf[o++] = AETHERNET_SYNC_DEVICE_LINK_FORMAT_VERSION;
    put_u16_le(buf + o, (uint16_t)id_len); o += 2;
    memcpy(buf + o, device_id, id_len); o += id_len;
    memcpy(buf + o, device_public_key, AETHERNET_SYNC_DEVICE_KEY_SIZE);
    o += AETHERNET_SYNC_DEVICE_KEY_SIZE;
    put_i64_le(buf + o, issued_at_ms);

    *out = buf;
    *out_len = (uint32_t)total;
    return true;
}

bool aethernet_device_link_create(const char *device_id,
                                  const uint8_t *device_public_key,
                                  int64_t issued_at_ms,
                                  const uint8_t *identity_seed,
                                  aethernet_device_link_t *out_link) {
    if (!device_public_key || !identity_seed || !out_link) return false;

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!aethernet_device_link_signed_body(device_id, device_public_key,
                                           issued_at_ms, &body, &body_len)) {
        return false;
    }

    uint8_t signature[AETHERNET_SYNC_SIGNATURE_SIZE];
    bool signed_ok = aethernet_ed25519_sign(identity_seed, body, body_len, signature);
    free(body);
    if (!signed_ok) return false;

    out_link->device_id = str_dup_or_empty(device_id);
    if (!out_link->device_id) return false;
    memcpy(out_link->device_public_key, device_public_key, AETHERNET_SYNC_DEVICE_KEY_SIZE);
    out_link->issued_at_ms = issued_at_ms;
    memcpy(out_link->signature, signature, AETHERNET_SYNC_SIGNATURE_SIZE);
    return true;
}

bool aethernet_device_link_verify(const aethernet_device_link_t *link,
                                  const uint8_t *identity_public) {
    if (!link || !identity_public) return false;

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!aethernet_device_link_signed_body(link->device_id, link->device_public_key,
                                           link->issued_at_ms, &body, &body_len)) {
        return false;
    }

    bool ok = aethernet_ed25519_verify(identity_public, body, body_len, link->signature);
    free(body);
    return ok;
}

bool aethernet_device_link_serialize(const aethernet_device_link_t *link,
                                     uint8_t **out, uint32_t *out_len) {
    if (!link || !out || !out_len) return false;

    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!aethernet_device_link_signed_body(link->device_id, link->device_public_key,
                                           link->issued_at_ms, &body, &body_len)) {
        return false;
    }

    size_t total = (size_t)body_len + AETHERNET_SYNC_SIGNATURE_SIZE;
    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) { free(body); return false; }
    memcpy(buf, body, body_len);
    memcpy(buf + body_len, link->signature, AETHERNET_SYNC_SIGNATURE_SIZE);
    free(body);

    *out = buf;
    *out_len = (uint32_t)total;
    return true;
}

bool aethernet_device_link_deserialize(const uint8_t *data, uint32_t len,
                                       aethernet_device_link_t *out_link) {
    if (!data || !out_link) return false;

    // Minimum: version + id_len + device_public_key(32) + issued_at_ms(8) + sig(64).
    if (len < 1 + 2 + AETHERNET_SYNC_DEVICE_KEY_SIZE + 8 + AETHERNET_SYNC_SIGNATURE_SIZE)
        return false;

    size_t o = 0;
    if (data[o++] != AETHERNET_SYNC_DEVICE_LINK_FORMAT_VERSION) return false;

    uint16_t id_len = get_u16_le(data + o); o += 2;
    // The trailing fixed fields (key + ts + sig) must still fit after the id.
    if ((size_t)o + id_len + AETHERNET_SYNC_DEVICE_KEY_SIZE + 8 + AETHERNET_SYNC_SIGNATURE_SIZE > len)
        return false;

    const uint8_t *id_ptr = data + o; o += id_len;
    const uint8_t *key_ptr = data + o; o += AETHERNET_SYNC_DEVICE_KEY_SIZE;
    int64_t issued_at_ms = get_i64_le(data + o); o += 8;
    const uint8_t *sig_ptr = data + o;

    char *device_id = (char *)malloc((size_t)id_len + 1);
    if (!device_id) return false;
    memcpy(device_id, id_ptr, id_len); device_id[id_len] = '\0';

    out_link->device_id = device_id;
    memcpy(out_link->device_public_key, key_ptr, AETHERNET_SYNC_DEVICE_KEY_SIZE);
    out_link->issued_at_ms = issued_at_ms;
    memcpy(out_link->signature, sig_ptr, AETHERNET_SYNC_SIGNATURE_SIZE);
    return true;
}
