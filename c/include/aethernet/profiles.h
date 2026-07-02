// SPDX-License-Identifier: MIT
// Aether ProfileSync — directed peer profile metadata exchange (PacketType 23).

#ifndef AETHERNET_PROFILES_H
#define AETHERNET_PROFILES_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A peer (or local) profile. Mirrors the C# ProfileSyncPayload. All string fields are always present
 * (empty string when unset, never NULL) — matching the wire contract where no field is omitted. The
 * owned string fields are managed by the profile service; callbacks and getters that return a
 * borrowed pointer are valid only until the next mutating call (see each function's contract).
 */
typedef struct {
    char   *uhid;           // owned; UHID this profile describes (the sender)
    char   *display_name;   // owned; human-readable display name (empty if unset)
    char   *avatar_ref;     // owned; content-addressed avatar ref (e.g. "blake3:…"), empty if none
    char   *status_message; // owned; free-text status/presence (empty if unset)
    int64_t updated_at_ms;  // owner-stamped Unix-ms last-update time
} aethernet_profile_t;

/**
 * Serialize a ProfileSyncPayload (PacketType 23) to canonical UTF-8 JSON:
 *   {"uhid":"<s>","display_name":"<s>","avatar_ref":"<s>","status_message":"<s>","updated_at_ms":<int>}
 * snake_case keys, field order uhid, display_name, avatar_ref, status_message, updated_at_ms, no
 * whitespace, updated_at_ms a bare integer, all string fields always present (empty when unset). This
 * is the cross-language byte-identity gate (fixtures/profiles/vectors.json) — every SDK must emit
 * exactly these bytes. String fields are ASCII in the vectors; the reference encoders interpolate
 * them verbatim (matching sos.c / heartbeat.c).
 *
 * A NULL string argument is treated as the empty string (mirrors the C# `?? string.Empty`). On
 * success, writes a heap-allocated buffer to *out_json (null-terminated just past *out_len; the
 * caller may treat [0, *out_len) as the JSON bytes) and its length to *out_len, and returns true.
 * The caller owns *out_json and frees it with free(). Returns false on allocation failure or if
 * out_json / out_len is NULL.
 */
bool aethernet_profile_payload_serialize(const char *uhid,
                                         const char *display_name,
                                         const char *avatar_ref,
                                         const char *status_message,
                                         int64_t updated_at_ms,
                                         uint8_t **out_json,
                                         uint32_t *out_len);

/**
 * Opaque profile service handle. Holds this node's local profile and a cache of peer profiles keyed
 * by UHID. The service borrows `sender` — caller keeps it alive for the service lifetime.
 *
 * Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
 * in their own mutex (matches sos.c / heartbeat.c).
 */
typedef struct aethernet_profile_service aethernet_profile_service_t;

aethernet_profile_service_t *aethernet_profile_service_new(aethernet_mesh_sender_t *sender);
void aethernet_profile_service_free(aethernet_profile_service_t *service);

/**
 * Set this node's own profile (uhid is taken from the sender's local UHID; stamps updated_at_ms to
 * now). NULL string arguments are stored as empty strings. No-op if `service` is NULL.
 */
void aethernet_profile_set_local(aethernet_profile_service_t *service,
                                 const char *display_name,
                                 const char *avatar_ref,
                                 const char *status_message);

/**
 * Borrowed pointer to this node's current local profile, valid until the next
 * aethernet_profile_set_local call (or NULL if `service` is NULL). Do not free.
 */
const aethernet_profile_t *aethernet_profile_get_local(const aethernet_profile_service_t *service);

/**
 * Send this node's local profile directly (unicast) to `peer_uhid` via sender->send — NOT broadcast
 * (a profile is directed to avoid leaking identity metadata to the whole mesh). Best-effort. Returns
 * true if the sender reported delivery, false if `service`/`peer_uhid` is NULL, `peer_uhid` is empty,
 * the sender wired no directed send, serialization failed, or delivery failed.
 */
bool aethernet_profile_publish_to(aethernet_profile_service_t *service, const char *peer_uhid);

/**
 * Process an inbound ProfileSync packet: cache the sender's profile (keyed by its uhid) and fire the
 * profile-updated callback. Returns false for the wrong packet type, a malformed payload, a NULL
 * argument, an empty uhid, or our own profile echoed back; true otherwise.
 */
bool aethernet_profile_handle_packet(aethernet_profile_service_t *service,
                                     const aethernet_mesh_packet_t *packet);

/**
 * Borrowed pointer to the cached profile for `uhid`, or NULL if none is known. Valid until the next
 * mutating call (a subsequent aethernet_profile_handle_packet refreshing this uhid, or service free).
 * Do not free.
 */
const aethernet_profile_t *aethernet_profile_get(const aethernet_profile_service_t *service,
                                                 const char *uhid);

/**
 * Snapshot of every peer profile this node has cached. Writes a heap-allocated array of
 * `aethernet_profile_t` (deep copies, each with its own owned strings) to *out_profiles and the count
 * to *out_count, returning the count (0 on empty, -1 on error). The caller owns the array and frees
 * it with aethernet_profile_list_free().
 */
int aethernet_profile_get_known(const aethernet_profile_service_t *service,
                                aethernet_profile_t **out_profiles,
                                int *out_count);

/**
 * Free an array returned by aethernet_profile_get_known, including each entry's owned strings. Safe
 * to call with NULL / count 0.
 */
void aethernet_profile_list_free(aethernet_profile_t *profiles, int count);

/**
 * Profile-updated callback. Fired once per received/refreshed peer profile. `profile` is borrowed
 * for the callback duration — copy any fields to retain. Mirrors the C# ProfileUpdated event.
 */
typedef void (*aethernet_profile_updated_cb)(const aethernet_profile_t *profile, void *user_data);

void aethernet_profile_set_updated_cb(aethernet_profile_service_t *service,
                                      aethernet_profile_updated_cb cb,
                                      void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_PROFILES_H
