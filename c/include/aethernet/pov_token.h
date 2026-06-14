// SPDX-License-Identifier: MIT
// AetherNet Market — Proof-of-Vicinity token (PoVTokenExchange = 43).
//
// C port of AetherNet.Market.Models.PoVToken / PoVTransportType and
// AetherNet.Market.PoVTokenCodec + PoVTokenExchangeService, byte-identical to the
// C# reference and every other language implementation, proven against
// fixtures/market/pov_token_basic.json.
//
// A PoV token is issued by one node (the witness) to another (the subject) during a
// physical co-presence event. Both parties must countersign — this prevents
// unilateral forgery. The token is transmitted over a short-range transport
// (BLE/NFC/NearLink only) to prevent remote minting.
//
// CRYPTO: signatures are real Ed25519 over the canonical token body
// (aethernet_pov_token_build_signable = "SubjectUhid + TimestampTicks + Transport"),
// byte-identical across every language implementation. timestamp_ticks is .NET
// DateTime.Ticks (100ns intervals since 0001-01-01).

#ifndef AETHERNET_POV_TOKEN_H
#define AETHERNET_POV_TOKEN_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#include "aethernet/protocol.h"   /* aethernet_mesh_packet_t */

#ifdef __cplusplus
extern "C" {
#endif

/** Ed25519 signature length in bytes. */
#define AETHERNET_POV_SIGNATURE_SIZE 64U

/**
 * Transport used for a co-presence Proof-of-Vicinity exchange. Only short-range
 * transports are valid (prevents remote minting). Wire byte values are fixed.
 */
typedef enum {
    AETHERNET_POV_TRANSPORT_BLE      = 0, /**< Bluetooth Low Energy.        */
    AETHERNET_POV_TRANSPORT_NFC      = 1, /**< Near-Field Communication.    */
    AETHERNET_POV_TRANSPORT_NEARLINK = 2  /**< Huawei NearLink.             */
} aethernet_pov_transport_t;

/** Reports whether `transport` is a valid short-range PoV channel. */
bool aethernet_pov_transport_is_short_range(aethernet_pov_transport_t transport);

/** Returns the lowercase wire name of `transport` ("ble"/"nfc"/"nearlink"/"unknown"). */
const char *aethernet_pov_transport_name(aethernet_pov_transport_t transport);

/**
 * Proof-of-Vicinity token. The JSON wire form is snake_case, matching the C#
 * serializer. String fields are heap-owned NUL-terminated UTF-8. Each signature is
 * present (len == 64) once filled, otherwise len == 0.
 */
typedef struct {
    char    *witness_uhid;     /**< UHID of the node issuing the voucher.            */
    char    *subject_uhid;     /**< UHID of the node being vouched for.              */
    int64_t  timestamp_ticks;  /**< Co-presence event time as .NET DateTime.Ticks.   */
    aethernet_pov_transport_t transport_used; /**< Transport channel (short-range).  */

    uint8_t  witness_signature[AETHERNET_POV_SIGNATURE_SIZE]; /**< Ed25519 by witness. */
    size_t   witness_signature_len;                           /**< 0 or 64.           */

    uint8_t  subject_signature[AETHERNET_POV_SIGNATURE_SIZE]; /**< Ed25519 by subject. */
    size_t   subject_signature_len;                           /**< 0 or 64.           */
} aethernet_pov_token_t;

/** Initialise a token to empty (NULL strings, BLE transport, no signatures). */
void aethernet_pov_token_init(aethernet_pov_token_t *token);

/** Free heap-owned string fields and reset to empty. Does NOT free the struct. */
void aethernet_pov_token_free_fields(aethernet_pov_token_t *token);

/** Set witness_uhid (copies). Returns false on OOM. */
bool aethernet_pov_token_set_witness(aethernet_pov_token_t *token, const char *uhid);

/** Set subject_uhid (copies). Returns false on OOM. */
bool aethernet_pov_token_set_subject(aethernet_pov_token_t *token, const char *uhid);

/**
 * Build the canonical signable bytes for a PoV token body. The same layout is signed
 * by the witness (on issue) and counter-signed by the subject (on accept):
 *
 *   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1)
 *
 * Allocates the buffer via malloc; caller must free(). Writes the length to
 * *out_len. Returns the buffer, or NULL on OOM / NULL args.
 */
uint8_t *aethernet_pov_token_build_signable(const char *subject_uhid,
                                            int64_t timestamp_ticks,
                                            aethernet_pov_transport_t transport,
                                            size_t *out_len);

/** Convenience: build the signable body for `token`. */
uint8_t *aethernet_pov_token_signable(const aethernet_pov_token_t *token, size_t *out_len);

/**
 * Sign the token body with a 32-byte Ed25519 private key (seed) AS THE WITNESS,
 * storing the 64-byte signature in token->witness_signature. Returns true on success.
 */
bool aethernet_pov_token_sign_witness(aethernet_pov_token_t *token, const uint8_t *private_key);

/**
 * Sign the token body with a 32-byte Ed25519 private key (seed) AS THE SUBJECT
 * (countersignature), storing it in token->subject_signature. Returns true on success.
 */
bool aethernet_pov_token_sign_subject(aethernet_pov_token_t *token, const uint8_t *private_key);

/** Verify token->witness_signature over the body against a 32-byte public key. */
bool aethernet_pov_token_verify_witness(const aethernet_pov_token_t *token, const uint8_t *public_key);

/** Verify token->subject_signature over the body against a 32-byte public key. */
bool aethernet_pov_token_verify_subject(const aethernet_pov_token_t *token, const uint8_t *public_key);

/**
 * Serialise the token to its snake_case UTF-8 JSON wire form. Fields: witness_uhid,
 * subject_uhid, timestamp_ticks (number), transport_used (number), witness_signature
 * / subject_signature (lowercase hex strings, omitted when absent).
 *
 * Allocates a NUL-terminated string via malloc; caller must free(). Returns NULL on
 * failure.
 */
char *aethernet_pov_token_to_json(const aethernet_pov_token_t *token);

/**
 * Parse a snake_case UTF-8 JSON PoV token of length json_len into *out_token. The
 * caller must aethernet_pov_token_free_fields(out_token) on success. Returns true on
 * success, false on malformed JSON / missing required fields / OOM.
 */
bool aethernet_pov_token_from_json(const char *json, size_t json_len, aethernet_pov_token_t *out_token);

/* ─────────────────────────────────────────────────────────────────────────
 * PoVTokenExchangeService — issues and accepts on-mesh PoV tokens (type 43).
 *
 * Mirrors the C# / Go PoVTokenExchangeService. The directed, two-key
 * witness→subject co-presence proof. The transport/sign/verify surfaces are
 * supplied as function pointers; the service holds non-owning pointers plus a small
 * in-memory record of accepted tokens per subject (heap-owned, freed by
 * aethernet_pov_exchange_service_free_state()).
 * ──────────────────────────────────────────────────────────────────────── */

/** Returns the local node's UHID (NUL-terminated). May return NULL if uninitialised. */
typedef const char *(*aethernet_pov_local_uhid_fn)(void *user_data);

/** Sign the enclosing MeshPacket envelope in place. Returns true on success. */
typedef bool (*aethernet_pov_sign_packet_fn)(void *user_data, aethernet_mesh_packet_t *packet);

/**
 * Verify pkt's envelope signature against sender_public_key AND enforce freshness +
 * nonce replay-dedup. Returns true only for a fresh, correctly-signed, non-replayed
 * packet.
 */
typedef bool (*aethernet_pov_verify_packet_fn)(void *user_data, const aethernet_mesh_packet_t *packet, const uint8_t *sender_public_key);

/** Sign data with the local identity key into out_signature (64 bytes). Returns true on success. */
typedef bool (*aethernet_pov_identity_sign_fn)(void *user_data, const uint8_t *data, size_t data_len, uint8_t *out_signature);

/** Verify sig over data against public_key. Returns true iff valid. */
typedef bool (*aethernet_pov_identity_verify_fn)(void *user_data, const uint8_t *public_key, const uint8_t *data, size_t data_len, const uint8_t *sig);

/** Deliver packet directed toward subject_uhid (one short-range hop). Returns true on success. */
typedef bool (*aethernet_pov_send_fn)(void *user_data, const aethernet_mesh_packet_t *packet, const char *subject_uhid);

/** Optional notification fired once a counter-signed token has been recorded locally. */
typedef void (*aethernet_pov_on_token_received_fn)(void *user_data, const aethernet_pov_token_t *token);

/** Opaque per-subject token record store. */
typedef struct aethernet_pov_token_store aethernet_pov_token_store_t;

/** PoVTokenExchangeService — non-owning function-pointer surface + local token store. */
typedef struct {
    void *user_data;                                  /**< Passed to every callback.   */
    aethernet_pov_local_uhid_fn      local_uhid;      /**< Required.                    */
    aethernet_pov_sign_packet_fn     sign_packet;     /**< Required for issue.          */
    aethernet_pov_verify_packet_fn   verify_packet;   /**< Required for handle.         */
    aethernet_pov_identity_sign_fn   identity_sign;   /**< Required.                    */
    aethernet_pov_identity_verify_fn identity_verify; /**< Required for handle.         */
    aethernet_pov_send_fn            send;            /**< Required for issue.          */
    aethernet_pov_on_token_received_fn on_token_received; /**< Optional (may be NULL).  */
    aethernet_pov_token_store_t     *store;           /**< Owned local record; init sets it. */
} aethernet_pov_exchange_service_t;

/**
 * Initialise a PoVTokenExchangeService. Allocates the internal token store. Returns
 * true on success, false on allocation failure. Free the store with
 * aethernet_pov_exchange_service_free_state().
 */
bool aethernet_pov_exchange_service_init(aethernet_pov_exchange_service_t *svc, void *user_data);

/** Free the service's internal token store. Safe to call once after init. */
void aethernet_pov_exchange_service_free_state(aethernet_pov_exchange_service_t *svc);

/**
 * Mint a witness-signed PoV token for subject_uhid and send it directed (TTL 1) over
 * packet 43. Refuses to mint over a non-short-range transport or to vouch for itself.
 *
 * On success, if out_token != NULL, *out_token receives the issued token (with an
 * empty subject signature — the subject fills it on receipt); the caller must
 * aethernet_pov_token_free_fields(out_token). out_issued (may be NULL) is set to true
 * when a token was issued, false when issuance was refused.
 *
 * Returns true on success (including a refused issuance, with out_issued = false),
 * false only on an internal error (allocation / signing / send failure).
 */
bool aethernet_pov_exchange_service_issue(aethernet_pov_exchange_service_t *svc,
                                          const char *subject_uhid,
                                          aethernet_pov_transport_t transport,
                                          aethernet_pov_token_t *out_token,
                                          bool *out_issued);

/**
 * Process an inbound PoV exchange packet (type 43). Verifies the envelope (freshness
 * + replay), deserialises the token, verifies the witness Ed25519 signature against
 * sender_public_key, enforces the distinct-parties / addressed-to-us / not-self-echo
 * invariants, counter-signs as the subject, records the token, and fires
 * on_token_received.
 *
 * Returns true when the token was accepted, counter-signed, and recorded. Returns
 * false when the packet should be silently discarded (wrong type,
 * bad/stale/replayed envelope, malformed payload, self-echo, not addressed to us,
 * missing/invalid witness signature, witness == subject) or on an internal signing
 * error.
 */
bool aethernet_pov_exchange_service_handle(aethernet_pov_exchange_service_t *svc,
                                           const aethernet_mesh_packet_t *packet,
                                           const uint8_t *sender_public_key);

/** Number of distinct witnesses who have issued recorded tokens to `uhid`. */
int aethernet_pov_exchange_service_unique_witnesses(const aethernet_pov_exchange_service_t *svc, const char *uhid);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_POV_TOKEN_H */
