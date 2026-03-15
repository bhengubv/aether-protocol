/**
 * Protocol constants from PROTOCOL_SPEC.md Appendix A
 * SPDX-License-Identifier: MIT
 */
export declare const DEFAULT_TTL = 7;
export declare const SOS_TTL = 15;
export declare const ROUTE_TIMEOUT_MS = 5000;
export declare const ROUTE_EXPIRY_SECONDS = 300;
export declare const PACKET_NONCE_SIZE = 8;
export declare const MAX_PACKET_AGE_SECONDS = 300;
export declare const PROTOCOL_VERSION_UNSIGNED = 1;
export declare const PROTOCOL_VERSION_SIGNED = 2;
export declare const MAX_SKIPPED_KEYS = 1000;
export declare const AES_GCM_NONCE_SIZE = 12;
export declare const AES_GCM_TAG_SIZE = 16;
export declare const SOS_PRIORITY = 999;
export declare const MAX_SOS_BROADCASTS_PER_HOUR = 3;
export declare const DTN_BUNDLE_TTL_HOURS = 72;
export declare const DTN_MAX_COPIES = 3;
export declare const DTN_MAX_BUNDLES_PER_NODE = 50;
export declare const DTN_SCAN_INTERVAL_SECONDS = 60;
export declare const BLE_MAX_PAYLOAD_BYTES = 1024;
export declare const DEFAULT_CHUNK_SIZE_BYTES = 262144;
export declare const MAX_CHUNK_SIZE_BYTES = 1048576;
export declare const WIFI_DIRECT_TIMEOUT_MS = 10000;
export declare const MAX_WIFI_DIRECT_PEERS = 8;
export declare const BLE_DISCOVERY_INTERVAL_MS = 10000;
export declare const BLE_SCAN_ON_MS = 2000;
export declare const BLE_SCAN_OFF_MS = 8000;
export declare const BLE_ADVERTISE_INTERVAL_MS = 1000;
export declare const BLE_UUID_ROTATION_SECONDS = 900;
export declare const BLE_SCAN_JITTER_MAX_MS = 2000;
export declare const AETHER_BLE_SERVICE_UUID = "A3E7-1001-0001-0000-000000000000";
export declare const HEARTBEAT_INTERVAL_SECONDS = 300;
export declare const NODE_OFFLINE_THRESHOLD_SECONDS = 900;
export declare const PRESENCE_BEACON_INTERVAL_MS = 15000;
export declare const PRESENCE_TIMEOUT_SECONDS = 60;
export declare const EPHEMERAL_ID_ROTATION_MINUTES = 15;
export declare const PROXIMITY_EVENT_DEBOUNCE_SECONDS = 30;
export declare const VOICE_FRAME_DURATION_MS = 20;
export declare const PTT_MAX_DURATION_SECONDS = 60;
export declare const JITTER_BUFFER_MIN_MS = 20;
export declare const JITTER_BUFFER_MAX_MS = 200;
export declare const OPUS_DEFAULT_BITRATE_KBPS = 64;
export declare const MAX_GROUP_VOICE_MEMBERS = 8;
export declare const DEFAULT_SEGMENT_DURATION_MS = 3000;
export declare const MAX_STREAM_TREE_FANOUT = 4;
export declare const MAX_STREAM_RELAY_HOPS = 3;
export declare const STREAM_SEGMENT_BUFFER_SIZE = 10;
export declare const BLE_AUDIO_BITRATE_KBPS = 64;
export declare const WIFI_DIRECT_VIDEO_BITRATE_KBPS = 500;
export declare const HKDF_SALT: Buffer<ArrayBuffer>;
export declare const HKDF_INFO_ROOT: Buffer<ArrayBuffer>;
export declare const HKDF_INFO_CHAIN_SEND: Buffer<ArrayBuffer>;
export declare const HKDF_INFO_CHAIN_RECV: Buffer<ArrayBuffer>;
//# sourceMappingURL=constants.d.ts.map