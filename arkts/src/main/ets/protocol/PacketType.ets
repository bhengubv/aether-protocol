/*
 * Packet type enumeration — values match the C# reference exactly, byte-for-byte
 * across every language port. ArkTS (HarmonyOS) port; mirrors
 * typescript/src/protocol/PacketType.ts.
 *
 * SPDX-License-Identifier: MIT
 */

/**
 * Packet type discriminator carried in the MeshPacket header. The numeric values
 * are wire-significant and MUST match the C# / TypeScript / Go reference.
 *
 * The two values exercised by this port are:
 *   - TipPacket = 24       — generic mesh tipping envelope (incentive layer).
 *   - PoVTokenExchange = 43 — on-mesh Proof-of-Vicinity token exchange (market layer).
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
  /** Generic "value-earned" relay-tip envelope. Matches the C# / Go reference value 24. */
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
  NamePublish = 38,
  NameQuery = 39,
  /**
   * On-mesh Proof-of-Vicinity token exchange. A witness node issues a directed
   * (TTL 1), Ed25519-signed PoVToken to a co-present subject; the subject verifies
   * the witness signature, counter-signs the same canonical body, and records it as
   * a local anti-Sybil routing/identity signal. Carried as a UTF-8 JSON-encoded
   * PoVToken. Two-key (witness + subject) co-presence proof — attaches NO value
   * semantics. Matches the C# / Go / TypeScript reference value 43.
   */
  PoVTokenExchange = 43,
  Hello = 50,
  HelloAck = 51,
  BandwidthProbe = 53,
  BandwidthAck = 54,
  BandwidthGossip = 55,
}

/** Returns the symbolic name for a packet type (diagnostics only). */
export function packetTypeToString(type: PacketType): string {
  switch (type) {
    case PacketType.RouteRequest:
      return 'RouteRequest';
    case PacketType.RouteReply:
      return 'RouteReply';
    case PacketType.Data:
      return 'Data';
    case PacketType.Ack:
      return 'Ack';
    case PacketType.SosBroadcast:
      return 'SosBroadcast';
    case PacketType.SosAck:
      return 'SosAck';
    case PacketType.ChannelMessage:
      return 'ChannelMessage';
    case PacketType.ChunkRequest:
      return 'ChunkRequest';
    case PacketType.ChunkData:
      return 'ChunkData';
    case PacketType.Heartbeat:
      return 'Heartbeat';
    case PacketType.StreamAnnounce:
      return 'StreamAnnounce';
    case PacketType.StreamSegment:
      return 'StreamSegment';
    case PacketType.StreamSubscribe:
      return 'StreamSubscribe';
    case PacketType.StreamUnsubscribe:
      return 'StreamUnsubscribe';
    case PacketType.VoicePtt:
      return 'VoicePtt';
    case PacketType.VoiceCall:
      return 'VoiceCall';
    case PacketType.VoiceSignaling:
      return 'VoiceSignaling';
    case PacketType.DtnBundle:
      return 'DtnBundle';
    case PacketType.DtnCustodyAck:
      return 'DtnCustodyAck';
    case PacketType.DtnDeliveryReceipt:
      return 'DtnDeliveryReceipt';
    case PacketType.PresenceBeacon:
      return 'PresenceBeacon';
    case PacketType.PresenceQuery:
      return 'PresenceQuery';
    case PacketType.ProfileSync:
      return 'ProfileSync';
    case PacketType.TipPacket:
      return 'TipPacket';
    case PacketType.PreKeyRequest:
      return 'PreKeyRequest';
    case PacketType.PreKeyResponse:
      return 'PreKeyResponse';
    case PacketType.VideoCall:
      return 'VideoCall';
    case PacketType.VideoSignaling:
      return 'VideoSignaling';
    case PacketType.WatchSync:
      return 'WatchSync';
    case PacketType.WatchReaction:
      return 'WatchReaction';
    case PacketType.VideoFrame:
      return 'VideoFrame';
    case PacketType.ScreenShare:
      return 'ScreenShare';
    case PacketType.WatchChunkRequest:
      return 'WatchChunkRequest';
    case PacketType.TorrentMetadata:
      return 'TorrentMetadata';
    case PacketType.NamePublish:
      return 'NamePublish';
    case PacketType.NameQuery:
      return 'NameQuery';
    case PacketType.PoVTokenExchange:
      return 'PoVTokenExchange';
    case PacketType.Hello:
      return 'Hello';
    case PacketType.HelloAck:
      return 'HelloAck';
    case PacketType.BandwidthProbe:
      return 'BandwidthProbe';
    case PacketType.BandwidthAck:
      return 'BandwidthAck';
    case PacketType.BandwidthGossip:
      return 'BandwidthGossip';
    default:
      return `Unknown(${type})`;
  }
}
