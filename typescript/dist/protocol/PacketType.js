/**
 * Packet type enumeration matching C# values exactly
 * SPDX-License-Identifier: MIT
 */
export var PacketType;
(function (PacketType) {
    PacketType[PacketType["RouteRequest"] = 1] = "RouteRequest";
    PacketType[PacketType["RouteReply"] = 2] = "RouteReply";
    PacketType[PacketType["Data"] = 3] = "Data";
    PacketType[PacketType["Ack"] = 4] = "Ack";
    PacketType[PacketType["SosBroadcast"] = 5] = "SosBroadcast";
    PacketType[PacketType["SosAck"] = 6] = "SosAck";
    PacketType[PacketType["ChannelMessage"] = 7] = "ChannelMessage";
    PacketType[PacketType["ChunkRequest"] = 8] = "ChunkRequest";
    PacketType[PacketType["ChunkData"] = 9] = "ChunkData";
    PacketType[PacketType["Heartbeat"] = 10] = "Heartbeat";
    PacketType[PacketType["StreamAnnounce"] = 11] = "StreamAnnounce";
    PacketType[PacketType["StreamSegment"] = 12] = "StreamSegment";
    PacketType[PacketType["StreamSubscribe"] = 13] = "StreamSubscribe";
    PacketType[PacketType["StreamUnsubscribe"] = 14] = "StreamUnsubscribe";
    PacketType[PacketType["VoicePtt"] = 15] = "VoicePtt";
    PacketType[PacketType["VoiceCall"] = 16] = "VoiceCall";
    PacketType[PacketType["VoiceSignaling"] = 17] = "VoiceSignaling";
    PacketType[PacketType["DtnBundle"] = 18] = "DtnBundle";
    PacketType[PacketType["DtnCustodyAck"] = 19] = "DtnCustodyAck";
    PacketType[PacketType["DtnDeliveryReceipt"] = 20] = "DtnDeliveryReceipt";
    PacketType[PacketType["PresenceBeacon"] = 21] = "PresenceBeacon";
    PacketType[PacketType["PresenceQuery"] = 22] = "PresenceQuery";
    PacketType[PacketType["ProfileSync"] = 23] = "ProfileSync";
})(PacketType || (PacketType = {}));
export function packetTypeToString(type) {
    switch (type) {
        case PacketType.RouteRequest:
            return "RouteRequest";
        case PacketType.RouteReply:
            return "RouteReply";
        case PacketType.Data:
            return "Data";
        case PacketType.Ack:
            return "Ack";
        case PacketType.SosBroadcast:
            return "SosBroadcast";
        case PacketType.SosAck:
            return "SosAck";
        case PacketType.ChannelMessage:
            return "ChannelMessage";
        case PacketType.ChunkRequest:
            return "ChunkRequest";
        case PacketType.ChunkData:
            return "ChunkData";
        case PacketType.Heartbeat:
            return "Heartbeat";
        case PacketType.StreamAnnounce:
            return "StreamAnnounce";
        case PacketType.StreamSegment:
            return "StreamSegment";
        case PacketType.StreamSubscribe:
            return "StreamSubscribe";
        case PacketType.StreamUnsubscribe:
            return "StreamUnsubscribe";
        case PacketType.VoicePtt:
            return "VoicePtt";
        case PacketType.VoiceCall:
            return "VoiceCall";
        case PacketType.VoiceSignaling:
            return "VoiceSignaling";
        case PacketType.DtnBundle:
            return "DtnBundle";
        case PacketType.DtnCustodyAck:
            return "DtnCustodyAck";
        case PacketType.DtnDeliveryReceipt:
            return "DtnDeliveryReceipt";
        case PacketType.PresenceBeacon:
            return "PresenceBeacon";
        case PacketType.PresenceQuery:
            return "PresenceQuery";
        case PacketType.ProfileSync:
            return "ProfileSync";
        default:
            return `Unknown(${type})`;
    }
}
//# sourceMappingURL=PacketType.js.map