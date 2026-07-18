// SPDX-License-Identifier: MIT
//
// AetherNet BitTorrent codec + logic core — C port of go/bittorrent/*.go and the
// C# reference src/AetherNet.BitTorrent. Byte-identical to every other language SDK,
// proven against fixtures/bittorrent/vectors.json.
//
// SHA-256 reuses the SDK's libsodium-backed aethernet_sha256() (security.h). SHA-1 is
// not exposed by libsodium and no prior module needed it, so a compact self-contained
// SHA-1 (public-domain, Steve Reid lineage) lives here — no new external dependency.

#include "aethernet/bittorrent.h"
#include "aethernet/security.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <errno.h>

/* ═══════════════════════════════════════════════════════════════════════════
 * Byte helpers.
 * ═══════════════════════════════════════════════════════════════════════════ */

static void put_be16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)(v >> 8); p[1] = (uint8_t)v; }
static void put_be32(uint8_t *p, uint32_t v) {
    p[0] = (uint8_t)(v >> 24); p[1] = (uint8_t)(v >> 16);
    p[2] = (uint8_t)(v >> 8);  p[3] = (uint8_t)v;
}
static uint16_t get_be16(const uint8_t *p) { return (uint16_t)(((uint16_t)p[0] << 8) | p[1]); }
static uint32_t get_be32(const uint8_t *p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

/* ═══════════════════════════════════════════════════════════════════════════
 * SHA-1 (self-contained, public domain).
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct { uint32_t state[5]; uint64_t count; uint8_t buffer[64]; } sha1_ctx;

#define SHA1_ROL(v, b) (((v) << (b)) | ((v) >> (32 - (b))))

static void sha1_transform(uint32_t state[5], const uint8_t buffer[64]) {
    uint32_t a, b, c, d, e, w[80];
    for (int i = 0; i < 16; i++)
        w[i] = ((uint32_t)buffer[i * 4] << 24) | ((uint32_t)buffer[i * 4 + 1] << 16) |
               ((uint32_t)buffer[i * 4 + 2] << 8) | (uint32_t)buffer[i * 4 + 3];
    for (int i = 16; i < 80; i++)
        w[i] = SHA1_ROL(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
    a = state[0]; b = state[1]; c = state[2]; d = state[3]; e = state[4];
    for (int i = 0; i < 80; i++) {
        uint32_t f, k;
        if (i < 20)      { f = (b & c) | ((~b) & d);       k = 0x5A827999; }
        else if (i < 40) { f = b ^ c ^ d;                   k = 0x6ED9EBA1; }
        else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
        else             { f = b ^ c ^ d;                   k = 0xCA62C1D6; }
        uint32_t t = SHA1_ROL(a, 5) + f + e + k + w[i];
        e = d; d = c; c = SHA1_ROL(b, 30); b = a; a = t;
    }
    state[0] += a; state[1] += b; state[2] += c; state[3] += d; state[4] += e;
}

static void sha1_init(sha1_ctx *ctx) {
    ctx->state[0] = 0x67452301; ctx->state[1] = 0xEFCDAB89; ctx->state[2] = 0x98BADCFE;
    ctx->state[3] = 0x10325476; ctx->state[4] = 0xC3D2E1F0; ctx->count = 0;
}

static void sha1_update(sha1_ctx *ctx, const uint8_t *data, size_t len) {
    size_t idx = (size_t)((ctx->count >> 3) & 63);
    ctx->count += (uint64_t)len << 3;
    size_t part = 64 - idx;
    size_t i = 0;
    if (len >= part) {
        memcpy(&ctx->buffer[idx], data, part);
        sha1_transform(ctx->state, ctx->buffer);
        for (i = part; i + 63 < len; i += 64)
            sha1_transform(ctx->state, &data[i]);
        idx = 0;
    }
    memcpy(&ctx->buffer[idx], &data[i], len - i);
}

static void sha1_final(sha1_ctx *ctx, uint8_t out[20]) {
    uint8_t finalcount[8];
    for (int i = 0; i < 8; i++)
        finalcount[i] = (uint8_t)((ctx->count >> ((7 - i) * 8)) & 0xFF);
    uint8_t c = 0x80;
    sha1_update(ctx, &c, 1);
    c = 0x00;
    while ((ctx->count & 504) != 448)   /* pad to 56 mod 64 bytes (in bits: 448 mod 512) */
        sha1_update(ctx, &c, 1);
    sha1_update(ctx, finalcount, 8);
    for (int i = 0; i < 20; i++)
        out[i] = (uint8_t)((ctx->state[i >> 2] >> ((3 - (i & 3)) * 8)) & 0xFF);
}

void aethernet_bt_sha1(const uint8_t *data, size_t len, uint8_t out[20]) {
    sha1_ctx ctx;
    sha1_init(&ctx);
    if (len) sha1_update(&ctx, data, len);
    sha1_final(&ctx, out);
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Dynamic byte buffer (encode helper).
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct { uint8_t *data; size_t len; size_t cap; bool err; } bytebuf_t;

static void bb_init(bytebuf_t *b) { b->data = NULL; b->len = 0; b->cap = 0; b->err = false; }

static bool bb_reserve(bytebuf_t *b, size_t extra) {
    if (b->err) return false;
    if (b->len + extra <= b->cap) return true;
    size_t ncap = b->cap ? b->cap * 2 : 64;
    while (ncap < b->len + extra) ncap *= 2;
    uint8_t *nd = (uint8_t *)realloc(b->data, ncap);
    if (!nd) { b->err = true; return false; }
    b->data = nd; b->cap = ncap;
    return true;
}
static void bb_byte(bytebuf_t *b, uint8_t c) { if (bb_reserve(b, 1)) b->data[b->len++] = c; }
static void bb_write(bytebuf_t *b, const uint8_t *p, size_t n) {
    if (n && bb_reserve(b, n)) { memcpy(b->data + b->len, p, n); b->len += n; }
}
static void bb_cstr(bytebuf_t *b, const char *s) { bb_write(b, (const uint8_t *)s, strlen(s)); }
/* Emit a decimal integer as ASCII. */
static void bb_int(bytebuf_t *b, int64_t v) {
    char tmp[24];
    int n = snprintf(tmp, sizeof(tmp), "%lld", (long long)v);
    if (n > 0) bb_write(b, (const uint8_t *)tmp, (size_t)n);
}
/* Finalize: hand ownership of the buffer to the caller (or NULL on error). */
static uint8_t *bb_finish(bytebuf_t *b, size_t *out_len) {
    if (b->err) { free(b->data); if (out_len) *out_len = 0; return NULL; }
    if (out_len) *out_len = b->len;
    if (!b->data) { b->data = (uint8_t *)malloc(1); if (b->data) b->data[0] = 0; }
    return b->data;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Bencode value tree.
 * ═══════════════════════════════════════════════════════════════════════════ */

static aethernet_benc_value_t *new_value(aethernet_benc_type_t t) {
    aethernet_benc_value_t *v = (aethernet_benc_value_t *)calloc(1, sizeof(*v));
    if (v) v->type = t;
    return v;
}

aethernet_benc_value_t *aethernet_benc_int(int64_t v) {
    aethernet_benc_value_t *x = new_value(AETHERNET_BENC_INT);
    if (x) x->i = v;
    return x;
}

aethernet_benc_value_t *aethernet_benc_str(const uint8_t *data, size_t len) {
    aethernet_benc_value_t *x = new_value(AETHERNET_BENC_STR);
    if (!x) return NULL;
    x->s = (uint8_t *)malloc(len ? len : 1);
    if (!x->s) { free(x); return NULL; }
    if (len) memcpy(x->s, data, len);
    x->s_len = len;
    return x;
}

aethernet_benc_value_t *aethernet_benc_str_c(const char *s) {
    return aethernet_benc_str((const uint8_t *)s, s ? strlen(s) : 0);
}

aethernet_benc_value_t *aethernet_benc_list(void) { return new_value(AETHERNET_BENC_LIST); }
aethernet_benc_value_t *aethernet_benc_dict(void) { return new_value(AETHERNET_BENC_DICT); }

static bool value_reserve(aethernet_benc_value_t *v, bool with_keys) {
    if (v->count < v->cap) return true;
    size_t ncap = v->cap ? v->cap * 2 : 4;
    aethernet_benc_value_t **ni = (aethernet_benc_value_t **)realloc(v->items, ncap * sizeof(*ni));
    if (!ni) return false;
    v->items = ni;
    if (with_keys) {
        uint8_t **nk = (uint8_t **)realloc(v->keys, ncap * sizeof(*nk));
        if (!nk) return false;
        v->keys = nk;
        size_t *nl = (size_t *)realloc(v->key_lens, ncap * sizeof(*nl));
        if (!nl) return false;
        v->key_lens = nl;
    }
    v->cap = ncap;
    return true;
}

bool aethernet_benc_list_append(aethernet_benc_value_t *list, aethernet_benc_value_t *value) {
    if (!list || list->type != AETHERNET_BENC_LIST || !value) return false;
    if (!value_reserve(list, false)) return false;
    list->items[list->count++] = value;
    return true;
}

bool aethernet_benc_dict_add(aethernet_benc_value_t *dict, const char *key, aethernet_benc_value_t *value) {
    if (!dict || dict->type != AETHERNET_BENC_DICT || !key || !value) return false;
    size_t klen = strlen(key);
    for (size_t i = 0; i < dict->count; i++)
        if (dict->key_lens[i] == klen && memcmp(dict->keys[i], key, klen) == 0)
            return false; /* duplicate */
    if (!value_reserve(dict, true)) return false;
    uint8_t *kc = (uint8_t *)malloc(klen ? klen : 1);
    if (!kc) return false;
    if (klen) memcpy(kc, key, klen);
    dict->keys[dict->count] = kc;
    dict->key_lens[dict->count] = klen;
    dict->items[dict->count] = value;
    dict->count++;
    return true;
}

const aethernet_benc_value_t *aethernet_benc_dict_get(const aethernet_benc_value_t *dict, const char *key) {
    if (!dict || dict->type != AETHERNET_BENC_DICT || !key) return NULL;
    size_t klen = strlen(key);
    for (size_t i = 0; i < dict->count; i++)
        if (dict->key_lens[i] == klen && memcmp(dict->keys[i], key, klen) == 0)
            return dict->items[i];
    return NULL;
}

void aethernet_benc_free(aethernet_benc_value_t *v) {
    if (!v) return;
    if (v->type == AETHERNET_BENC_STR) {
        free(v->s);
    } else if (v->type == AETHERNET_BENC_LIST || v->type == AETHERNET_BENC_DICT) {
        for (size_t i = 0; i < v->count; i++) {
            aethernet_benc_free(v->items[i]);
            if (v->keys) free(v->keys[i]);
        }
        free(v->items);
        free(v->keys);
        free(v->key_lens);
    }
    free(v);
}

/* Deep copy (used by KRPC/PEX decode to detach sub-trees before the root is freed). */
static aethernet_benc_value_t *benc_deep_copy(const aethernet_benc_value_t *v) {
    if (!v) return NULL;
    switch (v->type) {
    case AETHERNET_BENC_INT: return aethernet_benc_int(v->i);
    case AETHERNET_BENC_STR: return aethernet_benc_str(v->s, v->s_len);
    case AETHERNET_BENC_LIST: {
        aethernet_benc_value_t *l = aethernet_benc_list();
        if (!l) return NULL;
        for (size_t i = 0; i < v->count; i++) {
            aethernet_benc_value_t *c = benc_deep_copy(v->items[i]);
            if (!c || !aethernet_benc_list_append(l, c)) { aethernet_benc_free(c); aethernet_benc_free(l); return NULL; }
        }
        return l;
    }
    case AETHERNET_BENC_DICT: {
        aethernet_benc_value_t *d = aethernet_benc_dict();
        if (!d) return NULL;
        for (size_t i = 0; i < v->count; i++) {
            char *k = (char *)malloc(v->key_lens[i] + 1);
            if (!k) { aethernet_benc_free(d); return NULL; }
            memcpy(k, v->keys[i], v->key_lens[i]); k[v->key_lens[i]] = '\0';
            aethernet_benc_value_t *c = benc_deep_copy(v->items[i]);
            if (!c || !aethernet_benc_dict_add(d, k, c)) { free(k); aethernet_benc_free(c); aethernet_benc_free(d); return NULL; }
            free(k);
        }
        return d;
    }
    }
    return NULL;
}

/* ── encode ─────────────────────────────────────────────────────────────── */

static int key_cmp(const uint8_t *a, size_t alen, const uint8_t *b, size_t blen) {
    size_t m = alen < blen ? alen : blen;
    int c = m ? memcmp(a, b, m) : 0;
    if (c) return c;
    if (alen < blen) return -1;
    if (alen > blen) return 1;
    return 0;
}

static void benc_encode_into(const aethernet_benc_value_t *v, bytebuf_t *b) {
    switch (v->type) {
    case AETHERNET_BENC_INT:
        bb_byte(b, 'i'); bb_int(b, v->i); bb_byte(b, 'e');
        break;
    case AETHERNET_BENC_STR:
        bb_int(b, (int64_t)v->s_len); bb_byte(b, ':'); bb_write(b, v->s, v->s_len);
        break;
    case AETHERNET_BENC_LIST:
        bb_byte(b, 'l');
        for (size_t i = 0; i < v->count; i++) benc_encode_into(v->items[i], b);
        bb_byte(b, 'e');
        break;
    case AETHERNET_BENC_DICT: {
        bb_byte(b, 'd');
        size_t n = v->count;
        size_t *order = (size_t *)malloc((n ? n : 1) * sizeof(size_t));
        if (!order) { b->err = true; return; }
        for (size_t i = 0; i < n; i++) order[i] = i;
        /* insertion sort by raw key byte order (keys are unique) */
        for (size_t i = 1; i < n; i++) {
            size_t cur = order[i];
            size_t j = i;
            while (j > 0 && key_cmp(v->keys[order[j - 1]], v->key_lens[order[j - 1]],
                                    v->keys[cur], v->key_lens[cur]) > 0) {
                order[j] = order[j - 1];
                j--;
            }
            order[j] = cur;
        }
        for (size_t i = 0; i < n; i++) {
            size_t idx = order[i];
            bb_int(b, (int64_t)v->key_lens[idx]); bb_byte(b, ':');
            bb_write(b, v->keys[idx], v->key_lens[idx]);
            benc_encode_into(v->items[idx], b);
        }
        free(order);
        bb_byte(b, 'e');
        break;
    }
    }
}

uint8_t *aethernet_benc_encode(const aethernet_benc_value_t *v, size_t *out_len) {
    if (!v) return NULL;
    bytebuf_t b; bb_init(&b);
    benc_encode_into(v, &b);
    return bb_finish(&b, out_len);
}

/* ── decode (strict BEP-3) ──────────────────────────────────────────────── */

static aethernet_benc_value_t *decode_value(const uint8_t *data, size_t len, size_t *consumed);

static bool all_digits(const uint8_t *p, size_t n) {
    for (size_t i = 0; i < n; i++) if (p[i] < '0' || p[i] > '9') return false;
    return true;
}

static aethernet_benc_value_t *decode_int(const uint8_t *data, size_t len, size_t *consumed) {
    const uint8_t *e = (const uint8_t *)memchr(data, 'e', len);
    if (!e) return NULL;
    size_t body_len = (size_t)(e - data) - 1;  /* between 'i' and 'e' */
    const uint8_t *body = data + 1;
    if (body_len == 0) return NULL;
    if (body_len == 2 && body[0] == '-' && body[1] == '0') return NULL; /* -0 */
    const uint8_t *digits = body; size_t dlen = body_len;
    if (digits[0] == '-') { digits++; dlen--; if (dlen == 0) return NULL; }
    if (dlen > 1 && digits[0] == '0') return NULL; /* leading zero */
    if (!all_digits(digits, dlen)) return NULL;
    if (body_len > 20) return NULL; /* cannot fit int64 */
    char buf[24];
    memcpy(buf, body, body_len); buf[body_len] = '\0';
    errno = 0;
    char *end = NULL;
    long long val = strtoll(buf, &end, 10);
    if (errno == ERANGE || end == buf || *end != '\0') return NULL;
    aethernet_benc_value_t *v = aethernet_benc_int((int64_t)val);
    if (v && consumed) *consumed = (size_t)(e - data) + 1;
    return v;
}

static aethernet_benc_value_t *decode_string(const uint8_t *data, size_t len, size_t *consumed) {
    const uint8_t *colon = (const uint8_t *)memchr(data, ':', len);
    if (!colon) return NULL;
    size_t hdr = (size_t)(colon - data);
    if (hdr == 0) return NULL;
    if (hdr > 1 && data[0] == '0') return NULL;      /* leading zero */
    if (!all_digits(data, hdr)) return NULL;
    if (hdr > 18) return NULL;                        /* absurd length */
    uint64_t n = 0;
    for (size_t i = 0; i < hdr; i++) n = n * 10 + (uint64_t)(data[i] - '0');
    size_t start = hdr + 1;
    if (start + n > len) return NULL;                 /* runs past end */
    aethernet_benc_value_t *v = aethernet_benc_str(data + start, (size_t)n);
    if (v && consumed) *consumed = start + (size_t)n;
    return v;
}

static aethernet_benc_value_t *decode_list(const uint8_t *data, size_t len, size_t *consumed) {
    aethernet_benc_value_t *list = aethernet_benc_list();
    if (!list) return NULL;
    size_t pos = 1;
    for (;;) {
        if (pos >= len) { aethernet_benc_free(list); return NULL; }
        if (data[pos] == 'e') { if (consumed) *consumed = pos + 1; return list; }
        size_t used = 0;
        aethernet_benc_value_t *item = decode_value(data + pos, len - pos, &used);
        if (!item) { aethernet_benc_free(list); return NULL; }
        if (!aethernet_benc_list_append(list, item)) { aethernet_benc_free(item); aethernet_benc_free(list); return NULL; }
        pos += used;
    }
}

static aethernet_benc_value_t *decode_dict(const uint8_t *data, size_t len, size_t *consumed) {
    aethernet_benc_value_t *d = aethernet_benc_dict();
    if (!d) return NULL;
    size_t pos = 1;
    const uint8_t *prev_key = NULL; size_t prev_len = 0;
    for (;;) {
        if (pos >= len) { aethernet_benc_free(d); return NULL; }
        if (data[pos] == 'e') { if (consumed) *consumed = pos + 1; return d; }
        size_t kused = 0;
        aethernet_benc_value_t *keyv = decode_string(data + pos, len - pos, &kused);
        if (!keyv) { aethernet_benc_free(d); return NULL; } /* key must be a byte string */
        pos += kused;
        if (prev_key) {
            int c = key_cmp(prev_key, prev_len, keyv->s, keyv->s_len);
            if (c == 0 || c > 0) { aethernet_benc_free(keyv); aethernet_benc_free(d); return NULL; }
        }
        if (pos >= len) { aethernet_benc_free(keyv); aethernet_benc_free(d); return NULL; }
        size_t vused = 0;
        aethernet_benc_value_t *valv = decode_value(data + pos, len - pos, &vused);
        if (!valv) { aethernet_benc_free(keyv); aethernet_benc_free(d); return NULL; }
        pos += vused;
        /* add (dict_add copies the key from a NUL-terminated string) */
        char *kstr = (char *)malloc(keyv->s_len + 1);
        if (!kstr) { aethernet_benc_free(keyv); aethernet_benc_free(valv); aethernet_benc_free(d); return NULL; }
        memcpy(kstr, keyv->s, keyv->s_len); kstr[keyv->s_len] = '\0';
        bool ok = aethernet_benc_dict_add(d, kstr, valv);
        /* remember prev_key: point into the dict's just-added copy (stable) */
        if (ok) { prev_key = d->keys[d->count - 1]; prev_len = d->key_lens[d->count - 1]; }
        free(kstr);
        aethernet_benc_free(keyv);
        if (!ok) { aethernet_benc_free(valv); aethernet_benc_free(d); return NULL; }
    }
}

static aethernet_benc_value_t *decode_value(const uint8_t *data, size_t len, size_t *consumed) {
    if (len == 0) return NULL;
    uint8_t c = data[0];
    if (c == 'i') return decode_int(data, len, consumed);
    if (c == 'l') return decode_list(data, len, consumed);
    if (c == 'd') return decode_dict(data, len, consumed);
    if (c >= '0' && c <= '9') return decode_string(data, len, consumed);
    return NULL;
}

aethernet_benc_value_t *aethernet_benc_decode_n(const uint8_t *data, size_t len, size_t *consumed) {
    return decode_value(data, len, consumed);
}

aethernet_benc_value_t *aethernet_benc_decode(const uint8_t *data, size_t len) {
    size_t used = 0;
    aethernet_benc_value_t *v = decode_value(data, len, &used);
    if (!v) return NULL;
    if (used != len) { aethernet_benc_free(v); return NULL; } /* trailing data */
    return v;
}

/* Typed accessors (internal). */
static bool as_int(const aethernet_benc_value_t *v, int64_t *out) {
    if (!v || v->type != AETHERNET_BENC_INT) return false;
    *out = v->i; return true;
}
static bool as_bytes(const aethernet_benc_value_t *v, const uint8_t **out, size_t *len) {
    if (!v || v->type != AETHERNET_BENC_STR) return false;
    *out = v->s; *len = v->s_len; return true;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Metainfo / info-hash.
 * ═══════════════════════════════════════════════════════════════════════════ */

/* Copy a bencode byte string into a fresh NUL-terminated C string. */
static char *dup_text(const aethernet_benc_value_t *v) {
    if (!v || v->type != AETHERNET_BENC_STR) return NULL;
    char *s = (char *)malloc(v->s_len + 1);
    if (!s) return NULL;
    memcpy(s, v->s, v->s_len); s[v->s_len] = '\0';
    return s;
}

uint8_t *aethernet_bt_build_single_file_torrent(const char *name,
                                                 const uint8_t *data, size_t data_len,
                                                 int64_t piece_length,
                                                 const char *announce,
                                                 size_t *out_len) {
    if (!name || name[0] == '\0' || piece_length <= 0) return NULL;
    size_t pl = (size_t)piece_length;
    size_t piece_count = (data_len + pl - 1) / pl;
    uint8_t *pieces = (uint8_t *)malloc(piece_count ? piece_count * 20 : 1);
    if (!pieces) return NULL;
    for (size_t i = 0; i < piece_count; i++) {
        size_t start = i * pl;
        size_t end = start + pl;
        if (end > data_len) end = data_len;
        aethernet_bt_sha1(data + start, end - start, pieces + i * 20);
    }

    aethernet_benc_value_t *info = aethernet_benc_dict();
    aethernet_benc_value_t *root = aethernet_benc_dict();
    aethernet_benc_value_t *v_len = aethernet_benc_int((int64_t)data_len);
    aethernet_benc_value_t *v_name = aethernet_benc_str_c(name);
    aethernet_benc_value_t *v_pl = aethernet_benc_int(piece_length);
    aethernet_benc_value_t *v_pieces = aethernet_benc_str(pieces, piece_count * 20);
    free(pieces);
    if (!info || !root || !v_len || !v_name || !v_pl || !v_pieces) {
        aethernet_benc_free(info); aethernet_benc_free(root);
        aethernet_benc_free(v_len); aethernet_benc_free(v_name);
        aethernet_benc_free(v_pl); aethernet_benc_free(v_pieces);
        return NULL;
    }
    /* Add in canonical order (encode sorts regardless). */
    aethernet_benc_dict_add(info, "length", v_len);
    aethernet_benc_dict_add(info, "name", v_name);
    aethernet_benc_dict_add(info, "piece length", v_pl);
    aethernet_benc_dict_add(info, "pieces", v_pieces);

    if (announce && announce[0] != '\0') {
        /* trim leading/trailing whitespace check like Go's TrimSpace != "" */
        const char *p = announce;
        while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
        if (*p != '\0') {
            aethernet_benc_value_t *v_ann = aethernet_benc_str_c(announce);
            if (v_ann) aethernet_benc_dict_add(root, "announce", v_ann);
        }
    }
    aethernet_benc_dict_add(root, "info", info);

    uint8_t *encoded = aethernet_benc_encode(root, out_len);
    aethernet_benc_free(root);
    return encoded;
}

/* Walk the top-level dict tracking byte offsets; return the RAW span of "info". */
static bool extract_info_span(const uint8_t *data, size_t len, size_t *start, size_t *end) {
    if (len == 0 || data[0] != 'd') return false;
    size_t pos = 1;
    while (pos < len && data[pos] != 'e') {
        size_t kused = 0;
        aethernet_benc_value_t *keyv = decode_string(data + pos, len - pos, &kused);
        if (!keyv) return false;
        int is_info = (keyv->s_len == 4 && memcmp(keyv->s, "info", 4) == 0);
        aethernet_benc_free(keyv);
        pos += kused;
        size_t vstart = pos;
        size_t vused = 0;
        aethernet_benc_value_t *valv = decode_value(data + pos, len - pos, &vused);
        if (!valv) return false;
        aethernet_benc_free(valv);
        size_t vend = pos + vused;
        pos = vend;
        if (is_info) { *start = vstart; *end = vend; return true; }
    }
    return false;
}

void aethernet_bt_metainfo_info_hash_v1_hex(const aethernet_bt_metainfo_t *m, char out[41]) {
    static const char HEX[] = "0123456789abcdef";
    for (int i = 0; i < 20; i++) {
        out[i * 2] = HEX[(m->info_hash_v1[i] >> 4) & 0xF];
        out[i * 2 + 1] = HEX[m->info_hash_v1[i] & 0xF];
    }
    out[40] = '\0';
}

static void metainfo_add_announce(aethernet_bt_metainfo_t *m, const aethernet_benc_value_t *v) {
    if (!v || v->type != AETHERNET_BENC_STR || v->s_len == 0) return;
    for (size_t i = 0; i < m->announce_count; i++)
        if (strlen(m->announce[i]) == v->s_len && memcmp(m->announce[i], v->s, v->s_len) == 0)
            return; /* de-dup */
    char **na = (char **)realloc(m->announce, (m->announce_count + 1) * sizeof(char *));
    if (!na) return;
    m->announce = na;
    m->announce[m->announce_count] = dup_text(v);
    if (m->announce[m->announce_count]) m->announce_count++;
}

aethernet_bt_metainfo_t *aethernet_bt_parse_torrent(const uint8_t *data, size_t len) {
    aethernet_benc_value_t *root = aethernet_benc_decode(data, len);
    if (!root || root->type != AETHERNET_BENC_DICT) { aethernet_benc_free(root); return NULL; }

    const aethernet_benc_value_t *info = aethernet_benc_dict_get(root, "info");
    if (!info || info->type != AETHERNET_BENC_DICT) { aethernet_benc_free(root); return NULL; }

    size_t ispan_start = 0, ispan_end = 0;
    if (!extract_info_span(data, len, &ispan_start, &ispan_end)) { aethernet_benc_free(root); return NULL; }

    aethernet_bt_metainfo_t *m = (aethernet_bt_metainfo_t *)calloc(1, sizeof(*m));
    if (!m) { aethernet_benc_free(root); return NULL; }
    m->root = root;
    m->info = info;
    aethernet_bt_sha1(data + ispan_start, ispan_end - ispan_start, m->info_hash_v1);

    const aethernet_benc_value_t *nv = aethernet_benc_dict_get(info, "name");
    m->name = dup_text(nv);
    if (!m->name) { aethernet_bt_metainfo_free(m); return NULL; }

    const aethernet_benc_value_t *plv = aethernet_benc_dict_get(info, "piece length");
    if (!as_int(plv, &m->piece_length) || m->piece_length <= 0) { aethernet_bt_metainfo_free(m); return NULL; }

    const aethernet_benc_value_t *pv = aethernet_benc_dict_get(info, "pieces");
    const uint8_t *pbytes = NULL; size_t plen = 0;
    if (!as_bytes(pv, &pbytes, &plen) || (plen % 20) != 0) { aethernet_bt_metainfo_free(m); return NULL; }
    m->piece_count = plen / 20;
    m->piece_hashes = (uint8_t *)malloc(plen ? plen : 1);
    if (!m->piece_hashes) { aethernet_bt_metainfo_free(m); return NULL; }
    if (plen) memcpy(m->piece_hashes, pbytes, plen);

    const aethernet_benc_value_t *files = aethernet_benc_dict_get(info, "files");
    if (files && files->type == AETHERNET_BENC_LIST) {
        m->is_single_file = false;
        m->files = (aethernet_bt_file_entry_t *)calloc(files->count ? files->count : 1, sizeof(*m->files));
        if (!m->files) { aethernet_bt_metainfo_free(m); return NULL; }
        for (size_t i = 0; i < files->count; i++) {
            const aethernet_benc_value_t *fd = files->items[i];
            if (!fd || fd->type != AETHERNET_BENC_DICT) { aethernet_bt_metainfo_free(m); return NULL; }
            const aethernet_benc_value_t *lv = aethernet_benc_dict_get(fd, "length");
            int64_t flen = 0;
            if (!as_int(lv, &flen)) { aethernet_bt_metainfo_free(m); return NULL; }
            const aethernet_benc_value_t *pathv = aethernet_benc_dict_get(fd, "path");
            if (!pathv || pathv->type != AETHERNET_BENC_LIST || pathv->count == 0) { aethernet_bt_metainfo_free(m); return NULL; }
            aethernet_bt_file_entry_t *fe = &m->files[m->file_count];
            fe->path = (char **)calloc(pathv->count, sizeof(char *));
            if (!fe->path) { aethernet_bt_metainfo_free(m); return NULL; }
            for (size_t j = 0; j < pathv->count; j++) {
                fe->path[j] = dup_text(pathv->items[j]);
                if (!fe->path[j]) { aethernet_bt_metainfo_free(m); return NULL; }
                fe->path_count++;
            }
            fe->length = flen;
            m->total_length += flen;
            m->file_count++;
        }
    } else {
        m->is_single_file = true;
        const aethernet_benc_value_t *lv = aethernet_benc_dict_get(info, "length");
        int64_t flen = 0;
        if (!as_int(lv, &flen)) { aethernet_bt_metainfo_free(m); return NULL; }
        m->files = (aethernet_bt_file_entry_t *)calloc(1, sizeof(*m->files));
        if (!m->files) { aethernet_bt_metainfo_free(m); return NULL; }
        m->files[0].path = (char **)calloc(1, sizeof(char *));
        if (!m->files[0].path) { aethernet_bt_metainfo_free(m); return NULL; }
        m->files[0].path[0] = (char *)malloc(strlen(m->name) + 1);
        if (!m->files[0].path[0]) { aethernet_bt_metainfo_free(m); return NULL; }
        strcpy(m->files[0].path[0], m->name);
        m->files[0].path_count = 1;
        m->files[0].length = flen;
        m->file_count = 1;
        m->total_length = flen;
    }

    metainfo_add_announce(m, aethernet_benc_dict_get(root, "announce"));
    const aethernet_benc_value_t *al = aethernet_benc_dict_get(root, "announce-list");
    if (al && al->type == AETHERNET_BENC_LIST) {
        for (size_t i = 0; i < al->count; i++) {
            const aethernet_benc_value_t *tier = al->items[i];
            if (tier && tier->type == AETHERNET_BENC_LIST)
                for (size_t j = 0; j < tier->count; j++)
                    metainfo_add_announce(m, tier->items[j]);
        }
    }
    return m;
}

void aethernet_bt_metainfo_free(aethernet_bt_metainfo_t *m) {
    if (!m) return;
    aethernet_benc_free(m->root);
    free(m->name);
    free(m->piece_hashes);
    if (m->files) {
        for (size_t i = 0; i < m->file_count; i++) {
            for (size_t j = 0; j < m->files[i].path_count; j++) free(m->files[i].path[j]);
            free(m->files[i].path);
        }
        free(m->files);
    }
    if (m->announce) {
        for (size_t i = 0; i < m->announce_count; i++) free(m->announce[i]);
        free(m->announce);
    }
    free(m);
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Magnet (BEP-9 xt=urn:btih:).
 * ═══════════════════════════════════════════════════════════════════════════ */

static int hex_nibble(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

/* Percent-decode a query component ('+' → space). Returns malloc'd NUL-terminated. */
static char *percent_decode(const char *s, size_t len) {
    char *out = (char *)malloc(len + 1);
    if (!out) return NULL;
    size_t o = 0;
    for (size_t i = 0; i < len; i++) {
        char c = s[i];
        if (c == '+') { out[o++] = ' '; }
        else if (c == '%' && i + 2 < len) {
            int hi = hex_nibble(s[i + 1]), lo = hex_nibble(s[i + 2]);
            if (hi >= 0 && lo >= 0) { out[o++] = (char)((hi << 4) | lo); i += 2; }
            else out[o++] = c;
        } else out[o++] = c;
    }
    out[o] = '\0';
    return out;
}

static bool base32_decode_20(const char *s, uint8_t out[20]) {
    /* RFC 4648 base32, no padding, 32 chars → 20 bytes. */
    uint64_t buffer = 0; int bits = 0; size_t oi = 0;
    for (const char *p = s; *p; p++) {
        char c = *p;
        int val;
        if (c >= 'A' && c <= 'Z') val = c - 'A';
        else if (c >= 'a' && c <= 'z') val = c - 'a';
        else if (c >= '2' && c <= '7') val = c - '2' + 26;
        else return false;
        buffer = (buffer << 5) | (uint64_t)val;
        bits += 5;
        if (bits >= 8) {
            bits -= 8;
            if (oi >= 20) return false;
            out[oi++] = (uint8_t)((buffer >> bits) & 0xFF);
        }
    }
    return oi == 20;
}

static bool decode_info_hash_str(const char *s, uint8_t out[20]) {
    size_t n = strlen(s);
    if (n == 40) {
        for (int i = 0; i < 20; i++) {
            int hi = hex_nibble(s[i * 2]), lo = hex_nibble(s[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            out[i] = (uint8_t)((hi << 4) | lo);
        }
        return true;
    }
    if (n == 32) return base32_decode_20(s, out);
    return false;
}

aethernet_bt_magnet_t *aethernet_bt_parse_magnet(const char *uri) {
    const char *prefix = "magnet:?";
    size_t plen = strlen(prefix);
    if (!uri || strncmp(uri, prefix, plen) != 0) return NULL;

    aethernet_bt_magnet_t *m = (aethernet_bt_magnet_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    bool found = false;

    const char *q = uri + plen;
    const char *p = q;
    while (*p) {
        const char *amp = strchr(p, '&');
        size_t seg_len = amp ? (size_t)(amp - p) : strlen(p);
        const char *eq = (const char *)memchr(p, '=', seg_len);
        if (eq) {
            size_t klen = (size_t)(eq - p);
            const char *vstart = eq + 1;
            size_t vlen = seg_len - klen - 1;
            char *val = percent_decode(vstart, vlen);
            if (val) {
                if (klen == 2 && strncmp(p, "xt", 2) == 0) {
                    const char *btih = "urn:btih:";
                    if (!found && strncmp(val, btih, strlen(btih)) == 0) {
                        if (decode_info_hash_str(val + strlen(btih), m->info_hash)) found = true;
                    }
                } else if (klen == 2 && strncmp(p, "dn", 2) == 0) {
                    free(m->display_name);
                    m->display_name = val; val = NULL;
                } else if (klen == 2 && strncmp(p, "tr", 2) == 0) {
                    char **nt = (char **)realloc(m->trackers, (m->tracker_count + 1) * sizeof(char *));
                    if (nt) { m->trackers = nt; m->trackers[m->tracker_count++] = val; val = NULL; }
                }
                free(val);
            }
        }
        if (!amp) break;
        p = amp + 1;
    }
    if (!found) { aethernet_bt_magnet_free(m); return NULL; }
    return m;
}

void aethernet_bt_magnet_free(aethernet_bt_magnet_t *m) {
    if (!m) return;
    free(m->display_name);
    if (m->trackers) { for (size_t i = 0; i < m->tracker_count; i++) free(m->trackers[i]); free(m->trackers); }
    free(m);
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Peer-wire.
 * ═══════════════════════════════════════════════════════════════════════════ */

static const char PROTOCOL_STRING[] = "BitTorrent protocol"; /* 19 chars */

void aethernet_bt_default_reserved(uint8_t out[8]) {
    memset(out, 0, 8);
    out[5] |= 0x10; /* extension protocol */
    out[7] |= 0x01; /* DHT */
}

void aethernet_bt_handshake_to_bytes(const aethernet_bt_handshake_t *h, uint8_t out[68]) {
    out[0] = 19;
    memcpy(out + 1, PROTOCOL_STRING, 19);
    memcpy(out + 20, h->reserved, 8);
    memcpy(out + 28, h->info_hash, 20);
    memcpy(out + 48, h->peer_id, 20);
}

bool aethernet_bt_handshake_parse(const uint8_t *data, size_t len, aethernet_bt_handshake_t *out) {
    if (len < 68) return false;
    if (data[0] != 19) return false;
    if (memcmp(data + 1, PROTOCOL_STRING, 19) != 0) return false;
    memcpy(out->reserved, data + 20, 8);
    memcpy(out->info_hash, data + 28, 20);
    memcpy(out->peer_id, data + 48, 20);
    return true;
}

bool aethernet_bt_handshake_supports_extended(const aethernet_bt_handshake_t *h) { return (h->reserved[5] & 0x10) != 0; }
bool aethernet_bt_handshake_supports_dht(const aethernet_bt_handshake_t *h) { return (h->reserved[7] & 0x01) != 0; }

static bool msg_set(aethernet_bt_message_t *out, bool has_id, uint8_t id, const uint8_t *payload, size_t plen) {
    out->has_id = has_id;
    out->id = id;
    out->payload = NULL;
    out->payload_len = 0;
    if (plen) {
        out->payload = (uint8_t *)malloc(plen);
        if (!out->payload) return false;
        if (payload) memcpy(out->payload, payload, plen);
        out->payload_len = plen;
    }
    return true;
}

bool aethernet_bt_keepalive(aethernet_bt_message_t *out) { return msg_set(out, false, 0, NULL, 0); }
bool aethernet_bt_choke(aethernet_bt_message_t *out) { return msg_set(out, true, AETHERNET_BT_MSG_CHOKE, NULL, 0); }
bool aethernet_bt_unchoke(aethernet_bt_message_t *out) { return msg_set(out, true, AETHERNET_BT_MSG_UNCHOKE, NULL, 0); }
bool aethernet_bt_interested(aethernet_bt_message_t *out) { return msg_set(out, true, AETHERNET_BT_MSG_INTERESTED, NULL, 0); }
bool aethernet_bt_not_interested(aethernet_bt_message_t *out) { return msg_set(out, true, AETHERNET_BT_MSG_NOT_INTERESTED, NULL, 0); }

bool aethernet_bt_have(uint32_t piece_index, aethernet_bt_message_t *out) {
    uint8_t p[4]; put_be32(p, piece_index);
    return msg_set(out, true, AETHERNET_BT_MSG_HAVE, p, 4);
}
bool aethernet_bt_bitfield_msg(const uint8_t *bits, size_t bits_len, aethernet_bt_message_t *out) {
    return msg_set(out, true, AETHERNET_BT_MSG_BITFIELD, bits, bits_len);
}
bool aethernet_bt_request(uint32_t index, uint32_t begin, uint32_t length, aethernet_bt_message_t *out) {
    uint8_t p[12]; put_be32(p, index); put_be32(p + 4, begin); put_be32(p + 8, length);
    return msg_set(out, true, AETHERNET_BT_MSG_REQUEST, p, 12);
}
bool aethernet_bt_cancel(uint32_t index, uint32_t begin, uint32_t length, aethernet_bt_message_t *out) {
    uint8_t p[12]; put_be32(p, index); put_be32(p + 4, begin); put_be32(p + 8, length);
    return msg_set(out, true, AETHERNET_BT_MSG_CANCEL, p, 12);
}
bool aethernet_bt_piece(uint32_t index, uint32_t begin, const uint8_t *block, size_t block_len, aethernet_bt_message_t *out) {
    size_t plen = 8 + block_len;
    uint8_t *p = (uint8_t *)malloc(plen);
    if (!p) return false;
    put_be32(p, index); put_be32(p + 4, begin);
    if (block_len) memcpy(p + 8, block, block_len);
    bool ok = msg_set(out, true, AETHERNET_BT_MSG_PIECE, p, plen);
    free(p);
    return ok;
}
bool aethernet_bt_port(uint16_t port, aethernet_bt_message_t *out) {
    uint8_t p[2]; put_be16(p, port);
    return msg_set(out, true, AETHERNET_BT_MSG_PORT, p, 2);
}
bool aethernet_bt_extended(uint8_t sub_id, const uint8_t *body, size_t body_len, aethernet_bt_message_t *out) {
    size_t plen = 1 + body_len;
    uint8_t *p = (uint8_t *)malloc(plen);
    if (!p) return false;
    p[0] = sub_id;
    if (body_len) memcpy(p + 1, body, body_len);
    bool ok = msg_set(out, true, AETHERNET_BT_MSG_EXTENDED, p, plen);
    free(p);
    return ok;
}

void aethernet_bt_message_free(aethernet_bt_message_t *m) {
    if (!m) return;
    free(m->payload);
    m->payload = NULL; m->payload_len = 0; m->has_id = false; m->id = 0;
}

uint8_t *aethernet_bt_message_to_bytes(const aethernet_bt_message_t *m, size_t *out_len) {
    if (!m->has_id) {
        uint8_t *buf = (uint8_t *)malloc(4);
        if (!buf) return NULL;
        memset(buf, 0, 4);
        if (out_len) *out_len = 4;
        return buf;
    }
    size_t length = 1 + m->payload_len;
    uint8_t *buf = (uint8_t *)malloc(4 + length);
    if (!buf) return NULL;
    put_be32(buf, (uint32_t)length);
    buf[4] = m->id;
    if (m->payload_len) memcpy(buf + 5, m->payload, m->payload_len);
    if (out_len) *out_len = 4 + length;
    return buf;
}

bool aethernet_bt_message_parse_body(const uint8_t *body, size_t len, aethernet_bt_message_t *out) {
    if (len == 0) return aethernet_bt_keepalive(out);
    return msg_set(out, true, body[0], body + 1, len - 1);
}

bool aethernet_bt_message_parse_frame(const uint8_t *data, size_t len, aethernet_bt_message_t *out, size_t *consumed) {
    if (len < 4) return false;
    uint32_t length = get_be32(data);
    if ((size_t)length + 4 > len) return false;
    if (!aethernet_bt_message_parse_body(data + 4, length, out)) return false;
    if (consumed) *consumed = 4 + (size_t)length;
    return true;
}

/* ── Bitfield ───────────────────────────────────────────────────────────── */

bool aethernet_bt_bitfield_init(aethernet_bt_bitfield_t *bf, int piece_count) {
    bf->count = piece_count;
    bf->nbytes = (size_t)((piece_count + 7) / 8);
    bf->bits = (uint8_t *)calloc(bf->nbytes ? bf->nbytes : 1, 1);
    return bf->bits != NULL;
}
bool aethernet_bt_bitfield_from_bytes(aethernet_bt_bitfield_t *bf, const uint8_t *data, size_t len, int piece_count) {
    if (!aethernet_bt_bitfield_init(bf, piece_count)) return false;
    size_t n = len < bf->nbytes ? len : bf->nbytes;
    if (n) memcpy(bf->bits, data, n);
    return true;
}
void aethernet_bt_bitfield_free(aethernet_bt_bitfield_t *bf) {
    if (!bf) return;
    free(bf->bits); bf->bits = NULL; bf->nbytes = 0; bf->count = 0;
}
bool aethernet_bt_bitfield_get(const aethernet_bt_bitfield_t *bf, int i) {
    if (i < 0 || i >= bf->count) return false;
    return (bf->bits[i >> 3] & (0x80 >> (i & 7))) != 0;
}
void aethernet_bt_bitfield_set(aethernet_bt_bitfield_t *bf, int i) {
    if (i < 0 || i >= bf->count) return;
    bf->bits[i >> 3] |= (uint8_t)(0x80 >> (i & 7));
}
int aethernet_bt_bitfield_popcount(const aethernet_bt_bitfield_t *bf) {
    int n = 0;
    for (int i = 0; i < bf->count; i++) if (aethernet_bt_bitfield_get(bf, i)) n++;
    return n;
}
bool aethernet_bt_bitfield_has_all(const aethernet_bt_bitfield_t *bf) {
    return aethernet_bt_bitfield_popcount(bf) == bf->count;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * µTP.
 * ═══════════════════════════════════════════════════════════════════════════ */

uint8_t *aethernet_bt_utp_to_bytes(const aethernet_bt_utp_packet_t *p, size_t *out_len) {
    size_t total = AETHERNET_BT_UTP_HEADER_SIZE + p->payload_len;
    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) return NULL;
    buf[0] = (uint8_t)(((uint8_t)p->type << 4) | AETHERNET_BT_UTP_VERSION);
    buf[1] = 0; /* no extensions */
    put_be16(buf + 2, p->connection_id);
    put_be32(buf + 4, p->timestamp_micros);
    put_be32(buf + 8, p->timestamp_diff);
    put_be32(buf + 12, p->window_size);
    put_be16(buf + 16, p->seq_nr);
    put_be16(buf + 18, p->ack_nr);
    if (p->payload_len) memcpy(buf + AETHERNET_BT_UTP_HEADER_SIZE, p->payload, p->payload_len);
    if (out_len) *out_len = total;
    return buf;
}

bool aethernet_bt_utp_parse(const uint8_t *data, size_t len, aethernet_bt_utp_packet_t *out) {
    if (len < AETHERNET_BT_UTP_HEADER_SIZE) return false;
    uint8_t version = data[0] & 0x0F;
    if (version != AETHERNET_BT_UTP_VERSION) return false;
    out->type = (aethernet_bt_utp_type_t)(data[0] >> 4);
    size_t offset = AETHERNET_BT_UTP_HEADER_SIZE;
    int next_ext = data[1];
    while (next_ext != 0) {
        if (offset + 2 > len) return false;
        int this_next = data[offset];
        int ext_len = data[offset + 1];
        offset += 2 + (size_t)ext_len;
        if (offset > len) return false;
        next_ext = this_next;
    }
    out->connection_id = get_be16(data + 2);
    out->timestamp_micros = get_be32(data + 4);
    out->timestamp_diff = get_be32(data + 8);
    out->window_size = get_be32(data + 12);
    out->seq_nr = get_be16(data + 16);
    out->ack_nr = get_be16(data + 18);
    out->payload = data + offset;
    out->payload_len = len - offset;
    return true;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Merkle (BEP-52).
 * ═══════════════════════════════════════════════════════════════════════════ */

static void sha256_pair(const uint8_t left[32], const uint8_t right[32], uint8_t out[32]) {
    uint8_t combined[64];
    memcpy(combined, left, 32);
    memcpy(combined + 32, right, 32);
    aethernet_sha256(combined, 64, out);
}

void aethernet_bt_merkle_root_block(const uint8_t *data, size_t len, size_t block_size, uint8_t out[32]) {
    if (block_size == 0) { memset(out, 0, 32); return; }
    size_t leaf_count = (len + block_size - 1) / block_size;
    if (leaf_count == 0) { memset(out, 0, 32); return; }
    /* pad to next power of two */
    size_t width = 1;
    while (width < leaf_count) width <<= 1;
    uint8_t (*level)[32] = (uint8_t (*)[32])malloc(width * 32);
    if (!level) { memset(out, 0, 32); return; }
    for (size_t i = 0; i < leaf_count; i++) {
        size_t start = i * block_size;
        size_t end = start + block_size;
        if (end > len) end = len;
        aethernet_sha256(data + start, end - start, level[i]);
    }
    for (size_t i = leaf_count; i < width; i++) memset(level[i], 0, 32);
    size_t n = width;
    while (n > 1) {
        for (size_t i = 0; i < n; i += 2)
            sha256_pair(level[i], level[i + 1], level[i / 2]);
        n /= 2;
    }
    memcpy(out, level[0], 32);
    free(level);
}

void aethernet_bt_merkle_root(const uint8_t *data, size_t len, uint8_t out[32]) {
    aethernet_bt_merkle_root_block(data, len, AETHERNET_BT_MERKLE_BLOCK_SIZE, out);
}

void aethernet_bt_v2_info_hash(const uint8_t *info_dict, size_t len, uint8_t out[32]) {
    aethernet_sha256(info_dict, len, out);
}

/* ═══════════════════════════════════════════════════════════════════════════
 * DHT.
 * ═══════════════════════════════════════════════════════════════════════════ */

void aethernet_bt_node_id_distance(const aethernet_bt_node_id_t *a, const aethernet_bt_node_id_t *b, aethernet_bt_node_id_t *out) {
    for (int i = 0; i < 20; i++) out->bytes[i] = a->bytes[i] ^ b->bytes[i];
}
int aethernet_bt_node_id_compare(const aethernet_bt_node_id_t *a, const aethernet_bt_node_id_t *b) {
    return memcmp(a->bytes, b->bytes, 20);
}
int aethernet_bt_node_id_leading_zeros(const aethernet_bt_node_id_t *a) {
    for (int i = 0; i < 20; i++) {
        if (a->bytes[i] != 0) {
            uint8_t by = a->bytes[i];
            int lz = 0;
            for (int b = 7; b >= 0; b--) { if (by & (1 << b)) break; lz++; }
            return i * 8 + lz;
        }
    }
    return 160;
}

uint8_t *aethernet_bt_encode_compact_nodes(const aethernet_bt_dht_contact_t *nodes, size_t n, size_t *out_len) {
    uint8_t *out = (uint8_t *)malloc(n ? n * 26 : 1);
    if (!out) return NULL;
    for (size_t i = 0; i < n; i++) {
        uint8_t *p = out + i * 26;
        memcpy(p, nodes[i].id.bytes, 20);
        memcpy(p + 20, nodes[i].ip, 4);
        put_be16(p + 24, nodes[i].port);
    }
    if (out_len) *out_len = n * 26;
    return out;
}
aethernet_bt_dht_contact_t *aethernet_bt_decode_compact_nodes(const uint8_t *data, size_t len, size_t *out_count) {
    if (len % 26 != 0) return NULL;
    size_t n = len / 26;
    aethernet_bt_dht_contact_t *out = (aethernet_bt_dht_contact_t *)malloc(n ? n * sizeof(*out) : 1);
    if (!out) return NULL;
    for (size_t i = 0; i < n; i++) {
        const uint8_t *p = data + i * 26;
        memcpy(out[i].id.bytes, p, 20);
        memcpy(out[i].ip, p + 20, 4);
        out[i].port = get_be16(p + 24);
    }
    if (out_count) *out_count = n;
    return out;
}
uint8_t *aethernet_bt_encode_compact_peers(const aethernet_bt_peer_addr_t *peers, size_t n, size_t *out_len) {
    uint8_t *out = (uint8_t *)malloc(n ? n * 6 : 1);
    if (!out) return NULL;
    for (size_t i = 0; i < n; i++) {
        uint8_t *p = out + i * 6;
        memcpy(p, peers[i].ip, 4);
        put_be16(p + 4, peers[i].port);
    }
    if (out_len) *out_len = n * 6;
    return out;
}
aethernet_bt_peer_addr_t *aethernet_bt_decode_compact_peers(const uint8_t *data, size_t len, size_t *out_count) {
    if (len % 6 != 0) return NULL;
    size_t n = len / 6;
    aethernet_bt_peer_addr_t *out = (aethernet_bt_peer_addr_t *)malloc(n ? n * sizeof(*out) : 1);
    if (!out) return NULL;
    for (size_t i = 0; i < n; i++) {
        const uint8_t *p = data + i * 6;
        memcpy(out[i].ip, p, 4);
        out[i].port = get_be16(p + 4);
    }
    if (out_count) *out_count = n;
    return out;
}

void aethernet_bt_routing_table_init(aethernet_bt_routing_table_t *t, const aethernet_bt_node_id_t *self) {
    memset(t, 0, sizeof(*t));
    t->self = *self;
}
static int rt_bucket_index(const aethernet_bt_routing_table_t *t, const aethernet_bt_node_id_t *id) {
    aethernet_bt_node_id_t d;
    aethernet_bt_node_id_distance(&t->self, id, &d);
    int lz = aethernet_bt_node_id_leading_zeros(&d);
    if (lz >= 160) return 159;
    return lz;
}
bool aethernet_bt_routing_table_try_add(aethernet_bt_routing_table_t *t, const aethernet_bt_dht_contact_t *c) {
    if (memcmp(c->id.bytes, t->self.bytes, 20) == 0) return false;
    int idx = rt_bucket_index(t, &c->id);
    for (int i = 0; i < t->bucket_len[idx]; i++) {
        if (memcmp(t->buckets[idx][i].id.bytes, c->id.bytes, 20) == 0) { t->buckets[idx][i] = *c; return true; }
    }
    if (t->bucket_len[idx] < AETHERNET_BT_DHT_K) { t->buckets[idx][t->bucket_len[idx]++] = *c; return true; }
    return false;
}
size_t aethernet_bt_routing_table_count(const aethernet_bt_routing_table_t *t) {
    size_t n = 0;
    for (int i = 0; i < 160; i++) n += (size_t)t->bucket_len[i];
    return n;
}
size_t aethernet_bt_routing_table_closest(const aethernet_bt_routing_table_t *t,
                                           const aethernet_bt_node_id_t *target,
                                           aethernet_bt_dht_contact_t *out, size_t out_cap, size_t count) {
    /* Gather all contacts, then select the `count` nearest by XOR distance. */
    size_t total = aethernet_bt_routing_table_count(t);
    if (total == 0) return 0;
    aethernet_bt_dht_contact_t *all = (aethernet_bt_dht_contact_t *)malloc(total * sizeof(*all));
    if (!all) return 0;
    size_t k = 0;
    for (int i = 0; i < 160; i++)
        for (int j = 0; j < t->bucket_len[i]; j++) all[k++] = t->buckets[i][j];
    bool *used = (bool *)calloc(total, sizeof(bool));
    if (!used) { free(all); return 0; }
    size_t want = count < total ? count : total;
    if (want > out_cap) want = out_cap;
    size_t written = 0;
    for (size_t s = 0; s < want; s++) {
        int best = -1;
        aethernet_bt_node_id_t best_d;
        for (size_t i = 0; i < total; i++) {
            if (used[i]) continue;
            aethernet_bt_node_id_t d;
            aethernet_bt_node_id_distance(&all[i].id, target, &d);
            if (best == -1 || memcmp(d.bytes, best_d.bytes, 20) < 0) { best = (int)i; best_d = d; }
        }
        if (best < 0) break;
        used[best] = true;
        out[written++] = all[best];
    }
    free(used); free(all);
    return written;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * KRPC.
 * ═══════════════════════════════════════════════════════════════════════════ */

/* Emit a bencode byte string header+body directly to a buffer. */
static void emit_bstr(bytebuf_t *b, const uint8_t *bytes, size_t len) {
    bb_int(b, (int64_t)len); bb_byte(b, ':'); bb_write(b, bytes, len);
}
static void emit_key(bytebuf_t *b, const char *key) {
    emit_bstr(b, (const uint8_t *)key, strlen(key));
}

uint8_t *aethernet_bt_krpc_encode(const aethernet_bt_krpc_message_t *m, size_t *out_len) {
    bytebuf_t b; bb_init(&b);
    bb_byte(&b, 'd');
    /* Keys are emitted in canonical (sorted) order per message type:
       query {a,q,t,y} · response {r,t,y} · error {e,t,y}. */
    if (m->type == AETHERNET_BT_KRPC_QUERY) {
        emit_key(&b, "a");
        if (m->arguments) {
            size_t alen = 0;
            uint8_t *enc = aethernet_benc_encode(m->arguments, &alen);
            if (enc) { bb_write(&b, enc, alen); free(enc); } else { bb_cstr(&b, "de"); }
        } else { bb_cstr(&b, "de"); }
        emit_key(&b, "q");
        emit_bstr(&b, (const uint8_t *)(m->method ? m->method : ""), m->method ? strlen(m->method) : 0);
    } else if (m->type == AETHERNET_BT_KRPC_RESPONSE) {
        emit_key(&b, "r");
        if (m->response) {
            size_t rlen = 0;
            uint8_t *enc = aethernet_benc_encode(m->response, &rlen);
            if (enc) { bb_write(&b, enc, rlen); free(enc); } else { bb_cstr(&b, "de"); }
        } else { bb_cstr(&b, "de"); }
    } else if (m->type == AETHERNET_BT_KRPC_ERROR) {
        emit_key(&b, "e");
        bb_byte(&b, 'l');
        bb_byte(&b, 'i'); bb_int(&b, m->error_code); bb_byte(&b, 'e');
        emit_bstr(&b, (const uint8_t *)(m->error_message ? m->error_message : ""), m->error_message ? strlen(m->error_message) : 0);
        bb_byte(&b, 'e');
    } else {
        free(b.data); return NULL;
    }
    emit_key(&b, "t");
    emit_bstr(&b, m->transaction_id ? m->transaction_id : (const uint8_t *)"", m->transaction_id_len);
    emit_key(&b, "y");
    const char *y = m->type == AETHERNET_BT_KRPC_QUERY ? "q" : (m->type == AETHERNET_BT_KRPC_RESPONSE ? "r" : "e");
    emit_bstr(&b, (const uint8_t *)y, 1);
    bb_byte(&b, 'e');
    return bb_finish(&b, out_len);
}

bool aethernet_bt_krpc_decode(const uint8_t *data, size_t len, aethernet_bt_krpc_message_t *out) {
    memset(out, 0, sizeof(*out));
    aethernet_benc_value_t *root = aethernet_benc_decode(data, len);
    if (!root || root->type != AETHERNET_BENC_DICT) { aethernet_benc_free(root); return false; }
    out->owns = true;

    const aethernet_benc_value_t *tv = aethernet_benc_dict_get(root, "t");
    const uint8_t *tb = NULL; size_t tl = 0;
    if (!as_bytes(tv, &tb, &tl)) { aethernet_benc_free(root); return false; }
    out->transaction_id = (uint8_t *)malloc(tl ? tl : 1);
    if (!out->transaction_id) { aethernet_benc_free(root); return false; }
    if (tl) memcpy(out->transaction_id, tb, tl);
    out->transaction_id_len = tl;

    const aethernet_benc_value_t *yv = aethernet_benc_dict_get(root, "y");
    if (!yv || yv->type != AETHERNET_BENC_STR || yv->s_len != 1) { aethernet_benc_free(root); aethernet_bt_krpc_free(out); return false; }
    char y = (char)yv->s[0];
    if (y == 'q') {
        out->type = AETHERNET_BT_KRPC_QUERY;
        const aethernet_benc_value_t *qv = aethernet_benc_dict_get(root, "q");
        out->method = dup_text(qv);
        if (!out->method) { aethernet_benc_free(root); aethernet_bt_krpc_free(out); return false; }
        const aethernet_benc_value_t *av = aethernet_benc_dict_get(root, "a");
        if (av && av->type == AETHERNET_BENC_DICT) out->arguments = benc_deep_copy(av);
    } else if (y == 'r') {
        out->type = AETHERNET_BT_KRPC_RESPONSE;
        const aethernet_benc_value_t *rv = aethernet_benc_dict_get(root, "r");
        if (rv && rv->type == AETHERNET_BENC_DICT) out->response = benc_deep_copy(rv);
    } else if (y == 'e') {
        out->type = AETHERNET_BT_KRPC_ERROR;
        const aethernet_benc_value_t *ev = aethernet_benc_dict_get(root, "e");
        if (ev && ev->type == AETHERNET_BENC_LIST && ev->count >= 2) {
            as_int(ev->items[0], &out->error_code);
            out->error_message = dup_text(ev->items[1]);
        }
    } else { aethernet_benc_free(root); aethernet_bt_krpc_free(out); return false; }

    aethernet_benc_free(root);
    return true;
}

void aethernet_bt_krpc_free(aethernet_bt_krpc_message_t *m) {
    if (!m) return;
    free(m->transaction_id);
    free(m->method);
    free(m->error_message);
    if (m->owns) { aethernet_benc_free(m->arguments); aethernet_benc_free(m->response); }
    m->transaction_id = NULL; m->method = NULL; m->error_message = NULL;
    m->arguments = NULL; m->response = NULL;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Extension protocol (BEP-10) + ut_metadata (BEP-9) + PEX (BEP-11).
 * ═══════════════════════════════════════════════════════════════════════════ */

uint8_t *aethernet_bt_wrap_extended(uint8_t sub_id, const uint8_t *body, size_t body_len, size_t *out_len) {
    uint8_t *out = (uint8_t *)malloc(1 + body_len);
    if (!out) return NULL;
    out[0] = sub_id;
    if (body_len) memcpy(out + 1, body, body_len);
    if (out_len) *out_len = 1 + body_len;
    return out;
}
bool aethernet_bt_split_extended(const uint8_t *payload, size_t len, uint8_t *out_sub_id, const uint8_t **out_body, size_t *out_body_len) {
    if (len < 1) return false;
    *out_sub_id = payload[0];
    *out_body = payload + 1;
    *out_body_len = len - 1;
    return true;
}

uint8_t *aethernet_bt_build_metadata_request(int piece, size_t *out_len) {
    aethernet_benc_value_t *d = aethernet_benc_dict();
    if (!d) return NULL;
    aethernet_benc_dict_add(d, "msg_type", aethernet_benc_int(AETHERNET_BT_METADATA_REQUEST));
    aethernet_benc_dict_add(d, "piece", aethernet_benc_int(piece));
    uint8_t *out = aethernet_benc_encode(d, out_len);
    aethernet_benc_free(d);
    return out;
}
uint8_t *aethernet_bt_build_metadata_data(int piece, int total_size, const uint8_t *data, size_t data_len, size_t *out_len) {
    aethernet_benc_value_t *d = aethernet_benc_dict();
    if (!d) return NULL;
    aethernet_benc_dict_add(d, "msg_type", aethernet_benc_int(AETHERNET_BT_METADATA_DATA));
    aethernet_benc_dict_add(d, "piece", aethernet_benc_int(piece));
    aethernet_benc_dict_add(d, "total_size", aethernet_benc_int(total_size));
    size_t hlen = 0;
    uint8_t *header = aethernet_benc_encode(d, &hlen);
    aethernet_benc_free(d);
    if (!header) return NULL;
    uint8_t *out = (uint8_t *)malloc(hlen + data_len);
    if (!out) { free(header); return NULL; }
    memcpy(out, header, hlen);
    if (data_len) memcpy(out + hlen, data, data_len);
    free(header);
    if (out_len) *out_len = hlen + data_len;
    return out;
}
uint8_t *aethernet_bt_build_metadata_reject(int piece, size_t *out_len) {
    aethernet_benc_value_t *d = aethernet_benc_dict();
    if (!d) return NULL;
    aethernet_benc_dict_add(d, "msg_type", aethernet_benc_int(AETHERNET_BT_METADATA_REJECT));
    aethernet_benc_dict_add(d, "piece", aethernet_benc_int(piece));
    uint8_t *out = aethernet_benc_encode(d, out_len);
    aethernet_benc_free(d);
    return out;
}

bool aethernet_bt_parse_metadata(const uint8_t *body, size_t len, aethernet_bt_metadata_message_t *out) {
    memset(out, 0, sizeof(*out));
    size_t used = 0;
    aethernet_benc_value_t *v = aethernet_benc_decode_n(body, len, &used);
    if (!v || v->type != AETHERNET_BENC_DICT) { aethernet_benc_free(v); return false; }
    int64_t tmp;
    const aethernet_benc_value_t *mt = aethernet_benc_dict_get(v, "msg_type");
    if (as_int(mt, &tmp)) out->type = (aethernet_bt_metadata_type_t)tmp;
    const aethernet_benc_value_t *pc = aethernet_benc_dict_get(v, "piece");
    if (as_int(pc, &tmp)) out->piece = (int)tmp;
    const aethernet_benc_value_t *ts = aethernet_benc_dict_get(v, "total_size");
    if (as_int(ts, &tmp)) out->total_size = (int)tmp;
    aethernet_benc_free(v);
    size_t dlen = len - used;
    out->data = (uint8_t *)malloc(dlen ? dlen : 1);
    if (!out->data) return false;
    if (dlen) memcpy(out->data, body + used, dlen);
    out->data_len = dlen;
    return true;
}
void aethernet_bt_metadata_message_free(aethernet_bt_metadata_message_t *m) {
    if (!m) return;
    free(m->data); m->data = NULL; m->data_len = 0;
}

uint8_t *aethernet_bt_build_extension_handshake(const char *const *names, const int *ids, size_t n,
                                                int metadata_size, size_t *out_len) {
    aethernet_benc_value_t *mdict = aethernet_benc_dict();
    aethernet_benc_value_t *d = aethernet_benc_dict();
    if (!mdict || !d) { aethernet_benc_free(mdict); aethernet_benc_free(d); return NULL; }
    for (size_t i = 0; i < n; i++)
        aethernet_benc_dict_add(mdict, names[i], aethernet_benc_int(ids[i]));
    aethernet_benc_dict_add(d, "m", mdict);
    if (metadata_size > 0)
        aethernet_benc_dict_add(d, "metadata_size", aethernet_benc_int(metadata_size));
    size_t blen = 0;
    uint8_t *body = aethernet_benc_encode(d, &blen);
    aethernet_benc_free(d);
    if (!body) return NULL;
    uint8_t *out = aethernet_bt_wrap_extended(AETHERNET_BT_EXTENSION_HANDSHAKE_ID, body, blen, out_len);
    free(body);
    return out;
}

uint8_t *aethernet_bt_build_pex_added(const aethernet_bt_peer_addr_t *added, size_t n, size_t *out_len) {
    size_t clen = 0;
    uint8_t *compact = aethernet_bt_encode_compact_peers(added, n, &clen);
    if (!compact) return NULL;
    aethernet_benc_value_t *d = aethernet_benc_dict();
    if (!d) { free(compact); return NULL; }
    aethernet_benc_dict_add(d, "added", aethernet_benc_str(compact, clen));
    free(compact);
    uint8_t *out = aethernet_benc_encode(d, out_len);
    aethernet_benc_free(d);
    return out;
}
aethernet_bt_peer_addr_t *aethernet_bt_parse_pex_added(const uint8_t *body, size_t len, size_t *out_count) {
    aethernet_benc_value_t *v = aethernet_benc_decode(body, len);
    if (!v || v->type != AETHERNET_BENC_DICT) { aethernet_benc_free(v); return NULL; }
    const aethernet_benc_value_t *a = aethernet_benc_dict_get(v, "added");
    aethernet_bt_peer_addr_t *out = NULL;
    if (a && a->type == AETHERNET_BENC_STR)
        out = aethernet_bt_decode_compact_peers(a->s, a->s_len, out_count);
    aethernet_benc_free(v);
    return out;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Rarest-first picker.
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct { char *peer; bool *has; } picker_peer_t;

struct aethernet_bt_picker {
    int piece_count;
    bool *have;
    bool *in_flight;
    int  *availability;
    picker_peer_t *peers;
    size_t peer_count;
    size_t peer_cap;
};

aethernet_bt_picker_t *aethernet_bt_picker_new(int piece_count) {
    aethernet_bt_picker_t *p = (aethernet_bt_picker_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->piece_count = piece_count;
    p->have = (bool *)calloc(piece_count ? piece_count : 1, sizeof(bool));
    p->in_flight = (bool *)calloc(piece_count ? piece_count : 1, sizeof(bool));
    p->availability = (int *)calloc(piece_count ? piece_count : 1, sizeof(int));
    if (!p->have || !p->in_flight || !p->availability) { aethernet_bt_picker_free(p); return NULL; }
    return p;
}
void aethernet_bt_picker_free(aethernet_bt_picker_t *p) {
    if (!p) return;
    free(p->have); free(p->in_flight); free(p->availability);
    for (size_t i = 0; i < p->peer_count; i++) { free(p->peers[i].peer); free(p->peers[i].has); }
    free(p->peers);
    free(p);
}
static picker_peer_t *picker_find(aethernet_bt_picker_t *p, const char *peer) {
    for (size_t i = 0; i < p->peer_count; i++)
        if (strcmp(p->peers[i].peer, peer) == 0) return &p->peers[i];
    return NULL;
}
void aethernet_bt_picker_set_have(aethernet_bt_picker_t *p, int index) {
    if (index >= 0 && index < p->piece_count) { p->have[index] = true; p->in_flight[index] = false; }
}
void aethernet_bt_picker_add_peer(aethernet_bt_picker_t *p, const char *peer) {
    if (picker_find(p, peer)) return;
    if (p->peer_count == p->peer_cap) {
        size_t ncap = p->peer_cap ? p->peer_cap * 2 : 4;
        picker_peer_t *np = (picker_peer_t *)realloc(p->peers, ncap * sizeof(*np));
        if (!np) return;
        p->peers = np; p->peer_cap = ncap;
    }
    picker_peer_t *pp = &p->peers[p->peer_count];
    pp->peer = (char *)malloc(strlen(peer) + 1);
    if (!pp->peer) return;
    strcpy(pp->peer, peer);
    pp->has = (bool *)calloc(p->piece_count ? p->piece_count : 1, sizeof(bool));
    if (!pp->has) { free(pp->peer); return; }
    p->peer_count++;
}
void aethernet_bt_picker_peer_has(aethernet_bt_picker_t *p, const char *peer, int index) {
    aethernet_bt_picker_add_peer(p, peer);
    picker_peer_t *pp = picker_find(p, peer);
    if (pp && index >= 0 && index < p->piece_count && !pp->has[index]) {
        pp->has[index] = true;
        p->availability[index]++;
    }
}
int aethernet_bt_picker_pick_for(aethernet_bt_picker_t *p, const char *peer) {
    picker_peer_t *pp = picker_find(p, peer);
    if (!pp) return -1;
    int best = -1, best_avail = 0;
    for (int i = 0; i < p->piece_count; i++) {
        if (p->have[i] || p->in_flight[i] || !pp->has[i]) continue;
        if (best == -1 || p->availability[i] < best_avail) { best = i; best_avail = p->availability[i]; }
    }
    if (best != -1) p->in_flight[best] = true;
    return best;
}
void aethernet_bt_picker_release(aethernet_bt_picker_t *p, int index) {
    if (index >= 0 && index < p->piece_count) p->in_flight[index] = false;
}
bool aethernet_bt_picker_is_complete(const aethernet_bt_picker_t *p) {
    if (p->piece_count == 0) return false;
    for (int i = 0; i < p->piece_count; i++) if (!p->have[i]) return false;
    return true;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Piece store.
 * ═══════════════════════════════════════════════════════════════════════════ */

typedef struct { uint8_t *data; size_t len; bool present; } stored_piece_t;

struct aethernet_bt_piece_store {
    int piece_length;
    int64_t total_length;
    uint8_t *piece_hashes;   /* piece_count * 20 */
    size_t piece_count;
    stored_piece_t *pieces;
};

aethernet_bt_piece_store_t *aethernet_bt_piece_store_new(int piece_length, int64_t total_length,
                                                         const uint8_t *piece_hashes, size_t piece_count) {
    aethernet_bt_piece_store_t *s = (aethernet_bt_piece_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->piece_length = piece_length;
    s->total_length = total_length;
    s->piece_count = piece_count;
    if (piece_count) {
        s->piece_hashes = (uint8_t *)malloc(piece_count * 20);
        s->pieces = (stored_piece_t *)calloc(piece_count, sizeof(stored_piece_t));
        if (!s->piece_hashes || !s->pieces) { aethernet_bt_piece_store_free(s); return NULL; }
        if (piece_hashes) memcpy(s->piece_hashes, piece_hashes, piece_count * 20);
    }
    return s;
}
void aethernet_bt_piece_store_free(aethernet_bt_piece_store_t *s) {
    if (!s) return;
    free(s->piece_hashes);
    if (s->pieces) { for (size_t i = 0; i < s->piece_count; i++) free(s->pieces[i].data); free(s->pieces); }
    free(s);
}
size_t aethernet_bt_piece_store_piece_count(const aethernet_bt_piece_store_t *s) { return s->piece_count; }
int aethernet_bt_piece_store_length_of_piece(const aethernet_bt_piece_store_t *s, int i) {
    if (i < 0 || (size_t)i >= s->piece_count) return 0;
    if ((size_t)i == s->piece_count - 1)
        return (int)(s->total_length - (int64_t)i * (int64_t)s->piece_length);
    return s->piece_length;
}
bool aethernet_bt_piece_store_has(const aethernet_bt_piece_store_t *s, int i) {
    if (i < 0 || (size_t)i >= s->piece_count) return false;
    return s->pieces[i].present;
}
bool aethernet_bt_piece_store_try_complete(aethernet_bt_piece_store_t *s, int i, const uint8_t *data, size_t len) {
    if (i < 0 || (size_t)i >= s->piece_count) return false;
    if ((int)len != aethernet_bt_piece_store_length_of_piece(s, i)) return false;
    uint8_t h[20];
    aethernet_bt_sha1(data, len, h);
    if (memcmp(h, s->piece_hashes + (size_t)i * 20, 20) != 0) return false;
    uint8_t *cp = (uint8_t *)malloc(len ? len : 1);
    if (!cp) return false;
    if (len) memcpy(cp, data, len);
    free(s->pieces[i].data);
    s->pieces[i].data = cp; s->pieces[i].len = len; s->pieces[i].present = true;
    return true;
}
bool aethernet_bt_piece_store_is_complete(const aethernet_bt_piece_store_t *s) {
    for (size_t i = 0; i < s->piece_count; i++) if (!s->pieces[i].present) return false;
    return s->piece_count > 0;
}
uint8_t *aethernet_bt_piece_store_assemble(const aethernet_bt_piece_store_t *s, size_t *out_len) {
    if (!aethernet_bt_piece_store_is_complete(s)) return NULL;
    uint8_t *out = (uint8_t *)malloc(s->total_length ? (size_t)s->total_length : 1);
    if (!out) return NULL;
    size_t off = 0;
    for (size_t i = 0; i < s->piece_count; i++) {
        memcpy(out + off, s->pieces[i].data, s->pieces[i].len);
        off += s->pieces[i].len;
    }
    if (out_len) *out_len = (size_t)s->total_length;
    return out;
}

aethernet_bt_piece_store_t *aethernet_bt_piece_store_from_content(const uint8_t *data, size_t len, int piece_length) {
    if (piece_length <= 0) return NULL;
    size_t pl = (size_t)piece_length;
    size_t piece_count = (len + pl - 1) / pl;
    aethernet_bt_piece_store_t *s = aethernet_bt_piece_store_new(piece_length, (int64_t)len, NULL, piece_count);
    if (!s) return NULL;
    for (size_t i = 0; i < piece_count; i++) {
        size_t start = i * pl;
        size_t end = start + pl;
        if (end > len) end = len;
        aethernet_bt_sha1(data + start, end - start, s->piece_hashes + i * 20);
        size_t seg = end - start;
        s->pieces[i].data = (uint8_t *)malloc(seg ? seg : 1);
        if (!s->pieces[i].data) { aethernet_bt_piece_store_free(s); return NULL; }
        if (seg) memcpy(s->pieces[i].data, data + start, seg);
        s->pieces[i].len = seg;
        s->pieces[i].present = true;
    }
    return s;
}
