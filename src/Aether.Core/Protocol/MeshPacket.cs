// SPDX-License-Identifier: MIT

namespace Aether.Protocol;

/// <summary>
/// Defines the type of mesh packet being transmitted.
/// </summary>
public enum PacketType : byte
{
    RouteRequest = 1,
    RouteReply = 2,
    Data = 3,
    Ack = 4,
    SosBroadcast = 5,
    SosAck = 6,
    ChannelMessage = 7,
    ChunkRequest = 8,
    ChunkData = 9,
    Heartbeat = 10,
    StreamAnnounce = 11,
    StreamSegment = 12,
    StreamSubscribe = 13,
    StreamUnsubscribe = 14,
    VoicePtt = 15,
    VoiceCall = 16,
    VoiceSignaling = 17,
    DtnBundle = 18,
    DtnCustodyAck = 19,
    DtnDeliveryReceipt = 20,
    PresenceBeacon = 21,
    PresenceQuery = 22,
    ProfileSync = 23,
    TipPacket = 24,
    PreKeyRequest = 25,
    PreKeyResponse = 26,
    VideoCall = 27,
    VideoSignaling = 28,
    WatchSync = 29,
    WatchReaction = 30,
    VideoFrame = 31,
    ScreenShare = 32,
    WatchChunkRequest = 33,
    TorrentMetadata = 34,

    /// <summary>
    /// Group video session signaling — create, join, leave, kick, SFU relay assignment.
    /// Payload is JSON-encoded <c>GroupVideoSignalingMessage</c>.
    /// </summary>
    GroupVideoSignaling = 35,

    /// <summary>
    /// Adaptive-bitrate abandon marker. Publisher emits this instead of the next segment
    /// when the link cannot sustain even the floor bitrate rung. Receivers shred the
    /// in-flight parent and await the next announce.
    /// </summary>
    StreamAbandon = 36,

    /// <summary>
    /// Chunk availability bitmap — used by the Chunk Shuffle (Self-Assembling Peer
    /// Interleaving) protocol. A node broadcasts this to announce which chunks of a
    /// specific content it currently holds. Payload is a JSON-encoded
    /// <c>ChunkBitmapPayload</c> (snake_case). Peers use it to make informed
    /// pull decisions: each peer requests a non-overlapping random subset of the
    /// chunks the sender has that the requester lacks, avoiding duplicate in-flight
    /// transfers. The bitmap is re-broadcast after every 8 chunks received OR
    /// every 500 ms, whichever fires first (event-driven coalescing).
    /// </summary>
    ChunkBitmap = 37,

    /// <summary>
    /// Capability handshake — sender announces supported protocol-version range
    /// + capability flags. Sent on first contact with an unknown peer. The
    /// payload is a UTF-8 JSON-encoded <c>HelloPayload</c>. Unauthenticated and
    /// unencrypted — peer identity is verified later via Ed25519 packet
    /// signatures.
    /// </summary>
    Hello = 50,

    /// <summary>
    /// Reply to a <see cref="Hello"/> — receiver echoes back the agreed
    /// (highest mutually-supported) protocol version and the intersection of
    /// capability flags. Same JSON payload shape as <see cref="Hello"/>.
    /// </summary>
    HelloAck = 51,

    /// <summary>
    /// Reputation gossip — a node announces an observed score delta for a peer.
    /// Payload is a UTF-8 JSON-encoded <c>ReputationUpdatePayload</c>.
    /// Recipients MUST scale the claimed delta by the reporter's own local
    /// reputation score before applying it (see <c>ReputationUpdatePayload</c>).
    /// </summary>
    ReputationUpdate = 52,
}

/// <summary>
/// The core packet transmitted across the Aether mesh network.
/// Every piece of data — route discovery, messages, SOS broadcasts, voice,
/// streaming, DTN bundles — travels as a MeshPacket.
/// </summary>
public sealed class MeshPacket
{
    /// <summary>Unique identifier for this packet.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The type of packet, determining how the payload is interpreted.</summary>
    public PacketType Type { get; set; }

    /// <summary>Universal Hash ID of the source node.</summary>
    public string SourceUhid { get; set; } = string.Empty;

    /// <summary>Universal Hash ID of the destination node. Empty or "*" for broadcast.</summary>
    public string DestinationUhid { get; set; } = string.Empty;

    /// <summary>Time-to-live: decremented at each hop. Packet is dropped when TTL reaches 0.</summary>
    public int Ttl { get; set; } = 7;

    /// <summary>Priority level (higher = more urgent). SOS packets use priority 255.</summary>
    public byte Priority { get; set; }

    /// <summary>The packet payload. Interpretation depends on <see cref="Type"/>.</summary>
    public byte[] Payload { get; set; } = [];

    /// <summary>UTC timestamp when this packet was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cryptographic signature over the packet contents, produced by the source node.</summary>
    public byte[] Signature { get; set; } = [];

    /// <summary>Random nonce to prevent replay attacks. Must be unique per packet.</summary>
    public byte[] PacketNonce { get; set; } = [];

    /// <summary>Unix timestamp in milliseconds, used for age-based deduplication.</summary>
    public long TimestampMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Protocol version. Current version is 2.</summary>
    public byte ProtocolVersion { get; set; } = 2;

    /// <summary>
    /// Returns true if this packet has exceeded the maximum allowed age.
    /// </summary>
    public bool IsExpired(int maxAgeSeconds = 300)
    {
        var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - TimestampMs;
        return ageMs > maxAgeSeconds * 1000L;
    }

    /// <summary>
    /// Returns true if the packet can still be forwarded (TTL > 0).
    /// </summary>
    public bool CanForward => Ttl > 0;

    public override string ToString() =>
        $"[{Type}] {Id:N} src={SourceUhid} dst={DestinationUhid} ttl={Ttl} pri={Priority} ver={ProtocolVersion}";
}
