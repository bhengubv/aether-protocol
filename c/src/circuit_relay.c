// SPDX-License-Identifier: MIT
// Canonical binary circuit-relay-v2 frame serializer — see aethernet/circuit_relay.h.
// Conventions mirror dtn_envelope.c byte-for-byte.

#include "aethernet/circuit_relay.h"

#include <stdlib.h>
#include <string.h>

#define RELAY_MAX_PAYLOAD (16u * 1024u * 1024u) // AETHERNET_MAX_PAYLOAD_LEN

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

// ─── frame ───────────────────────────────────────────────

bool aethernet_relay_frame_encode(const aethernet_relay_frame_t *f, uint8_t **out, uint32_t *out_len) {
    if (!f || !out || !out_len) return false;
    if (f->payload_len > RELAY_MAX_PAYLOAD) return false;

    uint32_t slen = str_len_u16(f->source_uhid);
    uint32_t dlen = str_len_u16(f->destination_uhid);
    uint32_t rlen = str_len_u16(f->relay_uhid);

    size_t cap = 1 + 1 + 1
               + 2 + slen + 2 + dlen + 2 + rlen
               + AETHERNET_RELAY_CONN_ID_SIZE + 8 + 4 + 8
               + 4 + (size_t)f->payload_len;
    uint8_t *buf = (uint8_t *)malloc(cap);
    if (!buf) return false;
    size_t o = 0;

    buf[o++] = AETHERNET_RELAY_FRAME_VERSION;
    buf[o++] = f->type;
    buf[o++] = f->status;

    put_u16(buf + o, (uint16_t)slen); o += 2;
    if (slen) memcpy(buf + o, f->source_uhid, slen);
    o += slen;
    put_u16(buf + o, (uint16_t)dlen); o += 2;
    if (dlen) memcpy(buf + o, f->destination_uhid, dlen);
    o += dlen;
    put_u16(buf + o, (uint16_t)rlen); o += 2;
    if (rlen) memcpy(buf + o, f->relay_uhid, rlen);
    o += rlen;

    memcpy(buf + o, f->connection_id, AETHERNET_RELAY_CONN_ID_SIZE);
    o += AETHERNET_RELAY_CONN_ID_SIZE;
    put_i64(buf + o, f->reservation_expires_at_ms); o += 8;
    put_i32(buf + o, f->limit_duration_seconds); o += 4;
    put_i64(buf + o, f->limit_data_bytes); o += 8;

    put_i32(buf + o, (int32_t)f->payload_len); o += 4;
    if (f->payload_len) memcpy(buf + o, f->payload, f->payload_len);
    o += f->payload_len;

    *out = buf;
    *out_len = (uint32_t)o;
    return true;
}

aethernet_relay_frame_t *aethernet_relay_frame_decode(const uint8_t *data, uint32_t len) {
    reader_t r = { data, len, 0, true };
    if (rd_u8(&r) != AETHERNET_RELAY_FRAME_VERSION) return NULL;

    aethernet_relay_frame_t *f =
        (aethernet_relay_frame_t *)calloc(1, sizeof(aethernet_relay_frame_t));
    if (!f) return NULL;

    uint8_t type = rd_u8(&r);
    uint8_t status = rd_u8(&r);
    if (r.ok && (type == 0 || type > AETHERNET_RELAY_DATA || status > AETHERNET_RELAY_STATUS_MALFORMED_MESSAGE)) {
        r.ok = false;
    }
    f->type = type;
    f->status = status;

    f->source_uhid = rd_str(&r);
    f->destination_uhid = rd_str(&r);
    f->relay_uhid = rd_str(&r);
    rd_bytes(&r, f->connection_id, AETHERNET_RELAY_CONN_ID_SIZE);
    f->reservation_expires_at_ms = rd_i64(&r);
    f->limit_duration_seconds = rd_i32(&r);
    f->limit_data_bytes = rd_i64(&r);

    int32_t plen = rd_i32(&r);
    if (r.ok && (plen < 0 || (uint32_t)plen > RELAY_MAX_PAYLOAD || r.pos + (uint32_t)plen > r.len)) {
        r.ok = false;
    }
    if (r.ok && plen > 0) {
        f->payload = (uint8_t *)malloc((size_t)plen);
        if (!f->payload) {
            r.ok = false;
        } else {
            memcpy(f->payload, r.data + r.pos, (size_t)plen);
            f->payload_len = (uint32_t)plen;
            r.pos += (uint32_t)plen;
        }
    }

    if (!r.ok) {
        aethernet_relay_frame_free(f);
        return NULL;
    }
    return f;
}

void aethernet_relay_frame_free(aethernet_relay_frame_t *f) {
    if (!f) return;
    free(f->source_uhid);
    free(f->destination_uhid);
    free(f->relay_uhid);
    free(f->payload);
    free(f);
}
