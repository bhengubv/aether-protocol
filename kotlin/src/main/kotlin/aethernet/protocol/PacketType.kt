// SPDX-License-Identifier: MIT

package aethernet.protocol

/**
 * Enumeration of Aether mesh packet types.
 * Values match the C# implementation exactly for wire-format compatibility.
 */
enum class PacketType(val value: Byte) {
    RouteRequest(1),
    RouteReply(2),
    Data(3),
    Ack(4),
    SosBroadcast(5),
    SosAck(6),
    ChannelMessage(7),
    ChunkRequest(8),
    ChunkData(9),
    Heartbeat(10),
    StreamAnnounce(11),
    StreamSegment(12),
    StreamSubscribe(13),
    StreamUnsubscribe(14),
    VoicePtt(15),
    VoiceCall(16),
    VoiceSignaling(17),
    DtnBundle(18),
    DtnCustodyAck(19),
    DtnDeliveryReceipt(20),
    PresenceBeacon(21),
    PresenceQuery(22),
    ProfileSync(23),
    TipPacket(24),
    PreKeyRequest(25),
    PreKeyResponse(26),
    VideoCall(27),
    VideoSignaling(28),
    WatchSync(29),
    WatchReaction(30),
    VideoFrame(31),
    ScreenShare(32),
    WatchChunkRequest(33),
    TorrentMetadata(34),

    /**
     * PoVTokenExchange - directed, two-key Proof-of-Vicinity co-presence proof.
     * A witness node issues a token vouching for a co-present subject and sends it
     * point-to-point (TTL 1 - the subject is one short-range hop away); the subject
     * verifies the witness's Ed25519 signature over the canonical token body,
     * counter-signs, and records it as a purely local anti-Sybil routing/identity
     * signal (NO value semantics). Payload is a UTF-8 JSON-encoded
     * [aethernet.market.PoVToken]. Wire value 43 - matches the C# and Go
     * implementations.
     */
    PoVTokenExchange(43),

    /**
     * NamePublish - application-layer name resolution. Sent by IDirectoryService
     * to announce a (name -> ContentDescriptor) binding to the mesh, or in
     * response to an inbound NameQuery from a peer. Payload is a UTF-8 JSON-
     * encoded NamePublishPayload. Added in v1.2.0 - closes Issue #60.
     */
    NamePublish(38),

    /**
     * NameQuery - application-layer name resolution. Sent by IDirectoryService
     * when resolve() misses the local cache; flooded across the mesh so any
     * node holding the binding can reply with a NamePublish. Payload is a
     * UTF-8 JSON-encoded NameQueryPayload. Added in v1.2.0 - closes Issue #60.
     */
    NameQuery(39),

    /**
     * Capability handshake -- sender announces supported protocol-version range
     * + capability flags. Sent on first contact with an unknown peer. The
     * payload is a UTF-8 JSON-encoded `HelloPayload` (see the `aethernet.handshake`
     * package). Unauthenticated and unencrypted -- peer identity is verified
     * later via Ed25519 packet signatures.
     */
    Hello(50),

    /**
     * Reply to a [Hello] -- receiver echoes back the agreed (highest mutually-
     * supported) protocol version and the intersection of capability flags.
     * Same JSON payload shape as [Hello].
     */
    HelloAck(51),

    /**
     * Signed peer-to-peer reputation-score gossip packet.
     * Payload is UTF-8 JSON-encoded [aethernet.reputation.ReputationGossipService.ReputationUpdatePayload].
     * Wire value 52 -- matches all other language implementations.
     */
    ReputationUpdate(52),

    /**
     * Active bandwidth probe packet (AetherNet Bandwidth Measurement Framework -- ABMF W18-5).
     * Sent to a target peer to measure RTT and delivery rate. The peer responds with
     * [BandwidthAck] carrying four timestamps for clock-sync-free RTT derivation.
     * Probe overhead is limited to < 0.5 % of the current BDP estimate.
     * Wire value 53 -- matches C#, Go, Python, TypeScript, Rust implementations.
     */
    BandwidthProbe(53),

    /**
     * Response to a [BandwidthProbe] packet (ABMF W18-5).
     * Carries four timestamps: senderSendUs, receiverReceiveUs, receiverSendUs, senderReceiveUs.
     * RTT is derived from sender-side timestamps only -- no clock synchronisation required.
     * Wire value 54 -- matches all other language implementations.
     */
    BandwidthAck(54),

    /**
     * Mesh gossip payload for bandwidth warm-start (ABMF W18-5).
     * Broadcast to new peers during handshake so sessions start with a non-zero BtlBw estimate
     * instead of cold-starting at ~14.6 kB/s (RFC 6928 SS2). Unique to AetherNet.
     * Wire value 55 -- matches all other language implementations.
     */
    BandwidthGossip(55);

    companion object {
        fun fromValue(value: Byte): PacketType? = values().find { it.value == value }
    }
}
