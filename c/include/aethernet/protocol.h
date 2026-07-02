// SPDX-License-Identifier: MIT
// Aether Mesh Networking Protocol - Core Types and Serialization

#ifndef AETHERNET_PROTOCOL_H
#define AETHERNET_PROTOCOL_H

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
    AETHERNET_PACKET_TYPE_ROUTE_REQUEST         = 1,
    AETHERNET_PACKET_TYPE_ROUTE_REPLY           = 2,
    AETHERNET_PACKET_TYPE_DATA                  = 3,
    AETHERNET_PACKET_TYPE_ACK                   = 4,
    AETHERNET_PACKET_TYPE_SOS_BROADCAST         = 5,
    AETHERNET_PACKET_TYPE_SOS_ACK               = 6,
    AETHERNET_PACKET_TYPE_CHANNEL_MESSAGE       = 7,
    AETHERNET_PACKET_TYPE_CHUNK_REQUEST         = 8,
    AETHERNET_PACKET_TYPE_CHUNK_DATA            = 9,
    AETHERNET_PACKET_TYPE_HEARTBEAT             = 10,
    AETHERNET_PACKET_TYPE_STREAM_ANNOUNCE       = 11,
    AETHERNET_PACKET_TYPE_STREAM_SEGMENT        = 12,
    AETHERNET_PACKET_TYPE_STREAM_SUBSCRIBE      = 13,
    AETHERNET_PACKET_TYPE_STREAM_UNSUBSCRIBE    = 14,
    AETHERNET_PACKET_TYPE_VOICE_PTT             = 15,
    AETHERNET_PACKET_TYPE_VOICE_CALL            = 16,
    AETHERNET_PACKET_TYPE_VOICE_SIGNALING       = 17,
    AETHERNET_PACKET_TYPE_DTN_BUNDLE            = 18,
    AETHERNET_PACKET_TYPE_DTN_CUSTODY_ACK       = 19,
    AETHERNET_PACKET_TYPE_DTN_DELIVERY_RECEIPT  = 20,
    AETHERNET_PACKET_TYPE_PRESENCE_BEACON       = 21,
    AETHERNET_PACKET_TYPE_PRESENCE_QUERY        = 22,
    AETHERNET_PACKET_TYPE_PROFILE_SYNC          = 23,
    AETHERNET_PACKET_TYPE_TIP_PACKET            = 24,
    AETHERNET_PACKET_TYPE_PREKEY_REQUEST        = 25,
    AETHERNET_PACKET_TYPE_PREKEY_RESPONSE       = 26,
    AETHERNET_PACKET_TYPE_VIDEO_CALL            = 27,
    AETHERNET_PACKET_TYPE_VIDEO_SIGNALING       = 28,
    AETHERNET_PACKET_TYPE_WATCH_SYNC            = 29,
    AETHERNET_PACKET_TYPE_WATCH_REACTION        = 30,
    AETHERNET_PACKET_TYPE_VIDEO_FRAME           = 31,
    AETHERNET_PACKET_TYPE_SCREEN_SHARE          = 32,
    AETHERNET_PACKET_TYPE_WATCH_CHUNK_REQUEST   = 33,
    AETHERNET_PACKET_TYPE_TORRENT_METADATA      = 34,
    AETHERNET_PACKET_TYPE_NAME_PUBLISH          = 38,
    AETHERNET_PACKET_TYPE_NAME_QUERY            = 39,
    // aether-space geo-pinned breadcrumb -- a node drops a SpaceBreadcrumb at a geohash and
    // passing devices auto-pull/cache/re-host it (AetherNet.Space). JSON payload.
    AETHERNET_PACKET_TYPE_SPACE_BREADCRUMB      = 40,
    // aether-forge cache-entry announcement -- broadcast when a node caches a new package
    // artifact so mesh peers learn where it lives (AetherNet.Forge). JSON payload.
    AETHERNET_PACKET_TYPE_FORGE_ANNOUNCE        = 41,
    // aether-vault shard request -- broadcast to locate peers holding a specific erasure-coded
    // shard by hash (AetherNet.Vault). JSON payload.
    AETHERNET_PACKET_TYPE_VAULT_SHARD_REQUEST   = 42,
    // Proof-of-Vicinity directed witness->subject token exchange (AetherNet.Market).
    AETHERNET_PACKET_TYPE_POV_TOKEN_EXCHANGE    = 43,
    // AetherNet Bandwidth Measurement Framework (ABMF) WIRE bindings. Wire bytes 53/54/55
    // match the C# PacketType.BandwidthProbe/BandwidthAck/BandwidthGossip so a measurement
    // hop is byte-identical across languages; an un-upgraded node drops the unknown type.
    // Bodies are LITTLE-ENDIAN with no version byte -- see aethernet/bandwidth_wire.h.
    //   53 -- BandwidthProbe : sequence u32 | sender_send_us i64                          (12 B)
    //   54 -- BandwidthAck   : + receiver_receive_us i64 | receiver_send_us i64 | probe_bytes i32 (32 B)
    //   55 -- BandwidthGossip: btlbw_bps i64 | rtprop_us i32 | confidence u8              (13 B)
    AETHERNET_PACKET_TYPE_BANDWIDTH_PROBE       = 53,
    AETHERNET_PACKET_TYPE_BANDWIDTH_ACK         = 54,
    AETHERNET_PACKET_TYPE_BANDWIDTH_GOSSIP      = 55,
    // CircuitRelayControl -- one native circuit-relay-v2 hop's RelayFrame carried in the
    // packet body (reserve/connect/stop/data + responses). Wire byte 57 matches the C#
    // PacketType.CircuitRelayControl so a relayed hop is byte-identical across languages;
    // an un-upgraded node drops the unknown type. Processed by the relay transport via
    // its aethernet_relay_mesh_link.
    AETHERNET_PACKET_TYPE_CIRCUIT_RELAY_CONTROL = 57
} aethernet_packet_type_t;

/**
 * Node capabilities bitfield.
 */
typedef enum {
    AETHERNET_CAP_BLE              = 0x001,
    AETHERNET_CAP_WIFI_DIRECT      = 0x002,
    AETHERNET_CAP_GATEWAY          = 0x004,
    AETHERNET_CAP_RELAY            = 0x008,
    AETHERNET_CAP_SOS              = 0x010,
    AETHERNET_CAP_STREAMING        = 0x020,
    AETHERNET_CAP_VOICE            = 0x040,
    AETHERNET_CAP_DTN_CARRIER      = 0x080,
    AETHERNET_CAP_NEAR_LINK        = 0x100,
    AETHERNET_CAP_VIDEO            = 0x200
} aethernet_capabilities_t;

/**
 * Core mesh packet structure optimized for embedded devices.
 * Variable-length fields are stored as pointers with associated length fields.
 *
 * aethernet_packet_t is a convenience alias used throughout the service layer.
 * The two names are identical; use either interchangeably.
 */
typedef struct {
    // Fixed-size fields
    uint8_t  packet_nonce[AETHERNET_PACKET_NONCE_SIZE];  // 8 bytes
    uint8_t  packet_id[AETHERNET_PACKET_ID_SIZE];        // 16 bytes (UUID)
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
} aethernet_mesh_packet_t;

/** Convenience alias — service-layer headers use aethernet_packet_t. */
typedef aethernet_mesh_packet_t aethernet_packet_t;

/**
 * Initialize a mesh packet structure (zeros memory, sets defaults).
 */
aethernet_mesh_packet_t *aethernet_packet_new(void);

/**
 * Free a mesh packet and all its variable-length fields.
 */
void aethernet_packet_free(aethernet_mesh_packet_t *packet);

/**
 * Clone a packet (deep copy including all variable-length fields).
 */
aethernet_mesh_packet_t *aethernet_packet_clone(const aethernet_mesh_packet_t *packet);

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
int aethernet_packet_serialize(const aethernet_mesh_packet_t *packet,
                            uint8_t *buffer,
                            size_t buffer_len);

/**
 * Deserialize a mesh packet from binary wire format.
 * Allocates memory for variable-length fields using malloc.
 *
 * Returns: allocated packet on success, NULL on error.
 * Caller must free with aethernet_packet_free().
 */
aethernet_mesh_packet_t *aethernet_packet_deserialize(const uint8_t *data,
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
uint8_t *aethernet_packet_get_signable_data(const aethernet_mesh_packet_t *packet,
                                         size_t *out_len);

/**
 * Check if packet has exceeded maximum age (in seconds).
 */
bool aethernet_packet_is_expired(const aethernet_mesh_packet_t *packet,
                              int max_age_seconds);

/**
 * Check if packet can be forwarded (TTL > 0).
 */
bool aethernet_packet_can_forward(const aethernet_mesh_packet_t *packet);

/**
 * Set source UHID (copies string, allocates memory).
 */
bool aethernet_packet_set_source_uhid(aethernet_mesh_packet_t *packet,
                                   const char *uhid);

/**
 * Set destination UHID (copies string, allocates memory).
 */
bool aethernet_packet_set_destination_uhid(aethernet_mesh_packet_t *packet,
                                        const char *uhid);

/**
 * Set payload (copies bytes, allocates memory).
 */
bool aethernet_packet_set_payload(aethernet_mesh_packet_t *packet,
                               const uint8_t *data,
                               size_t len);

/**
 * Set signature (copies bytes, allocates memory).
 */
bool aethernet_packet_set_signature(aethernet_mesh_packet_t *packet,
                                 const uint8_t *sig,
                                 size_t len);

/**
 * Estimate serialized size without actually serializing.
 */
size_t aethernet_packet_estimate_size(const aethernet_mesh_packet_t *packet);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_PROTOCOL_H
