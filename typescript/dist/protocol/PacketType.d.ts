/**
 * Packet type enumeration matching C# values exactly
 * SPDX-License-Identifier: MIT
 */
export declare enum PacketType {
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
    ProfileSync = 23
}
export declare function packetTypeToString(type: PacketType): string;
//# sourceMappingURL=PacketType.d.ts.map