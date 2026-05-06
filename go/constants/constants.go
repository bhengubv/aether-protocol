// SPDX-License-Identifier: MIT

package constants

// Routing constants
const (
	DefaultTtl         int32 = 7
	SosTtl             int32 = 15
	DtnTtl             int32 = 30
	RouteTimeoutMs     int32 = 5000
	RouteExpirySeconds int32 = 300
)

// BLE Discovery constants
const (
	BleDiscoveryIntervalMs  int32 = 10000
	BleScanOnMs             int32 = 2000
	BleScanOffMs            int32 = 8000
	BleAdvertiseIntervalMs  int32 = 1000
	BleUuidRotationSeconds  int32 = 900
	BleScanJitterMaxMs      int32 = 2000
	AetherBleServiceUUID          = "A3E7-1001-0001-0000-000000000000"
)

// Security constants
const (
	PacketNonceSize         int32 = 8
	MaxPacketAgeSeconds     int32 = 300
	ProtocolVersionUnsigned byte  = 1
	ProtocolVersionSigned   byte  = 2
	// CurrentProtocolVersion is the highest protocol version this
	// implementation can speak. Mirrors the C# reference's
	// ProtocolConstants.CurrentProtocolVersion. Bumped when the wire
	// format gains a backward-incompatible field.
	CurrentProtocolVersion  byte  = 2
	MaxSkippedKeys          int32 = 1000
	AesGcmNonceSize         int32 = 12
	AesGcmTagSize           int32 = 16
)

// SOS constants
const (
	// SosPriority is the priority value for SOS packets (max byte value).
	// Was originally declared as 999 which did not fit in a byte; corrected
	// to 255 to match the C# reference (ProtocolConstants.SosPriority).
	SosPriority             byte  = 255
	MaxSosBroadcastsPerHour int32 = 3
)

// DTN constants
const (
	DtnBundleTtlHours      int32 = 72
	DtnMaxCopies           int32 = 3
	DtnMaxBundlesPerNode   int32 = 50
	DtnScanIntervalSeconds int32 = 60
)

// Transport constants
const (
	BleMaxPayloadBytes   int32 = 1024
	DefaultChunkSizeBytes int32 = 262144
	MaxChunkSizeBytes    int32 = 1048576
	WifiDirectTimeoutMs  int32 = 10000
	MaxWifiDirectPeers   int32 = 8
)

// Heartbeat constants
const (
	HeartbeatIntervalSeconds    int32 = 300
	NodeOfflineThresholdSeconds int32 = 900
)

// Presence constants
const (
	PresenceBeaconIntervalMs      int32 = 15000
	PresenceTimeoutSeconds        int32 = 60
	EphemeralIdRotationMinutes    int32 = 15
	ProximityEventDebounceSeconds int32 = 30
)

// Voice constants
const (
	VoiceFrameDurationMs     int32 = 20
	PttMaxDurationSeconds    int32 = 60
	JitterBufferMinMs        int32 = 20
	JitterBufferMaxMs        int32 = 200
	OpusDefaultBitrateKbps   int32 = 64
	MaxGroupVoiceMembers     int32 = 8
)

// Streaming constants
const (
	DefaultSegmentDurationMs  int32 = 3000
	MaxStreamTreeFanout       int32 = 4
	MaxStreamRelayHops        int32 = 3
	StreamSegmentBufferSize   int32 = 10
	BleAudioBitrateKbps       int32 = 64
	WifiDirectVideoBitrateKbps int32 = 500
)

// Salt for HKDF
const (
	HkdfSalt = "AetherSignal"
)
