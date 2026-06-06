// SPDX-License-Identifier: MIT
// Aether Mesh Networking Protocol - Core Types and Serialization

#ifndef AETHERMESH_PROTOCOL_H
#define AETHERMESH_PROTOCOL_H

#include <stdint.h>
#include <stdbool.h>
#include <time.h>
#include "constants.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Packet type enumeration matching the protocol specification.
 */
typedef enum {
    AETHERMESH_PACKET_TYPE_ROUTE_REQUEST         = 1,
    AETHERMESH_PACKET_TYPE_ROUTE_REPLY           = 2,
    AETHERMESH_PACKET_TYPE_DATA                  = 3,
    AETHERMESH_PACKET_TYPE_ACK                   = 4,
    AETHERMESH_PACKET_TYPE_SOS_BROADCAST         = 5,
    AETHERMESH_PACKET_TYPE_SOS_ACK               = 6,
    AETHERMESH_PACKET_TYPE_CHANNEL_MESSAGE       = 7,
    AETHERMESH_PACKET_TYPE_CHUNK_REQUEST         = 8,
    AETHERMESH_PACKET_TYPE_CHUNK_DATA            = 9,
    AETHERMESH_PACKET_TYPE_HEARTBEAT             = 10,
    AETHERMESH_PACKET_TYPE_STREAM_ANNOUNCE       = 11,
    AETHERMESH_PACKET_TYPE_STREAM_SEGMENT        = 12,
    AETHERMESH_PACKET_TYPE_STREAM_SUBSCRIBE      = 13,
    AETHERMESH_PACKET_TYPE_STREAM_UNSUBSCRIBE    = 14,
    AETHERMESH_PACKET_TYPE_VOICE_PTT             = 15,
    AETHERMESH_PACKET_TYPE_VOICE_CALL            = 16,
    AETHERMESH_PACKET_TYPE_VOICE_SIGNALING       = 17,
    AETHERMESH_PACKET_TYPE_DTN_BUNDLE            = 18,
    AETHERMESH_PACKET_TYPE_DTN_CUSTODY_ACK       = 19,
    AETHERMESH_PACKET_TYPE_DTN_DELIVERY_RECEIPT  = 20,
    AETHERMESH_PACKET_TYPE_PRESENCE_BEACON       = 21,
    AETHERMESH_PACKET_TYPE_PRESENCE_QUERY        = 22,
    AETHERMESH_PACKET_TYPE_PROFILE_SYNC          = 23,
    AETHERMESH_PACKET_TYPE_TIP_PACKET            = 24,
    AETHERMESH_PACKET_TYPE_PREKEY_REQUEST        = 25,
    AETHERMESH_PACKET_TYPE_PREKEY_RESPONSE       = 26,
    AETHERMESH_PACKET_TYPE_VIDEO_CALL            = 27,
    AETHERMESH_PACKET_TYPE_VIDEO_SIGNALING       = 28,
    AETHERMESH_PACKET_TYPE_WATCH_SYNC            = 29,
    AETHERMESH_PACKET_TYPE_WATCH_REACTION        = 30,
    AETHERMESH_PACKET_TYPE_VIDEO_FRAME           = 31,
    AETHERMESH_PACKET_TYPE_SCREEN_SHARE          = 32,
    AETHERMESH_PACKET_TYPE_WATCH_CHUNK_REQUEST   = 33,
    AETHERMESH_PACKET_TYPE_TORRENT_METADATA      = 34
} aethermesh_packet_type_t;

/**
 * Node capabilities bitfield.
 */
typedef enum {
    AETHERMESH_CAP_BLE              = 0x001,
    AETHERMESH_CAP_WIFI_DIRECT      = 0x002,
    AETHERMESH_CAP_GATEWAY          = 0x004,
    AETHERMESH_CAP_RELAY            = 0x008,
    AETHERMESH_CAP_SOS              = 0x010,
    AETHERMESH_CAP_STREAMING        = 0x020,
    AETHERMESH_CAP_VOICE            = 0x040,
    AETHERMESH_CAP_DTN_CARRIER      = 0x080,
    AETHERMESH_CAP_NEAR_LINK        = 0x100,
    AETHERMESH_CAP_VIDEO            = 0x200
} aethermesh_capabilities_t;

/**
 * Core mesh packet structure optimized for embedded devices.
 * Variable-length fields are stored as pointers with associated length fields.
 *
 * aethermesh_packet_t is a convenience alias used throughout the service layer.
 * The two names are identical; use either interchangeably.
 */
typedef struct {
    // Fixed-size fields
    uint8_t  packet_nonce[AETHERMESH_PACKET_NONCE_SIZE];  // 8 bytes
    uint8_t  packet_id[AETHERMESH_PACKET_ID_SIZE];        // 16 bytes (UUID)
    int64_t  timestamp_ms;                             // 8 bytes
    uint8_t  protocol_version;                         // 1 byte
    uint8_t  type;                                     // 1 byte
    int32_t  ttl;                                      // 4 bytes (wire is little-endian int32; was uint8_t — truncation bug fixed 2026-05-02)
    uint8_t  priority;                                 // 1 byte

    // Variable-length fields (pointers + lengths)
    char    *source_uhid;                              // UHID string (UTF-8)
    uint16_t source_uhid_len;                          // Length in bytes (not including null terminator)

    char    *destination_uhid;                         // UHID string (UTF-8)
    uint16_t destination_uhid_len;

    uint8_t *payload;                                  // Raw bytes
    uint32_t payload_len;

    uint8_t *signature;                                // Ed25519 signature
    uint16_t signature_len;                            // 0 or 64 for Ed25519
} aethermesh_mesh_packet_t;

/** Convenience alias — service-layer headers use aethermesh_packet_t. */
typedef aethermesh_mesh_packet_t aethermesh_packet_t;

/**
 * Initialize a mesh packet structure (zeros memory, sets defaults).
 */
aethermesh_mesh_packet_t *aethermesh_packet_new(void);

/**
 * Free a mesh packet and all its variable-length fields.
 */
void aethermesh_packet_free(aethermesh_mesh_packet_t *packet);

/**
 * Clone a packet (deep copy including all variable-length fields).
 */
aethermesh_mesh_packet_t *aethermesh_packet_clone(const aethermesh_mesh_packet_t *packet);

/**
 * Serialize a mesh packet to binary wire format (little-endian).
 * Buffer must be allocated by caller.
 *
 * Wire format:
 *   [1] protocol_version
 *   [1] type
 *   [16] packet_id
 *   [1] priority
 *   [4] ttl (little-endian int32)
 *   [8] timestamp_ms (little-endian int64)
 *   [2] source_uhid_len (little-endian uint16)
 *   [N] source_uhid UTF-8 bytes
 *   [2] destination_uhid_len (little-endian uint16)
 *   [N] destination_uhid UTF-8 bytes
 *   [2] nonce_len (little-endian uint16)
 *   [N] packet_nonce bytes
 *   [4] payload_len (little-endian int32)
 *   [N] payload bytes
 *   [2] signature_len (little-endian uint16)
 *   [N] signature bytes
 *
 * Returns: number of bytes written to buffer, or -1 on error.
 */
int aethermesh_packet_serialize(const aethermesh_mesh_packet_t *packet,
                            uint8_t *buffer,
                            size_t buffer_len);

/**
 * Deserialize a mesh packet from binary wire format.
 * Allocates memory for variable-length fields using malloc.
 *
 * Returns: allocated packet on success, NULL on error.
 * Caller must free with aethermesh_packet_free().
 */
aethermesh_mesh_packet_t *aethermesh_packet_deserialize(const uint8_t *data,
                                                size_t data_len);

/**
 * Construct the signable data for a packet (for Ed25519 signing).
 * This follows the protocol spec:
 *   PacketNonce (8 bytes)
 *   || TimestampMs (8 bytes, little-endian int64)
 *   || Type (4 bytes, little-endian int32)
 *   || SourceUhidLength (4 bytes, little-endian int32)
 *   || SourceUhid (UTF-8 bytes)
 *   || DestinationUhidLength (4 bytes, little-endian int32)
 *   || DestinationUhid (UTF-8 bytes)
 *   || SHA-256(Payload) (32 bytes)
 *   || Ttl (4 bytes, little-endian int32)
 *   || Priority (4 bytes, little-endian int32)
 *
 * Allocates buffer via malloc; caller must free.
 * Returns: allocated buffer with signable data, or NULL on error.
 */
uint8_t *aethermesh_packet_get_signable_data(const aethermesh_mesh_packet_t *packet,
                                         size_t *out_len);

/**
 * Check if packet has exceeded maximum age (in seconds).
 */
bool aethermesh_packet_is_expired(const aethermesh_mesh_packet_t *packet,
                              int max_age_seconds);

/**
 * Check if packet can be forwarded (TTL > 0).
 */
bool aethermesh_packet_can_forward(const aethermesh_mesh_packet_t *packet);

/**
 * Set source UHID (copies string, allocates memory).
 */
bool aethermesh_packet_set_source_uhid(aethermesh_mesh_packet_t *packet,
                                   const char *uhid);

/**
 * Set destination UHID (copies string, allocates memory).
 */
bool aethermesh_packet_set_destination_uhid(aethermesh_mesh_packet_t *packet,
                                        const char *uhid);

/**
 * Set payload (copies bytes, allocates memory).
 */
bool aethermesh_packet_set_payload(aethermesh_mesh_packet_t *packet,
                               const uint8_t *data,
                               size_t len);

/**
 * Set signature (copies bytes, allocates memory).
 */
bool aethermesh_packet_set_signature(aethermesh_mesh_packet_t *packet,
                                 const uint8_t *sig,
                                 size_t len);

/**
 * Estimate serialized size without actually serializing.
 */
size_t aethermesh_packet_estimate_size(const aethermesh_mesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERMESH_PROTOCOL_H
