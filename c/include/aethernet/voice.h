// SPDX-License-Identifier: MIT
// Aether Mesh — Voice call services (1-to-1 and group).
//
// NOTE: Build verification requires libsodium via pkg-config (Linux/macOS).
// CI on Linux is the verification gate for this header and its implementations.

#ifndef AETHERNET_VOICE_H
#define AETHERNET_VOICE_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"
#include "aethernet/transport.h"

#ifdef __cplusplus
extern "C" {
#endif

// ─── VoiceCallState ──────────────────────────────────────

typedef enum {
    AETHERNET_VOICE_STATE_OUTGOING  = 0,  // offer sent, awaiting accept
    AETHERNET_VOICE_STATE_INCOMING  = 1,  // offer received, awaiting accept/reject
    AETHERNET_VOICE_STATE_CONNECTED = 2,  // both sides accepted; frames flowing
    AETHERNET_VOICE_STATE_ENDED     = 3,  // normal hangup
    AETHERNET_VOICE_STATE_FAILED    = 4,  // timeout or transport error
} aethernet_voice_call_state_t;

// ─── Callbacks ────────────────────────────────────────────

typedef void (*aethernet_voice_incoming_cb)(
    const uint8_t call_id[16],
    const char   *from_uhid,
    const char  **codecs,        // null-terminated array of codec name strings
    int           codec_count,
    int           sample_rate_hz,
    void         *user_data
);

typedef void (*aethernet_voice_state_changed_cb)(
    const uint8_t           call_id[16],
    aethernet_voice_call_state_t state,
    void                   *user_data
);

typedef void (*aethernet_voice_frame_cb)(
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
typedef struct aethernet_voice_service aethernet_voice_service_t;

/**
 * Create a voice call service.
 * `transport` and `routing` must outlive the service.
 * `local_uhid` is borrowed — caller owns.
 */
aethernet_voice_service_t *aethernet_voice_service_create(
    aethernet_transport_t  *transport,
    aethernet_routing_service_t *routing,
    const char          *local_uhid
);

void aethernet_voice_service_destroy(aethernet_voice_service_t *svc);

/** Register callbacks. NULL clears the callback. */
void aethernet_voice_set_incoming_cb(aethernet_voice_service_t *svc, aethernet_voice_incoming_cb cb, void *user_data);
void aethernet_voice_set_state_changed_cb(aethernet_voice_service_t *svc, aethernet_voice_state_changed_cb cb, void *user_data);
void aethernet_voice_set_frame_cb(aethernet_voice_service_t *svc, aethernet_voice_frame_cb cb, void *user_data);

/**
 * Originate a call offer to `to_uhid`.
 * `codecs` is an array of `codec_count` null-terminated strings.
 * On success returns 0 and writes the new call UUID to `call_id_out[16]`.
 * Returns -1 on error.
 */
int aethernet_voice_send_offer(
    aethernet_voice_service_t *svc,
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
int aethernet_voice_accept_call(aethernet_voice_service_t *svc, const uint8_t call_id[16]);

/**
 * Hang up (either side). Returns 0 on success, -1 if call not found.
 */
int aethernet_voice_hang_up(aethernet_voice_service_t *svc, const uint8_t call_id[16]);

/**
 * Send an encoded audio frame.
 * `call_id[16]` identifies the active call (must be CONNECTED).
 * `audio` / `audio_len` are the encoded audio bytes.
 * `is_silence` — 1 if the frame is a silence/comfort-noise frame, 0 otherwise.
 * Returns 0 on success, -1 on error.
 */
int aethernet_voice_send_frame(
    aethernet_voice_service_t *svc,
    const uint8_t          *call_id,     // 16 bytes
    const uint8_t          *audio,
    size_t                  audio_len,
    int                     is_silence
);

/**
 * Pump an inbound packet (voiceCall or voiceSignaling type).
 * Returns 0 on success, -1 if the packet type is not handled.
 */
int aethernet_voice_handle_packet(aethernet_voice_service_t *svc, const aethernet_packet_t *packet);

// ─── GroupVoiceCallService ────────────────────────────────

/**
 * Callbacks for group voice events.
 */
typedef void (*aethernet_group_voice_invite_cb)(
    const uint8_t  *call_id,        // 16 bytes
    const char     *from_uhid,
    const char    **codecs,
    int             codec_count,
    void           *user_data
);

typedef void (*aethernet_group_voice_member_cb)(
    const uint8_t *call_id,         // 16 bytes
    const char    *uhid,
    void          *user_data
);

typedef void (*aethernet_group_voice_frame_cb)(
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
typedef struct aethernet_group_voice_service aethernet_group_voice_service_t;

aethernet_group_voice_service_t *aethernet_group_voice_service_create(
    aethernet_transport_t       *transport,
    aethernet_routing_service_t *routing,
    const char               *local_uhid
);
void aethernet_group_voice_service_destroy(aethernet_group_voice_service_t *svc);

void aethernet_group_voice_set_invite_cb(aethernet_group_voice_service_t *svc, aethernet_group_voice_invite_cb cb, void *user_data);
void aethernet_group_voice_set_member_joined_cb(aethernet_group_voice_service_t *svc, aethernet_group_voice_member_cb cb, void *user_data);
void aethernet_group_voice_set_member_left_cb(aethernet_group_voice_service_t *svc, aethernet_group_voice_member_cb cb, void *user_data);
void aethernet_group_voice_set_frame_cb(aethernet_group_voice_service_t *svc, aethernet_group_voice_frame_cb cb, void *user_data);

/**
 * Create a group call and invite `to_uhids` (array of `to_count` UHID strings).
 * Writes the new session UUID to `call_id_out[16]`.
 * Returns 0 on success, -1 on error.
 */
int aethernet_group_voice_invite(
    aethernet_group_voice_service_t *svc,
    const char                  **to_uhids,
    int                           to_count,
    const char                  **codecs,
    int                           codec_count,
    uint8_t                       call_id_out[16]
);

int aethernet_group_voice_join(aethernet_group_voice_service_t *svc, const uint8_t call_id[16]);
int aethernet_group_voice_leave(aethernet_group_voice_service_t *svc, const uint8_t call_id[16]);
int aethernet_group_voice_kick(aethernet_group_voice_service_t *svc, const uint8_t call_id[16], const char *uhid);

/**
 * Send a group audio frame with optional `key_generation` (0 = no rekey).
 */
int aethernet_group_voice_send_frame(
    aethernet_group_voice_service_t *svc,
    const uint8_t                *call_id,    // 16 bytes
    const uint8_t                *audio,
    size_t                        audio_len,
    int                           is_silence,
    uint32_t                      key_generation
);

int aethernet_group_voice_handle_packet(aethernet_group_voice_service_t *svc, const aethernet_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_VOICE_H
