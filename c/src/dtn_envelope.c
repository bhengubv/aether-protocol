// SPDX-License-Identifier: MIT
// Canonical binary DTN envelope serializer — see aethernet/dtn_envelope.h.

#include "aethernet/dtn_envelope.h"

#include <stdlib.h>
#include <string.h>

#define DTN_MAX_PAYLOAD (16u * 1024u * 1024u) // AETHERNET_MAX_PAYLOAD_LEN

// ─── little-endian writers ───────────────────────────────

static void put_u16(uint8_t *p, uint16_t v) {
    p[0] = (uint8_t)(v & 0xff);
    p[1] = (uint8_t)((v >> 8) & 0xff);
}

static void put_i32(uint8_t *p, int32_t v) {
    uint32_t u = (uint32_t)v;
    p[0] = (uint8_t)(u & 0xff);
    p[1] = (uint8_t)((u >> 8) & 0xff);
    p[2] = (uint8_t)((u >> 16) & 0xff);
    p[3] = (uint8_t)((u >> 24) & 0xff);
}

static void put_i64(uint8_t *p, int64_t v) {
    uint64_t u = (uint64_t)v;
    for (int i = 0; i < 8; i++) p[i] = (uint8_t)((u >> (8 * i)) & 0xff);
}

static uint32_t str_len_u16(const char *s) {
    if (!s) return 0;
    size_t n = strlen(s);
    return (n > 0xFFFFu) ? 0xFFFFu : (uint32_t)n;
}

// ─── bounded cursor reader ───────────────────────────────

typedef struct {
    const uint8_t *data;
    uint32_t len;
    uint32_t pos;
    bool ok;
} reader_t;

static uint8_t rd_u8(reader_t *r) {
    if (!r->ok || r->pos + 1 > r->len) { r->ok = false; return 0; }
    return r->data[r->pos++];
}

static uint16_t rd_u16(reader_t *r) {
    if (!r->ok || r->pos + 2 > r->len) { r->ok = false; return 0; }
    uint16_t v = (uint16_t)(r->data[r->pos] | ((uint16_t)r->data[r->pos + 1] << 8));
    r->pos += 2;
    return v;
}

static int32_t rd_i32(reader_t *r) {
    if (!r->ok || r->pos + 4 > r->len) { r->ok = false; return 0; }
    uint32_t v = (uint32_t)r->data[r->pos]
               | ((uint32_t)r->data[r->pos + 1] << 8)
               | ((uint32_t)r->data[r->pos + 2] << 16)
               | ((uint32_t)r->data[r->pos + 3] << 24);
    r->pos += 4;
    return (int32_t)v;
}

static int64_t rd_i64(reader_t *r) {
    if (!r->ok || r->pos + 8 > r->len) { r->ok = false; return 0; }
    uint64_t v = 0;
    for (int i = 0; i < 8; i++) v |= (uint64_t)r->data[r->pos + i] << (8 * i);
    r->pos += 8;
    return (int64_t)v;
}

static void rd_bytes(reader_t *r, uint8_t *dst, uint32_t n) {
    if (!r->ok || r->pos + n > r->len) { r->ok = false; return; }
    if (dst && n) memcpy(dst, r->data + r->pos, n);
    r->pos += n;
}

// Reads a u16-prefixed UTF-8 string into a fresh malloc'd NUL-terminated buffer.
static char *rd_str(reader_t *r) {
    uint16_t n = rd_u16(r);
    if (!r->ok || r->pos + n > r->len) { r->ok = false; return NULL; }
    char *s = (char *)malloc((size_t)n + 1);
    if (!s) { r->ok = false; return NULL; }
    if (n) memcpy(s, r->data + r->pos, n);
    s[n] = '\0';
    r->pos += n;
    return s;
}

// ─── bundle ──────────────────────────────────────────────

bool aethernet_dtn_bundle_encode(const aethernet_dtn_bundle_t *b, uint8_t **out, uint32_t *out_len) {
    if (!b || !out || !out_len) return false;
    uint32_t slen = str_len_u16(b->sender_uhid);
    uint32_t rlen = str_len_u16(b->recipient_uhid);
    uint32_t sglen = str_len_u16(b->sender_geohash);
    uint32_t rglen = str_len_u16(b->recipient_last_geohash);

    size_t cap = 1 + AETHERNET_PACKET_ID_SIZE + 1 + 1 + 4 + 4 + 4 + 8 + 8
               + 2 + slen + 2 + rlen + 2 + sglen + 2 + rglen
               + 4 + (size_t)b->encrypted_payload_len;
    uint8_t *buf = (uint8_t *)malloc(cap);
    if (!buf) return false;
    size_t o = 0;

    buf[o++] = AETHERNET_DTN_ENVELOPE_VERSION;
    memcpy(buf + o, b->id, AETHERNET_PACKET_ID_SIZE);
    o += AETHERNET_PACKET_ID_SIZE;
    buf[o++] = b->priority;
    buf[o++] = b->status;
    put_i32(buf + o, b->copy_count); o += 4;
    put_i32(buf + o, b->max_copies); o += 4;
    put_i32(buf + o, b->hop_count); o += 4;
    put_i64(buf + o, b->created_at_ms); o += 8;
    put_i64(buf + o, b->expires_at_ms); o += 8;

    put_u16(buf + o, (uint16_t)slen); o += 2;
    if (slen) memcpy(buf + o, b->sender_uhid, slen);
    o += slen;
    put_u16(buf + o, (uint16_t)rlen); o += 2;
    if (rlen) memcpy(buf + o, b->recipient_uhid, rlen);
    o += rlen;
    put_u16(buf + o, (uint16_t)sglen); o += 2;
    if (sglen) memcpy(buf + o, b->sender_geohash, sglen);
    o += sglen;
    put_u16(buf + o, (uint16_t)rglen); o += 2;
    if (rglen) memcpy(buf + o, b->recipient_last_geohash, rglen);
    o += rglen;

    put_i32(buf + o, (int32_t)b->encrypted_payload_len); o += 4;
    if (b->encrypted_payload_len) memcpy(buf + o, b->encrypted_payload, b->encrypted_payload_len);
    o += b->encrypted_payload_len;

    *out = buf;
    *out_len = (uint32_t)o;
    return true;
}

aethernet_dtn_bundle_t *aethernet_dtn_bundle_decode(const uint8_t *data, uint32_t len) {
    reader_t r = { data, len, 0, true };
    if (rd_u8(&r) != AETHERNET_DTN_ENVELOPE_VERSION) return NULL;

    aethernet_dtn_bundle_t *b =
        (aethernet_dtn_bundle_t *)calloc(1, sizeof(aethernet_dtn_bundle_t));
    if (!b) return NULL;

    rd_bytes(&r, b->id, AETHERNET_PACKET_ID_SIZE);
    uint8_t priority = rd_u8(&r);
    uint8_t status = rd_u8(&r);
    if (r.ok && (priority > 3 || status > 4)) r.ok = false;
    b->priority = priority;
    b->status = status;
    b->copy_count = rd_i32(&r);
    b->max_copies = rd_i32(&r);
    b->hop_count = rd_i32(&r);
    b->created_at_ms = rd_i64(&r);
    b->expires_at_ms = rd_i64(&r);
    b->sender_uhid = rd_str(&r);
    b->recipient_uhid = rd_str(&r);
    b->sender_geohash = rd_str(&r);
    b->recipient_last_geohash = rd_str(&r);

    int32_t plen = rd_i32(&r);
    if (r.ok && (plen < 0 || (uint32_t)plen > DTN_MAX_PAYLOAD || r.pos + (uint32_t)plen > r.len)) {
        r.ok = false;
    }
    if (r.ok && plen > 0) {
        b->encrypted_payload = (uint8_t *)malloc((size_t)plen);
        if (!b->encrypted_payload) {
            r.ok = false;
        } else {
            memcpy(b->encrypted_payload, r.data + r.pos, (size_t)plen);
            b->encrypted_payload_len = (uint32_t)plen;
            r.pos += (uint32_t)plen;
        }
    }

    if (!r.ok) {
        aethernet_dtn_bundle_free(b);
        return NULL;
    }
    return b;
}

// ─── custody-ack ─────────────────────────────────────────

bool aethernet_dtn_custody_ack_encode(const uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE],
                                      bool accepted, uint8_t **out, uint32_t *out_len) {
    if (!out || !out_len) return false;
    uint8_t *buf = (uint8_t *)malloc(2 + AETHERNET_PACKET_ID_SIZE);
    if (!buf) return false;
    buf[0] = AETHERNET_DTN_ENVELOPE_VERSION;
    memcpy(buf + 1, bundle_id, AETHERNET_PACKET_ID_SIZE);
    buf[1 + AETHERNET_PACKET_ID_SIZE] = accepted ? 1 : 0;
    *out = buf;
    *out_len = 2 + AETHERNET_PACKET_ID_SIZE;
    return true;
}

bool aethernet_dtn_custody_ack_decode(const uint8_t *data, uint32_t len,
                                      uint8_t out_bundle_id[AETHERNET_PACKET_ID_SIZE],
                                      bool *out_accepted) {
    reader_t r = { data, len, 0, true };
    if (rd_u8(&r) != AETHERNET_DTN_ENVELOPE_VERSION) return false;
    rd_bytes(&r, out_bundle_id, AETHERNET_PACKET_ID_SIZE);
    uint8_t acc = rd_u8(&r);
    if (!r.ok) return false;
    if (out_accepted) *out_accepted = (acc != 0);
    return true;
}

// ─── delivery-receipt ────────────────────────────────────

bool aethernet_dtn_delivery_receipt_encode(const uint8_t bundle_id[AETHERNET_PACKET_ID_SIZE],
                                           const char *recipient_uhid,
                                           int32_t total_hops,
                                           int32_t total_custody_transfers,
                                           int64_t delivered_at_ms,
                                           uint8_t **out, uint32_t *out_len) {
    if (!out || !out_len) return false;
    uint32_t rlen = str_len_u16(recipient_uhid);
    size_t cap = 1 + AETHERNET_PACKET_ID_SIZE + 2 + rlen + 4 + 4 + 8;
    uint8_t *buf = (uint8_t *)malloc(cap);
    if (!buf) return false;
    size_t o = 0;
    buf[o++] = AETHERNET_DTN_ENVELOPE_VERSION;
    memcpy(buf + o, bundle_id, AETHERNET_PACKET_ID_SIZE);
    o += AETHERNET_PACKET_ID_SIZE;
    put_u16(buf + o, (uint16_t)rlen); o += 2;
    if (rlen) memcpy(buf + o, recipient_uhid, rlen);
    o += rlen;
    put_i32(buf + o, total_hops); o += 4;
    put_i32(buf + o, total_custody_transfers); o += 4;
    put_i64(buf + o, delivered_at_ms); o += 8;
    *out = buf;
    *out_len = (uint32_t)o;
    return true;
}

bool aethernet_dtn_delivery_receipt_decode(const uint8_t *data, uint32_t len,
                                           uint8_t out_bundle_id[AETHERNET_PACKET_ID_SIZE],
                                           char **out_recipient_uhid,
                                           int32_t *out_total_hops,
                                           int32_t *out_total_custody_transfers,
                                           int64_t *out_delivered_at_ms) {
    reader_t r = { data, len, 0, true };
    if (rd_u8(&r) != AETHERNET_DTN_ENVELOPE_VERSION) return false;
    rd_bytes(&r, out_bundle_id, AETHERNET_PACKET_ID_SIZE);
    char *recipient = rd_str(&r);
    int32_t hops = rd_i32(&r);
    int32_t transfers = rd_i32(&r);
    int64_t delivered = rd_i64(&r);
    if (!r.ok) {
        free(recipient);
        return false;
    }
    if (out_recipient_uhid) *out_recipient_uhid = recipient;
    else free(recipient);
    if (out_total_hops) *out_total_hops = hops;
    if (out_total_custody_transfers) *out_total_custody_transfers = transfers;
    if (out_delivered_at_ms) *out_delivered_at_ms = delivered;
    return true;
}
