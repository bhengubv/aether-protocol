// SPDX-License-Identifier: MIT
// Aether video call-control — directed ring/accept/decline/hangup signalling (PacketType 27).

#ifndef AETHERNET_VIDEOCALL_H
#define AETHERNET_VIDEOCALL_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A video call-control signal surfaced to the host when one arrives from a peer. Mirrors the C#
 * VideoCallStateChanged. This is the caller-intent layer (ring/accept/decline/hangup), distinct from
 * the media plane (SDP/ICE negotiation + frames) carried by VideoSignaling / VideoFrame and handled
 * by the streaming VideoCallService. The string fields are borrowed for the callback duration — copy
 * anything you wish to retain.
 */
typedef struct {
    uint8_t call_id[AETHERNET_PACKET_ID_SIZE]; // 16-byte UUID of the call
    char   *action;                            // borrowed; "ring" / "accept" / "decline" / "hangup"
    char   *from_uhid;                         // borrowed; UHID of the peer that sent the signal
} aethernet_video_call_state_changed_t;

/**
 * Serialize a VideoCallControlPayload (PacketType 27) to canonical UTF-8 JSON:
 *   {"call_id":"<uuid>","action":"<verb>","sent_at_ms":<int>}
 * snake_case keys, field order call_id, action, sent_at_ms, no whitespace, lowercase-dashed 36-char
 * UUID, sent_at_ms a bare integer, action an ASCII verb. This is the cross-language byte-identity gate
 * (fixtures/videocall/vectors.json) — every SDK must emit exactly these bytes. The action string is
 * interpolated verbatim (matching sos.c / channels.c), so callers must not pass an action containing
 * characters that require JSON escaping if byte-identity with other SDKs is required.
 *
 * `call_id` is a 16-byte UUID. On success, writes a heap-allocated buffer to *out_json
 * (null-terminated just past *out_len; the caller may treat [0, *out_len) as the JSON bytes) and its
 * length to *out_len, and returns true. The caller owns *out_json and frees it with free(). Returns
 * false on allocation failure or if any required pointer is NULL.
 */
bool aethernet_video_call_control_payload_serialize(const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                                    const char *action,
                                                    int64_t sent_at_ms,
                                                    uint8_t **out_json,
                                                    uint32_t *out_len);

/**
 * Opaque video call-control service handle. Sends directed VideoCall control signals and surfaces
 * inbound ones via the call-state-changed callback. The service borrows `sender` — caller keeps it
 * alive for the service lifetime.
 *
 * Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
 * in their own mutex (matches sos.c / channels.c).
 */
typedef struct aethernet_video_call_control_service aethernet_video_call_control_service_t;

aethernet_video_call_control_service_t *aethernet_video_call_control_service_new(aethernet_mesh_sender_t *sender);
void aethernet_video_call_control_service_free(aethernet_video_call_control_service_t *service);

/**
 * Ring `peer_uhid`: mint a fresh call id and directed-send a "ring" VideoCall packet (dest peer_uhid,
 * TTL AETHERNET_DEFAULT_TTL) via sender->send. On success writes the new 16-byte call id to
 * `out_call_id` and returns true. Returns false if `service`/`peer_uhid`/`out_call_id` is NULL,
 * `peer_uhid` is empty, or delivery fails. Mirrors the C# RingAsync (returns the minted call id).
 */
bool aethernet_video_call_control_ring(aethernet_video_call_control_service_t *service,
                                       const char *peer_uhid,
                                       uint8_t out_call_id[AETHERNET_PACKET_ID_SIZE]);

/**
 * Directed-send an "accept" for `call_id` to `peer_uhid`. `call_id` is a 16-byte UUID. Returns the
 * delivery result from sender->send (true if delivered). Returns false if `service`/`peer_uhid`/
 * `call_id` is NULL or `peer_uhid` is empty. Mirrors the C# AcceptAsync.
 */
bool aethernet_video_call_control_accept(aethernet_video_call_control_service_t *service,
                                         const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                         const char *peer_uhid);

/**
 * Directed-send a "decline" for `call_id` to `peer_uhid`. See aethernet_video_call_control_accept.
 * Mirrors the C# DeclineAsync.
 */
bool aethernet_video_call_control_decline(aethernet_video_call_control_service_t *service,
                                          const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                          const char *peer_uhid);

/**
 * Directed-send a "hangup" for `call_id` to `peer_uhid`. See aethernet_video_call_control_accept.
 * Mirrors the C# HangupAsync.
 */
bool aethernet_video_call_control_hangup(aethernet_video_call_control_service_t *service,
                                         const uint8_t call_id[AETHERNET_PACKET_ID_SIZE],
                                         const char *peer_uhid);

/**
 * Process an inbound VideoCall (PacketType 27) packet: parse the payload (call_id / action /
 * sent_at_ms) via the vendored cJSON and fire the call-state-changed callback with the call id, action
 * verb, and the packet's source UHID as the originating peer. Returns false for the wrong packet type,
 * a malformed payload (missing/empty action or an unparseable call id), or a NULL argument; true
 * otherwise. Mirrors the C# HandleAsync (wrong type / malformed → false, else raise CallStateChanged).
 */
bool aethernet_video_call_control_handle_packet(aethernet_video_call_control_service_t *service,
                                                const aethernet_mesh_packet_t *packet);

/**
 * Call-state-changed callback. Fired once per inbound VideoCall control signal. `event` is borrowed
 * for the callback duration — copy any fields to retain. Mirrors the C# CallStateChanged event.
 */
typedef void (*aethernet_video_call_state_changed_cb)(const aethernet_video_call_state_changed_t *event,
                                                      void *user_data);

void aethernet_video_call_control_set_state_changed_cb(aethernet_video_call_control_service_t *service,
                                                       aethernet_video_call_state_changed_cb cb,
                                                       void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_VIDEOCALL_H
