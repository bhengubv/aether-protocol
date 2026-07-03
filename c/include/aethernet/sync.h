// SPDX-License-Identifier: MIT
// Decentralised multi-device sync (no server): SyncRecord binary envelope,
// deterministic last-write-wins reconciliation, and signed DeviceLink membership.

#ifndef AETHERNET_SYNC_H
#define AETHERNET_SYNC_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Decentralised multi-device sync for an AetherNet identity, with no server and
 * no coordinator. Three pieces, all byte-identical across every AetherNet SDK
 * (verified against fixtures/sync/vectors.json), a faithful mirror of the C#
 * reference under src/AetherNet.Security/Sync/:
 *
 *   - SyncRecord — one state change to a synced item (a message, a read-marker,
 *     a deletion) emitted by one of a user's devices and gossiped to that user's
 *     other devices so they all converge. The payload is already E2E-encrypted,
 *     so any relaying node learns nothing.
 *   - Reconcile — deterministic last-write-wins: every device that receives the
 *     same set of records (in any order, over any path) picks the identical
 *     winner per item.
 *   - DeviceLink — a signed device-membership record: the user's long-term
 *     Ed25519 identity key signs a new device's public key; every other device
 *     verifies that signature to admit the newcomer into the "self" device set.
 *
 * Wire (all integers little-endian; strings are a u16 byte-length + UTF-8;
 * record_id is 16 bytes big-endian, i.e. the UUID as written):
 *
 *   SyncRecord: version(u8=1) · record_id(16 BE) · op(u8) · logical_clock(i64 LE)
 *     · created_at_ms(i64 LE) · device_id(u16 len + utf8) · item_id(u16 len + utf8)
 *     · encrypted_payload(i32 len + bytes).
 *
 *   DeviceLink signed body: version(u8=1) · device_id(u16 len + utf8)
 *     · device_public_key(32) · issued_at_ms(i64 LE). The serialized link is the
 *     signed body followed by the 64-byte Ed25519 signature.
 *
 * Crypto reuses the SDK's existing libsodium Ed25519 (aethernet_ed25519_sign /
 * aethernet_ed25519_verify, src/security.c). Ed25519 signatures are
 * deterministic, so a link's serialized bytes are identical across SDKs.
 */

/* ─── SyncRecord ────────────────────────────────────────────────────────── */

/** Wire format version for a SyncRecord; readers reject any other value. */
#define AETHERNET_SYNC_RECORD_FORMAT_VERSION 0x01

/** Length of a record id in bytes (RFC-4122 UUID, big-endian on the wire). */
#define AETHERNET_SYNC_RECORD_ID_SIZE 16U

/** The kind of state change a SyncRecord carries. */
typedef enum {
    AETHERNET_SYNC_OP_UPSERT = 0, /* create or update the item */
    AETHERNET_SYNC_OP_DELETE = 1, /* delete the item */
    AETHERNET_SYNC_OP_READ   = 2, /* mark the item read (read-state sync) */
} aethernet_sync_op_t;

/**
 * One state change to a synced item. String fields are NUL-terminated and owned
 * by the record; encrypted_payload is a length-counted buffer (may be NULL when
 * encrypted_payload_len is 0).
 */
typedef struct {
    uint8_t  record_id[AETHERNET_SYNC_RECORD_ID_SIZE];
    char    *device_id;             /* owned; the emitting device */
    uint8_t  op;                    /* aethernet_sync_op_t */
    char    *item_id;               /* owned; the sync key */
    int64_t  logical_clock;         /* device's monotonic counter at emit time */
    int64_t  created_at_ms;         /* wall-clock time (Unix ms) at creation */
    uint8_t *encrypted_payload;     /* owned; opaque E2E ciphertext, may be NULL */
    uint32_t encrypted_payload_len;
} aethernet_sync_record_t;

/**
 * Serialize a record to its canonical bytes. On success writes a freshly
 * malloc'd buffer via *out (caller frees) and its length via *out_len, and
 * returns true. Returns false on a NULL argument or allocation failure.
 */
bool aethernet_sync_record_serialize(const aethernet_sync_record_t *record,
                                     uint8_t **out, uint32_t *out_len);

/**
 * Parse canonical bytes back into a record, validating framing (version, op<=2,
 * string and payload lengths). On success writes a freshly-allocated record via
 * *out_record (free with aethernet_sync_record_free) and returns true. Returns
 * false on any framing error.
 */
bool aethernet_sync_record_deserialize(const uint8_t *data, uint32_t len,
                                       aethernet_sync_record_t **out_record);

/** Free a record allocated by aethernet_sync_record_deserialize. */
void aethernet_sync_record_free(aethernet_sync_record_t *record);

/* ─── Reconcile (deterministic last-write-wins) ─────────────────────────── */

/**
 * Order two records: >0 if `a` wins, <0 if `b` wins, 0 only if they are the same
 * record. Total order (later wins): created_at_ms, then logical_clock, then
 * device_id (ordinal, via strcmp of the UTF-8 bytes), then record_id bytes (via
 * memcmp). The last two are arbitrary-but-stable tie-breakers so genuinely
 * concurrent writes resolve identically on every device.
 */
int aethernet_sync_compare(const aethernet_sync_record_t *a,
                           const aethernet_sync_record_t *b);

/**
 * The winning record among `records` (all assumed to be for one item). Returns a
 * pointer into the caller-owned array (never copies), or NULL if count == 0 or
 * records is NULL.
 */
const aethernet_sync_record_t *aethernet_sync_winner(
    const aethernet_sync_record_t *records, size_t count);

/* ─── DeviceLink (signed device membership) ─────────────────────────────── */

/** Wire format version for a DeviceLink; readers reject any other value. */
#define AETHERNET_SYNC_DEVICE_LINK_FORMAT_VERSION 0x01

/** Ed25519 device / identity public key length in bytes. */
#define AETHERNET_SYNC_DEVICE_KEY_SIZE 32U

/** Ed25519 identity private seed length in bytes. */
#define AETHERNET_SYNC_IDENTITY_SEED_SIZE 32U

/** Ed25519 signature length in bytes. */
#define AETHERNET_SYNC_SIGNATURE_SIZE 64U

/**
 * A signed device-membership record. device_id is NUL-terminated and owned by
 * the link; the two key/signature fields are fixed-size inline buffers.
 */
typedef struct {
    char    *device_id;                                          /* owned */
    uint8_t  device_public_key[AETHERNET_SYNC_DEVICE_KEY_SIZE];
    int64_t  issued_at_ms;
    uint8_t  signature[AETHERNET_SYNC_SIGNATURE_SIZE];
} aethernet_device_link_t;

/**
 * Build the canonical signed body (everything but the signature): version ·
 * device_id(u16 len + utf8) · device_public_key(32) · issued_at_ms(i64 LE).
 * Signer and verifier operate over exactly these bytes. On success writes a
 * freshly malloc'd buffer via *out (caller frees) and its length via *out_len.
 * Returns false on NULL args or allocation failure.
 */
bool aethernet_device_link_signed_body(const char *device_id,
                                       const uint8_t *device_public_key,
                                       int64_t issued_at_ms,
                                       uint8_t **out, uint32_t *out_len);

/**
 * Create a device-link signed by the user's 32-byte Ed25519 identity seed. The
 * result's device_id is a fresh owned copy. On success writes the link via
 * *out_link (free device_id with free(); the struct itself is caller-owned/
 * stack-allocatable) and returns true. Returns false on NULL args, a signing
 * failure, or allocation failure.
 */
bool aethernet_device_link_create(const char *device_id,
                                  const uint8_t *device_public_key,
                                  int64_t issued_at_ms,
                                  const uint8_t *identity_seed,
                                  aethernet_device_link_t *out_link);

/**
 * True if `link` was signed by the identity behind `identity_public` (32 bytes)
 * — i.e. this device belongs to that user. Recomputes the signed body and
 * verifies the Ed25519 signature over it.
 */
bool aethernet_device_link_verify(const aethernet_device_link_t *link,
                                  const uint8_t *identity_public);

/**
 * Serialize a link as its signed body followed by the 64-byte signature. On
 * success writes a freshly malloc'd buffer via *out (caller frees) and its
 * length via *out_len. Returns false on NULL args or allocation failure.
 */
bool aethernet_device_link_serialize(const aethernet_device_link_t *link,
                                     uint8_t **out, uint32_t *out_len);

/**
 * Parse a serialized link, validating framing. On success writes the link via
 * *out_link (its device_id is a fresh owned copy — free with free()) and returns
 * true. Returns false on any framing error.
 */
bool aethernet_device_link_deserialize(const uint8_t *data, uint32_t len,
                                       aethernet_device_link_t *out_link);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_SYNC_H
