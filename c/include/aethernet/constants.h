// SPDX-License-Identifier: MIT
// Aether Protocol Constants

#ifndef AETHERNET_CONSTANTS_H
#define AETHERNET_CONSTANTS_H

#include <stdint.h>

// Protocol versioning
#define AETHERNET_PROTOCOL_VERSION_UNSIGNED 1
#define AETHERNET_PROTOCOL_VERSION_SIGNED   2
#define AETHERNET_PROTOCOL_VERSION_CURRENT  2

// Packet nonce and timestamp
#define AETHERNET_PACKET_NONCE_SIZE    8
#define AETHERNET_PACKET_ID_SIZE       16  // UUID
#define AETHERNET_MAX_PACKET_AGE_SECONDS 300

// Routing parameters
#define AETHERNET_DEFAULT_TTL           7
#define AETHERNET_SOS_TTL               15
#define AETHERNET_DTN_TTL               30
#define AETHERNET_ROUTE_TIMEOUT_MS      5000
#define AETHERNET_ROUTE_EXPIRY_SECONDS  300

/** Maximum unique RREQs from a single source in the rate-limit window. */
#define AETHERNET_RREQ_RATE_LIMIT_MAX       10
/** Rate-limit sliding window in seconds. */
#define AETHERNET_RREQ_RATE_LIMIT_WINDOW_S  10

// String size limits (for embedded devices)
#define AETHERNET_MAX_UHID_LEN          128
#define AETHERNET_MAX_PAYLOAD_LEN       (16 * 1024 * 1024)  // 16MB max payload (int32 length field)

// Security constants
#define AETHERNET_ED25519_PUBLIC_KEY_SIZE   32
#define AETHERNET_ED25519_PRIVATE_KEY_SIZE  32
#define AETHERNET_ED25519_SIGNATURE_SIZE    64
#define AETHERNET_AES_GCM_NONCE_SIZE        12
#define AETHERNET_AES_GCM_TAG_SIZE          16
#define AETHERNET_SHA256_SIZE               32
#define AETHERNET_HMAC_SHA256_SIZE          32
#define AETHERNET_BLAKE3_SIZE               32

// BLE discovery
#define AETHERNET_BLE_DISCOVERY_INTERVAL_MS  10000
#define AETHERNET_BLE_SCAN_ON_MS             2000
#define AETHERNET_BLE_SCAN_OFF_MS            8000
#define AETHERNET_BLE_ADVERTISE_INTERVAL_MS  1000
#define AETHERNET_BLE_UUID_ROTATION_SECONDS  900
#define AETHERNET_BLE_MAX_PAYLOAD_BYTES      1024

// SOS parameters
// AETHERNET_SOS_PRIORITY: byte value used in mesh_packet.priority for emergency packets.
// Was originally 999 — invalid for a uint8_t; corrected to 255 to match the
// C# reference (ProtocolConstants.SosPriority).
#define AETHERNET_SOS_PRIORITY              255
#define AETHERNET_MAX_SOS_BROADCASTS_PER_HOUR 3

// DTN parameters
#define AETHERNET_DTN_BUNDLE_TTL_HOURS   72
#define AETHERNET_DTN_MAX_COPIES         3
#define AETHERNET_DTN_MAX_BUNDLES_PER_NODE 50
#define AETHERNET_DTN_SCAN_INTERVAL_SECONDS 60

// Minimum packet size (empty UHIDs, empty payload)
// 8 (nonce) + 8 (timestamp) + 1 (version) + 1 (type) + 1 (ttl) + 1 (priority) +
// 4 (source_len) + 4 (dest_len) + 4 (payload_len) + 4 (sig_len) + 16 (id) = 52 bytes
#define AETHERNET_MIN_PACKET_SIZE 52

// Embedded transport limits
#define AETHERNET_MAX_PEERS_IN_MEMORY 256
#define AETHERNET_MAX_ROUTES_IN_MEMORY 512

// Symmetric ratchet
#define AETHERNET_MAX_SKIPPED_KEYS 1000

#endif // AETHERNET_CONSTANTS_H
