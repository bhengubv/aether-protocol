// SPDX-License-Identifier: MIT

namespace Aether.Constants;

/// <summary>
/// Constants governing the Aether mesh protocol behaviour.
/// </summary>
public static class ProtocolConstants
{
    // ── Protocol version ─────────────────────────────────────────────
    public const byte CurrentProtocolVersion = 2;

    // ── TTL defaults ─────────────────────────────────────────────────
    /// <summary>Default time-to-live for regular packets.</summary>
    public const int DefaultTtl = 7;

    /// <summary>Extended TTL for SOS broadcast packets to maximise reach.</summary>
    public const int SosTtl = 15;

    /// <summary>TTL for DTN bundles which may traverse many hops over hours/days.</summary>
    public const int DtnTtl = 30;

    // ── Timeouts and expiry ──────────────────────────────────────────
    /// <summary>How long to wait for a route reply before giving up (ms).</summary>
    public const int RouteTimeoutMs = 5_000;

    /// <summary>How long a discovered route remains valid (seconds).</summary>
    public const int RouteExpirySeconds = 300;

    /// <summary>Maximum age of a packet before it is discarded as stale (seconds).</summary>
    public const int MaxPacketAgeSeconds = 300;

    /// <summary>Time-to-live for DTN bundles in hours.</summary>
    public const int DtnBundleTtlHours = 72;

    /// <summary>How long a DTN custody transfer can remain pending (seconds).</summary>
    public const int DtnCustodyTimeoutSeconds = 3_600;

    // ── Beacon and heartbeat intervals ───────────────────────────────
    /// <summary>Interval between presence beacon broadcasts (ms).</summary>
    public const int PresenceBeaconIntervalMs = 15_000;

    /// <summary>Interval between node heartbeat packets (ms).</summary>
    public const int HeartbeatIntervalMs = 30_000;

    /// <summary>Time after which a node with no heartbeat is considered offline (ms).</summary>
    public const int NodeOfflineThresholdMs = 90_000;

    // ── Transport constraints ────────────────────────────────────────
    /// <summary>Maximum payload size for BLE GATT transport (bytes).</summary>
    public const int BleMaxPayloadBytes = 1_024;

    /// <summary>Maximum payload size for Wi-Fi Direct transport (bytes).</summary>
    public const int WifiDirectMaxPayloadBytes = 65_536;

    /// <summary>Maximum payload for NearLink transport (bytes).</summary>
    public const int NearLinkMaxPayloadBytes = 4_096;

    // ── Security ─────────────────────────────────────────────────────
    /// <summary>Size of the random nonce included in each packet (bytes).</summary>
    public const int PacketNonceSize = 8;

    /// <summary>Size of the Ed25519 signature (bytes).</summary>
    public const int SignatureSize = 64;

    /// <summary>Size of the Ed25519 public key (bytes).</summary>
    public const int PublicKeySize = 32;

    /// <summary>Maximum number of packet IDs retained in the deduplication cache.</summary>
    public const int DeduplicationCacheSize = 10_000;

    /// <summary>How long packet IDs are retained for deduplication (seconds).</summary>
    public const int DeduplicationWindowSeconds = 300;

    // ── Routing ──────────────────────────────────────────────────────
    /// <summary>Maximum number of routes stored per destination.</summary>
    public const int MaxRoutesPerDestination = 5;

    /// <summary>Maximum number of pending route requests tracked simultaneously.</summary>
    public const int MaxPendingRouteRequests = 100;

    /// <summary>Number of retries for route discovery before failing.</summary>
    public const int RouteDiscoveryRetries = 3;

    // ── Streaming ────────────────────────────────────────────────────
    /// <summary>Duration of each live stream segment (ms).</summary>
    public const int StreamSegmentDurationMs = 2_000;

    /// <summary>Maximum subscribers per stream relay node.</summary>
    public const int MaxStreamSubscribers = 50;

    // ── Voice ────────────────────────────────────────────────────────
    /// <summary>Jitter buffer target depth (ms).</summary>
    public const int JitterBufferTargetMs = 60;

    /// <summary>Maximum jitter buffer depth before dropping old frames (ms).</summary>
    public const int JitterBufferMaxMs = 200;

    /// <summary>Voice codec frame duration (ms).</summary>
    public const int VoiceFrameDurationMs = 20;

    // ── Content verification ─────────────────────────────────────────
    /// <summary>Default chunk size for content transfer (bytes).</summary>
    public const int DefaultChunkSizeBytes = 8_192;

    /// <summary>Maximum number of concurrent chunk transfers per peer.</summary>
    public const int MaxConcurrentChunkTransfers = 4;

    // ── SOS ──────────────────────────────────────────────────────────
    /// <summary>Priority value for SOS packets (maximum urgency).</summary>
    public const byte SosPriority = 255;

    /// <summary>How long SOS state remains active after broadcast (seconds).</summary>
    public const int SosActiveWindowSeconds = 3_600;

    // ── Geohash ──────────────────────────────────────────────────────
    /// <summary>Default geohash precision for full-privacy nodes (characters).</summary>
    public const int GeohashPrecisionFull = 7;

    /// <summary>Reduced geohash precision for approximate-privacy nodes.</summary>
    public const int GeohashPrecisionApproximate = 4;
}
