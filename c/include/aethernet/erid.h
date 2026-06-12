// SPDX-License-Identifier: MIT
// Ephemeral Routing Id (ERID) — a rotating, key-derived wire address designed to replace the
// stable, phone-derived UHID on the public wire.
//
// A node's UHID is SHA-256(phone : deviceId : publicKey) — stable for the life of the install
// and carried in cleartext on every packet, so a passive observer can follow it indefinitely
// and attempt to confirm a suspected phone number by recomputing the hash. The ERID replaces
// it on the wire:
//
//     ERID(epoch) = base32( HMAC-SHA256(routingKey, epoch) )[0 .. length]
//
// where routingKey is SECRET (HKDF-SHA256 of the identity secret — never the public key) and
// epoch = floor(unixSeconds / epochSeconds). The epoch is encoded big-endian (8-byte signed
// int64) so every language port produces byte-identical HMAC input. Verified against
// fixtures/erid/vectors.json.

#ifndef AETHERNET_ERID_H
#define AETHERNET_ERID_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/** Default rotation window: 15 minutes, in seconds. */
#define AETHERNET_ERID_DEFAULT_EPOCH_SECONDS 900
/** Default ERID length in Crockford base-32 characters (16 × 5 = 80 bits). */
#define AETHERNET_ERID_DEFAULT_LENGTH 16
/** Maximum ERID length (SHA-256 = 256 bits = 51 base-32 chars). */
#define AETHERNET_ERID_MAX_LENGTH 51
/** Routing-key size in bytes. */
#define AETHERNET_ERID_ROUTING_KEY_SIZE 32

/* ─── Primitive ──────────────────────────────────────────────────────────── */

/**
 * Derive the 32-byte SECRET routing key from a node's identity secret via HKDF-SHA256
 * (RFC 5869, no salt, info = "aether-erid-routing-key-v1"). MUST be fed a secret — never a
 * public value, or the rotation schedule becomes computable by anyone.
 *
 * out_routing_key: AETHERNET_ERID_ROUTING_KEY_SIZE bytes (caller allocates).
 * Returns true on success, false on NULL/empty inputs.
 */
bool aethernet_erid_derive_routing_key(const uint8_t *identity_secret,
                                       size_t identity_secret_len,
                                       uint8_t *out_routing_key);

/**
 * The epoch (rotation-window index) that contains unix_seconds. Negative unix_seconds clamp
 * to 0. Returns -1 if epoch_seconds <= 0.
 */
int64_t aethernet_erid_epoch_for(int64_t unix_seconds, int64_t epoch_seconds);

/**
 * Derive the ERID for the epoch containing unix_seconds. Writes a NUL-terminated string of
 * `length` Crockford base-32 chars into out (caller allocates >= length + 1 bytes).
 * Returns true on success.
 */
bool aethernet_erid_derive(const uint8_t *routing_key, size_t routing_key_len,
                           int64_t unix_seconds, int64_t epoch_seconds, int length,
                           char *out, size_t out_size);

/**
 * Derive the ERID for an explicit epoch number. The epoch is encoded big-endian so every
 * language port produces byte-identical HMAC input.
 * Returns true on success; false if routing_key empty, length outside 1..51, or out too small.
 */
bool aethernet_erid_derive_for_epoch(const uint8_t *routing_key, size_t routing_key_len,
                                     int64_t epoch, int length,
                                     char *out, size_t out_size);

/* ─── In-session announcement codec ──────────────────────────────────────── */

/** Header: magic(4)+version(1)+epochSeconds(4)+eridLength(4)+routingKeyLen(4) = 17 bytes. */
#define AETHERNET_ERID_ANNOUNCE_HEADER_LEN 17

/**
 * Frame an in-session ERID announcement (shared with a peer INSIDE the Signal session so it
 * can resolve this node's rotating address): magic "AERD" + version + epochSeconds +
 * eridLength + routingKeyLen + routingKey, integer fields big-endian. Writes into out (caller
 * allocates >= AETHERNET_ERID_ANNOUNCE_HEADER_LEN + routing_key_len). On success sets *out_len.
 * Returns true on success.
 */
bool aethernet_erid_announcement_encode(const uint8_t *routing_key, size_t routing_key_len,
                                        int32_t epoch_seconds, int32_t erid_length,
                                        uint8_t *out, size_t out_size, size_t *out_len);

/**
 * Parse an ERID announcement. Returns false (not an error) when the bytes are not a
 * well-formed announcement, so a receiver can cheaply test an arbitrary decrypted in-session
 * payload against the magic. On success copies the routing key into out_routing_key
 * (>= out_key_size) and sets the out_* fields (any of which may be NULL to ignore).
 */
bool aethernet_erid_announcement_try_decode(const uint8_t *data, size_t data_len,
                                            uint8_t *out_routing_key, size_t out_key_size,
                                            size_t *out_key_len,
                                            int32_t *out_epoch_seconds,
                                            int32_t *out_erid_length);

/* ─── Directory: resolve a peer's rotating ERID to/from its stable UHID ───── */

/** Max peers a directory holds (embedded, fixed-capacity, no heap). */
#define AETHERNET_ERID_MAX_PEERS 64
/** Max UHID length stored per peer, including the NUL terminator. */
#define AETHERNET_ERID_MAX_UHID 128

/** One peer entry: its UHID and the secret routing key learned in-session. */
typedef struct {
    char uhid[AETHERNET_ERID_MAX_UHID];
    uint8_t routing_key[AETHERNET_ERID_ROUTING_KEY_SIZE];
    bool used;
} aethernet_erid_peer_t;

/**
 * In-memory directory: holds this node's routing key plus a fixed-capacity table of peer
 * routing keys. Embed by value (no heap). NOT thread-safe — single-threaded embedded targets,
 * matching the rest of the C library.
 */
typedef struct {
    uint8_t my_routing_key[AETHERNET_ERID_ROUTING_KEY_SIZE];
    int64_t epoch_seconds;
    int erid_length;
    aethernet_erid_peer_t peers[AETHERNET_ERID_MAX_PEERS];
} aethernet_erid_directory_t;

/**
 * Initialise a directory with this node's 32-byte routing key. epoch_seconds <= 0 or
 * erid_length <= 0 fall back to the defaults. Returns true on success.
 */
bool aethernet_erid_directory_init(aethernet_erid_directory_t *dir,
                                   const uint8_t *my_routing_key,
                                   int64_t epoch_seconds, int erid_length);

/** Our own current ERID for the epoch containing unix_seconds. */
bool aethernet_erid_directory_my_erid(const aethernet_erid_directory_t *dir,
                                      int64_t unix_seconds, char *out, size_t out_size);

/**
 * Store a peer's 32-byte routing key (learned in-session). Idempotent; replaces an existing
 * entry for the same UHID. Returns false if the table is full or inputs are invalid.
 */
bool aethernet_erid_directory_remember_peer(aethernet_erid_directory_t *dir,
                                            const char *peer_uhid,
                                            const uint8_t *peer_routing_key);

/** Forget a peer. Returns true if removed, false if the peer was unknown. */
bool aethernet_erid_directory_forget_peer(aethernet_erid_directory_t *dir,
                                          const char *peer_uhid);

/** The current ERID a known peer presents. Returns false if the peer is unknown. */
bool aethernet_erid_directory_erid_for_peer(const aethernet_erid_directory_t *dir,
                                            const char *peer_uhid, int64_t unix_seconds,
                                            char *out, size_t out_size);

/**
 * Reverse-resolve an inbound ERID to the stable peer UHID, writing it into out_uhid. Returns
 * true if a known peer presents `erid` this epoch, false otherwise.
 */
bool aethernet_erid_directory_resolve_peer(const aethernet_erid_directory_t *dir,
                                           const char *erid, int64_t unix_seconds,
                                           char *out_uhid, size_t out_size);

/** Number of peers whose routing key we currently hold. */
size_t aethernet_erid_directory_known_peer_count(const aethernet_erid_directory_t *dir);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_ERID_H
