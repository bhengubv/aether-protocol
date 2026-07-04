// SPDX-License-Identifier: MIT
// Transport-backed WebRTC signalling carrier — see webrtc_signaling.h.
//
// Frames each SDP/ICE signal as AWS1 (4-byte magic) + compact JSON and carries
// it over an injected aethernet_transport_t, so two SEPARATE nodes can exchange
// the WebRTC handshake over a real transport (the relay, the serverless
// circuit-relay fallback, or the in-process transport) without a signalling
// server. The C mirror of C# RelayWebRtcSignaling.
//
// The JSON body is byte-identical to the C# WebRtcSignal serialization:
// PascalCase keys, numeric Type enum, SdpMLineIndex always present, and the
// null-valued Sdp/Candidate/SdpMid string fields omitted.
//
// The SERIALIZE / frame-encode path is a hand-built encoder (NOT cJSON) so the
// string escaping matches System.Text.Json's default JavaScriptEncoder EXACTLY —
// STJ escapes `+ < > & ' ` ` (backtick) and every non-ASCII code point as
// UPPERCASE \uXXXX, which cJSON does not. This is the C port of the TypeScript
// reference transport/webrtc/RelayWebRtcSignaling.ts (serializeSignalBody +
// stjString + STJ_ESCAPE_ASCII). Real SDP fingerprints carry base64 `+`, so this
// escaping difference is load-bearing for cross-language byte-identity.
//
// The DECODE / parse path still reuses cJSON — the same JSON helper the DTN /
// directory-service wire code vendors — via cJSON_ParseWithLength, established
// in c/src/directory_service.c.

#include "aethernet/webrtc_signaling.h"

#include <cjson/cJSON.h>

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ───────────────────────── framing: magic ─────────────────────────────────*/

static const uint8_t AWS_MAGIC[AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN] = {
    (uint8_t)AETHERNET_WEBRTC_SIGNAL_MAGIC_0,
    (uint8_t)AETHERNET_WEBRTC_SIGNAL_MAGIC_1,
    (uint8_t)AETHERNET_WEBRTC_SIGNAL_MAGIC_2,
    (uint8_t)AETHERNET_WEBRTC_SIGNAL_MAGIC_3,
};

bool aethernet_webrtc_signal_frame_has_magic(const uint8_t *data, size_t data_len) {
    return data != NULL &&
           data_len >= AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN &&
           memcmp(data, AWS_MAGIC, AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN) == 0;
}

/* ───────────────────────── framing: encode (hand-built, STJ-exact) ─────────
 *
 * The body is built by hand — NOT via cJSON — so key order, always-present
 * numeric fields, null-omission, AND string escaping all match System.Text.Json's
 * default JavaScriptEncoder.Default output. This is the C port of the TypeScript
 * reference (serializeSignalBody + stjString + STJ_ESCAPE_ASCII).
 */

/* A minimal growable byte buffer. On any allocation failure it latches `failed`;
 * callers check it once at the end rather than after every append. */
typedef struct {
    char  *data;
    size_t len;
    size_t cap;
    bool   failed;
} json_buf_t;

static void jb_init(json_buf_t *b) {
    b->cap    = 256;
    b->len    = 0;
    b->failed = false;
    b->data   = (char *)malloc(b->cap);
    if (!b->data) b->failed = true;
}

static void jb_ensure(json_buf_t *b, size_t extra) {
    if (b->failed) return;
    size_t need = b->len + extra;
    if (need <= b->cap) return;
    size_t new_cap = b->cap;
    while (new_cap < need) new_cap *= 2;
    char *p = (char *)realloc(b->data, new_cap);
    if (!p) { b->failed = true; return; }
    b->data = p;
    b->cap  = new_cap;
}

static void jb_putc(json_buf_t *b, char c) {
    jb_ensure(b, 1);
    if (b->failed) return;
    b->data[b->len++] = c;
}

/* Append a NUL-terminated literal (raw JSON: keys, punctuation, numbers). */
static void jb_puts(json_buf_t *b, const char *s) {
    size_t n = strlen(s);
    jb_ensure(b, n);
    if (b->failed) return;
    memcpy(b->data + b->len, s, n);
    b->len += n;
}

/* True for the ASCII code points (0x20–0x7E) STJ's default encoder escapes as
 * \uXXXX even though plain JSON would not: `" & ' + < > ` ` (backtick).
 * Mirrors STJ_ESCAPE_ASCII in the TypeScript reference. */
static bool stj_escape_ascii(uint32_t cp) {
    switch (cp) {
        case 0x22: /* " */
        case 0x26: /* & */
        case 0x27: /* ' */
        case 0x2B: /* + */
        case 0x3C: /* < */
        case 0x3E: /* > */
        case 0x60: /* ` (backtick) */
            return true;
        default:
            return false;
    }
}

/* Emit one UTF-16 code unit as an UPPERCASE \uHHHH escape. */
static void jb_put_u_escape(json_buf_t *b, uint32_t unit) {
    static const char HEX[] = "0123456789ABCDEF";
    char esc[6];
    esc[0] = '\\';
    esc[1] = 'u';
    esc[2] = HEX[(unit >> 12) & 0xF];
    esc[3] = HEX[(unit >> 8)  & 0xF];
    esc[4] = HEX[(unit >> 4)  & 0xF];
    esc[5] = HEX[unit         & 0xF];
    jb_ensure(b, 6);
    if (b->failed) return;
    memcpy(b->data + b->len, esc, 6);
    b->len += 6;
}

/* Decode ONE UTF-8 code point starting at s[i]; advance *i past it. On a
 * malformed/truncated sequence, consume a single byte and surface it as U+FFFD
 * (STJ's default encoder likewise never emits a raw invalid byte). The C signal
 * fields are already valid UTF-8 in practice; this just keeps the encoder total. */
static uint32_t utf8_next(const char *s, size_t len, size_t *i) {
    unsigned char c0 = (unsigned char)s[*i];
    if (c0 < 0x80) { (*i)++; return c0; }

    int extra;
    uint32_t cp;
    if      ((c0 & 0xE0) == 0xC0) { extra = 1; cp = c0 & 0x1F; }
    else if ((c0 & 0xF0) == 0xE0) { extra = 2; cp = c0 & 0x0F; }
    else if ((c0 & 0xF8) == 0xF0) { extra = 3; cp = c0 & 0x07; }
    else { (*i)++; return 0xFFFD; }  /* stray continuation / invalid lead */

    if (*i + (size_t)extra >= len) { (*i)++; return 0xFFFD; }  /* truncated */

    for (int k = 1; k <= extra; k++) {
        unsigned char cc = (unsigned char)s[*i + (size_t)k];
        if ((cc & 0xC0) != 0x80) { (*i)++; return 0xFFFD; }    /* bad continuation */
        cp = (cp << 6) | (cc & 0x3F);
    }
    *i += (size_t)(extra + 1);
    return cp;
}

/* Encode `s` as a JSON string literal (WITH surrounding quotes) exactly as
 * System.Text.Json's default JavaScriptEncoder.Default does. The C port of
 * stjString: decode each UTF-8 code point, then emit per UTF-16 code unit —
 * short escapes for \b \t \n \f \r \\, literal for the safe ASCII range, and
 * \uXXXX (UPPERCASE) for everything else, astral code points as a surrogate
 * pair of two \uXXXX. `/` and `=` stay literal. */
static void jb_put_stj_string(json_buf_t *b, const char *s) {
    jb_putc(b, '"');
    size_t len = strlen(s);
    size_t i = 0;
    while (i < len) {
        uint32_t cp = utf8_next(s, len, &i);
        switch (cp) {
            case 0x08: jb_puts(b, "\\b"); continue;
            case 0x09: jb_puts(b, "\\t"); continue;
            case 0x0A: jb_puts(b, "\\n"); continue;
            case 0x0C: jb_puts(b, "\\f"); continue;
            case 0x0D: jb_puts(b, "\\r"); continue;
            case 0x5C: jb_puts(b, "\\\\"); continue;  /* backslash */
            default: break;
        }
        if (cp >= 0x20 && cp <= 0x7E && !stj_escape_ascii(cp)) {
            jb_putc(b, (char)cp);                     /* safe literal ASCII byte */
        } else if (cp <= 0xFFFF) {
            jb_put_u_escape(b, cp);                   /* BMP → single \uXXXX     */
        } else {
            /* Astral → UTF-16 surrogate pair, two \uXXXX. */
            uint32_t v = cp - 0x10000;
            uint32_t hi = 0xD800 + (v >> 10);
            uint32_t lo = 0xDC00 + (v & 0x3FF);
            jb_put_u_escape(b, hi);
            jb_put_u_escape(b, lo);
        }
    }
    jb_putc(b, '"');
}

/* Append `"Key":` (the key literal plus its colon), reusing the STJ escaper for
 * the key — the field names here are pure ASCII, but this keeps the emission
 * uniform with the reference. */
static void jb_put_key(json_buf_t *b, const char *key) {
    jb_put_stj_string(b, key);
    jb_putc(b, ':');
}

uint8_t *aethernet_webrtc_signal_frame_encode(const aethernet_webrtc_signal_t *signal,
                                              size_t *out_len) {
    if (!signal) return NULL;

    /* Hand-build the body in C# System.Text.Json property-declaration order,
     * matching serializeSignalBody() in the TypeScript reference exactly:
     *   FromUhid, ToUhid, Type, [Sdp], [Candidate], SdpMLineIndex, [SdpMid]. */
    json_buf_t b;
    jb_init(&b);

    jb_putc(&b, '{');

    /* FromUhid / ToUhid: required strings, always written. */
    jb_put_key(&b, "FromUhid");
    jb_put_stj_string(&b, signal->from_uhid);
    jb_putc(&b, ',');
    jb_put_key(&b, "ToUhid");
    jb_put_stj_string(&b, signal->to_uhid);
    jb_putc(&b, ',');

    /* Type: numeric enum value (0/1/2), always written. */
    jb_put_key(&b, "Type");
    {
        char numbuf[16];
        snprintf(numbuf, sizeof(numbuf), "%d", (int)signal->type);
        jb_puts(&b, numbuf);
    }

    /* Sdp: present only for OFFER/ANSWER — omit when empty, mirroring C#'s
     * DefaultIgnoreCondition = WhenWritingNull (a null Sdp is dropped). */
    if (signal->sdp[0] != '\0') {
        jb_putc(&b, ',');
        jb_put_key(&b, "Sdp");
        jb_put_stj_string(&b, signal->sdp);
    }

    /* Candidate: present only for CANDIDATE — omit when empty. */
    if (signal->candidate[0] != '\0') {
        jb_putc(&b, ',');
        jb_put_key(&b, "Candidate");
        jb_put_stj_string(&b, signal->candidate);
    }

    /* SdpMLineIndex: non-nullable ushort in C#, so ALWAYS emitted. Emit the
     * signal's real m-line index as a decimal integer — byte-identical to the
     * other languages (C#/Go/Rust/Kotlin/Swift/TS/Python), which all emit the
     * true index. A memset(0,...) default of 0 reproduces the canonical single
     * data m-section case for callers that never set it. */
    jb_putc(&b, ',');
    jb_put_key(&b, "SdpMLineIndex");
    {
        char numbuf[16];
        snprintf(numbuf, sizeof(numbuf), "%d", (int)signal->sdp_mline_index);
        jb_puts(&b, numbuf);
    }

    /* SdpMid: present only for CANDIDATE — omit when empty. */
    if (signal->sdp_mid[0] != '\0') {
        jb_putc(&b, ',');
        jb_put_key(&b, "SdpMid");
        jb_put_stj_string(&b, signal->sdp_mid);
    }

    jb_putc(&b, '}');

    if (b.failed) {
        free(b.data);
        return NULL;
    }

    size_t body_len  = b.len;
    size_t frame_len = (size_t)AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN + body_len;
    uint8_t *frame = (uint8_t *)malloc(frame_len);
    if (!frame) {
        free(b.data);
        return NULL;
    }
    memcpy(frame, AWS_MAGIC, AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN);
    memcpy(frame + AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN, b.data, body_len);
    free(b.data);

    if (out_len) *out_len = frame_len;
    return frame;
}

/* ───────────────────────── framing: decode ────────────────────────────────*/

static void copy_str_field(char *dst, size_t dst_cap, const cJSON *item) {
    if (cJSON_IsString(item) && item->valuestring) {
        strncpy(dst, item->valuestring, dst_cap - 1);
        dst[dst_cap - 1] = '\0';
    }
}

bool aethernet_webrtc_signal_frame_decode(const uint8_t *data, size_t data_len,
                                          aethernet_webrtc_signal_t *out_signal) {
    if (!out_signal) return false;
    memset(out_signal, 0, sizeof(*out_signal));

    if (!aethernet_webrtc_signal_frame_has_magic(data, data_len))
        return false;  /* ordinary app traffic, not a signalling frame */

    const char *body = (const char *)(data + AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN);
    size_t body_len = data_len - (size_t)AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN;

    cJSON *obj = cJSON_ParseWithLength(body, body_len);
    if (!obj) return false;                 /* malformed JSON after the magic */
    if (!cJSON_IsObject(obj)) { cJSON_Delete(obj); return false; }

    copy_str_field(out_signal->from_uhid, sizeof(out_signal->from_uhid),
                   cJSON_GetObjectItemCaseSensitive(obj, "FromUhid"));
    copy_str_field(out_signal->to_uhid, sizeof(out_signal->to_uhid),
                   cJSON_GetObjectItemCaseSensitive(obj, "ToUhid"));

    const cJSON *jtype = cJSON_GetObjectItemCaseSensitive(obj, "Type");
    if (cJSON_IsNumber(jtype))
        out_signal->type = (aethernet_webrtc_signal_type_t)jtype->valueint;

    copy_str_field(out_signal->sdp, sizeof(out_signal->sdp),
                   cJSON_GetObjectItemCaseSensitive(obj, "Sdp"));
    copy_str_field(out_signal->candidate, sizeof(out_signal->candidate),
                   cJSON_GetObjectItemCaseSensitive(obj, "Candidate"));
    copy_str_field(out_signal->sdp_mid, sizeof(out_signal->sdp_mid),
                   cJSON_GetObjectItemCaseSensitive(obj, "SdpMid"));
    /* SdpMLineIndex: populate the parsed index so a C node relaying a signal
     * preserves the origin's m-line index (wire fidelity), even though
     * libdatachannel keys remote candidates by mid. Absent/non-numeric leaves
     * the memset default of 0. */
    const cJSON *jmline = cJSON_GetObjectItemCaseSensitive(obj, "SdpMLineIndex");
    if (cJSON_IsNumber(jmline))
        out_signal->sdp_mline_index = (int32_t)jmline->valueint;

    cJSON_Delete(obj);
    return true;
}

/* ───────────────────────── carrier state ──────────────────────────────────*/

struct aethernet_webrtc_signaling_carrier {
    aethernet_transport_t        *channel;  /* borrowed; not owned */
    aethernet_webrtc_signaling_t  iface;    /* handed to the WebRTC transport   */

    aethernet_webrtc_signal_handler handler;
    void                           *handler_user;
};

/* ───────────────────────── transport data → signal ────────────────────────
 *
 * Registered as the transport's on-data-received callback. Ignores any bytes
 * that don't start with the AWS1 magic (ordinary app traffic), else decodes the
 * JSON and dispatches to the WebRTC transport's registered handler. Matches
 * C# RelayWebRtcSignaling.OnChannelData.
 */
static void carrier_on_channel_data(const char *sender_uhid,
                                    const uint8_t *data,
                                    size_t data_len,
                                    void *user_data) {
    (void)sender_uhid;  /* the addressing lives in the JSON body, not the wire */
    aethernet_webrtc_signaling_carrier_t *c =
        (aethernet_webrtc_signaling_carrier_t *)user_data;
    if (!c) return;

    if (!aethernet_webrtc_signal_frame_has_magic(data, data_len))
        return;  /* not a signalling frame — leave app traffic alone */

    aethernet_webrtc_signal_t sig;
    if (!aethernet_webrtc_signal_frame_decode(data, data_len, &sig))
        return;  /* malformed signalling frame — discard (best-effort) */

    if (c->handler)
        c->handler(&sig, c->handler_user);
}

/* ───────────────────────── signalling vtable ──────────────────────────────*/

/* send(): frame the signal as AWS1 + JSON and push it onto the transport,
 * addressed to signal->to_uhid — exactly C# RelayWebRtcSignaling.SendAsync. */
static bool carrier_iface_send(void *handle, const aethernet_webrtc_signal_t *signal) {
    aethernet_webrtc_signaling_carrier_t *c =
        (aethernet_webrtc_signaling_carrier_t *)handle;
    if (!c || !signal) return false;

    size_t frame_len = 0;
    uint8_t *frame = aethernet_webrtc_signal_frame_encode(signal, &frame_len);
    if (!frame) return false;

    bool ok = aethernet_transport_send(c->channel, signal->to_uhid, frame, frame_len);
    free(frame);
    return ok;
}

static void carrier_iface_set_handler(void *handle,
                                      aethernet_webrtc_signal_handler handler,
                                      void *user_data) {
    aethernet_webrtc_signaling_carrier_t *c =
        (aethernet_webrtc_signaling_carrier_t *)handle;
    if (!c) return;
    c->handler      = handler;
    c->handler_user = user_data;
}

/* ───────────────────────── lifecycle ──────────────────────────────────────*/

aethernet_webrtc_signaling_carrier_t *
aethernet_webrtc_signaling_carrier_new(aethernet_transport_t *channel) {
    if (!channel) return NULL;

    aethernet_webrtc_signaling_carrier_t *c =
        (aethernet_webrtc_signaling_carrier_t *)malloc(sizeof(*c));
    if (!c) return NULL;
    memset(c, 0, sizeof(*c));

    c->channel            = channel;
    c->iface.handle       = c;
    c->iface.send         = carrier_iface_send;
    c->iface.set_handler  = carrier_iface_set_handler;

    /* Claim the transport's data path for signalling. Inbound frames without the
     * AWS1 magic are ignored, so ordinary app traffic on this transport is left
     * untouched — but dedicate the transport to signalling to be safe. */
    aethernet_transport_set_on_data_received(channel, carrier_on_channel_data, c);

    return c;
}

aethernet_webrtc_signaling_t *
aethernet_webrtc_signaling_carrier_iface(aethernet_webrtc_signaling_carrier_t *carrier) {
    if (!carrier) return NULL;
    return &carrier->iface;
}

void aethernet_webrtc_signaling_carrier_destroy(aethernet_webrtc_signaling_carrier_t *carrier) {
    if (!carrier) return;
    /* Detach our callback so no inbound frame reaches a freed carrier. */
    if (carrier->channel)
        aethernet_transport_set_on_data_received(carrier->channel, NULL, NULL);
    free(carrier);
}
