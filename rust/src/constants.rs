// SPDX-License-Identifier: MIT

/// Protocol version for signed packets (current)
pub const PROTOCOL_VERSION_SIGNED: u8 = 2;

/// Protocol version for unsigned packets (legacy)
pub const PROTOCOL_VERSION_UNSIGNED: u8 = 1;

/// Default time-to-live for packets
pub const DEFAULT_TTL: i32 = 7;

/// Time-to-live for SOS broadcast packets
pub const SOS_TTL: i32 = 15;

/// Priority level for SOS packets (max byte value).
/// Was originally 999 — invalid for a u8; corrected to 255 to match the
/// C# reference (ProtocolConstants.SosPriority).
pub const SOS_PRIORITY: u8 = 255;

/// Maximum packet age in seconds
pub const MAX_PACKET_AGE_SECONDS: u64 = 300;

/// Packet nonce size in bytes
pub const PACKET_NONCE_SIZE: usize = 8;

/// AES-GCM nonce size in bytes
pub const AES_GCM_NONCE_SIZE: usize = 12;

/// AES-GCM authentication tag size in bytes
pub const AES_GCM_TAG_SIZE: usize = 16;

/// AES key size in bytes
pub const AES_KEY_SIZE: usize = 32;

/// Ed25519 private key size in bytes
pub const ED25519_PRIVATE_KEY_SIZE: usize = 32;

/// Ed25519 public key size in bytes
pub const ED25519_PUBLIC_KEY_SIZE: usize = 32;

/// Ed25519 signature size in bytes
pub const ED25519_SIGNATURE_SIZE: usize = 64;

/// Maximum number of skipped message keys per Signal session
pub const MAX_SKIPPED_KEYS: usize = 1000;

/// Route discovery timeout in milliseconds
pub const ROUTE_TIMEOUT_MS: u64 = 5000;

/// Route expiry in seconds
pub const ROUTE_EXPIRY_SECONDS: u64 = 300;

/// BLE payload limit in bytes
pub const BLE_MAX_PAYLOAD_BYTES: usize = 1024;

/// Default chunk size in bytes
pub const DEFAULT_CHUNK_SIZE_BYTES: usize = 262144;

/// Maximum chunk size in bytes
pub const MAX_CHUNK_SIZE_BYTES: usize = 1048576;

/// Wi-Fi Direct timeout in milliseconds
pub const WIFI_DIRECT_TIMEOUT_MS: u64 = 10000;

/// Maximum concurrent Wi-Fi Direct peers
pub const MAX_WIFI_DIRECT_PEERS: usize = 8;

/// Maximum SOS broadcasts per hour
pub const MAX_SOS_BROADCASTS_PER_HOUR: usize = 3;

/// DTN bundle TTL in hours
pub const DTN_BUNDLE_TTL_HOURS: u64 = 72;

/// DTN maximum copies per bundle
pub const DTN_MAX_COPIES: i32 = 3;

/// DTN maximum bundles per node
pub const DTN_MAX_BUNDLES_PER_NODE: usize = 50;

/// DTN delivery scan interval in seconds
pub const DTN_SCAN_INTERVAL_SECONDS: u64 = 60;

/// Heartbeat interval in seconds
pub const HEARTBEAT_INTERVAL_SECONDS: u64 = 300;

/// Node offline threshold in seconds
pub const NODE_OFFLINE_THRESHOLD_SECONDS: u64 = 900;

/// Presence beacon interval in milliseconds
pub const PRESENCE_BEACON_INTERVAL_MS: u64 = 15000;

/// Presence timeout in seconds
pub const PRESENCE_TIMEOUT_SECONDS: u64 = 60;

/// Ephemeral ID rotation interval in minutes
pub const EPHEMERAL_ID_ROTATION_MINUTES: u64 = 15;

/// Proximity event debounce in seconds
pub const PROXIMITY_EVENT_DEBOUNCE_SECONDS: u64 = 30;

/// Voice frame duration in milliseconds
pub const VOICE_FRAME_DURATION_MS: u64 = 20;

/// PTT maximum duration in seconds
pub const PTT_MAX_DURATION_SECONDS: u64 = 60;

/// Jitter buffer minimum in milliseconds
pub const JITTER_BUFFER_MIN_MS: u64 = 20;

/// Jitter buffer maximum in milliseconds
pub const JITTER_BUFFER_MAX_MS: u64 = 200;

/// Opus default bitrate in kbps
pub const OPUS_DEFAULT_BITRATE_KBPS: usize = 64;

/// Maximum group voice members
pub const MAX_GROUP_VOICE_MEMBERS: usize = 8;

/// Default stream segment duration in milliseconds
pub const DEFAULT_SEGMENT_DURATION_MS: u64 = 3000;

/// Maximum stream tree fanout
pub const MAX_STREAM_TREE_FANOUT: usize = 4;

/// Maximum stream relay hops
pub const MAX_STREAM_RELAY_HOPS: i32 = 3;

/// Stream segment buffer size
pub const STREAM_SEGMENT_BUFFER_SIZE: usize = 10;

/// BLE audio bitrate in kbps
pub const BLE_AUDIO_BITRATE_KBPS: usize = 64;

/// Wi-Fi Direct video bitrate in kbps
pub const WIFI_DIRECT_VIDEO_BITRATE_KBPS: usize = 500;

/// HKDF root info string
pub const HKDF_ROOT_INFO: &[u8] = b"aether-root-v1";

/// HKDF chain send info string
pub const HKDF_CHAIN_SEND_INFO: &[u8] = b"aether-chain-send-v1";

/// HKDF chain receive info string
pub const HKDF_CHAIN_RECV_INFO: &[u8] = b"aether-chain-recv-v1";

/// HKDF salt value
pub const HKDF_SALT: &[u8] = b"AetherNetSignal";
