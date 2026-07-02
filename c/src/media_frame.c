// SPDX-License-Identifier: MIT
// VoicePtt(15) + ScreenShare(32) directed media-frame bindings for the Aether mesh.
//
// BINARY frames (no cJSON) sharing the exact 29-byte header of VoiceCall(16)/VideoFrame(31):
//   [0..15]  call_id       16 bytes, RFC-4122 BIG-ENDIAN (written order; same as the DTN bundle id)
//   [16..19] sequence      u32 LITTLE-ENDIAN
//   [20..27] timestamp_ms  i64 LITTLE-ENDIAN
//   [28]     flag          u8 (is_silence / is_keyframe)
//   [29..]   payload       opaque encoded bytes
// The LE write/read helpers and the call_id-memcpy idiom mirror voice.c's build_voice_frame /
// parse_voice_frame verbatim, so the two frame families are byte-identical on the wire. Byte-identity
// gate: fixtures/media/vectors.json. Behaviour mirrors the green C# MediaFrameService (send_frame →
// directed 15/32 packet with the payload copied; handle_packet → callback(frame, from_uhid); wrong
// type / short (<29) → false).
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service in
// their own mutex (matches sos.c / channels.c / videocall.c / prekey.c).

#include "aethernet/media_frame.h"
#include "aethernet/constants.h"

#include <stdlib.h>
#include <string.h>

// ─── LE write/read helpers (mirror voice.c, file-private) ───────────────────

static void mf_write_le_u32(uint8_t *buf, uint32_t v) {
    buf[0] = (uint8_t)(v);
    buf[1] = (uint8_t)(v >> 8);
    buf[2] = (uint8_t)(v >> 16);
    buf[3] = (uint8_t)(v >> 24);
}

static void mf_write_le_i64(uint8_t *buf, int64_t v) {
    uint64_t u = (uint64_t)v;
    buf[0] = (uint8_t)(u);
    buf[1] = (uint8_t)(u >> 8);
    buf[2] = (uint8_t)(u >> 16);
    buf[3] = (uint8_t)(u >> 24);
    buf[4] = (uint8_t)(u >> 32);
    buf[5] = (uint8_t)(u >> 40);
    buf[6] = (uint8_t)(u >> 48);
    buf[7] = (uint8_t)(u >> 56);
}

static uint32_t mf_read_le_u32(const uint8_t *buf) {
    return (uint32_t)buf[0]
         | ((uint32_t)buf[1] << 8)
         | ((uint32_t)buf[2] << 16)
         | ((uint32_t)buf[3] << 24);
}

static int64_t mf_read_le_i64(const uint8_t *buf) {
    uint64_t u = (uint64_t)buf[0]
               | ((uint64_t)buf[1] << 8)
               | ((uint64_t)buf[2] << 16)
               | ((uint64_t)buf[3] << 24)
               | ((uint64_t)buf[4] << 32)
               | ((uint64_t)buf[5] << 40)
               | ((uint64_t)buf[6] << 48)
               | ((uint64_t)buf[7] << 56);
    return (int64_t)u;
}

static char *mf_str_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

#define MF_HEADER_LEN 29

// ─── Shared frame codec ─────────────────────────────────────────────────────

// Serialize the shared 29-byte header + payload into a fresh heap buffer. `flag` is the is_silence /
// is_keyframe bit. Mirrors the C# MediaFrameCodec.Serialize and voice.c build_voice_frame.
static bool mf_serialize(const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                         uint32_t sequence, int64_t timestamp_ms, bool flag,
                         const uint8_t *payload, uint32_t payload_len,
                         uint8_t **out_frame, uint32_t *out_len) {
    size_t total = (size_t)MF_HEADER_LEN + payload_len;
    uint8_t *buf = (uint8_t *)malloc(total ? total : 1);
    if (!buf) return false;
    memcpy(buf, call_id, AETHERNET_PACKET_ID_SIZE);   // call_id: 16 bytes, big-endian (written order)
    mf_write_le_u32(buf + 16, sequence);
    mf_write_le_i64(buf + 20, timestamp_ms);
    buf[28] = (uint8_t)(flag ? 1 : 0);
    if (payload && payload_len) memcpy(buf + MF_HEADER_LEN, payload, payload_len);
    *out_frame = buf;
    *out_len = (uint32_t)total;
    return true;
}

// Parse the shared 29-byte header. `out_payload` points INTO `data` (borrowed). Every read is
// bounds-checked via the single length guard (the header is fixed-width, so len >= 29 covers all
// reads). Mirrors voice.c parse_voice_frame.
static bool mf_deserialize(const uint8_t *data, uint32_t len,
                           uint8_t call_id_out[AETHERNET_PACKET_ID_SIZE],
                           uint32_t *seq_out, int64_t *ts_out, bool *flag_out,
                           const uint8_t **payload_out, uint32_t *payload_len_out) {
    if (!data || len < MF_HEADER_LEN) return false;
    memcpy(call_id_out, data, AETHERNET_PACKET_ID_SIZE);
    *seq_out         = mf_read_le_u32(data + 16);
    *ts_out          = mf_read_le_i64(data + 20);
    *flag_out        = data[28] != 0;
    *payload_out     = data + MF_HEADER_LEN;
    *payload_len_out = len - MF_HEADER_LEN;
    return true;
}

// ─── Public codec: VoicePtt ─────────────────────────────────────────────────

bool aethernet_voice_ptt_frame_serialize(const aethernet_voice_ptt_frame_t *frame,
                                         uint8_t **out_frame, uint32_t *out_len) {
    if (!frame || !out_frame || !out_len) return false;
    return mf_serialize(frame->call_id, frame->sequence, frame->timestamp_ms, frame->is_silence,
                        frame->encoded_payload, frame->encoded_payload_len, out_frame, out_len);
}

bool aethernet_voice_ptt_frame_deserialize(const uint8_t *data, uint32_t len,
                                           aethernet_voice_ptt_frame_t *out) {
    if (!out) return false;
    memset(out, 0, sizeof(*out));
    return mf_deserialize(data, len, out->call_id, &out->sequence, &out->timestamp_ms,
                          &out->is_silence, &out->encoded_payload, &out->encoded_payload_len);
}

// ─── Public codec: ScreenShare ──────────────────────────────────────────────

bool aethernet_screen_share_frame_serialize(const aethernet_screen_share_frame_t *frame,
                                            uint8_t **out_frame, uint32_t *out_len) {
    if (!frame || !out_frame || !out_len) return false;
    return mf_serialize(frame->call_id, frame->sequence, frame->timestamp_ms, frame->is_keyframe,
                        frame->encoded_payload, frame->encoded_payload_len, out_frame, out_len);
}

bool aethernet_screen_share_frame_deserialize(const uint8_t *data, uint32_t len,
                                              aethernet_screen_share_frame_t *out) {
    if (!out) return false;
    memset(out, 0, sizeof(*out));
    return mf_deserialize(data, len, out->call_id, &out->sequence, &out->timestamp_ms,
                          &out->is_keyframe, &out->encoded_payload, &out->encoded_payload_len);
}

// ─── Directed send (shared) ─────────────────────────────────────────────────

// Build and directed-send a media packet of `type` carrying `body` to `peer_uhid`. Copies body into
// the packet. Returns the delivery result from sender->send (false if the host wired no directed
// send). Mirrors prekey.c send_pre_key_packet.
static bool mf_send_packet(aethernet_mesh_sender_t *sender, aethernet_packet_type_t type,
                           const char *peer_uhid, const uint8_t *body, uint32_t body_len) {
    if (!sender->send) return false;  // host wired no directed send — cannot deliver

    aethernet_mesh_packet_t *pkt = aethernet_packet_new();
    if (!pkt) return false;
    pkt->type = (uint8_t)type;
    aethernet_packet_set_source_uhid(pkt, sender->local_uhid);
    aethernet_packet_set_destination_uhid(pkt, peer_uhid);
    pkt->ttl = AETHERNET_DEFAULT_TTL;
    aethernet_packet_set_payload(pkt, body, body_len);

    bool delivered = sender->send(sender, pkt, peer_uhid);
    aethernet_packet_free(pkt);
    return delivered;
}

// ─── VoicePtt service ───────────────────────────────────────────────────────

struct aethernet_voice_ptt_service {
    aethernet_mesh_sender_t              *sender;
    aethernet_voice_ptt_frame_received_cb cb;
    void                                 *cb_user_data;
};

aethernet_voice_ptt_service_t *aethernet_voice_ptt_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_voice_ptt_service_t *svc =
        (aethernet_voice_ptt_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_voice_ptt_service_free(aethernet_voice_ptt_service_t *service) {
    free(service);
}

bool aethernet_voice_ptt_service_send_frame(aethernet_voice_ptt_service_t *service,
                                            const char *peer_uhid,
                                            const aethernet_voice_ptt_frame_t *frame) {
    if (!service || !peer_uhid || peer_uhid[0] == '\0' || !frame) return false;
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!aethernet_voice_ptt_frame_serialize(frame, &body, &body_len)) return false;
    bool delivered = mf_send_packet(service->sender, AETHERNET_PACKET_TYPE_VOICE_PTT,
                                    peer_uhid, body, body_len);
    free(body);
    return delivered;
}

bool aethernet_voice_ptt_service_handle_packet(aethernet_voice_ptt_service_t *service,
                                               const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_VOICE_PTT) return false;   // wrong type → false

    aethernet_voice_ptt_frame_t frame;
    if (!aethernet_voice_ptt_frame_deserialize(packet->payload, packet->payload_len, &frame)) {
        return false;   // short / malformed → benign drop
    }

    if (service->cb) {
        // Copy the packet source into an owned buffer for the callback's from_uhid — the frame's
        // encoded_payload still borrows from packet->payload, which is valid for the call. (An owned
        // from_uhid mirrors prekey.c: never hand the callback a pointer that may be freed underneath
        // it; the Mac allocator is unforgiving of a stale borrow even where Windows may pass.)
        char *from_uhid = mf_str_dup(packet->source_uhid ? packet->source_uhid : "");
        if (!from_uhid) return false;
        aethernet_voice_ptt_frame_received_t evt;
        evt.frame = &frame;
        evt.from_uhid = from_uhid;
        service->cb(&evt, service->cb_user_data);
        free(from_uhid);
    }
    return true;
}

void aethernet_voice_ptt_service_set_frame_received_cb(aethernet_voice_ptt_service_t *service,
                                                       aethernet_voice_ptt_frame_received_cb cb,
                                                       void *user_data) {
    if (!service) return;
    service->cb = cb;
    service->cb_user_data = user_data;
}

// ─── ScreenShare service ────────────────────────────────────────────────────

struct aethernet_screen_share_service {
    aethernet_mesh_sender_t                 *sender;
    aethernet_screen_share_frame_received_cb cb;
    void                                    *cb_user_data;
};

aethernet_screen_share_service_t *aethernet_screen_share_service_new(aethernet_mesh_sender_t *sender) {
    if (!sender) return NULL;
    aethernet_screen_share_service_t *svc =
        (aethernet_screen_share_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->sender = sender;
    return svc;
}

void aethernet_screen_share_service_free(aethernet_screen_share_service_t *service) {
    free(service);
}

bool aethernet_screen_share_service_send_frame(aethernet_screen_share_service_t *service,
                                               const char *peer_uhid,
                                               const aethernet_screen_share_frame_t *frame) {
    if (!service || !peer_uhid || peer_uhid[0] == '\0' || !frame) return false;
    uint8_t *body = NULL;
    uint32_t body_len = 0;
    if (!aethernet_screen_share_frame_serialize(frame, &body, &body_len)) return false;
    bool delivered = mf_send_packet(service->sender, AETHERNET_PACKET_TYPE_SCREEN_SHARE,
                                    peer_uhid, body, body_len);
    free(body);
    return delivered;
}

bool aethernet_screen_share_service_handle_packet(aethernet_screen_share_service_t *service,
                                                  const aethernet_mesh_packet_t *packet) {
    if (!service || !packet) return false;
    if (packet->type != AETHERNET_PACKET_TYPE_SCREEN_SHARE) return false;   // wrong type → false

    aethernet_screen_share_frame_t frame;
    if (!aethernet_screen_share_frame_deserialize(packet->payload, packet->payload_len, &frame)) {
        return false;   // short / malformed → benign drop
    }

    if (service->cb) {
        char *from_uhid = mf_str_dup(packet->source_uhid ? packet->source_uhid : "");
        if (!from_uhid) return false;
        aethernet_screen_share_frame_received_t evt;
        evt.frame = &frame;
        evt.from_uhid = from_uhid;
        service->cb(&evt, service->cb_user_data);
        free(from_uhid);
    }
    return true;
}

void aethernet_screen_share_service_set_frame_received_cb(aethernet_screen_share_service_t *service,
                                                          aethernet_screen_share_frame_received_cb cb,
                                                          void *user_data) {
    if (!service) return;
    service->cb = cb;
    service->cb_user_data = user_data;
}
