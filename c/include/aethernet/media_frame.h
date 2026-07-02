// SPDX-License-Identifier: MIT
// Aether Mesh — VoicePtt(15) + ScreenShare(32) directed media-frame bindings.
//
// Binary codec + directed mesh transport for the two remaining media frames. Both share the exact
// 29-byte header the existing VoiceCall(16)/VideoFrame(31) frames use, so a node treats them
// uniformly (mirrors src/AetherNet.Core/Media/MediaFrameService.cs):
//   [0..15]  call_id       — 16 bytes, RFC-4122 BIG-ENDIAN (network order, the uuid bytes in written
//                            order — same layout as the DTN bundle id; NOT the .NET mixed-endian
//                            Guid.ToByteArray()).
//   [16..19] sequence      — u32 LITTLE-ENDIAN
//   [20..27] timestamp_ms  — i64 LITTLE-ENDIAN
//   [28]     flag          — u8 (VoicePtt: is_silence; ScreenShare: is_keyframe)
//   [29..]   payload       — opaque encoded audio/video bytes
// Byte-identity gate: fixtures/media/vectors.json (expected_hex). BINARY frames — no JSON.
//
// This is the media-plane TRANSPORT only: each service directed-sends a frame to one peer (never
// broadcast) and surfaces inbound frames via a callback. The host owns capture/encode/decode/render.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
// in their own mutex (matches sos.c / channels.c / videocall.c / prekey.c).

#ifndef AETHERNET_MEDIA_FRAME_H
#define AETHERNET_MEDIA_FRAME_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A push-to-talk audio frame (PacketType VoicePtt = 15 body). Mirrors the C# VoicePttFrame.
 * `encoded_payload` is a borrowed pointer to opaque encoded audio bytes (may be NULL when
 * `encoded_payload_len` is 0). The serializer copies it; the deserializer/handler points it into the
 * source frame (borrowed for the callback duration — copy anything you wish to retain).
 */
typedef struct {
    uint8_t        call_id[AETHERNET_PACKET_ID_SIZE]; // 16-byte UUID of the call, big-endian on the wire
    uint32_t       sequence;                          // monotonically increasing frame sequence
    int64_t        timestamp_ms;                      // capture timestamp (ms)
    bool           is_silence;                        // true when this frame carries a silence marker
    const uint8_t *encoded_payload;                   // borrowed; opaque encoded audio (may be NULL)
    uint32_t       encoded_payload_len;               // length of encoded_payload in bytes
} aethernet_voice_ptt_frame_t;

/**
 * A screen-share video frame (PacketType ScreenShare = 32 body). Mirrors the C# ScreenShareFrame.
 * Same borrowing rules as aethernet_voice_ptt_frame_t.
 */
typedef struct {
    uint8_t        call_id[AETHERNET_PACKET_ID_SIZE]; // 16-byte UUID of the call, big-endian on the wire
    uint32_t       sequence;                          // monotonically increasing frame sequence
    int64_t        timestamp_ms;                      // capture timestamp (ms)
    bool           is_keyframe;                        // true when this frame is a keyframe (I-frame)
    const uint8_t *encoded_payload;                   // borrowed; opaque encoded video (may be NULL)
    uint32_t       encoded_payload_len;               // length of encoded_payload in bytes
} aethernet_screen_share_frame_t;

// ─── Binary codec (byte-identity gate: fixtures/media/vectors.json) ─────────────────────────────

/**
 * Serialize a VoicePtt frame to its canonical 29-byte-header binary wire form (header + payload). On
 * success writes a heap-allocated buffer to *out_frame and its length to *out_len, returns true. The
 * caller owns *out_frame and frees it with free(). Returns false on allocation failure or a NULL
 * required pointer. `frame->encoded_payload` may be NULL iff `frame->encoded_payload_len` is 0.
 */
bool aethernet_voice_ptt_frame_serialize(const aethernet_voice_ptt_frame_t *frame,
                                         uint8_t **out_frame,
                                         uint32_t *out_len);

/**
 * Serialize a ScreenShare frame to its canonical binary wire form. See
 * aethernet_voice_ptt_frame_serialize. Byte-identity gate: fixtures/media/vectors.json.
 */
bool aethernet_screen_share_frame_serialize(const aethernet_screen_share_frame_t *frame,
                                            uint8_t **out_frame,
                                            uint32_t *out_len);

/**
 * Deserialize a VoicePtt frame from `data[0..len)`. On success fills *out (with out->encoded_payload
 * pointing INTO `data` — borrowed, valid only while `data` lives) and returns true. Returns false if
 * `data`/`out` is NULL or `len` < 29 (too short for the header). Every read is bounds-checked.
 */
bool aethernet_voice_ptt_frame_deserialize(const uint8_t *data, uint32_t len,
                                           aethernet_voice_ptt_frame_t *out);

/**
 * Deserialize a ScreenShare frame from `data[0..len)`. See aethernet_voice_ptt_frame_deserialize.
 */
bool aethernet_screen_share_frame_deserialize(const uint8_t *data, uint32_t len,
                                              aethernet_screen_share_frame_t *out);

// ─── VoicePtt service (PacketType 15) ───────────────────────────────────────────────────────────

/**
 * A received VoicePtt frame surfaced to the host. Mirrors the C# VoicePttFrameReceived.
 * `frame` and `frame->encoded_payload` and `from_uhid` are all borrowed for the callback duration —
 * copy anything you wish to retain.
 */
typedef struct {
    const aethernet_voice_ptt_frame_t *frame;     // borrowed; the decoded frame
    const char                        *from_uhid; // borrowed; peer that sent the frame
} aethernet_voice_ptt_frame_received_t;

typedef void (*aethernet_voice_ptt_frame_received_cb)(const aethernet_voice_ptt_frame_received_t *event,
                                                      void *user_data);

/**
 * Opaque VoicePtt service handle. Directed-sends VoicePtt(15) frames and surfaces inbound ones via
 * the frame-received callback. Borrows `sender` — caller keeps it alive for the service lifetime.
 */
typedef struct aethernet_voice_ptt_service aethernet_voice_ptt_service_t;

aethernet_voice_ptt_service_t *aethernet_voice_ptt_service_new(aethernet_mesh_sender_t *sender);
void aethernet_voice_ptt_service_free(aethernet_voice_ptt_service_t *service);

/**
 * Directed-send `frame` to `peer_uhid`: build a VoicePtt(15) packet (source local_uhid, dest
 * peer_uhid, TTL AETHERNET_DEFAULT_TTL, payload = the serialized frame) and hand it to sender->send.
 * Returns the delivery result (true if delivered). Returns false if `service`/`peer_uhid`/`frame` is
 * NULL, `peer_uhid` is empty, or the frame fails to serialize. Mirrors the C# SendFrameAsync.
 */
bool aethernet_voice_ptt_service_send_frame(aethernet_voice_ptt_service_t *service,
                                            const char *peer_uhid,
                                            const aethernet_voice_ptt_frame_t *frame);

/**
 * Process an inbound packet. If `packet->type` is VoicePtt(15) and the payload decodes, fire the
 * frame-received callback with the decoded frame and the packet's source UHID, and return true.
 * Returns false for the wrong packet type, a short/malformed payload (< 29 bytes), or a NULL argument.
 * Mirrors the C# HandleAsync (wrong type / malformed → false, else raise FrameReceived).
 */
bool aethernet_voice_ptt_service_handle_packet(aethernet_voice_ptt_service_t *service,
                                               const aethernet_mesh_packet_t *packet);

void aethernet_voice_ptt_service_set_frame_received_cb(aethernet_voice_ptt_service_t *service,
                                                       aethernet_voice_ptt_frame_received_cb cb,
                                                       void *user_data);

// ─── ScreenShare service (PacketType 32) ────────────────────────────────────────────────────────

/**
 * A received ScreenShare frame surfaced to the host. Mirrors the C# ScreenShareFrameReceived.
 * Same borrowing rules as aethernet_voice_ptt_frame_received_t.
 */
typedef struct {
    const aethernet_screen_share_frame_t *frame;     // borrowed; the decoded frame
    const char                           *from_uhid; // borrowed; peer that sent the frame
} aethernet_screen_share_frame_received_t;

typedef void (*aethernet_screen_share_frame_received_cb)(const aethernet_screen_share_frame_received_t *event,
                                                         void *user_data);

/**
 * Opaque ScreenShare service handle. Directed-sends ScreenShare(32) frames and surfaces inbound ones
 * via the frame-received callback. Borrows `sender` — caller keeps it alive for the service lifetime.
 */
typedef struct aethernet_screen_share_service aethernet_screen_share_service_t;

aethernet_screen_share_service_t *aethernet_screen_share_service_new(aethernet_mesh_sender_t *sender);
void aethernet_screen_share_service_free(aethernet_screen_share_service_t *service);

/**
 * Directed-send `frame` to `peer_uhid` as a ScreenShare(32) packet. See
 * aethernet_voice_ptt_service_send_frame. Mirrors the C# SendFrameAsync.
 */
bool aethernet_screen_share_service_send_frame(aethernet_screen_share_service_t *service,
                                               const char *peer_uhid,
                                               const aethernet_screen_share_frame_t *frame);

/**
 * Process an inbound packet (ScreenShare(32)). See aethernet_voice_ptt_service_handle_packet.
 * Mirrors the C# HandleAsync.
 */
bool aethernet_screen_share_service_handle_packet(aethernet_screen_share_service_t *service,
                                                  const aethernet_mesh_packet_t *packet);

void aethernet_screen_share_service_set_frame_received_cb(aethernet_screen_share_service_t *service,
                                                          aethernet_screen_share_frame_received_cb cb,
                                                          void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_MEDIA_FRAME_H
