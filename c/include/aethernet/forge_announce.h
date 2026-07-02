// SPDX-License-Identifier: MIT
// aether-forge announce WIRE binding — ForgeAnnounce (PacketType 41) transport.
//
// Thin mesh binding over the aether-forge extension: a node broadcasts this when it caches a new
// package artifact so mesh peers with the aethernet.forge/v1 capability learn where the artifact lives,
// and surfaces inbound announcements via a callback (the host records them in its
// aethernet_forge_service). Transport only. Mirrors the green C# ForgeAnnounceService.
//
// The wire payload is encoded with snprintf (byte-identical to the C# System.Text.Json output — field
// order package_id, content_hash, size_bytes, announced_at_ms; size_bytes + announced_at_ms bare
// int64) and decoded on receive with the vendored cJSON. Byte-identity gate: fixtures/forge/vectors.json.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service in
// their own mutex (matches sos.c / channels.c / videocall.c / prekey.c).

#ifndef AETHERNET_FORGE_ANNOUNCE_H
#define AETHERNET_FORGE_ANNOUNCE_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A ForgeAnnounce announcement, both the broadcast input and the received-callback payload. All owned
 * pointer fields are borrowed for the callback duration — copy anything you wish to retain.
 * `package_id` is "ecosystem:name@version", e.g. "npm:react@18.2.0". `content_hash` may be empty.
 */
typedef struct {
    const char *package_id;     // borrowed; never NULL on a received announcement
    const char *content_hash;   // borrowed; may be "" (empty)
    int64_t     size_bytes;
    int64_t     announced_at_ms;
} aethernet_forge_announce_t;

/**
 * Serialize a ForgeAnnounce (PacketType 41) payload to canonical UTF-8 JSON. Field order:
 *   package_id, content_hash, size_bytes, announced_at_ms
 * No whitespace; size_bytes + announced_at_ms are bare int64s. Cross-language byte-identity gate
 * (fixtures/forge/vectors.json). `content_hash` NULL is emitted as "" (mirrors the C# `?? string.Empty`).
 * On success writes a heap-allocated buffer to *out_json (null-terminated just past *out_len) and its
 * length to *out_len, returns true. Caller owns *out_json and frees it with free(). Returns false on
 * allocation failure or a NULL package_id / out pointer.
 */
bool aethernet_forge_announce_payload_serialize(const char *package_id,
                                                const char *content_hash,
                                                int64_t size_bytes,
                                                int64_t announced_at_ms,
                                                uint8_t **out_json,
                                                uint32_t *out_len);

/**
 * Announce-received callback. Fired once per inbound ForgeAnnounce. `announce` is borrowed for the
 * callback duration — copy any fields to retain. Mirrors the C# AnnounceReceived event.
 */
typedef void (*aethernet_forge_announce_received_cb)(const aethernet_forge_announce_t *announce,
                                                     void *user_data);

/**
 * Opaque ForgeAnnounce wire-binding handle. Broadcasts ForgeAnnounce packets and surfaces inbound
 * announcements via the received callback. The service borrows `sender` — caller keeps it alive for the
 * service lifetime.
 */
typedef struct aethernet_forge_announce_service aethernet_forge_announce_service_t;

aethernet_forge_announce_service_t *aethernet_forge_announce_service_new(aethernet_mesh_sender_t *sender);
void aethernet_forge_announce_service_free(aethernet_forge_announce_service_t *service);

/**
 * Set the announce-received callback (fired on each inbound ForgeAnnounce). Pass NULL to clear.
 * Mirrors wiring the C# AnnounceReceived event handler.
 */
void aethernet_forge_announce_set_received_cb(aethernet_forge_announce_service_t *service,
                                              aethernet_forge_announce_received_cb cb,
                                              void *user_data);

/**
 * Announce a cached artifact to mesh peers: build a ForgeAnnounce packet (source local UHID, dest "*",
 * TTL AETHERNET_DEFAULT_TTL) carrying the canonical JSON payload and broadcast it via sender->broadcast.
 * On success writes the fan-out count (peers reached) to *out_count if non-NULL and returns true.
 * Returns false if `service`/`package_id` is NULL, `package_id` is empty (mirrors the C#
 * ArgumentException.ThrowIfNullOrEmpty), the payload fails to encode, or the host wired no broadcast.
 * `content_hash` NULL is treated as empty. Mirrors the C# BroadcastAsync.
 */
bool aethernet_forge_announce_broadcast(aethernet_forge_announce_service_t *service,
                                        const char *package_id,
                                        const char *content_hash,
                                        int64_t size_bytes,
                                        int64_t announced_at_ms,
                                        int *out_count);

/**
 * Process an inbound ForgeAnnounce packet: decode the payload and fire the received callback with the
 * decoded announcement, returning true. Returns false for the wrong packet type, a malformed payload, an
 * empty package_id (mirrors the C# string.IsNullOrEmpty(PackageId) guard), or a NULL argument. Mirrors
 * the C# HandleAsync.
 */
bool aethernet_forge_announce_handle_packet(aethernet_forge_announce_service_t *service,
                                            const aethernet_mesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_FORGE_ANNOUNCE_H
