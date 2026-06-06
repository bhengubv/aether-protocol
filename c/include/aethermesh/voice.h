// SPDX-License-Identifier: MIT
// Aether Mesh — Voice call services (1-to-1 and group).
//
// NOTE: Build verification requires libsodium via pkg-config (Linux/macOS).
// CI on Linux is the verification gate for this header and its implementations.

#ifndef AETHERMESH_VOICE_H
#define AETHERMESH_VOICE_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#include "aethermesh/protocol.h"
#include "aethermesh/routing.h"
#include "aethermesh/transport.h"

#ifdef __cplusplus
extern "C" {
#endif

// ─── VoiceCallState ──────────────────────────────────────

typedef enum {
    AETHERMESH_VOICE_STATE_OUTGOING  = 0,  // offer sent, awaiting accept
    AETHERMESH_VOICE_STATE_INCOMING  = 1,  // offer received, awaiting accept/reject
    AETHERMESH_VOICE_STATE_CONNECTED = 2,  // both sides accepted; frames flowing
    AETHERMESH_VOICE_STATE_ENDED     = 3,  // normal hangup
    AETHERMESH_VOICE_STATE_FAILED    = 4,  // timeout or transport error
} aethermesh_voice_call_state_t;

// ─── Callbacks ────────────────────────────────────────────

typedef void (*aethermesh_voice_incoming_cb)(
    const uint8_t call_id[16],
    const char   *from_uhid,
    const char  **codecs,        // null-terminated array of codec name strings
    int           codec_count,
    int           sample_rate_hz,
    void         *user_data
);

typedef void (*aethermesh_voice_state_changed_cb)(
    const uint8_t           call_id[16],
    aethermesh_voice_call_state_t state,
    void                   *user_data
);

typedef void (*aethermesh_voice_frame_cb)(
    const uint8_t *call_id,        // 16 bytes
    const uint8_t *audio,          // encoded audio bytes
    size_t         audio_len,
    int            is_silence,
    int64_t        timestamp_ms,
    void          *user_data
);

// ─── VoiceCallService (1-to-1) ───────────────────────────

/**
 * Opaque 1-to-1 voice call service handle.
 */
typedef struct aethermesh_voice_service aethermesh_voice_service_t;

/**
 * Create a voice call service.
 * `transport` and `routing` must outlive the service.
 * `local_uhid` is borrowed — caller owns.
 */
aethermesh_voice_service_t *aethermesh_voice_service_create(
    aethermesh_transport_t  *transport,
    aethermesh_routing_service_t *routing,
    const char          *local_uhid
);

void aethermesh_voice_service_destroy(aethermesh_voice_service_t *svc);

/** Register callbacks. NULL clears the callback. */
void aethermesh_voice_set_incoming_cb(aethermesh_voice_service_t *svc, aethermesh_voice_incoming_cb cb, void *user_data);
void aethermesh_voice_set_state_changed_cb(aethermesh_voice_service_t *svc, aethermesh_voice_state_changed_cb cb, void *user_data);
void aethermesh_voice_set_frame_cb(aethermesh_voice_service_t *svc, aethermesh_voice_frame_cb cb, void *user_data);

/**
 * Originate a call offer to `to_uhid`.
 * `codecs` is an array of `codec_count` null-terminated strings.
 * On success returns 0 and writes the new call UUID to `call_id_out[16]`.
 * Returns -1 on error.
 */
int aethermesh_voice_send_offer(
    aethermesh_voice_service_t *svc,
    const char             *to_uhid,
    const char            **codecs,
    int                     codec_count,
    int                     sample_rate_hz,
    uint8_t                 call_id_out[16]
);

/**
 * Accept an incoming call identified by `call_id[16]`.
 * Returns 0 on success, -1 on error (call not found or wrong state).
 */
int aethermesh_voice_accept_call(aethermesh_voice_service_t *svc, const uint8_t call_id[16]);

/**
 * Hang up (either side). Returns 0 on success, -1 if call not found.
 */
int aethermesh_voice_hang_up(aethermesh_voice_service_t *svc, const uint8_t call_id[16]);

/**
 * Send an encoded audio frame.
 * `call_id[16]` identifies the active call (must be CONNECTED).
 * `audio` / `audio_len` are the encoded audio bytes.
 * `is_silence` — 1 if the frame is a silence/comfort-noise frame, 0 otherwise.
 * Returns 0 on success, -1 on error.
 */
int aethermesh_voice_send_frame(
    aethermesh_voice_service_t *svc,
    const uint8_t          *call_id,     // 16 bytes
    const uint8_t          *audio,
    size_t                  audio_len,
    int                     is_silence
);

/**
 * Pump an inbound packet (voiceCall or voiceSignaling type).
 * Returns 0 on success, -1 if the packet type is not handled.
 */
int aethermesh_voice_handle_packet(aethermesh_voice_service_t *svc, const aethermesh_packet_t *packet);

// ─── GroupVoiceCallService ────────────────────────────────

/**
 * Callbacks for group voice events.
 */
typedef void (*aethermesh_group_voice_invite_cb)(
    const uint8_t  *call_id,        // 16 bytes
    const char     *from_uhid,
    const char    **codecs,
    int             codec_count,
    void           *user_data
);

typedef void (*aethermesh_group_voice_member_cb)(
    const uint8_t *call_id,         // 16 bytes
    const char    *uhid,
    void          *user_data
);

typedef void (*aethermesh_group_voice_frame_cb)(
    const uint8_t *call_id,         // 16 bytes
    const char    *from_uhid,
    const uint8_t *audio,
    size_t         audio_len,
    int            is_silence,
    uint32_t       key_generation,
    int64_t        timestamp_ms,
    void          *user_data
);

/**
 * Opaque group voice call service handle.
 */
typedef struct aethermesh_group_voice_service aethermesh_group_voice_service_t;

aethermesh_group_voice_service_t *aethermesh_group_voice_service_create(
    aethermesh_transport_t       *transport,
    aethermesh_routing_service_t *routing,
    const char               *local_uhid
);
void aethermesh_group_voice_service_destroy(aethermesh_group_voice_service_t *svc);

void aethermesh_group_voice_set_invite_cb(aethermesh_group_voice_service_t *svc, aethermesh_group_voice_invite_cb cb, void *user_data);
void aethermesh_group_voice_set_member_joined_cb(aethermesh_group_voice_service_t *svc, aethermesh_group_voice_member_cb cb, void *user_data);
void aethermesh_group_voice_set_member_left_cb(aethermesh_group_voice_service_t *svc, aethermesh_group_voice_member_cb cb, void *user_data);
void aethermesh_group_voice_set_frame_cb(aethermesh_group_voice_service_t *svc, aethermesh_group_voice_frame_cb cb, void *user_data);

/**
 * Create a group call and invite `to_uhids` (array of `to_count` UHID strings).
 * Writes the new session UUID to `call_id_out[16]`.
 * Returns 0 on success, -1 on error.
 */
int aethermesh_group_voice_invite(
    aethermesh_group_voice_service_t *svc,
    const char                  **to_uhids,
    int                           to_count,
    const char                  **codecs,
    int                           codec_count,
    uint8_t                       call_id_out[16]
);

int aethermesh_group_voice_join(aethermesh_group_voice_service_t *svc, const uint8_t call_id[16]);
int aethermesh_group_voice_leave(aethermesh_group_voice_service_t *svc, const uint8_t call_id[16]);
int aethermesh_group_voice_kick(aethermesh_group_voice_service_t *svc, const uint8_t call_id[16], const char *uhid);

/**
 * Send a group audio frame with optional `key_generation` (0 = no rekey).
 */
int aethermesh_group_voice_send_frame(
    aethermesh_group_voice_service_t *svc,
    const uint8_t                *call_id,    // 16 bytes
    const uint8_t                *audio,
    size_t                        audio_len,
    int                           is_silence,
    uint32_t                      key_generation
);

int aethermesh_group_voice_handle_packet(aethermesh_group_voice_service_t *svc, const aethermesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERMESH_VOICE_H
