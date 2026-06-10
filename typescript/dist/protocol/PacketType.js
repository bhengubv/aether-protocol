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
    PacketType[PacketType["TipPacket"] = 24] = "TipPacket";
    PacketType[PacketType["PreKeyRequest"] = 25] = "PreKeyRequest";
    PacketType[PacketType["PreKeyResponse"] = 26] = "PreKeyResponse";
    PacketType[PacketType["VideoCall"] = 27] = "VideoCall";
    PacketType[PacketType["VideoSignaling"] = 28] = "VideoSignaling";
    PacketType[PacketType["WatchSync"] = 29] = "WatchSync";
    PacketType[PacketType["WatchReaction"] = 30] = "WatchReaction";
    PacketType[PacketType["VideoFrame"] = 31] = "VideoFrame";
    PacketType[PacketType["ScreenShare"] = 32] = "ScreenShare";
    PacketType[PacketType["WatchChunkRequest"] = 33] = "WatchChunkRequest";
    PacketType[PacketType["TorrentMetadata"] = 34] = "TorrentMetadata";
    /**
     * NamePublish — application-layer name resolution. Sent by IDirectoryService
     * to announce a (name -> ContentDescriptor) binding to the mesh, or in
     * response to an inbound NameQuery from a peer that asked for the binding.
     * Payload is a UTF-8 JSON-encoded NamePublishPayload. Added in v1.2.0 —
     * closes Issue #60 surfaced by Wave 16.
     */
    PacketType[PacketType["NamePublish"] = 38] = "NamePublish";
    /**
     * NameQuery — application-layer name resolution. Sent by IDirectoryService
     * when resolve() misses the local cache; flooded across the mesh so any
     * node holding the binding can reply with a NamePublish carrying the
     * matching ContentDescriptor. Payload is a UTF-8 JSON-encoded
     * NameQueryPayload. Added in v1.2.0 — closes Issue #60.
     */
    PacketType[PacketType["NameQuery"] = 39] = "NameQuery";
    /**
     * Capability handshake — sender announces supported protocol-version range
     * + capability flags. Sent on first contact with an unknown peer. The
     * payload is a UTF-8 JSON-encoded HelloPayload. Unauthenticated and
     * unencrypted — peer identity is verified later via Ed25519 packet
     * signatures.
     */
    PacketType[PacketType["Hello"] = 50] = "Hello";
    /**
     * Reply to a Hello — receiver echoes back the agreed (highest
     * mutually-supported) protocol version and the intersection of capability
     * flags. Same JSON payload shape as Hello.
     */
    PacketType[PacketType["HelloAck"] = 51] = "HelloAck";
    /**
     * BandwidthProbe — active probe packet sent to a peer to measure RTT and
     * delivery rate. Payload carries four timestamps per RFC 5136 §3.
     * Part of the AetherNet Bandwidth Measurement Framework (ABMF, W18-5).
     */
    PacketType[PacketType["BandwidthProbe"] = 53] = "BandwidthProbe";
    /**
     * BandwidthAck — ACK response to a BandwidthProbe.  Carries the receiver
     * timestamps so the sender can compute RTT without clock synchronisation.
     */
    PacketType[PacketType["BandwidthAck"] = 54] = "BandwidthAck";
    /**
     * BandwidthGossip — gossip payload broadcast during handshake to warm-start
     * a new session's BtlBw estimate from a previously measured value.
     * Unique to AetherNet; QUIC/TCP always cold-start.
     */
    PacketType[PacketType["BandwidthGossip"] = 55] = "BandwidthGossip";
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
        case PacketType.TipPacket:
            return "TipPacket";
        case PacketType.PreKeyRequest:
            return "PreKeyRequest";
        case PacketType.PreKeyResponse:
            return "PreKeyResponse";
        case PacketType.VideoCall:
            return "VideoCall";
        case PacketType.VideoSignaling:
            return "VideoSignaling";
        case PacketType.WatchSync:
            return "WatchSync";
        case PacketType.WatchReaction:
            return "WatchReaction";
        case PacketType.VideoFrame:
            return "VideoFrame";
        case PacketType.ScreenShare:
            return "ScreenShare";
        case PacketType.WatchChunkRequest:
            return "WatchChunkRequest";
        case PacketType.TorrentMetadata:
            return "TorrentMetadata";
        case PacketType.NamePublish:
            return "NamePublish";
        case PacketType.NameQuery:
            return "NameQuery";
        case PacketType.Hello:
            return "Hello";
        case PacketType.HelloAck:
            return "HelloAck";
        case PacketType.BandwidthProbe:
            return "BandwidthProbe";
        case PacketType.BandwidthAck:
            return "BandwidthAck";
        case PacketType.BandwidthGossip:
            return "BandwidthGossip";
        default:
            return `Unknown(${type})`;
    }
}
//# sourceMappingURL=PacketType.js.map