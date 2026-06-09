// SPDX-License-Identifier: MIT
// AetherNet URI scheme — C implementation matching the C# reference under
// src/AetherNet.Core/Uri/.

#include <ctype.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/aethernet_uri.h"
#include "aethernet/aethernet_tag.h"

/* ─── Local helpers ─────────────────────────────────────────────────────── */

static char *str_dup_n(const char *s, size_t n)
{
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    if (n) memcpy(out, s, n);
    out[n] = '\0';
    return out;
}

static char *str_dup_c(const char *s)
{
    if (!s) return NULL;
    return str_dup_n(s, strlen(s));
}

static void set_error(char *buf, size_t cap, const char *fmt, ...)
{
    if (!buf || cap == 0) return;
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, cap, fmt, ap);
    va_end(ap);
}

static int starts_with_iascii(const char *s, const char *prefix)
{
    while (*prefix) {
        if (!*s) return 0;
        unsigned char a = (unsigned char)*s++;
        unsigned char b = (unsigned char)*prefix++;
        if (a >= 'A' && a <= 'Z') a = (unsigned char)(a - 'A' + 'a');
        if (b >= 'A' && b <= 'Z') b = (unsigned char)(b - 'A' + 'a');
        if (a != b) return 0;
    }
    return 1;
}

static char ascii_tolower(char c)
{
    if (c >= 'A' && c <= 'Z') return (char)(c - 'A' + 'a');
    return c;
}

static char ascii_toupper(char c)
{
    if (c >= 'a' && c <= 'z') return (char)(c - 'a' + 'A');
    return c;
}

static int is_hex_char(char c)
{
    return (c >= '0' && c <= '9') ||
           (c >= 'A' && c <= 'F') ||
           (c >= 'a' && c <= 'f');
}

static int hex_value(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

static int is_unreserved(unsigned char c)
{
    return (c >= 'A' && c <= 'Z') ||
           (c >= 'a' && c <= 'z') ||
           (c >= '0' && c <= '9') ||
           c == '-' || c == '.' || c == '_' || c == '~';
}

static int is_sub_delim(unsigned char c)
{
    switch (c) {
        case '!': case '$': case '&': case '\'':
        case '(': case ')': case '*': case '+':
        case ',': case ';': case '=':
            return 1;
        default:
            return 0;
    }
}

/* ─── Dynamic string buffer ─────────────────────────────────────────────── */

typedef struct {
    char  *data;
    size_t len;
    size_t cap;
} sbuf_t;

static int sbuf_init(sbuf_t *b, size_t initial_cap)
{
    if (initial_cap < 32) initial_cap = 32;
    b->data = (char *)malloc(initial_cap);
    if (!b->data) return -1;
    b->data[0] = '\0';
    b->len = 0;
    b->cap = initial_cap;
    return 0;
}

static void sbuf_free(sbuf_t *b)
{
    if (!b) return;
    free(b->data);
    b->data = NULL;
    b->len = b->cap = 0;
}

static int sbuf_reserve(sbuf_t *b, size_t extra)
{
    /* +1 for NUL */
    size_t need = b->len + extra + 1;
    if (need <= b->cap) return 0;
    size_t newcap = b->cap ? b->cap : 32;
    while (newcap < need) {
        if (newcap > (size_t)-1 / 2) { newcap = need; break; }
        newcap *= 2;
    }
    char *nd = (char *)realloc(b->data, newcap);
    if (!nd) return -1;
    b->data = nd;
    b->cap = newcap;
    return 0;
}

static int sbuf_append_char(sbuf_t *b, char c)
{
    if (sbuf_reserve(b, 1) != 0) return -1;
    b->data[b->len++] = c;
    b->data[b->len] = '\0';
    return 0;
}

static int sbuf_append_n(sbuf_t *b, const char *s, size_t n)
{
    if (n == 0) return 0;
    if (sbuf_reserve(b, n) != 0) return -1;
    memcpy(b->data + b->len, s, n);
    b->len += n;
    b->data[b->len] = '\0';
    return 0;
}

static int sbuf_append(sbuf_t *b, const char *s)
{
    return sbuf_append_n(b, s, strlen(s));
}

static int sbuf_append_pct(sbuf_t *b, unsigned char byte)
{
    static const char HEX[] = "0123456789ABCDEF";
    char enc[3];
    enc[0] = '%';
    enc[1] = HEX[(byte >> 4) & 0x0F];
    enc[2] = HEX[byte & 0x0F];
    return sbuf_append_n(b, enc, 3);
}

/* steal the buffer; caller owns the returned malloc'd C string */
static char *sbuf_steal(sbuf_t *b)
{
    char *out = b->data;
    b->data = NULL;
    b->len = b->cap = 0;
    return out;
}

/* ─── Percent encoding/decoding ─────────────────────────────────────────── */

typedef enum {
    ENC_PATH_SEGMENT,
    ENC_QUERY_KEY,
    ENC_QUERY_VALUE,
    ENC_FRAGMENT
} encode_kind_t;

static int is_allowed_unencoded(unsigned char c, encode_kind_t kind)
{
    if (is_unreserved(c)) return 1;
    switch (kind) {
        case ENC_PATH_SEGMENT:
            return is_sub_delim(c) || c == ':' || c == '@';
        case ENC_QUERY_KEY:
            /* '&' and '=' always encoded; allow ':' '@' and the other sub-delims */
            switch (c) {
                case ':': case '@':
                case '!': case '$': case '\'':
                case '(': case ')': case '*':
                case '+': case ',': case ';':
                    return 1;
                default:
                    return 0;
            }
        case ENC_QUERY_VALUE:
            /* Allow sub-delims except '&'; '=' is fine inside a value */
            switch (c) {
                case ':': case '@': case '/': case '?':
                case '!': case '$': case '\'':
                case '(': case ')': case '*':
                case '+': case ',': case ';': case '=':
                    return 1;
                default:
                    return 0;
            }
        case ENC_FRAGMENT:
            if (is_sub_delim(c)) return 1;
            switch (c) {
                case ':': case '@': case '/': case '?':
                    return 1;
                default:
                    return 0;
            }
    }
    return 0;
}

/* Encode a single string component into b. Treats input as a raw byte
 * stream (UTF-8). Returns 0 on success, -1 on alloc failure. */
static int encode_component(sbuf_t *b, const char *value, encode_kind_t kind)
{
    if (!value) return 0;
    for (const unsigned char *p = (const unsigned char *)value; *p; p++) {
        if (is_allowed_unencoded(*p, kind)) {
            if (sbuf_append_char(b, (char)*p) != 0) return -1;
        } else {
            if (sbuf_append_pct(b, *p) != 0) return -1;
        }
    }
    return 0;
}

/* Encode the path: emit segments joined by '/', each segment percent-encoded. */
static int encode_path(sbuf_t *b, const char *path)
{
    if (!path || !*path) return 0;
    const char *p = path;
    while (1) {
        const char *slash = strchr(p, '/');
        size_t seg_len = slash ? (size_t)(slash - p) : strlen(p);
        /* Emit segment */
        for (size_t i = 0; i < seg_len; i++) {
            unsigned char c = (unsigned char)p[i];
            if (is_allowed_unencoded(c, ENC_PATH_SEGMENT)) {
                if (sbuf_append_char(b, (char)c) != 0) return -1;
            } else {
                if (sbuf_append_pct(b, c) != 0) return -1;
            }
        }
        if (!slash) break;
        if (sbuf_append_char(b, '/') != 0) return -1;
        p = slash + 1;
    }
    return 0;
}

/* Percent-decode an input string (any length) into a freshly-allocated
 * NUL-terminated string. Non-encoded bytes are passed through verbatim;
 * decoder is byte-oriented so UTF-8 round-trips bit-for-bit. */
static char *percent_decode(const char *input, size_t len)
{
    /* Output is at most as long as input. */
    char *out = (char *)malloc(len + 1);
    if (!out) return NULL;
    size_t oi = 0;
    for (size_t i = 0; i < len; i++) {
        char c = input[i];
        if (c == '%' && i + 2 < len &&
            is_hex_char(input[i + 1]) && is_hex_char(input[i + 2])) {
            int hi = hex_value(input[i + 1]);
            int lo = hex_value(input[i + 2]);
            out[oi++] = (char)((hi << 4) | lo);
            i += 2;
        } else {
            out[oi++] = c;
        }
    }
    out[oi] = '\0';
    return out;
}

static char *percent_decode_cstr(const char *input)
{
    if (!input) return str_dup_c("");
    return percent_decode(input, strlen(input));
}

/* Decode a path: each segment independently so '/' is preserved literally. */
static char *percent_decode_path(const char *path)
{
    if (!path || !*path) return str_dup_c("");
    sbuf_t b;
    if (sbuf_init(&b, strlen(path) + 1) != 0) return NULL;
    const char *p = path;
    int first = 1;
    while (1) {
        const char *slash = strchr(p, '/');
        size_t seg_len = slash ? (size_t)(slash - p) : strlen(p);
        char *dec = percent_decode(p, seg_len);
        if (!dec) { sbuf_free(&b); return NULL; }
        if (!first) {
            if (sbuf_append_char(&b, '/') != 0) { free(dec); sbuf_free(&b); return NULL; }
        }
        first = 0;
        if (sbuf_append(&b, dec) != 0) { free(dec); sbuf_free(&b); return NULL; }
        free(dec);
        if (!slash) break;
        p = slash + 1;
    }
    return sbuf_steal(&b);
}

/* ─── Authority canonicalisation ────────────────────────────────────────── */

/* Returns a malloc'd canonical authority string, or NULL on failure. On
 * failure, *err_msg may be set to a static const string with the reason. */
static char *canonicalise_authority(const char *raw, const char **err_msg)
{
    *err_msg = NULL;
    if (!raw || !*raw) {
        *err_msg = "Authority is missing.";
        return NULL;
    }

    size_t len = strlen(raw);

    /* UHID — 64 hex chars. */
    if (len == 64) {
        int all_hex = 1;
        for (size_t i = 0; i < 64; i++) {
            if (!is_hex_char(raw[i])) { all_hex = 0; break; }
        }
        if (all_hex) {
            char *out = (char *)malloc(65);
            if (!out) return NULL;
            for (size_t i = 0; i < 64; i++) out[i] = ascii_toupper(raw[i]);
            out[64] = '\0';
            return out;
        }
    }

    /* AetherTag — 10 Crockford chars with optional dash. */
    if (len == 10 || len == 11) {
        aethernet_tag_t tag;
        if (aethernet_tag_parse(raw, &tag) == 0) {
            return str_dup_c(tag.value);
        }
    }

    *err_msg = "Authority is neither a valid AetherTag nor a 64-char hex UHID.";
    return NULL;
}

/* ─── Query parameter map ───────────────────────────────────────────────── */

/* Parallel arrays of malloc'd key/value strings. Keys are stored
 * lower-cased for case-insensitive lookup; values are kept verbatim. */
typedef struct {
    char  **keys;
    char  **values;
    size_t  count;
    size_t  cap;
} query_map_t;

static int qmap_init(query_map_t *m)
{
    m->keys = NULL;
    m->values = NULL;
    m->count = 0;
    m->cap = 0;
    return 0;
}

static void qmap_free(query_map_t *m)
{
    if (!m) return;
    for (size_t i = 0; i < m->count; i++) {
        free(m->keys[i]);
        free(m->values[i]);
    }
    free(m->keys);
    free(m->values);
    m->keys = NULL;
    m->values = NULL;
    m->count = m->cap = 0;
}

static int qmap_reserve(query_map_t *m, size_t extra)
{
    size_t need = m->count + extra;
    if (need <= m->cap) return 0;
    size_t newcap = m->cap ? m->cap : 4;
    while (newcap < need) newcap *= 2;
    char **nk = (char **)realloc(m->keys, newcap * sizeof(char *));
    if (!nk) return -1;
    m->keys = nk;
    char **nv = (char **)realloc(m->values, newcap * sizeof(char *));
    if (!nv) return -1;
    m->values = nv;
    m->cap = newcap;
    return 0;
}

static int qmap_find(const query_map_t *m, const char *lc_key)
{
    for (size_t i = 0; i < m->count; i++) {
        if (strcmp(m->keys[i], lc_key) == 0) return (int)i;
    }
    return -1;
}

/* Set key=value. The key is lower-cased on store; value is taken verbatim.
 * Replaces any existing entry with the same lower-cased key.
 * Takes ownership of `key` and `value`: caller must NOT free them again,
 * and on failure they are freed by this function. */
static int qmap_set_take(query_map_t *m, char *key, char *value)
{
    if (!key) { free(value); return -1; }
    /* Lower-case the key in place */
    for (char *p = key; *p; p++) *p = ascii_tolower(*p);
    int idx = qmap_find(m, key);
    if (idx >= 0) {
        free(m->values[idx]);
        m->values[idx] = value ? value : str_dup_c("");
        free(key);
        if (!m->values[idx]) return -1;
        return 0;
    }
    if (qmap_reserve(m, 1) != 0) { free(key); free(value); return -1; }
    m->keys[m->count] = key;
    m->values[m->count] = value ? value : str_dup_c("");
    if (!m->values[m->count]) { free(key); return -1; }
    m->count++;
    return 0;
}

static int qmap_remove_ci(query_map_t *m, const char *key)
{
    if (!m || !key) return -1;
    char *lc = str_dup_c(key);
    if (!lc) return -1;
    for (char *p = lc; *p; p++) *p = ascii_tolower(*p);
    int idx = qmap_find(m, lc);
    free(lc);
    if (idx < 0) return -1;
    free(m->keys[idx]);
    free(m->values[idx]);
    for (size_t i = (size_t)idx + 1; i < m->count; i++) {
        m->keys[i - 1] = m->keys[i];
        m->values[i - 1] = m->values[i];
    }
    m->count--;
    return 0;
}

static const char *qmap_get_ci(const query_map_t *m, const char *key)
{
    if (!m || !key) return NULL;
    /* Build a stack-buffer lower-cased key. */
    char  small[128];
    char *lc = small;
    size_t klen = strlen(key);
    if (klen + 1 > sizeof(small)) {
        lc = (char *)malloc(klen + 1);
        if (!lc) return NULL;
    }
    for (size_t i = 0; i < klen; i++) lc[i] = ascii_tolower(key[i]);
    lc[klen] = '\0';
    int idx = qmap_find(m, lc);
    if (lc != small) free(lc);
    if (idx < 0) return NULL;
    return m->values[idx];
}

/* ─── Path segment cache ────────────────────────────────────────────────── */

typedef struct {
    char  **segments;
    size_t  count;
} path_segs_t;

static void path_segs_free(path_segs_t *p)
{
    if (!p) return;
    for (size_t i = 0; i < p->count; i++) free(p->segments[i]);
    free(p->segments);
    p->segments = NULL;
    p->count = 0;
}

/* Splits the (already percent-decoded) path on '/'. Returns -1 on alloc fail.
 * An empty path yields zero segments. */
static int path_segs_build(path_segs_t *p, const char *decoded_path)
{
    p->segments = NULL;
    p->count = 0;
    if (!decoded_path || !*decoded_path) return 0;
    /* Count '/' separators */
    size_t count = 1;
    for (const char *q = decoded_path; *q; q++) if (*q == '/') count++;
    p->segments = (char **)calloc(count, sizeof(char *));
    if (!p->segments) return -1;
    const char *start = decoded_path;
    size_t i = 0;
    while (1) {
        const char *slash = strchr(start, '/');
        size_t n = slash ? (size_t)(slash - start) : strlen(start);
        p->segments[i] = str_dup_n(start, n);
        if (!p->segments[i]) {
            for (size_t j = 0; j < i; j++) free(p->segments[j]);
            free(p->segments);
            p->segments = NULL;
            return -1;
        }
        i++;
        if (!slash) break;
        start = slash + 1;
    }
    p->count = i;
    return 0;
}

/* ─── Path validation (RAW path — before decoding) ─────────────────────── */

/* Validates each segment using the pct-allowed-character rule and rejects
 * empty segments (which would be "//"). Returns 0 if valid, -1 otherwise,
 * setting *err on failure. */
static int validate_raw_path(const char *path, const char **err)
{
    *err = NULL;
    if (!path || !*path) return 0;
    const char *p = path;
    while (1) {
        const char *slash = strchr(p, '/');
        size_t seg_len = slash ? (size_t)(slash - p) : strlen(p);
        if (seg_len == 0) {
            *err = "Empty path segment (consecutive slashes).";
            return -1;
        }
        for (size_t i = 0; i < seg_len; i++) {
            unsigned char c = (unsigned char)p[i];
            if (is_unreserved(c) || is_sub_delim(c) || c == ':' || c == '@')
                continue;
            if (c == '%') {
                if (i + 2 >= seg_len ||
                    !is_hex_char(p[i + 1]) || !is_hex_char(p[i + 2])) {
                    *err = "Malformed percent-encoding in path segment.";
                    return -1;
                }
                i += 2;
                continue;
            }
            *err = "Illegal character in path segment.";
            return -1;
        }
        if (!slash) break;
        p = slash + 1;
    }
    return 0;
}

/* ─── aethernet_uri_t ───────────────────────────────────────────────────── */

struct aethernet_uri {
    char        *authority;     /* canonical upper-case AetherTag or UHID */
    char        *path;          /* decoded; empty string for root */
    char        *fragment;      /* decoded; empty if absent */
    char        *handler_name;  /* first path segment; empty for root */
    query_map_t  query;
    path_segs_t  path_segs;
};

void aethernet_uri_free(aethernet_uri_t *uri)
{
    if (!uri) return;
    free(uri->authority);
    free(uri->path);
    free(uri->fragment);
    free(uri->handler_name);
    qmap_free(&uri->query);
    path_segs_free(&uri->path_segs);
    free(uri);
}

aethernet_uri_t *aethernet_uri_parse(const char *input,
                                     char       *error_out,
                                     size_t      error_out_size)
{
    if (!input) {
        set_error(error_out, error_out_size, "Input is null.");
        return NULL;
    }
    size_t in_len = strlen(input);
    if (in_len == 0) {
        set_error(error_out, error_out_size, "Input is empty.");
        return NULL;
    }

    /* Scheme is case-insensitive. */
    const size_t prefix_len = strlen(AETHERNET_URI_SCHEME_PREFIX);
    if (in_len < prefix_len || !starts_with_iascii(input, AETHERNET_URI_SCHEME_PREFIX)) {
        set_error(error_out, error_out_size,
                  "Scheme must be '%s'.", AETHERNET_URI_SCHEME_PREFIX);
        return NULL;
    }

    const char *rest = input + prefix_len;
    size_t      rest_len = in_len - prefix_len;

    /* Split on fragment (first '#'). */
    char       *fragment_raw = NULL;
    const char *hashp = memchr(rest, '#', rest_len);
    if (hashp) {
        size_t off = (size_t)(hashp - rest);
        size_t frag_len = rest_len - off - 1;
        fragment_raw = str_dup_n(hashp + 1, frag_len);
        if (!fragment_raw) {
            set_error(error_out, error_out_size, "Out of memory.");
            return NULL;
        }
        rest_len = off;
    }

    /* Split on query (first '?'). */
    char *query_raw = NULL;
    if (rest_len > 0) {
        const char *qp = memchr(rest, '?', rest_len);
        if (qp) {
            size_t off = (size_t)(qp - rest);
            size_t q_len = rest_len - off - 1;
            query_raw = str_dup_n(qp + 1, q_len);
            if (!query_raw) {
                free(fragment_raw);
                set_error(error_out, error_out_size, "Out of memory.");
                return NULL;
            }
            rest_len = off;
        }
    }

    /* Split authority / path on first '/'. */
    const char *slashp = rest_len > 0 ? memchr(rest, '/', rest_len) : NULL;
    char *authority_raw = NULL;
    char *path_raw = NULL;
    if (slashp) {
        size_t off = (size_t)(slashp - rest);
        authority_raw = str_dup_n(rest, off);
        path_raw = str_dup_n(slashp + 1, rest_len - off - 1);
    } else {
        authority_raw = str_dup_n(rest, rest_len);
        path_raw = str_dup_c("");
    }
    if (!authority_raw || !path_raw) {
        free(fragment_raw); free(query_raw);
        free(authority_raw); free(path_raw);
        set_error(error_out, error_out_size, "Out of memory.");
        return NULL;
    }

    if (authority_raw[0] == '\0') {
        free(fragment_raw); free(query_raw);
        free(authority_raw); free(path_raw);
        set_error(error_out, error_out_size, "Authority is missing.");
        return NULL;
    }

    /* Validate path before any decoding. */
    const char *path_err = NULL;
    if (validate_raw_path(path_raw, &path_err) != 0) {
        free(fragment_raw); free(query_raw);
        free(authority_raw); free(path_raw);
        set_error(error_out, error_out_size, "%s", path_err);
        return NULL;
    }

    /* Canonicalise the authority. */
    const char *auth_err = NULL;
    char *authority = canonicalise_authority(authority_raw, &auth_err);
    free(authority_raw);
    if (!authority) {
        free(fragment_raw); free(query_raw); free(path_raw);
        set_error(error_out, error_out_size, "%s",
                  auth_err ? auth_err : "Invalid authority.");
        return NULL;
    }

    /* Parse query parameters. */
    query_map_t qmap;
    qmap_init(&qmap);
    if (query_raw && *query_raw) {
        char  *p = query_raw;
        while (*p) {
            /* Skip empty entries from a trailing & */
            if (*p == '&') { p++; continue; }
            char *amp = strchr(p, '&');
            size_t pair_len = amp ? (size_t)(amp - p) : strlen(p);
            if (pair_len > 0) {
                char *eq = (char *)memchr(p, '=', pair_len);
                char *key_raw, *val_raw;
                if (eq) {
                    key_raw = str_dup_n(p, (size_t)(eq - p));
                    val_raw = str_dup_n(eq + 1, pair_len - (size_t)(eq - p) - 1);
                } else {
                    key_raw = str_dup_n(p, pair_len);
                    val_raw = str_dup_c("");
                }
                if (!key_raw || !val_raw) {
                    free(key_raw); free(val_raw);
                    qmap_free(&qmap);
                    free(query_raw); free(fragment_raw);
                    free(path_raw); free(authority);
                    set_error(error_out, error_out_size, "Out of memory.");
                    return NULL;
                }
                char *key_dec = percent_decode_cstr(key_raw);
                char *val_dec = percent_decode_cstr(val_raw);
                free(key_raw); free(val_raw);
                if (!key_dec || !val_dec) {
                    free(key_dec); free(val_dec);
                    qmap_free(&qmap);
                    free(query_raw); free(fragment_raw);
                    free(path_raw); free(authority);
                    set_error(error_out, error_out_size, "Out of memory.");
                    return NULL;
                }
                if (key_dec[0] == '\0') {
                    free(key_dec); free(val_dec);
                    qmap_free(&qmap);
                    free(query_raw); free(fragment_raw);
                    free(path_raw); free(authority);
                    set_error(error_out, error_out_size, "Empty query parameter key.");
                    return NULL;
                }
                if (qmap_set_take(&qmap, key_dec, val_dec) != 0) {
                    /* qmap_set_take has freed key/value on failure */
                    qmap_free(&qmap);
                    free(query_raw); free(fragment_raw);
                    free(path_raw); free(authority);
                    set_error(error_out, error_out_size, "Out of memory.");
                    return NULL;
                }
            }
            if (!amp) break;
            p = amp + 1;
        }
    }
    free(query_raw);

    /* Decode the path so callers get the natural form. */
    char *path_decoded = percent_decode_path(path_raw);
    free(path_raw);
    if (!path_decoded) {
        qmap_free(&qmap);
        free(fragment_raw); free(authority);
        set_error(error_out, error_out_size, "Out of memory.");
        return NULL;
    }

    /* Decode the fragment. */
    char *fragment_decoded = fragment_raw
        ? percent_decode_cstr(fragment_raw)
        : str_dup_c("");
    free(fragment_raw);
    if (!fragment_decoded) {
        free(path_decoded);
        qmap_free(&qmap);
        free(authority);
        set_error(error_out, error_out_size, "Out of memory.");
        return NULL;
    }

    /* Build path segments cache. */
    path_segs_t segs;
    if (path_segs_build(&segs, path_decoded) != 0) {
        free(path_decoded); free(fragment_decoded);
        qmap_free(&qmap);
        free(authority);
        set_error(error_out, error_out_size, "Out of memory.");
        return NULL;
    }

    /* Handler name = first segment (or ""). */
    char *handler_name;
    if (segs.count > 0) {
        handler_name = str_dup_c(segs.segments[0]);
    } else {
        handler_name = str_dup_c("");
    }
    if (!handler_name) {
        path_segs_free(&segs);
        free(path_decoded); free(fragment_decoded);
        qmap_free(&qmap);
        free(authority);
        set_error(error_out, error_out_size, "Out of memory.");
        return NULL;
    }

    aethernet_uri_t *uri = (aethernet_uri_t *)calloc(1, sizeof(*uri));
    if (!uri) {
        free(handler_name);
        path_segs_free(&segs);
        free(path_decoded); free(fragment_decoded);
        qmap_free(&qmap);
        free(authority);
        set_error(error_out, error_out_size, "Out of memory.");
        return NULL;
    }
    uri->authority = authority;
    uri->path = path_decoded;
    uri->fragment = fragment_decoded;
    uri->handler_name = handler_name;
    uri->query = qmap;        /* struct copy: ownership transferred */
    uri->path_segs = segs;    /* struct copy: ownership transferred */
    return uri;
}

/* ── Getters ────────────────────────────────────────────────────────────── */

const char *aethernet_uri_authority(const aethernet_uri_t *uri)
{
    return uri ? uri->authority : NULL;
}

const char *aethernet_uri_path(const aethernet_uri_t *uri)
{
    return uri ? uri->path : NULL;
}

const char *aethernet_uri_fragment(const aethernet_uri_t *uri)
{
    return uri ? uri->fragment : NULL;
}

const char *aethernet_uri_handler_name(const aethernet_uri_t *uri)
{
    return uri ? uri->handler_name : NULL;
}

size_t aethernet_uri_path_segment_count(const aethernet_uri_t *uri)
{
    return uri ? uri->path_segs.count : 0;
}

const char *aethernet_uri_path_segment(const aethernet_uri_t *uri, size_t index)
{
    if (!uri || index >= uri->path_segs.count) return NULL;
    return uri->path_segs.segments[index];
}

size_t aethernet_uri_query_count(const aethernet_uri_t *uri)
{
    return uri ? uri->query.count : 0;
}

const char *aethernet_uri_query_get(const aethernet_uri_t *uri, const char *key)
{
    if (!uri) return NULL;
    return qmap_get_ci(&uri->query, key);
}

const char *aethernet_uri_query_key_at(const aethernet_uri_t *uri, size_t index)
{
    if (!uri || index >= uri->query.count) return NULL;
    return uri->query.keys[index];
}

const char *aethernet_uri_query_value_at(const aethernet_uri_t *uri, size_t index)
{
    if (!uri || index >= uri->query.count) return NULL;
    return uri->query.values[index];
}

/* ── Canonical serialisation ────────────────────────────────────────────── */

char *aethernet_uri_to_string(const aethernet_uri_t *uri)
{
    if (!uri || !uri->authority || !*uri->authority) return NULL;
    sbuf_t b;
    if (sbuf_init(&b, 64) != 0) return NULL;
    if (sbuf_append(&b, AETHERNET_URI_SCHEME_PREFIX) != 0) goto oom;
    if (sbuf_append(&b, uri->authority) != 0) goto oom;
    if (uri->path && *uri->path) {
        if (sbuf_append_char(&b, '/') != 0) goto oom;
        if (encode_path(&b, uri->path) != 0) goto oom;
    }
    if (uri->query.count > 0) {
        if (sbuf_append_char(&b, '?') != 0) goto oom;
        for (size_t i = 0; i < uri->query.count; i++) {
            if (i > 0) {
                if (sbuf_append_char(&b, '&') != 0) goto oom;
            }
            if (encode_component(&b, uri->query.keys[i], ENC_QUERY_KEY) != 0) goto oom;
            if (uri->query.values[i] && *uri->query.values[i]) {
                if (sbuf_append_char(&b, '=') != 0) goto oom;
                if (encode_component(&b, uri->query.values[i], ENC_QUERY_VALUE) != 0) goto oom;
            }
        }
    }
    if (uri->fragment && *uri->fragment) {
        if (sbuf_append_char(&b, '#') != 0) goto oom;
        if (encode_component(&b, uri->fragment, ENC_FRAGMENT) != 0) goto oom;
    }
    return sbuf_steal(&b);
oom:
    sbuf_free(&b);
    return NULL;
}

void aethernet_uri_free_string(char *s)
{
    free(s);
}

/* ── Equality ───────────────────────────────────────────────────────────── */

bool aethernet_uri_equals(const aethernet_uri_t *a, const aethernet_uri_t *b)
{
    if (a == b) return true;
    if (!a || !b) return false;
    if (strcmp(a->authority, b->authority) != 0) return false;
    if (strcmp(a->path, b->path) != 0) return false;
    if (strcmp(a->fragment, b->fragment) != 0) return false;
    if (a->query.count != b->query.count) return false;
    /* Order-insensitive: every key in a must appear in b with the same value. */
    for (size_t i = 0; i < a->query.count; i++) {
        int idx = qmap_find(&b->query, a->query.keys[i]);
        if (idx < 0) return false;
        if (strcmp(a->query.values[i], b->query.values[idx]) != 0) return false;
    }
    return true;
}

/* ─── Builder ───────────────────────────────────────────────────────────── */

struct aethernet_uri_builder {
    char        *authority;
    char        *path;
    char        *fragment;
    query_map_t  query;
};

aethernet_uri_builder_t *aethernet_uri_builder_new(void)
{
    aethernet_uri_builder_t *b = (aethernet_uri_builder_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->path = str_dup_c("");
    b->fragment = str_dup_c("");
    if (!b->path || !b->fragment) {
        aethernet_uri_builder_free(b);
        return NULL;
    }
    qmap_init(&b->query);
    return b;
}

void aethernet_uri_builder_free(aethernet_uri_builder_t *b)
{
    if (!b) return;
    free(b->authority);
    free(b->path);
    free(b->fragment);
    qmap_free(&b->query);
    free(b);
}

void aethernet_uri_builder_authority(aethernet_uri_builder_t *b, const char *authority)
{
    if (!b) return;
    free(b->authority);
    b->authority = authority ? str_dup_c(authority) : NULL;
}

static char *trim_leading(const char *s, char ch)
{
    if (!s) return str_dup_c("");
    while (*s == ch) s++;
    return str_dup_c(s);
}

void aethernet_uri_builder_path(aethernet_uri_builder_t *b, const char *path)
{
    if (!b) return;
    free(b->path);
    b->path = trim_leading(path, '/');
    if (!b->path) b->path = str_dup_c("");
}

void aethernet_uri_builder_append_segment(aethernet_uri_builder_t *b, const char *segment)
{
    if (!b || !segment || !*segment) return;
    /* Strip leading slashes from the segment. */
    while (*segment == '/') segment++;
    if (!*segment) return;
    if (!b->path || !*b->path) {
        free(b->path);
        b->path = str_dup_c(segment);
        return;
    }
    size_t cur_len = strlen(b->path);
    size_t seg_len = strlen(segment);
    char *combined = (char *)malloc(cur_len + 1 + seg_len + 1);
    if (!combined) return;
    memcpy(combined, b->path, cur_len);
    combined[cur_len] = '/';
    memcpy(combined + cur_len + 1, segment, seg_len);
    combined[cur_len + 1 + seg_len] = '\0';
    free(b->path);
    b->path = combined;
}

void aethernet_uri_builder_query(aethernet_uri_builder_t *b,
                                 const char *key,
                                 const char *value)
{
    if (!b || !key || !*key) return;
    char *k = str_dup_c(key);
    char *v = value ? str_dup_c(value) : str_dup_c("");
    if (!k || !v) { free(k); free(v); return; }
    /* Ignore failure — leaves builder unchanged. */
    qmap_set_take(&b->query, k, v);
}

void aethernet_uri_builder_remove_query(aethernet_uri_builder_t *b, const char *key)
{
    if (!b || !key) return;
    qmap_remove_ci(&b->query, key);
}

void aethernet_uri_builder_fragment(aethernet_uri_builder_t *b, const char *fragment)
{
    if (!b) return;
    free(b->fragment);
    b->fragment = trim_leading(fragment, '#');
    if (!b->fragment) b->fragment = str_dup_c("");
}

/* Render the current builder state as a string with NO percent-encoding
 * applied — the values are inserted verbatim. The string is then re-parsed
 * by aethernet_uri_parse() which validates + canonicalises. This mirrors the
 * C# AetherUriBuilder.Build() approach. */
static char *builder_render_raw(const aethernet_uri_builder_t *b)
{
    if (!b || !b->authority || !*b->authority) return NULL;
    sbuf_t s;
    if (sbuf_init(&s, 64) != 0) return NULL;
    if (sbuf_append(&s, AETHERNET_URI_SCHEME_PREFIX) != 0) goto oom;
    if (sbuf_append(&s, b->authority) != 0) goto oom;
    if (b->path && *b->path) {
        if (sbuf_append_char(&s, '/') != 0) goto oom;
        /* Encode path so spaces and other chars survive the round-trip. */
        if (encode_path(&s, b->path) != 0) goto oom;
    }
    if (b->query.count > 0) {
        if (sbuf_append_char(&s, '?') != 0) goto oom;
        for (size_t i = 0; i < b->query.count; i++) {
            if (i > 0) { if (sbuf_append_char(&s, '&') != 0) goto oom; }
            if (encode_component(&s, b->query.keys[i], ENC_QUERY_KEY) != 0) goto oom;
            if (b->query.values[i] && *b->query.values[i]) {
                if (sbuf_append_char(&s, '=') != 0) goto oom;
                if (encode_component(&s, b->query.values[i], ENC_QUERY_VALUE) != 0) goto oom;
            }
        }
    }
    if (b->fragment && *b->fragment) {
        if (sbuf_append_char(&s, '#') != 0) goto oom;
        if (encode_component(&s, b->fragment, ENC_FRAGMENT) != 0) goto oom;
    }
    return sbuf_steal(&s);
oom:
    sbuf_free(&s);
    return NULL;
}

aethernet_uri_t *aethernet_uri_builder_build(aethernet_uri_builder_t *b,
                                             char  *error_out,
                                             size_t error_out_size)
{
    if (!b) {
        set_error(error_out, error_out_size, "Builder is null.");
        return NULL;
    }
    if (!b->authority || !*b->authority) {
        set_error(error_out, error_out_size, "Authority is required.");
        return NULL;
    }
    char *rendered = builder_render_raw(b);
    if (!rendered) {
        set_error(error_out, error_out_size, "Out of memory.");
        return NULL;
    }
    aethernet_uri_t *uri = aethernet_uri_parse(rendered, error_out, error_out_size);
    free(rendered);
    return uri;
}

/* ─── Handler descriptor ────────────────────────────────────────────────── */

/* Per-segment record for a descriptor's compiled template.
 *
 * Literal segment:  is_capture = 0, literal = "watch", capture_name = NULL.
 * Capture segment:  is_capture = 1, literal = NULL,    capture_name = "hash".
 *                                                     (no surrounding braces)
 */
typedef struct {
    int   is_capture;
    char *literal;        /* owned when is_capture == 0 */
    char *capture_name;   /* owned when is_capture == 1 — stripped of braces */
} template_seg_t;

struct aethernet_uri_handler_descriptor {
    char *name;
    char *path_template;
    char *description;
    /* Cached split of (name "/" template) into segments for matching. */
    template_seg_t *template_segs;
    size_t          template_seg_count;
};

static void template_segs_free(template_seg_t *segs, size_t count)
{
    if (!segs) return;
    for (size_t i = 0; i < count; i++) {
        free(segs[i].literal);
        free(segs[i].capture_name);
    }
    free(segs);
}

static int split_template(const char *name, const char *path_template,
                          template_seg_t **segs_out, size_t *count_out)
{
    /* The template's effective form is: name + ("/" + path_template if any). */
    sbuf_t joined;
    if (sbuf_init(&joined, 32) != 0) return -1;
    if (sbuf_append(&joined, name) != 0) { sbuf_free(&joined); return -1; }
    if (path_template && *path_template) {
        const char *p = path_template;
        while (*p == '/') p++;
        if (*p) {
            if (sbuf_append_char(&joined, '/') != 0) { sbuf_free(&joined); return -1; }
            if (sbuf_append(&joined, p) != 0) { sbuf_free(&joined); return -1; }
        }
    }
    /* Count slashes. */
    size_t count = 1;
    for (const char *q = joined.data; *q; q++) if (*q == '/') count++;
    template_seg_t *segs = (template_seg_t *)calloc(count, sizeof(template_seg_t));
    if (!segs) { sbuf_free(&joined); return -1; }
    const char *start = joined.data;
    size_t i = 0;
    while (1) {
        const char *slash = strchr(start, '/');
        size_t n = slash ? (size_t)(slash - start) : strlen(start);
        /* Detect {captureName} form. */
        if (n >= 2 && start[0] == '{' && start[n - 1] == '}') {
            segs[i].is_capture = 1;
            segs[i].capture_name = str_dup_n(start + 1, n - 2);
            if (!segs[i].capture_name) {
                template_segs_free(segs, i);
                sbuf_free(&joined);
                return -1;
            }
        } else {
            segs[i].is_capture = 0;
            segs[i].literal = str_dup_n(start, n);
            if (!segs[i].literal) {
                template_segs_free(segs, i);
                sbuf_free(&joined);
                return -1;
            }
        }
        i++;
        if (!slash) break;
        start = slash + 1;
    }
    sbuf_free(&joined);
    *segs_out = segs;
    *count_out = i;
    return 0;
}

aethernet_uri_handler_descriptor_t *aethernet_uri_handler_descriptor_new(
    const char *name, const char *path_template, const char *description)
{
    if (!name || !*name) return NULL;
    aethernet_uri_handler_descriptor_t *d =
        (aethernet_uri_handler_descriptor_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->name = str_dup_c(name);
    d->path_template = str_dup_c(path_template ? path_template : "");
    d->description = str_dup_c(description ? description : "");
    if (!d->name || !d->path_template || !d->description) {
        aethernet_uri_handler_descriptor_free(d);
        return NULL;
    }
    if (split_template(d->name, d->path_template,
                       &d->template_segs, &d->template_seg_count) != 0) {
        aethernet_uri_handler_descriptor_free(d);
        return NULL;
    }
    return d;
}

void aethernet_uri_handler_descriptor_free(aethernet_uri_handler_descriptor_t *d)
{
    if (!d) return;
    free(d->name);
    free(d->path_template);
    free(d->description);
    template_segs_free(d->template_segs, d->template_seg_count);
    free(d);
}

const char *aethernet_uri_handler_descriptor_name(const aethernet_uri_handler_descriptor_t *d)
{
    return d ? d->name : NULL;
}

const char *aethernet_uri_handler_descriptor_template(const aethernet_uri_handler_descriptor_t *d)
{
    return d ? d->path_template : NULL;
}

const char *aethernet_uri_handler_descriptor_description(const aethernet_uri_handler_descriptor_t *d)
{
    return d ? d->description : NULL;
}

/* ─── Handler manifest ──────────────────────────────────────────────────── */

struct aethernet_uri_handler_manifest {
    char                                *app_id;
    aethernet_uri_handler_descriptor_t **handlers;
    size_t                               count;
    size_t                               cap;
};

aethernet_uri_handler_manifest_t *aethernet_uri_handler_manifest_new(const char *app_id)
{
    if (!app_id || !*app_id) return NULL;
    /* Whitespace-only is rejected too. */
    int has_non_ws = 0;
    for (const char *p = app_id; *p; p++) {
        if (!isspace((unsigned char)*p)) { has_non_ws = 1; break; }
    }
    if (!has_non_ws) return NULL;
    aethernet_uri_handler_manifest_t *m =
        (aethernet_uri_handler_manifest_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->app_id = str_dup_c(app_id);
    if (!m->app_id) {
        free(m);
        return NULL;
    }
    return m;
}

int aethernet_uri_handler_manifest_add(aethernet_uri_handler_manifest_t   *m,
                                       aethernet_uri_handler_descriptor_t *d)
{
    if (!m || !d) return -1;
    if (m->count == m->cap) {
        size_t newcap = m->cap ? m->cap * 2 : 4;
        aethernet_uri_handler_descriptor_t **nh =
            (aethernet_uri_handler_descriptor_t **)realloc(
                m->handlers, newcap * sizeof(*nh));
        if (!nh) return -1;
        m->handlers = nh;
        m->cap = newcap;
    }
    m->handlers[m->count++] = d;
    return 0;
}

void aethernet_uri_handler_manifest_free(aethernet_uri_handler_manifest_t *m)
{
    if (!m) return;
    free(m->app_id);
    if (m->handlers) {
        for (size_t i = 0; i < m->count; i++) {
            aethernet_uri_handler_descriptor_free(m->handlers[i]);
        }
        free(m->handlers);
    }
    free(m);
}

const char *aethernet_uri_handler_manifest_app_id(const aethernet_uri_handler_manifest_t *m)
{
    return m ? m->app_id : NULL;
}

size_t aethernet_uri_handler_manifest_count(const aethernet_uri_handler_manifest_t *m)
{
    return m ? m->count : 0;
}

const aethernet_uri_handler_descriptor_t *aethernet_uri_handler_manifest_at(
    const aethernet_uri_handler_manifest_t *m, size_t index)
{
    if (!m || index >= m->count) return NULL;
    return m->handlers[index];
}

/* Returns 1 if the path matches the descriptor's template; populates the
 * captures arrays (length capture_cap). On a non-match returns 0 — captures
 * may still have been written but the caller MUST ignore them. */
static int descriptor_match(const aethernet_uri_handler_descriptor_t *d,
                            const aethernet_uri_t                    *uri,
                            const char **cap_keys_out,
                            const char **cap_values_out,
                            size_t       cap_cap,
                            size_t      *cap_count_out)
{
    if (cap_count_out) *cap_count_out = 0;
    size_t tcount = d->template_seg_count;
    size_t pcount = uri->path_segs.count;
    if (tcount != pcount) return 0;
    size_t written = 0;
    for (size_t i = 0; i < tcount; i++) {
        const template_seg_t *ts = &d->template_segs[i];
        const char           *p  = uri->path_segs.segments[i];
        if (ts->is_capture) {
            if (cap_keys_out && cap_values_out && written < cap_cap) {
                cap_keys_out[written]   = ts->capture_name;
                cap_values_out[written] = p;
            }
            written++;
        } else {
            if (strcmp(ts->literal, p) != 0) return 0;
        }
    }
    if (cap_count_out) *cap_count_out = written;
    return 1;
}

int aethernet_uri_handler_manifest_resolve(
    const aethernet_uri_handler_manifest_t *m,
    const aethernet_uri_t                  *uri,
    const char **capture_keys_out,
    const char **capture_values_out,
    size_t       capture_cap,
    size_t      *captures_out_count)
{
    if (captures_out_count) *captures_out_count = 0;
    if (!m || !uri) return -1;
    if (!uri->authority || !*uri->authority) return -1;
    const char *handler_name = uri->handler_name ? uri->handler_name : "";
    for (size_t i = 0; i < m->count; i++) {
        const aethernet_uri_handler_descriptor_t *d = m->handlers[i];
        if (strcmp(d->name, handler_name) != 0) continue;
        size_t cap_count = 0;
        if (descriptor_match(d, uri,
                             capture_keys_out, capture_values_out,
                             capture_cap, &cap_count)) {
            if (captures_out_count) *captures_out_count = cap_count;
            return (int)i;
        }
    }
    return -1;
}

/* ─── Router ────────────────────────────────────────────────────────────── */

typedef struct {
    int                            handler_index;
    aethernet_uri_handler_callback cb;
    void                          *user_data;
    int                            in_use;
} router_entry_t;

struct aethernet_uri_router {
    const aethernet_uri_handler_manifest_t *manifest;
    router_entry_t                         *entries;
    size_t                                  count;     /* same as manifest count */
    pthread_mutex_t                         lock;
};

aethernet_uri_router_t *aethernet_uri_router_new(
    const aethernet_uri_handler_manifest_t *manifest)
{
    if (!manifest) return NULL;
    aethernet_uri_router_t *r = (aethernet_uri_router_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->manifest = manifest;
    r->count = manifest->count;
    if (r->count > 0) {
        r->entries = (router_entry_t *)calloc(r->count, sizeof(router_entry_t));
        if (!r->entries) { free(r); return NULL; }
    }
    if (pthread_mutex_init(&r->lock, NULL) != 0) {
        free(r->entries);
        free(r);
        return NULL;
    }
    return r;
}

void aethernet_uri_router_free(aethernet_uri_router_t *r)
{
    if (!r) return;
    pthread_mutex_destroy(&r->lock);
    free(r->entries);
    free(r);
}

int aethernet_uri_router_register(aethernet_uri_router_t        *r,
                                  int                            handler_index,
                                  aethernet_uri_handler_callback cb,
                                  void                          *user_data)
{
    if (!r || !cb) return -1;
    if (handler_index < 0 || (size_t)handler_index >= r->count) return -1;
    pthread_mutex_lock(&r->lock);
    r->entries[handler_index].handler_index = handler_index;
    r->entries[handler_index].cb = cb;
    r->entries[handler_index].user_data = user_data;
    r->entries[handler_index].in_use = 1;
    pthread_mutex_unlock(&r->lock);
    return 0;
}

#ifndef AETHERNET_URI_ROUTER_MAX_CAPTURES
#define AETHERNET_URI_ROUTER_MAX_CAPTURES 16
#endif

int aethernet_uri_router_dispatch(aethernet_uri_router_t *r,
                                  const aethernet_uri_t  *uri)
{
    if (!r || !uri) return -2;
    const char *cap_keys[AETHERNET_URI_ROUTER_MAX_CAPTURES];
    const char *cap_values[AETHERNET_URI_ROUTER_MAX_CAPTURES];
    size_t cap_count = 0;
    int idx = aethernet_uri_handler_manifest_resolve(
        r->manifest, uri,
        cap_keys, cap_values,
        AETHERNET_URI_ROUTER_MAX_CAPTURES, &cap_count);
    if (idx < 0) return -1;
    aethernet_uri_handler_callback cb = NULL;
    void *user_data = NULL;
    pthread_mutex_lock(&r->lock);
    if (r->entries[idx].in_use) {
        cb = r->entries[idx].cb;
        user_data = r->entries[idx].user_data;
    }
    pthread_mutex_unlock(&r->lock);
    if (!cb) return -1;
    const aethernet_uri_handler_descriptor_t *d = r->manifest->handlers[idx];
    return cb(uri, d, cap_keys, cap_values, cap_count, user_data);
}

int aethernet_uri_router_dispatch_string(aethernet_uri_router_t *r,
                                         const char             *uri_str,
                                         char                   *error_out,
                                         size_t                  error_out_size)
{
    if (!r) {
        set_error(error_out, error_out_size, "Router is null.");
        return -2;
    }
    aethernet_uri_t *uri = aethernet_uri_parse(uri_str, error_out, error_out_size);
    if (!uri) return -3;
    int rc = aethernet_uri_router_dispatch(r, uri);
    aethernet_uri_free(uri);
    return rc;
}
