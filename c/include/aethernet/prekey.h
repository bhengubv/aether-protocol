// SPDX-License-Identifier: MIT
// Aether mesh pre-key exchange — directed PreKeyRequest (25) / PreKeyResponse (26) transport.
//
// Closes the "how does a peer get another peer's PreKeyBundle over the mesh" gap the messaging
// layer previously left out-of-band. A node publishes its current bundle via set_local_bundle
// (the host produces it with the Signal service); a peer asks for it with request_bundle; the
// responder replies with its bundle; the requester caches it and fires a callback. This is the
// mesh TRANSPORT of bundles only — the host performs the actual X3DH by feeding a received bundle
// to the Signal service (Signal-canonical: no key agreement happens here).

#ifndef AETHERNET_PREKEY_H
#define AETHERNET_PREKEY_H

#include <stdbool.h>
#include <stdint.h>

#include "aethernet/protocol.h"
#include "aethernet/routing.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * A published pre-key bundle carried over the mesh. Mirrors the C# AetherNet.Security.Models
 * PreKeyBundle record (constructor order Uhid, IdentityKey, IdentityKeyX25519, PreKeyId, PreKey,
 * SignedPreKeyId, SignedPreKey, SignedPreKeySignature). This is the transport DTO for the exchange
 * service; it is intentionally distinct from the Signal-layer aethernet_pre_key_bundle_t (which
 * models the crypto layer's has_pre_key/int32 shape). The host projects between the two.
 *
 * `uhid` is an owned, null-terminated heap string (freed by aethernet_pre_key_bundle_free). All
 * key material is fixed-size raw bytes — the wire encodes them as STANDARD base64 (RFC 4648).
 */
typedef struct {
    char   *uhid;                          // owned, null-terminated; bundle owner UHID
    uint8_t identity_key[32];              // long-term Ed25519 identity public key
    uint8_t identity_key_x25519[32];       // long-term X25519 identity public key (X3DH DH2)
    int32_t pre_key_id;                    // one-time pre-key id
    uint8_t pre_key[32];                   // one-time pre-key public key (X3DH DH4)
    int32_t signed_pre_key_id;             // signed pre-key id
    uint8_t signed_pre_key[32];            // signed pre-key public key (X3DH DH1/DH3)
    uint8_t signed_pre_key_signature[64];  // Ed25519 signature over signed_pre_key
} aethernet_pre_key_exchange_bundle_t;

/**
 * Free the owned `uhid` inside a transport bundle and zero the struct. Does NOT free the struct
 * itself (caller owns the allocation). Safe on a NULL argument or an already-zeroed bundle.
 */
void aethernet_pre_key_bundle_free(aethernet_pre_key_exchange_bundle_t *bundle);

/**
 * Serialize a PreKeyRequest (PacketType 25) payload to canonical UTF-8 JSON:
 *   {"request_id":"<uuid>","requester_uhid":"<string>"}
 * Field order request_id, requester_uhid, no whitespace, lowercase-dashed 36-char UUID. This is the
 * cross-language byte-identity gate (fixtures/prekey/vectors.json). `request_id` is a 16-byte UUID.
 * On success writes a heap-allocated buffer to *out_json (null-terminated just past *out_len) and
 * its length to *out_len, returns true. Caller owns *out_json and frees it with free(). Returns
 * false on allocation failure, a NULL required pointer, or a NULL/over-long requester_uhid.
 */
bool aethernet_pre_key_request_payload_serialize(const uint8_t request_id[AETHERNET_PACKET_ID_SIZE],
                                                 const char *requester_uhid,
                                                 uint8_t **out_json,
                                                 uint32_t *out_len);

/**
 * Serialize a PreKeyResponse (PacketType 26) payload to canonical UTF-8 JSON. Field order:
 *   request_id, uhid, identity_key, identity_key_x25519, pre_key_id, pre_key, signed_pre_key_id,
 *   signed_pre_key, signed_pre_key_signature
 * No whitespace, lowercase-dashed UUID, bare-int ids, every byte[] field STANDARD base64 (RFC 4648,
 * '+/' alphabet, '=' padding). Cross-language byte-identity gate (fixtures/prekey/vectors.json).
 * `request_id` is a 16-byte UUID; `bundle` supplies the remaining fields. On success writes a
 * heap-allocated buffer to *out_json (null-terminated just past *out_len) and its length to
 * *out_len, returns true. Caller owns *out_json and frees with free(). Returns false on allocation
 * failure or a NULL argument.
 */
bool aethernet_pre_key_response_payload_serialize(const uint8_t request_id[AETHERNET_PACKET_ID_SIZE],
                                                  const aethernet_pre_key_exchange_bundle_t *bundle,
                                                  uint8_t **out_json,
                                                  uint32_t *out_len);

/**
 * A received pre-key bundle surfaced to the host when a PreKeyResponse arrives. Mirrors the C#
 * PreKeyBundleReceivedEventArgs. `request_id` is the id echoed from the original request (all-zero
 * if unsolicited). `from_uhid` is borrowed for the callback duration (the packet source); `bundle`
 * is borrowed too — copy anything you wish to retain past the callback.
 */
typedef struct {
    uint8_t                                    request_id[AETHERNET_PACKET_ID_SIZE]; // echoed request id
    const char                                *from_uhid;  // borrowed; peer that sent the bundle
    const aethernet_pre_key_exchange_bundle_t *bundle;     // borrowed; the received bundle
} aethernet_pre_key_bundle_received_t;

/**
 * Bundle-received callback. Fired once per inbound PreKeyResponse. `event` is borrowed for the
 * callback duration — copy any fields to retain. Mirrors the C# BundleReceived event.
 */
typedef void (*aethernet_pre_key_bundle_received_cb)(const aethernet_pre_key_bundle_received_t *event,
                                                     void *user_data);

/**
 * Opaque pre-key exchange service handle. Sends directed PreKeyRequest/PreKeyResponse packets and
 * surfaces inbound bundles via the bundle-received callback. The service borrows `sender` — caller
 * keeps it alive for the service lifetime.
 *
 * Single-threaded reference impl; hosts pumping packets from multiple threads must wrap the service
 * in their own mutex (matches sos.c / channels.c / videocall.c).
 */
typedef struct aethernet_pre_key_exchange_service aethernet_pre_key_exchange_service_t;

aethernet_pre_key_exchange_service_t *aethernet_pre_key_exchange_service_new(aethernet_mesh_sender_t *sender);
void aethernet_pre_key_exchange_service_free(aethernet_pre_key_exchange_service_t *service);

/**
 * Set (or replace) this node's published bundle — served in reply to inbound PreKeyRequests. Copies
 * `bundle` (including its uhid string) into service-owned storage. Returns true on success, false if
 * `service`/`bundle`/`bundle->uhid` is NULL or the copy fails. Mirrors the C# SetLocalBundle.
 */
bool aethernet_pre_key_exchange_set_local_bundle(aethernet_pre_key_exchange_service_t *service,
                                                 const aethernet_pre_key_exchange_bundle_t *bundle);

/**
 * Copy this node's currently-published local bundle into `out`. Returns true if a bundle is set
 * (out receives an owned copy — free with aethernet_pre_key_bundle_free), false if none is set or on
 * a NULL argument. Mirrors the C# GetLocalBundle.
 */
bool aethernet_pre_key_exchange_get_local_bundle(const aethernet_pre_key_exchange_service_t *service,
                                                 aethernet_pre_key_exchange_bundle_t *out);

/**
 * Ask `peer_uhid` for its pre-key bundle: mint a fresh request id and directed-send a PreKeyRequest
 * (dest peer_uhid, TTL AETHERNET_DEFAULT_TTL) via sender->send. On success writes the new 16-byte
 * request id to `out_request_id` and returns true. Returns false if `service`/`peer_uhid`/
 * `out_request_id` is NULL, `peer_uhid` is empty, or delivery fails. Mirrors the C#
 * RequestBundleAsync (returns the minted request id).
 */
bool aethernet_pre_key_exchange_request_bundle(aethernet_pre_key_exchange_service_t *service,
                                               const char *peer_uhid,
                                               uint8_t out_request_id[AETHERNET_PACKET_ID_SIZE]);

/**
 * Process an inbound pre-key packet.
 *   - PreKeyRequest (25) with a local bundle set → directed-send a PreKeyResponse carrying the
 *     bundle to the requester (the payload's requester_uhid, else the packet source) and return true.
 *   - PreKeyRequest (25) with no local bundle set → return false, send nothing.
 *   - PreKeyResponse (26) → cache the peer bundle by uhid and fire the bundle-received callback,
 *     return true.
 * Returns false for the wrong packet type, a malformed payload, or a NULL argument. Mirrors the C#
 * HandleAsync.
 */
bool aethernet_pre_key_exchange_handle_packet(aethernet_pre_key_exchange_service_t *service,
                                              const aethernet_mesh_packet_t *packet);

/**
 * Copy the most-recently-received bundle for `uhid` into `out`. Returns true if one is cached (out
 * receives an owned copy — free with aethernet_pre_key_bundle_free), false if none is cached or on a
 * NULL argument. Mirrors the C# GetReceivedBundle.
 */
bool aethernet_pre_key_exchange_get_received_bundle(const aethernet_pre_key_exchange_service_t *service,
                                                    const char *uhid,
                                                    aethernet_pre_key_exchange_bundle_t *out);

/**
 * Set the bundle-received callback (fired on each inbound PreKeyResponse). Pass NULL to clear.
 * Mirrors wiring the C# BundleReceived event handler.
 */
void aethernet_pre_key_exchange_set_bundle_received_cb(aethernet_pre_key_exchange_service_t *service,
                                                       aethernet_pre_key_bundle_received_cb cb,
                                                       void *user_data);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_PREKEY_H
