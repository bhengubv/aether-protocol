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
    default:
      return `Unknown(${type})`;
  }
}
