// SPDX-License-Identifier: MIT

import Foundation

/// Protocol-level constants matching the Aether specification.
public struct ProtocolConstants {
    // MARK: - Routing
    // Wire format is Int32 LE; constants typed Int32 so they assign directly into MeshPacket.ttl.
    public static let defaultTtl: Int32 = 7
    public static let sosTtl: Int32 = 15
    public static let routeTimeoutMs: Int = 5000
    public static let routeExpirySeconds: Int = 300

    // MARK: - BLE Discovery
    public static let bleDiscoveryIntervalMs: Int = 10000
    public static let bleScanOnMs: Int = 2000
    public static let bleScanOffMs: Int = 8000
    public static let bleAdvertiseIntervalMs: Int = 1000
    public static let bleUuidRotationSeconds: Int = 900
    public static let bleScanJitterMaxMs: Int = 2000
    public static let aetherBleServiceUuid = "A3E71001-0001-0000-000000000000"

    // MARK: - Security
    public static let packetNonceSize: Int = 8
    public static let maxPacketAgeSeconds: Int = 300
    public static let protocolVersionUnsigned: UInt8 = 1
    public static let protocolVersionSigned: UInt8 = 2
    public static let maxSkippedKeys: Int = 1000
    public static let aesGcmNonceSize: Int = 12
    public static let aesGcmTagSize: Int = 16

    // MARK: - SOS
    public static let sosPriority: UInt8 = 255
    public static let maxSosBroadcastsPerHour: Int = 3

    // MARK: - DTN
    public static let dtnBundleTtlHours: Int = 72
    public static let dtnMaxCopies: Int = 3
    public static let dtnMaxBundlesPerNode: Int = 50
    public static let dtnScanIntervalSeconds: Int = 60

    // MARK: - Transport
    public static let bleMaxPayloadBytes: Int = 1024
    public static let defaultChunkSizeBytes: Int = 262144
    public static let maxChunkSizeBytes: Int = 1048576
    public static let wifiDirectTimeoutMs: Int = 10000
    public static let maxWifiDirectPeers: Int = 8

    // MARK: - Heartbeat
    public static let heartbeatIntervalSeconds: Int = 300
    public static let nodeOfflineThresholdSeconds: Int = 900

    // MARK: - Presence
    public static let presenceBeaconIntervalMs: Int = 15000
    public static let presenceTimeoutSeconds: Int = 60
    public static let ephemeralIdRotationMinutes: Int = 15
    public static let proximityEventDebounceSeconds: Int = 30

    // MARK: - Voice
    public static let voiceFrameDurationMs: Int = 20
    public static let pttMaxDurationSeconds: Int = 60
    public static let jitterBufferMinMs: Int = 20
    public static let jitterBufferMaxMs: Int = 200
    public static let opusDefaultBitrateKbps: Int = 64
    public static let maxGroupVoiceMembers: Int = 8

    // MARK: - Streaming
    public static let defaultSegmentDurationMs: Int = 3000
    public static let maxStreamTreeFanout: Int = 4
    public static let maxStreamRelayHops: Int = 3
    public static let streamSegmentBufferSize: Int = 10
    public static let bleAudioBitrateKbps: Int = 64
    public static let wifiDirectVideoBitrateKbps: Int = 500
}

/// Packet type enumeration matching the C# specification.
public enum PacketType: UInt8, Codable {
    case routeRequest = 1
    case routeReply = 2
    case data = 3
    case ack = 4
    case sosBroadcast = 5
    case sosAck = 6
    case channelMessage = 7
    case chunkRequest = 8
    case chunkData = 9
    case heartbeat = 10
    case streamAnnounce = 11
    case streamSegment = 12
    case streamSubscribe = 13
    case streamUnsubscribe = 14
    case voicePtt = 15
    case voiceCall = 16
    case voiceSignaling = 17
    case dtnBundle = 18
    case dtnCustodyAck = 19
    case dtnDeliveryReceipt = 20
    case presenceBeacon = 21
    case presenceQuery = 22
    case profileSync = 23
    case tipPacket = 24
    case preKeyRequest = 25
    case preKeyResponse = 26
    case videoCall = 27
    case videoSignaling = 28
    case watchSync = 29
    case watchReaction = 30
    case videoFrame = 31
    case screenShare = 32
    case watchChunkRequest = 33
    case torrentMetadata = 34

    /// Capability handshake — sender announces supported protocol-version range
    /// + capability flags. Sent on first contact with an unknown peer. The
    /// payload is a UTF-8 JSON-encoded `HelloPayload`. Unauthenticated and
    /// unencrypted — peer identity is verified later via Ed25519 packet
    /// signatures.
    case hello = 50

    /// Reply to a `hello` — receiver echoes back the agreed (highest
    /// mutually-supported) protocol version and the intersection of capability
    /// flags. Same JSON payload shape as `hello`.
    case helloAck = 51
}

/// Node capabilities as a bitfield.
public struct NodeCapabilities: OptionSet {
    public let rawValue: UInt16

    public static let ble = NodeCapabilities(rawValue: 1)
    public static let wifiDirect = NodeCapabilities(rawValue: 2)
    public static let gateway = NodeCapabilities(rawValue: 4)
    public static let relay = NodeCapabilities(rawValue: 8)
    public static let sos = NodeCapabilities(rawValue: 16)
    public static let streaming = NodeCapabilities(rawValue: 32)
    public static let voice = NodeCapabilities(rawValue: 64)
    public static let dtnCarrier = NodeCapabilities(rawValue: 128)
    public static let nearLink = NodeCapabilities(rawValue: 256)
    public static let video = NodeCapabilities(rawValue: 512)

    public init(rawValue: UInt16) {
        self.rawValue = rawValue
    }
}
