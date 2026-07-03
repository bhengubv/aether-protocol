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
// null-valued Sdp/Candidate/SdpMid string fields omitted. Reuses cJSON — the
// same JSON helper the DTN / directory-service wire code vendors — via the
// cJSON_ParseWithLength / cJSON_PrintUnformatted style established in
// c/src/directory_service.c.

#include "aethernet/webrtc_signaling.h"

#include <cjson/cJSON.h>

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

/* ───────────────────────── framing: encode ────────────────────────────────*/

uint8_t *aethernet_webrtc_signal_frame_encode(const aethernet_webrtc_signal_t *signal,
                                              size_t *out_len) {
    if (!signal) return NULL;

    cJSON *obj = cJSON_CreateObject();
    if (!obj) return NULL;

    /* PascalCase keys, matching the C# System.Text.Json default (no naming
     * policy) source-generated contract. Order is irrelevant to a DOM parser;
     * we follow the C# property declaration order for readability. */
    cJSON_AddStringToObject(obj, "FromUhid", signal->from_uhid);
    cJSON_AddStringToObject(obj, "ToUhid",   signal->to_uhid);
    /* Type is serialized as its numeric value (C# default enum handling). */
    cJSON_AddNumberToObject(obj, "Type", (double)signal->type);

    /* Sdp: present only for OFFER/ANSWER — omit when empty, mirroring C#'s
     * DefaultIgnoreCondition = WhenWritingNull (a null Sdp is dropped). */
    if (signal->sdp[0] != '\0')
        cJSON_AddStringToObject(obj, "Sdp", signal->sdp);

    /* Candidate: present only for CANDIDATE — omit when empty. */
    if (signal->candidate[0] != '\0')
        cJSON_AddStringToObject(obj, "Candidate", signal->candidate);

    /* SdpMLineIndex: non-nullable ushort in C#, so ALWAYS emitted (default 0).
     * The C signal struct routes candidates by mid, not m-line index, so we
     * emit the canonical 0 for the single data m-section — keeping the JSON
     * schema identical to C#. */
    cJSON_AddNumberToObject(obj, "SdpMLineIndex", 0);

    /* SdpMid: present only for CANDIDATE — omit when empty. */
    if (signal->sdp_mid[0] != '\0')
        cJSON_AddStringToObject(obj, "SdpMid", signal->sdp_mid);

    char *body = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);
    if (!body) return NULL;

    size_t body_len = strlen(body);
    size_t frame_len = (size_t)AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN + body_len;
    uint8_t *frame = (uint8_t *)malloc(frame_len);
    if (!frame) {
        free(body);
        return NULL;
    }
    memcpy(frame, AWS_MAGIC, AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN);
    memcpy(frame + AETHERNET_WEBRTC_SIGNAL_MAGIC_LEN, body, body_len);
    free(body);

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
    /* SdpMLineIndex is parsed for schema-completeness but the C signal struct
     * has no field for it (libdatachannel keys remote candidates by mid), so
     * it is intentionally not stored. */

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
