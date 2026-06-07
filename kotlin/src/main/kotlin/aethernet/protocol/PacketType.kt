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
     * Capability handshake — sender announces supported protocol-version range
     * + capability flags. Sent on first contact with an unknown peer. The
     * payload is a UTF-8 JSON-encoded `HelloPayload` (see the `aethernet.handshake`
     * package). Unauthenticated and unencrypted — peer identity is verified
     * later via Ed25519 packet signatures.
     */
    Hello(50),

    /**
     * Reply to a [Hello] — receiver echoes back the agreed (highest mutually-
     * supported) protocol version and the intersection of capability flags.
     * Same JSON payload shape as [Hello].
     */
    HelloAck(51),

    /**
     * Signed peer-to-peer reputation-score gossip packet.
     * Payload is UTF-8 JSON-encoded [aethernet.reputation.ReputationGossipService.ReputationUpdatePayload].
     * Wire value 52 — matches all other language implementations.
     */
    ReputationUpdate(52);

    companion object {
        fun fromValue(value: Byte): PacketType? = values().find { it.value == value }
    }
}
