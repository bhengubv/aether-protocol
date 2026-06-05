// SPDX-License-Identifier: MIT

namespace AetherMesh.Constants;

/// <summary>
/// Constants governing the Aether mesh protocol behaviour.
/// </summary>
public static class ProtocolConstants
{
    // ── Protocol version ─────────────────────────────────────────────
    /// <summary>
    /// Wire-format protocol version emitted into <c>MeshPacket.ProtocolVersion</c>.
    /// Value <c>2</c> indicates a signed packet.
    /// Note: <c>docs/PROTOCOL_SPEC.md</c> Appendix A names this constant
    /// <c>ProtocolVersionSigned</c>; that is the canonical cross-language name.
    /// The C# constant is currently unreferenced — <c>MeshPacket</c>,
    /// <c>PacketSerializer</c>, and <c>PacketSigningService</c> all carry the
    /// literal <c>2</c>. Kept here as the single source of truth for the value.
    /// </summary>
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

    /// <summary>Maximum number of copies of a DTN bundle that may exist concurrently in the mesh.</summary>
    public const int DtnMaxCopies = 3;

    /// <summary>Maximum number of DTN bundles a single node will hold in custody.</summary>
    public const int DtnMaxBundlesPerNode = 50;

    /// <summary>Default scan interval for the DTN delivery loop (seconds).</summary>
    public const int DtnScanIntervalSeconds = 60;

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

    // ── Video (Phase 7) ───────────────────────────────────────────────
    /// <summary>Target video frame duration at 30fps (ms).</summary>
    public const int VideoFrameDurationMs = 33;

    /// <summary>Minimum video jitter buffer depth (ms).</summary>
    public const int VideoJitterBufferMinMs = 60;

    /// <summary>Maximum video jitter buffer depth (ms).</summary>
    public const int VideoJitterBufferMaxMs = 500;

    /// <summary>
    /// Watch-together target buffer ahead of playback (seconds).
    /// Spec name (<c>docs/PROTOCOL_SPEC.md</c> Appendix A): <c>WatchTogetherBufferAheadSeconds</c>.
    /// </summary>
    public const int WatchBufferAheadSeconds = 30;

    /// <summary>
    /// Watch-together minimum buffer before auto-pause (seconds).
    /// Spec name (<c>docs/PROTOCOL_SPEC.md</c> Appendix A): <c>WatchTogetherMinBufferSeconds</c>.
    /// </summary>
    public const int WatchMinBufferSeconds = 10;

    /// <summary>Participant count at which group video switches from FullMesh to SFU.</summary>
    public const int SfuThresholdParticipants = 4;

    // ── Aether Tag ───────────────────────────────────────────────────
    /// <summary>
    /// Number of significant data characters in an Aether Tag (excluding the separator).
    /// Format: XXXXX-XXXXX → 10 data chars + 1 separator = 11 total.
    /// </summary>
    public const int AetherTagDataLength = 10;

    /// <summary>
    /// Total length of a formatted Aether Tag string including the separator dash.
    /// </summary>
    public const int AetherTagFormattedLength = 11;

    /// <summary>
    /// Number of bits extracted from the SHA-256 public key hash to form an Aether Tag.
    /// 50 bits → ~1.1 quadrillion combinations.
    /// </summary>
    public const int AetherTagBits = 50;

    // ── Content verification ─────────────────────────────────────────
    /// <summary>
    /// Default chunk size for content transfer (bytes).
    /// Canonical runtime value is <c>8 192</c> — used by
    /// <c>ContentDescriptor</c> and the swarm/torrent piece-to-chunk mapping
    /// described in <c>docs/VIDEO_STREAMING.md</c>. <c>docs/PROTOCOL_SPEC.md</c>
    /// Appendix A currently lists <c>DefaultChunkSizeBytes = 262144</c>; that
    /// is a spec drift tracked under OPEN_ISSUES.md item 4 — runtime wins.
    /// </summary>
    public const int DefaultChunkSizeBytes = 8_192;

    /// <summary>Maximum number of concurrent chunk transfers per peer.</summary>
    public const int MaxConcurrentChunkTransfers = 4;

    /// <summary>
    /// Chunk Shuffle — number of chunks received since the last bitmap broadcast
    /// that triggers an early re-broadcast (event-driven batch coalescing).
    /// Chosen to match ~65 KB received at 8 192-byte chunks, which is well-paced
    /// against BLE throughput without flooding the radio.
    /// </summary>
    public const int ChunkBitmapBroadcastBatchSize = 8;

    /// <summary>
    /// Chunk Shuffle — maximum milliseconds between bitmap re-broadcasts even if
    /// the batch-size threshold has not been reached (timer-driven coalescing).
    /// Keeps bitmap information fresh for late-joining peers without per-chunk overhead.
    /// </summary>
    public const int ChunkBitmapBroadcastCoalesceMs = 500;

    // ── SOS ──────────────────────────────────────────────────────────
    /// <summary>
    /// Priority value for SOS packets (maximum urgency).
    /// Canonical value is <c>255</c> — the byte-field maximum — and every
    /// other-language port (Go/Rust/Kotlin/Swift/TypeScript/Python/C) anchors
    /// on this constant. <c>docs/PROTOCOL_SPEC.md</c> Appendix A currently
    /// lists <c>SosPriority = 999</c>, which is impossible in a byte field;
    /// that is a spec drift tracked under OPEN_ISSUES.md item 4.
    /// </summary>
    public const byte SosPriority = 255;

    /// <summary>How long SOS state remains active after broadcast (seconds).</summary>
    public const int SosActiveWindowSeconds = 3_600;

    /// <summary>Maximum SOS broadcasts a single node may originate per rolling hour. Floods abuse-protection.</summary>
    public const int MaxSosBroadcastsPerHour = 3;

    // ── Geohash ──────────────────────────────────────────────────────
    /// <summary>Default geohash precision for full-privacy nodes (characters).</summary>
    public const int GeohashPrecisionFull = 7;

    /// <summary>Reduced geohash precision for approximate-privacy nodes.</summary>
    public const int GeohashPrecisionApproximate = 4;
}
