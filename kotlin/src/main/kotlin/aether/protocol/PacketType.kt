// SPDX-License-Identifier: MIT

package aether.protocol

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
    TorrentMetadata(34);

    companion object {
        fun fromValue(value: Byte): PacketType? = values().find { it.value == value }
    }
}
