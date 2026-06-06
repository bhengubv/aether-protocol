// SPDX-License-Identifier: MIT
// Aether Mesh — Streaming, Video Call, and Watch-Together services.
//
// NOTE: Build verification requires libsodium via pkg-config (Linux/macOS).
// CI on Linux is the verification gate.

#ifndef AETHERNET_STREAMING_H
#define AETHERNET_STREAMING_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"
#include "aethernet/transport.h"

#ifdef __cplusplus
extern "C" {
#endif

// ─── StreamingService ─────────────────────────────────────

/**
 * Maximum concurrent subscribers per stream.
 * Fixed-size array per stream record; chosen to fit in an embedded device's
 * typical working set. Callers that need more must shard across sessions.
 */
#define AETHERNET_MAX_STREAM_SUBSCRIBERS 64

typedef void (*aethernet_stream_announced_cb)(
    const uint8_t *stream_id,      // 16 bytes
    const char    *publisher_uhid,
    const char    *title,
    void          *user_data
);

typedef void (*aethernet_stream_segment_cb)(
    const uint8_t *stream_id,      // 16 bytes
    const uint8_t *data,
    size_t         data_len,
    int            is_keyframe,
    int64_t        timestamp_ms,
    uint32_t       sequence,
    void          *user_data
);

typedef void (*aethernet_stream_ended_cb)(
    const uint8_t *stream_id,      // 16 bytes
    void          *user_data
);

typedef struct aethernet_streaming_service aethernet_streaming_service_t;

aethernet_streaming_service_t *aethernet_streaming_service_create(
    aethernet_transport_t       *transport,
    aethernet_routing_service_t *routing,
    const char               *local_uhid
);
void aethernet_streaming_service_destroy(aethernet_streaming_service_t *svc);

void aethernet_streaming_set_announced_cb(aethernet_streaming_service_t *svc, aethernet_stream_announced_cb cb, void *user_data);
void aethernet_streaming_set_segment_cb(aethernet_streaming_service_t *svc, aethernet_stream_segment_cb cb, void *user_data);
void aethernet_streaming_set_ended_cb(aethernet_streaming_service_t *svc, aethernet_stream_ended_cb cb, void *user_data);

/**
 * Announce a new live stream to the mesh.
 * Writes the new stream UUID to `stream_id_out[16]`.
 * `mime_type` — e.g. "audio/opus", "video/h264".
 * Returns 0 on success, -1 on error.
 */
int aethernet_streaming_start(
    aethernet_streaming_service_t *svc,
    const char                 *title,
    const char                 *mime_type,
    uint8_t                     stream_id_out[16]
);

/** End a stream and broadcast termination. Returns 0 on success, -1 if not found. */
int aethernet_streaming_end(aethernet_streaming_service_t *svc, const uint8_t stream_id[16]);

/**
 * Publish an encoded media segment to all subscribers.
 * Returns 0 on success, -1 on error.
 */
int aethernet_streaming_publish_segment(
    aethernet_streaming_service_t *svc,
    const uint8_t              *stream_id,   // 16 bytes
    const uint8_t              *data,
    size_t                      data_len,
    int                         is_keyframe
);

/**
 * Subscribe to a stream announced by `publisher_uhid`.
 * Returns 0 on success, -1 on error.
 */
int aethernet_streaming_subscribe(
    aethernet_streaming_service_t *svc,
    const uint8_t              *stream_id,      // 16 bytes
    const char                 *publisher_uhid
);

/**
 * Unsubscribe from a stream.
 * Returns 0 on success, -1 if not subscribed.
 */
int aethernet_streaming_unsubscribe(
    aethernet_streaming_service_t *svc,
    const uint8_t              *stream_id,      // 16 bytes
    const char                 *publisher_uhid
);

/** Pump an inbound streaming packet. Returns 0 handled, -1 ignored. */
int aethernet_streaming_handle_packet(aethernet_streaming_service_t *svc, const aethernet_packet_t *packet);

// ─── VideoCallService ─────────────────────────────────────

typedef void (*aethernet_video_incoming_cb)(
    const uint8_t  *call_id,        // 16 bytes
    const char     *from_uhid,
    const char    **video_codecs,
    int             video_codec_count,
    const char    **audio_codecs,
    int             audio_codec_count,
    void           *user_data
);

typedef void (*aethernet_video_state_changed_cb)(
    const uint8_t *call_id,         // 16 bytes
    int            state,           // aethernet_voice_call_state_t values (reused)
    void          *user_data
);

typedef void (*aethernet_video_frame_cb)(
    const uint8_t *call_id,         // 16 bytes
    const uint8_t *video,
    size_t         video_len,
    int            is_keyframe,
    int64_t        timestamp_ms,
    void          *user_data
);

typedef void (*aethernet_video_keyframe_request_cb)(
    const uint8_t *call_id,         // 16 bytes
    void          *user_data
);

typedef void (*aethernet_video_quality_changed_cb)(
    const uint8_t *call_id,         // 16 bytes
    const char    *quality,
    void          *user_data
);

typedef struct aethernet_video_call_service aethernet_video_call_service_t;

aethernet_video_call_service_t *aethernet_video_call_service_create(
    aethernet_transport_t       *transport,
    aethernet_routing_service_t *routing,
    const char               *local_uhid
);
void aethernet_video_call_service_destroy(aethernet_video_call_service_t *svc);

void aethernet_video_set_incoming_cb(aethernet_video_call_service_t *svc, aethernet_video_incoming_cb cb, void *user_data);
void aethernet_video_set_state_changed_cb(aethernet_video_call_service_t *svc, aethernet_video_state_changed_cb cb, void *user_data);
void aethernet_video_set_frame_cb(aethernet_video_call_service_t *svc, aethernet_video_frame_cb cb, void *user_data);
void aethernet_video_set_keyframe_request_cb(aethernet_video_call_service_t *svc, aethernet_video_keyframe_request_cb cb, void *user_data);
void aethernet_video_set_quality_changed_cb(aethernet_video_call_service_t *svc, aethernet_video_quality_changed_cb cb, void *user_data);

/**
 * Send a video call offer.
 * Writes the new call UUID to `call_id_out[16]`. Returns 0 on success, -1 on error.
 */
int aethernet_video_send_offer(
    aethernet_video_call_service_t *svc,
    const char                  *to_uhid,
    const char                 **video_codecs,
    int                          video_codec_count,
    const char                 **audio_codecs,
    int                          audio_codec_count,
    uint8_t                      call_id_out[16]
);

int aethernet_video_accept_call(aethernet_video_call_service_t *svc, const uint8_t call_id[16]);
int aethernet_video_hang_up(aethernet_video_call_service_t *svc, const uint8_t call_id[16]);

int aethernet_video_send_frame(
    aethernet_video_call_service_t *svc,
    const uint8_t               *call_id,    // 16 bytes
    const uint8_t               *video,
    size_t                       video_len,
    int                          is_keyframe
);

int aethernet_video_request_keyframe(aethernet_video_call_service_t *svc, const uint8_t call_id[16]);
int aethernet_video_notify_quality_change(aethernet_video_call_service_t *svc, const uint8_t call_id[16], const char *quality);

int aethernet_video_handle_packet(aethernet_video_call_service_t *svc, const aethernet_packet_t *packet);

// ─── WatchTogetherService ─────────────────────────────────

typedef void (*aethernet_watch_invite_cb)(
    const uint8_t *session_id,     // 16 bytes
    const char    *host_uhid,
    const char    *media_url,
    void          *user_data
);

typedef void (*aethernet_watch_playback_cb)(
    const uint8_t *session_id,     // 16 bytes
    int            is_playing,
    int64_t        position_ms,    // RTT-compensated
    void          *user_data
);

typedef void (*aethernet_watch_reaction_cb)(
    const uint8_t *session_id,     // 16 bytes
    const char    *from_uhid,
    const char    *emoji,
    void          *user_data
);

typedef void (*aethernet_watch_member_cb)(
    const uint8_t *session_id,     // 16 bytes
    const char    *uhid,
    void          *user_data
);

typedef struct aethernet_watch_together_service aethernet_watch_together_service_t;

aethernet_watch_together_service_t *aethernet_watch_together_service_create(
    aethernet_transport_t       *transport,
    aethernet_routing_service_t *routing,
    const char               *local_uhid
);
void aethernet_watch_together_service_destroy(aethernet_watch_together_service_t *svc);

void aethernet_watch_set_invite_cb(aethernet_watch_together_service_t *svc, aethernet_watch_invite_cb cb, void *user_data);
void aethernet_watch_set_playback_cb(aethernet_watch_together_service_t *svc, aethernet_watch_playback_cb cb, void *user_data);
void aethernet_watch_set_reaction_cb(aethernet_watch_together_service_t *svc, aethernet_watch_reaction_cb cb, void *user_data);
void aethernet_watch_set_member_joined_cb(aethernet_watch_together_service_t *svc, aethernet_watch_member_cb cb, void *user_data);
void aethernet_watch_set_member_left_cb(aethernet_watch_together_service_t *svc, aethernet_watch_member_cb cb, void *user_data);

/**
 * Create a session and invite `to_uhids`.
 * Writes session UUID to `session_id_out[16]`. Returns 0 on success, -1 on error.
 */
int aethernet_watch_invite_to_session(
    aethernet_watch_together_service_t *svc,
    const char                     **to_uhids,
    int                              to_count,
    const char                      *media_url,
    uint8_t                          session_id_out[16]
);

int aethernet_watch_play(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms);
int aethernet_watch_pause(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms);
int aethernet_watch_seek(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms);
int aethernet_watch_set_speed(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], double speed);

/**
 * Send an emoji reaction to all session members.
 * `emoji` must be a null-terminated UTF-8 string.
 */
int aethernet_watch_send_reaction(aethernet_watch_together_service_t *svc, const uint8_t session_id[16], const char *emoji);

int aethernet_watch_handle_packet(aethernet_watch_together_service_t *svc, const aethernet_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_STREAMING_H
