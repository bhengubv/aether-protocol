/**
 * Packet type enumeration matching C# values exactly
 * SPDX-License-Identifier: MIT
 */

export enum PacketType {
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

  /**
   * Capability handshake — sender announces supported protocol-version range
   * + capability flags. Sent on first contact with an unknown peer. The
   * payload is a UTF-8 JSON-encoded HelloPayload. Unauthenticated and
   * unencrypted — peer identity is verified later via Ed25519 packet
   * signatures.
   */
  Hello = 50,

  /**
   * Reply to a Hello — receiver echoes back the agreed (highest
   * mutually-supported) protocol version and the intersection of capability
   * flags. Same JSON payload shape as Hello.
   */
  HelloAck = 51,
}

export function packetTypeToString(type: PacketType): string {
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
    case PacketType.Hello:
      return "Hello";
    case PacketType.HelloAck:
      return "HelloAck";
    default:
      return `Unknown(${type})`;
  }
}
