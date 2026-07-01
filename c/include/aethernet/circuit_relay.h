// SPDX-License-Identifier: MIT
//
// Native circuit-relay-v2 wire frame — the decentralised any-node relay that
// lets a node reach a peer it cannot contact directly by routing through a third
// node reachable to both. Binary layout mirrors aethernet/dtn_envelope.h: a
// version byte first, little-endian integers, the 16-byte connection id as a
// UUID in RFC-4122 big-endian order, uint16-LE length-prefixed UTF-8 strings,
// and an int32-LE length-prefixed payload last. Byte-identical to the C#, Go,
// Python, TypeScript reference and pinned by fixtures/circuit-relay.

#ifndef AETHERNET_CIRCUIT_RELAY_H
#define AETHERNET_CIRCUIT_RELAY_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define AETHERNET_RELAY_FRAME_VERSION 0x01
#define AETHERNET_RELAY_CONN_ID_SIZE 16

// Message type (verb) — one fixed frame layout carries every verb.
enum {
    AETHERNET_RELAY_RESERVE = 1,
    AETHERNET_RELAY_RESERVE_RESPONSE = 2,
    AETHERNET_RELAY_CONNECT = 3,
    AETHERNET_RELAY_STOP = 4,
    AETHERNET_RELAY_STOP_RESPONSE = 5,
    AETHERNET_RELAY_CONNECT_RESPONSE = 6,
    AETHERNET_RELAY_DATA = 7
};

// Response status code.
enum {
    AETHERNET_RELAY_STATUS_OK = 0,
    AETHERNET_RELAY_STATUS_RESERVATION_REFUSED = 1,
    AETHERNET_RELAY_STATUS_NO_RESERVATION = 2,
    AETHERNET_RELAY_STATUS_RESOURCE_LIMIT_EXCEEDED = 3,
    AETHERNET_RELAY_STATUS_PERMISSION_DENIED = 4,
    AETHERNET_RELAY_STATUS_CONNECTION_FAILED = 5,
    AETHERNET_RELAY_STATUS_MALFORMED_MESSAGE = 6
};

typedef struct {
    uint8_t type;
    uint8_t status;
    char *source_uhid;       // origin client A (may be NULL => empty)
    char *destination_uhid;  // final target B (may be NULL => empty)
    char *relay_uhid;        // relay node R (may be NULL => empty)
    uint8_t connection_id[AETHERNET_RELAY_CONN_ID_SIZE]; // UUID, RFC-4122 big-endian bytes
    int64_t reservation_expires_at_ms;
    int32_t limit_duration_seconds;
    int64_t limit_data_bytes;
    uint8_t *payload;        // DATA only; may be NULL
    uint32_t payload_len;
} aethernet_relay_frame_t;

// Serialize a frame. On success sets *out (caller frees) and *out_len. Returns false on error.
bool aethernet_relay_frame_encode(const aethernet_relay_frame_t *f, uint8_t **out, uint32_t *out_len);

// Deserialize a frame. Returns a heap frame (free via aethernet_relay_frame_free) or NULL
// on malformed input (bad version, type not in 1..7, status > 6, bad length).
aethernet_relay_frame_t *aethernet_relay_frame_decode(const uint8_t *data, uint32_t len);

// Frees a frame returned by aethernet_relay_frame_decode (its strings and payload).
void aethernet_relay_frame_free(aethernet_relay_frame_t *f);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_CIRCUIT_RELAY_H
