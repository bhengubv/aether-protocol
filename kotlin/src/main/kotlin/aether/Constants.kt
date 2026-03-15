// SPDX-License-Identifier: MIT

package aether

/**
 * Protocol constants for the Aether mesh networking protocol.
 */
object AetherConstants {
    // Packet structure
    const val PACKET_NONCE_SIZE = 8
    const val PACKET_ID_SIZE = 16
    const val PROTOCOL_VERSION_UNSIGNED = 1
    const val PROTOCOL_VERSION_SIGNED = 2
    const val PROTOCOL_VERSION_CURRENT = 2

    // Routing
    const val DEFAULT_TTL = 7
    const val SOS_TTL = 15
    const val ROUTE_TIMEOUT_MS = 5000L
    const val ROUTE_EXPIRY_SECONDS = 300L

    // Security
    const val MAX_PACKET_AGE_SECONDS = 300
    const val MAX_SKIPPED_KEYS = 1000
    const val AES_GCM_NONCE_SIZE = 12
    const val AES_GCM_TAG_SIZE = 16
    const val AES_KEY_SIZE = 32
    const val ED25519_KEY_SIZE = 32
    const val ED25519_SIGNATURE_SIZE = 64

    // BLE Discovery
    const val BLE_DISCOVERY_INTERVAL_MS = 10000L
    const val BLE_SCAN_ON_MS = 2000
    const val BLE_SCAN_OFF_MS = 8000
    const val BLE_ADVERTISE_INTERVAL_MS = 1000
    const val BLE_UUID_ROTATION_SECONDS = 900L
    const val BLE_SCAN_JITTER_MAX_MS = 2000
    const val BLE_MAX_PAYLOAD_BYTES = 1024
    const val AETHER_BLE_SERVICE_UUID = "A3E71001-0001-0000-000000000000"

    // SOS
    const val SOS_PRIORITY = 999
    const val MAX_SOS_BROADCASTS_PER_HOUR = 3

    // DTN
    const val DTN_BUNDLE_TTL_HOURS = 72L
    const val DTN_MAX_COPIES = 3
    const val DTN_MAX_BUNDLES_PER_NODE = 50
    const val DTN_SCAN_INTERVAL_SECONDS = 60L

    // Transport
    const val DEFAULT_CHUNK_SIZE_BYTES = 262144
    const val MAX_CHUNK_SIZE_BYTES = 1048576
    const val WIFI_DIRECT_TIMEOUT_MS = 10000L
    const val MAX_WIFI_DIRECT_PEERS = 8

    // Heartbeat
    const val HEARTBEAT_INTERVAL_SECONDS = 300L
    const val NODE_OFFLINE_THRESHOLD_SECONDS = 900L

    // Presence
    const val PRESENCE_BEACON_INTERVAL_MS = 15000L
    const val PRESENCE_TIMEOUT_SECONDS = 60L
    const val EPHEMERAL_ID_ROTATION_MINUTES = 15L
    const val PROXIMITY_EVENT_DEBOUNCE_SECONDS = 30L

    // Voice
    const val VOICE_FRAME_DURATION_MS = 20
    const val PTT_MAX_DURATION_SECONDS = 60
    const val JITTER_BUFFER_MIN_MS = 20
    const val JITTER_BUFFER_MAX_MS = 200
    const val OPUS_DEFAULT_BITRATE_KBPS = 64
    const val MAX_GROUP_VOICE_MEMBERS = 8

    // Streaming
    const val DEFAULT_SEGMENT_DURATION_MS = 3000L
    const val MAX_STREAM_TREE_FANOUT = 4
    const val MAX_STREAM_RELAY_HOPS = 3
    const val STREAM_SEGMENT_BUFFER_SIZE = 10
    const val BLE_AUDIO_BITRATE_KBPS = 64
    const val WIFI_DIRECT_VIDEO_BITRATE_KBPS = 500

    // HKDF info strings
    val HKDF_ROOT_INFO = "aether-root-v1".toByteArray(Charsets.UTF_8)
    val HKDF_CHAIN_SEND_INFO = "aether-chain-send-v1".toByteArray(Charsets.UTF_8)
    val HKDF_CHAIN_RECV_INFO = "aether-chain-recv-v1".toByteArray(Charsets.UTF_8)
}
