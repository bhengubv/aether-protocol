// SPDX-License-Identifier: MIT
// Aether Mesh — Streaming, Video Call, and Watch-Together services.
//
// NOTE: Build verification requires libsodium via pkg-config (Linux/macOS).
// CI on Linux is the verification gate.

#ifndef AETHERMESH_STREAMING_H
#define AETHERMESH_STREAMING_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#include "aethermesh/protocol.h"
#include "aethermesh/routing.h"
#include "aethermesh/transport.h"

#ifdef __cplusplus
extern "C" {
#endif

// ─── StreamingService ─────────────────────────────────────

/**
 * Maximum concurrent subscribers per stream.
 * Fixed-size array per stream record; chosen to fit in an embedded device's
 * typical working set. Callers that need more must shard across sessions.
 */
#define AETHERMESH_MAX_STREAM_SUBSCRIBERS 64

typedef void (*aethermesh_stream_announced_cb)(
    const uint8_t *stream_id,      // 16 bytes
    const char    *publisher_uhid,
    const char    *title,
    void          *user_data
);

typedef void (*aethermesh_stream_segment_cb)(
    const uint8_t *stream_id,      // 16 bytes
    const uint8_t *data,
    size_t         data_len,
    int            is_keyframe,
    int64_t        timestamp_ms,
    uint32_t       sequence,
    void          *user_data
);

typedef void (*aethermesh_stream_ended_cb)(
    const uint8_t *stream_id,      // 16 bytes
    void          *user_data
);

typedef struct aethermesh_streaming_service aethermesh_streaming_service_t;

aethermesh_streaming_service_t *aethermesh_streaming_service_create(
    aethermesh_transport_t       *transport,
    aethermesh_routing_service_t *routing,
    const char               *local_uhid
);
void aethermesh_streaming_service_destroy(aethermesh_streaming_service_t *svc);

void aethermesh_streaming_set_announced_cb(aethermesh_streaming_service_t *svc, aethermesh_stream_announced_cb cb, void *user_data);
void aethermesh_streaming_set_segment_cb(aethermesh_streaming_service_t *svc, aethermesh_stream_segment_cb cb, void *user_data);
void aethermesh_streaming_set_ended_cb(aethermesh_streaming_service_t *svc, aethermesh_stream_ended_cb cb, void *user_data);

/**
 * Announce a new live stream to the mesh.
 * Writes the new stream UUID to `stream_id_out[16]`.
 * `mime_type` — e.g. "audio/opus", "video/h264".
 * Returns 0 on success, -1 on error.
 */
int aethermesh_streaming_start(
    aethermesh_streaming_service_t *svc,
    const char                 *title,
    const char                 *mime_type,
    uint8_t                     stream_id_out[16]
);

/** End a stream and broadcast termination. Returns 0 on success, -1 if not found. */
int aethermesh_streaming_end(aethermesh_streaming_service_t *svc, const uint8_t stream_id[16]);

/**
 * Publish an encoded media segment to all subscribers.
 * Returns 0 on success, -1 on error.
 */
int aethermesh_streaming_publish_segment(
    aethermesh_streaming_service_t *svc,
    const uint8_t              *stream_id,   // 16 bytes
    const uint8_t              *data,
    size_t                      data_len,
    int                         is_keyframe
);

/**
 * Subscribe to a stream announced by `publisher_uhid`.
 * Returns 0 on success, -1 on error.
 */
int aethermesh_streaming_subscribe(
    aethermesh_streaming_service_t *svc,
    const uint8_t              *stream_id,      // 16 bytes
    const char                 *publisher_uhid
);

/**
 * Unsubscribe from a stream.
 * Returns 0 on success, -1 if not subscribed.
 */
int aethermesh_streaming_unsubscribe(
    aethermesh_streaming_service_t *svc,
    const uint8_t              *stream_id,      // 16 bytes
    const char                 *publisher_uhid
);

/** Pump an inbound streaming packet. Returns 0 handled, -1 ignored. */
int aethermesh_streaming_handle_packet(aethermesh_streaming_service_t *svc, const aethermesh_packet_t *packet);

// ─── VideoCallService ─────────────────────────────────────

typedef void (*aethermesh_video_incoming_cb)(
    const uint8_t  *call_id,        // 16 bytes
    const char     *from_uhid,
    const char    **video_codecs,
    int             video_codec_count,
    const char    **audio_codecs,
    int             audio_codec_count,
    void           *user_data
);

typedef void (*aethermesh_video_state_changed_cb)(
    const uint8_t *call_id,         // 16 bytes
    int            state,           // aethermesh_voice_call_state_t values (reused)
    void          *user_data
);

typedef void (*aethermesh_video_frame_cb)(
    const uint8_t *call_id,         // 16 bytes
    const uint8_t *video,
    size_t         video_len,
    int            is_keyframe,
    int64_t        timestamp_ms,
    void          *user_data
);

typedef void (*aethermesh_video_keyframe_request_cb)(
    const uint8_t *call_id,         // 16 bytes
    void          *user_data
);

typedef void (*aethermesh_video_quality_changed_cb)(
    const uint8_t *call_id,         // 16 bytes
    const char    *quality,
    void          *user_data
);

typedef struct aethermesh_video_call_service aethermesh_video_call_service_t;

aethermesh_video_call_service_t *aethermesh_video_call_service_create(
    aethermesh_transport_t       *transport,
    aethermesh_routing_service_t *routing,
    const char               *local_uhid
);
void aethermesh_video_call_service_destroy(aethermesh_video_call_service_t *svc);

void aethermesh_video_set_incoming_cb(aethermesh_video_call_service_t *svc, aethermesh_video_incoming_cb cb, void *user_data);
void aethermesh_video_set_state_changed_cb(aethermesh_video_call_service_t *svc, aethermesh_video_state_changed_cb cb, void *user_data);
void aethermesh_video_set_frame_cb(aethermesh_video_call_service_t *svc, aethermesh_video_frame_cb cb, void *user_data);
void aethermesh_video_set_keyframe_request_cb(aethermesh_video_call_service_t *svc, aethermesh_video_keyframe_request_cb cb, void *user_data);
void aethermesh_video_set_quality_changed_cb(aethermesh_video_call_service_t *svc, aethermesh_video_quality_changed_cb cb, void *user_data);

/**
 * Send a video call offer.
 * Writes the new call UUID to `call_id_out[16]`. Returns 0 on success, -1 on error.
 */
int aethermesh_video_send_offer(
    aethermesh_video_call_service_t *svc,
    const char                  *to_uhid,
    const char                 **video_codecs,
    int                          video_codec_count,
    const char                 **audio_codecs,
    int                          audio_codec_count,
    uint8_t                      call_id_out[16]
);

int aethermesh_video_accept_call(aethermesh_video_call_service_t *svc, const uint8_t call_id[16]);
int aethermesh_video_hang_up(aethermesh_video_call_service_t *svc, const uint8_t call_id[16]);

int aethermesh_video_send_frame(
    aethermesh_video_call_service_t *svc,
    const uint8_t               *call_id,    // 16 bytes
    const uint8_t               *video,
    size_t                       video_len,
    int                          is_keyframe
);

int aethermesh_video_request_keyframe(aethermesh_video_call_service_t *svc, const uint8_t call_id[16]);
int aethermesh_video_notify_quality_change(aethermesh_video_call_service_t *svc, const uint8_t call_id[16], const char *quality);

int aethermesh_video_handle_packet(aethermesh_video_call_service_t *svc, const aethermesh_packet_t *packet);

// ─── WatchTogetherService ─────────────────────────────────

typedef void (*aethermesh_watch_invite_cb)(
    const uint8_t *session_id,     // 16 bytes
    const char    *host_uhid,
    const char    *media_url,
    void          *user_data
);

typedef void (*aethermesh_watch_playback_cb)(
    const uint8_t *session_id,     // 16 bytes
    int            is_playing,
    int64_t        position_ms,    // RTT-compensated
    void          *user_data
);

typedef void (*aethermesh_watch_reaction_cb)(
    const uint8_t *session_id,     // 16 bytes
    const char    *from_uhid,
    const char    *emoji,
    void          *user_data
);

typedef void (*aethermesh_watch_member_cb)(
    const uint8_t *session_id,     // 16 bytes
    const char    *uhid,
    void          *user_data
);

typedef struct aethermesh_watch_together_service aethermesh_watch_together_service_t;

aethermesh_watch_together_service_t *aethermesh_watch_together_service_create(
    aethermesh_transport_t       *transport,
    aethermesh_routing_service_t *routing,
    const char               *local_uhid
);
void aethermesh_watch_together_service_destroy(aethermesh_watch_together_service_t *svc);

void aethermesh_watch_set_invite_cb(aethermesh_watch_together_service_t *svc, aethermesh_watch_invite_cb cb, void *user_data);
void aethermesh_watch_set_playback_cb(aethermesh_watch_together_service_t *svc, aethermesh_watch_playback_cb cb, void *user_data);
void aethermesh_watch_set_reaction_cb(aethermesh_watch_together_service_t *svc, aethermesh_watch_reaction_cb cb, void *user_data);
void aethermesh_watch_set_member_joined_cb(aethermesh_watch_together_service_t *svc, aethermesh_watch_member_cb cb, void *user_data);
void aethermesh_watch_set_member_left_cb(aethermesh_watch_together_service_t *svc, aethermesh_watch_member_cb cb, void *user_data);

/**
 * Create a session and invite `to_uhids`.
 * Writes session UUID to `session_id_out[16]`. Returns 0 on success, -1 on error.
 */
int aethermesh_watch_invite_to_session(
    aethermesh_watch_together_service_t *svc,
    const char                     **to_uhids,
    int                              to_count,
    const char                      *media_url,
    uint8_t                          session_id_out[16]
);

int aethermesh_watch_play(aethermesh_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms);
int aethermesh_watch_pause(aethermesh_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms);
int aethermesh_watch_seek(aethermesh_watch_together_service_t *svc, const uint8_t session_id[16], int64_t position_ms);
int aethermesh_watch_set_speed(aethermesh_watch_together_service_t *svc, const uint8_t session_id[16], double speed);

/**
 * Send an emoji reaction to all session members.
 * `emoji` must be a null-terminated UTF-8 string.
 */
int aethermesh_watch_send_reaction(aethermesh_watch_together_service_t *svc, const uint8_t session_id[16], const char *emoji);

int aethermesh_watch_handle_packet(aethermesh_watch_together_service_t *svc, const aethermesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERMESH_STREAMING_H
