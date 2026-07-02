// SPDX-License-Identifier: MIT
// aether-vault shard-request WIRE binding — VaultShardRequest (PacketType 42) transport.
//
// Thin mesh binding over the aether-vault extension: a node broadcasts this to ask the mesh for an
// erasure-coded shard it needs to recover a file, and surfaces inbound shard requests via a callback
// (the host answers from its aethernet_vault_service if it holds the shard). Transport only. Mirrors
// the green C# VaultShardRequestService.
//
// The wire payload is encoded with snprintf (byte-identical to the C# System.Text.Json output — field
// order shard_hash, requester_uhid) and decoded on receive with the vendored cJSON. Byte-identity gate:
// fixtures/vaultshard/vectors.json.
//
// Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service in
// their own mutex (matches sos.c / channels.c / videocall.c / prekey.c).

#ifndef AETHERNET_VAULTSHARD_H
#define AETHERNET_VAULTSHARD_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A vault shard request — the received-callback payload. Mirrors the C# VaultShardRequest event args
 * (ShardHash, RequesterUhid). Both string fields are borrowed for the callback duration — copy anything
 * you wish to retain.
 */
typedef struct {
    const char *shard_hash;      // borrowed; the requested shard hash, never NULL on a received request
    const char *requester_uhid;  // borrowed; UHID of the requesting peer, may be "" (empty)
} aethernet_vault_shard_request_t;

/**
 * Serialize a VaultShardRequest (PacketType 42) payload to canonical UTF-8 JSON. Field order:
 *   shard_hash, requester_uhid
 * No whitespace. Cross-language byte-identity gate (fixtures/vaultshard/vectors.json). On success writes
 * a heap-allocated buffer to *out_json (null-terminated just past *out_len) and its length to *out_len,
 * returns true. Caller owns *out_json and frees it with free(). Returns false on allocation failure or a
 * NULL argument.
 */
bool aethernet_vault_shard_request_payload_serialize(const char *shard_hash,
                                                     const char *requester_uhid,
                                                     uint8_t **out_json,
                                                     uint32_t *out_len);

/**
 * Shard-requested callback. Fired once per inbound VaultShardRequest. `request` is borrowed for the
 * callback duration — copy any fields to retain. Mirrors the C# ShardRequested event.
 */
typedef void (*aethernet_vault_shard_requested_cb)(const aethernet_vault_shard_request_t *request,
                                                   void *user_data);

/**
 * Opaque VaultShardRequest wire-binding handle. Broadcasts VaultShardRequest packets and surfaces
 * inbound requests via the shard-requested callback. The service borrows `sender` — caller keeps it
 * alive for the service lifetime.
 */
typedef struct aethernet_vault_shard_request_service aethernet_vault_shard_request_service_t;

aethernet_vault_shard_request_service_t *aethernet_vault_shard_request_service_new(aethernet_mesh_sender_t *sender);
void aethernet_vault_shard_request_service_free(aethernet_vault_shard_request_service_t *service);

/**
 * Set the shard-requested callback (fired on each inbound VaultShardRequest). Pass NULL to clear.
 * Mirrors wiring the C# ShardRequested event handler.
 */
void aethernet_vault_shard_request_set_requested_cb(aethernet_vault_shard_request_service_t *service,
                                                    aethernet_vault_shard_requested_cb cb,
                                                    void *user_data);

/**
 * Broadcast a request for `shard_hash`: build a VaultShardRequest packet (source local UHID, dest "*",
 * TTL AETHERNET_DEFAULT_TTL) carrying the canonical JSON payload — with requester_uhid set to the
 * sender's local UHID — and broadcast it via sender->broadcast. On success writes the fan-out count
 * (peers reached) to *out_count if non-NULL and returns true. Returns false if `service`/`shard_hash`
 * is NULL, `shard_hash` is empty (mirrors the C# ArgumentException.ThrowIfNullOrEmpty), the payload
 * fails to encode, or the host wired no broadcast. Mirrors the C# RequestShardAsync.
 */
bool aethernet_vault_shard_request_request_shard(aethernet_vault_shard_request_service_t *service,
                                                 const char *shard_hash,
                                                 int *out_count);

/**
 * Process an inbound VaultShardRequest packet: decode the payload and fire the shard-requested callback
 * with the decoded request, returning true. Returns false for the wrong packet type, a malformed
 * payload, an empty shard_hash (mirrors the C# string.IsNullOrEmpty(ShardHash) guard), or a NULL
 * argument. Mirrors the C# HandleAsync.
 */
bool aethernet_vault_shard_request_handle_packet(aethernet_vault_shard_request_service_t *service,
                                                 const aethernet_mesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_VAULTSHARD_H
