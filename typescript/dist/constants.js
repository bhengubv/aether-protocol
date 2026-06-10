/**

 * Protocol constants from PROTOCOL_SPEC.md Appendix A

 * SPDX-License-Identifier: MIT

 */
// Routing
export const DEFAULT_TTL = 7;
export const SOS_TTL = 15;
export const ROUTE_TIMEOUT_MS = 5000;
export const ROUTE_EXPIRY_SECONDS = 300;
export const RREQ_RATE_LIMIT_MAX = 10; // max unique RREQs per source per sliding window
export const RREQ_RATE_LIMIT_WINDOW_SECONDS = 10; // sliding window duration in seconds
// Security
export const PACKET_NONCE_SIZE = 8;
export const MAX_PACKET_AGE_SECONDS = 300;
export const PROTOCOL_VERSION_UNSIGNED = 1;
export const PROTOCOL_VERSION_SIGNED = 2;
export const MAX_SKIPPED_KEYS = 1000;
export const AES_GCM_NONCE_SIZE = 12;
export const AES_GCM_TAG_SIZE = 16;
// SOS
// SOS_PRIORITY: byte value used in MeshPacket.priority for emergency packets.
// Was originally 999 — invalid for a single byte; corrected to 255 to match the
// C# reference (ProtocolConstants.SosPriority).
export const SOS_PRIORITY = 255;
export const MAX_SOS_BROADCASTS_PER_HOUR = 3;
// DTN
export const DTN_BUNDLE_TTL_HOURS = 72;
export const DTN_MAX_COPIES = 3;
export const DTN_MAX_BUNDLES_PER_NODE = 50;
export const DTN_SCAN_INTERVAL_SECONDS = 60;
// Transport
export const BLE_MAX_PAYLOAD_BYTES = 1024;
export const DEFAULT_CHUNK_SIZE_BYTES = 262144;
export const MAX_CHUNK_SIZE_BYTES = 1048576;
export const WIFI_DIRECT_TIMEOUT_MS = 10000;
export const MAX_WIFI_DIRECT_PEERS = 8;
// BLE Discovery
export const BLE_DISCOVERY_INTERVAL_MS = 10000;
export const BLE_SCAN_ON_MS = 2000;
export const BLE_SCAN_OFF_MS = 8000;
export const BLE_ADVERTISE_INTERVAL_MS = 1000;
export const BLE_UUID_ROTATION_SECONDS = 900;
export const BLE_SCAN_JITTER_MAX_MS = 2000;
export const AETHERNET_BLE_SERVICE_UUID = "A3E7-1001-0001-0000-000000000000";
// Heartbeat
export const HEARTBEAT_INTERVAL_SECONDS = 300;
export const NODE_OFFLINE_THRESHOLD_SECONDS = 900;
// Presence
export const PRESENCE_BEACON_INTERVAL_MS = 15000;
export const PRESENCE_TIMEOUT_SECONDS = 60;
export const EPHEMERAL_ID_ROTATION_MINUTES = 15;
export const PROXIMITY_EVENT_DEBOUNCE_SECONDS = 30;
// Voice
export const VOICE_FRAME_DURATION_MS = 20;
export const PTT_MAX_DURATION_SECONDS = 60;
export const JITTER_BUFFER_MIN_MS = 20;
export const JITTER_BUFFER_MAX_MS = 200;
export const OPUS_DEFAULT_BITRATE_KBPS = 64;
export const MAX_GROUP_VOICE_MEMBERS = 8;
// Streaming
export const DEFAULT_SEGMENT_DURATION_MS = 3000;
export const MAX_STREAM_TREE_FANOUT = 4;
export const MAX_STREAM_RELAY_HOPS = 3;
export const STREAM_SEGMENT_BUFFER_SIZE = 10;
export const BLE_AUDIO_BITRATE_KBPS = 64;
export const WIFI_DIRECT_VIDEO_BITRATE_KBPS = 500;
// HKDF Info Strings
export const HKDF_SALT = Buffer.from("AetherNetSignal", "utf-8");
export const HKDF_INFO_ROOT = Buffer.from("aether-root-v1", "utf-8");
export const HKDF_INFO_CHAIN_SEND = Buffer.from("aether-chain-send-v1", "utf-8");
export const HKDF_INFO_CHAIN_RECV = Buffer.from("aether-chain-recv-v1", "utf-8");
//# sourceMappingURL=constants.js.map