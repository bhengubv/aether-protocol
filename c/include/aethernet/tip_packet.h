// SPDX-License-Identifier: MIT
// AetherNet Incentive — generic relay-tip envelope (TipPacket = 24).
//
// C port of AetherNet.Incentive.TipPacketPayload + the MeshTipService send/receive
// flow, byte-identical to the C# reference and every other language implementation
// (Go/Rust/…), proven against fixtures/tipping/tip_packet_basic.json.
//
// This model is deliberately value-agnostic. The amount is a bare decimal STRING
// with NO units, NO policy, and NO settlement semantics attached at the protocol
// layer. The protocol carries the signal that one node wishes to credit another
// for some kind of relayed traffic; what (if anything) that signal is worth is
// entirely the host's business. A bare node accepts and relays the packet but
// settles nothing — only a host that has wired a settlement hook
// (aethernet_mesh_tip_settlement_fn, the SettleMeshTip analog) decides how to
// interpret the value.
//
// The payload is self-signed by the tipper: the signature is an Ed25519 signature
// over the canonical byte layout produced by aethernet_tip_packet_build_canonical().
// The signature binds the tipper, recipient, amount, traffic type, reference, and
// timestamp together so an intermediate relay cannot tamper with any field without
// invalidating it.

#ifndef AETHERNET_TIP_PACKET_H
#define AETHERNET_TIP_PACKET_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#include "aethernet/protocol.h"   /* aethernet_mesh_packet_t */

#ifdef __cplusplus
extern "C" {
#endif

/** Ed25519 signature length in bytes (re-stated for the best-effort inbound check). */
#define AETHERNET_TIP_SIGNATURE_SIZE 64U

/** Size in bytes of a .NET GUID reference id field in the canonical layout. */
#define AETHERNET_TIP_REFERENCE_ID_SIZE 16U

/**
 * Generic "value-earned" relay-tip envelope carried inside a TipPacket (24).
 *
 * The amount is carried as the INVARIANT decimal string (the .NET
 * decimal.ToString(InvariantCulture) round-trip form, e.g. "12.50", "0.0001",
 * "123456.789") — NOT a float. Keeping it a string is what makes the signed bytes
 * stable across locales and decimal scales without baking in any unit or
 * fixed-point assumption, and is required for byte-identity with the C# canonical
 * data.
 *
 * All string fields are owned heap copies (NUL-terminated UTF-8). reference_id is
 * a 16-byte fixed buffer in .NET GUID in-memory byte order; has_reference_id ==
 * false ⇒ the field is serialised as 16 zero bytes. signature_len is 0 until the
 * payload has been signed, then AETHERNET_TIP_SIGNATURE_SIZE.
 */
typedef struct {
    char    *tipper_uhid;       /**< UHID of the node offering the tip (the signer). */
    char    *recipient_uhid;    /**< UHID of the node the tip is addressed to.       */
    char    *amount;            /**< Invariant decimal string. NO unit / policy.     */
    char    *traffic_type;      /**< Free-form relayed-traffic tag. Opaque.          */

    bool     has_reference_id;  /**< false ⇒ reference_id serialises as 16 zero bytes. */
    uint8_t  reference_id[AETHERNET_TIP_REFERENCE_ID_SIZE]; /**< .NET GUID byte order. */

    int64_t  timestamp_unix_ms; /**< When the tipper created this payload (Unix ms).  */

    uint8_t  signature[AETHERNET_TIP_SIGNATURE_SIZE]; /**< Ed25519 sig over canonical. */
    size_t   signature_len;     /**< 0 until signed, then AETHERNET_TIP_SIGNATURE_SIZE. */
} aethernet_tip_packet_t;

/**
 * Initialise a tip packet to empty (all pointers NULL, no reference id, unsigned).
 * Use this before populating fields. Free owned memory with
 * aethernet_tip_packet_free_fields().
 */
void aethernet_tip_packet_init(aethernet_tip_packet_t *tip);

/**
 * Free the heap-owned string fields of a tip packet and reset it to empty. Safe to
 * call on a zero/inited struct or repeatedly. Does NOT free the struct itself.
 */
void aethernet_tip_packet_free_fields(aethernet_tip_packet_t *tip);

/**
 * Set tipper_uhid (copies the string). Returns false on allocation failure.
 */
bool aethernet_tip_packet_set_tipper(aethernet_tip_packet_t *tip, const char *uhid);

/**
 * Set recipient_uhid (copies the string). Returns false on allocation failure.
 */
bool aethernet_tip_packet_set_recipient(aethernet_tip_packet_t *tip, const char *uhid);

/**
 * Set amount as the invariant decimal string (copies). Returns false on OOM.
 */
bool aethernet_tip_packet_set_amount(aethernet_tip_packet_t *tip, const char *amount);

/**
 * Set traffic_type (copies the string). Returns false on allocation failure.
 */
bool aethernet_tip_packet_set_traffic_type(aethernet_tip_packet_t *tip, const char *traffic_type);

/**
 * Set the 16-byte reference id from .NET GUID in-memory byte order (copies). Pass
 * a 16-byte buffer; marks has_reference_id = true.
 */
void aethernet_tip_packet_set_reference_id(aethernet_tip_packet_t *tip, const uint8_t reference_id[16]);

/**
 * Parse a canonical 36-char dashed GUID string (e.g.
 * "11112222-3333-4444-5555-666677778888") into reference_id using .NET GUID
 * in-memory (mixed-endian) byte order, and set has_reference_id = true.
 *
 * Returns true on success, false on a malformed GUID string.
 */
bool aethernet_tip_packet_set_reference_id_guid(aethernet_tip_packet_t *tip, const char *guid);

/**
 * Build the canonical byte array that is signed/verified for this payload. The
 * signature field is excluded from the canonical data.
 *
 * Layout (little-endian lengths, matching the project's signable-data conventions):
 *   TipperLen(4 LE i32)    || Tipper(UTF-8)
 *   RecipientLen(4 LE i32) || Recipient(UTF-8)
 *   AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip decimal string)
 *   TrafficLen(4 LE i32)   || TrafficType(UTF-8)
 *   ReferenceId(16, all-zero GUID when absent, .NET in-memory byte order otherwise)
 *   TimestampUnixMs(8 LE i64)
 *
 * Allocates the buffer via malloc; caller must free(). Writes the length to
 * *out_len. Returns the buffer, or NULL on allocation failure / NULL args.
 */
uint8_t *aethernet_tip_packet_build_canonical(const aethernet_tip_packet_t *tip, size_t *out_len);

/**
 * Sign the payload's canonical bytes with a 32-byte Ed25519 private key (seed) and
 * store the 64-byte signature in tip->signature. Reuses aethernet_ed25519_sign().
 *
 * Returns true on success, false on canonical-build / signing failure.
 */
bool aethernet_tip_packet_sign(aethernet_tip_packet_t *tip, const uint8_t *private_key);

/**
 * Verify tip->signature over the payload's canonical bytes against a 32-byte
 * Ed25519 public key. Reuses aethernet_ed25519_verify().
 *
 * Returns true iff the signature is present (64 bytes) and verifies.
 */
bool aethernet_tip_packet_verify(const aethernet_tip_packet_t *tip, const uint8_t *public_key);

/**
 * Serialise the payload to its snake_case UTF-8 JSON wire form (the body carried
 * inside a TipPacket(24)). Fields: tipper_uhid, recipient_uhid, amount,
 * traffic_type, reference_id (canonical dashed GUID string or null), timestamp
 * (Unix ms number), signature (lowercase hex string, omitted when unsigned).
 *
 * Allocates a NUL-terminated string via malloc; caller must free(). Returns NULL
 * on failure.
 */
char *aethernet_tip_packet_to_json(const aethernet_tip_packet_t *tip);

/**
 * Parse a snake_case UTF-8 JSON tip payload of length json_len into *out_tip. The
 * caller must aethernet_tip_packet_free_fields(out_tip) on success.
 *
 * Returns true on success, false on malformed JSON / missing required fields /
 * allocation failure.
 */
bool aethernet_tip_packet_from_json(const char *json, size_t json_len, aethernet_tip_packet_t *out_tip);

/* ─────────────────────────────────────────────────────────────────────────
 * MeshTipService — sends and receives TipPacket(24) packets.
 *
 * Mirrors the C# MeshTipService / Go incentive.MeshTipService flow. The service
 * holds only non-owning pointers + a settlement hook; it owns no heap allocation
 * and may be embedded by value. The transport/sign/route surfaces are supplied as
 * function pointers so the protocol library stays transport-agnostic.
 * ──────────────────────────────────────────────────────────────────────── */

/**
 * Host settlement hook — the C analog of IAetherNetIncentiveProvider.SettleMeshTip.
 * Invoked for every inbound, well-formed tip payload. The default
 * (aethernet_mesh_tip_service_init with settle == NULL) is a no-op that settles
 * nothing. user_data is passed through verbatim. A non-zero return is logged by the
 * caller but never propagated to the wire — a settlement failure must not break
 * relaying.
 */
typedef int (*aethernet_mesh_tip_settlement_fn)(void *user_data, const aethernet_tip_packet_t *payload);

/** Returns the local node's UHID (NUL-terminated). Must not return NULL. */
typedef const char *(*aethernet_mesh_local_uhid_fn)(void *user_data);

/**
 * Sign the enclosing MeshPacket envelope in place (populate nonce/timestamp +
 * envelope signature). Returns true on success.
 */
typedef bool (*aethernet_mesh_sign_packet_fn)(void *user_data, aethernet_mesh_packet_t *packet);

/**
 * Sign data with the local identity key, writing a 64-byte Ed25519 signature to
 * out_signature. Returns true on success.
 */
typedef bool (*aethernet_mesh_identity_sign_fn)(void *user_data, const uint8_t *data, size_t data_len, uint8_t *out_signature);

/**
 * Deliver packet toward next_hop_uhid (unicast). Returns true on success.
 */
typedef bool (*aethernet_mesh_send_fn)(void *user_data, const aethernet_mesh_packet_t *packet, const char *next_hop_uhid);

/**
 * Broadcast packet to every directly-connected peer. Returns the fan-out count.
 */
typedef int (*aethernet_mesh_broadcast_fn)(void *user_data, const aethernet_mesh_packet_t *packet);

/**
 * Resolve a next hop toward destination_uhid. On success write the next-hop UHID to
 * out_next_hop (caller-allocated, out_cap bytes) and return true; return false to
 * fall back to broadcast.
 */
typedef bool (*aethernet_mesh_find_next_hop_fn)(void *user_data, const char *destination_uhid, char *out_next_hop, size_t out_cap);

/** MeshTipService — non-owning function-pointer surface + settlement hook. */
typedef struct {
    void *user_data;                              /**< Passed to every callback.   */
    aethernet_mesh_local_uhid_fn    local_uhid;   /**< Required.                    */
    aethernet_mesh_sign_packet_fn   sign_packet;  /**< Required for send.           */
    aethernet_mesh_identity_sign_fn identity_sign;/**< Required for send.           */
    aethernet_mesh_send_fn          send;         /**< Required.                    */
    aethernet_mesh_broadcast_fn     broadcast;    /**< Required.                    */
    aethernet_mesh_find_next_hop_fn find_next_hop;/**< NULL ⇒ always broadcast.     */
    aethernet_mesh_tip_settlement_fn settle;      /**< NULL ⇒ no-op settlement.     */
    int32_t default_ttl;                          /**< DefaultTtl (7).              */
} aethernet_mesh_tip_service_t;

/**
 * Initialise a MeshTipService. Pass NULL for settle to use the default no-op
 * settlement provider; pass NULL for find_next_hop to always broadcast. The
 * default TTL is set to AETHERNET_DEFAULT_TTL (7).
 */
void aethernet_mesh_tip_service_init(aethernet_mesh_tip_service_t *svc, void *user_data);

/**
 * Build, sign, and route a TipPacket(24) addressed to recipient_uhid. amount is the
 * caller's invariant decimal string verbatim — the protocol imposes NO policy on
 * it. reference_id may be NULL (no correlation id) or a 16-byte .NET-GUID-order
 * buffer.
 *
 * On success, if out_packet != NULL, *out_packet receives the signed MeshPacket
 * that was routed onto the mesh (caller owns it; free with aethernet_packet_free()).
 *
 * Returns true on success, false on allocation/sign/send failure.
 */
bool aethernet_mesh_tip_service_send(aethernet_mesh_tip_service_t *svc,
                                     const char *recipient_uhid,
                                     const char *amount,
                                     const char *traffic_type,
                                     const uint8_t *reference_id,
                                     int64_t timestamp_unix_ms,
                                     aethernet_mesh_packet_t **out_packet);

/**
 * Process an inbound TipPacket(24) received off the mesh.
 *
 * Workflow: verify type → deserialise payload → best-effort signature check (must
 * be present and exactly 64 bytes) → hand to the settlement hook → relay onward
 * toward the addressed recipient if this node is not the destination and the packet
 * may still be forwarded.
 *
 * Returns true when the payload was accepted and handed to the settlement provider.
 * Returns false when the packet should be silently discarded (wrong type, malformed
 * payload, missing/malformed signature) or on an internal relay send error.
 */
bool aethernet_mesh_tip_service_handle(aethernet_mesh_tip_service_t *svc,
                                       const aethernet_mesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_TIP_PACKET_H */
