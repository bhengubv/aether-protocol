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
   * PoVTokenExchange — on-mesh Proof-of-Vicinity token exchange. A witness node
   * issues a directed (TTL 1), Ed25519-signed PoVToken to a co-present subject;
   * the subject verifies the witness signature, counter-signs the same canonical
   * body, and records it as a local anti-Sybil routing/identity signal. Carried
   * as a UTF-8 JSON-encoded PoVToken. Two-key (witness + subject) co-presence
   * proof — attaches NO value semantics. Matches the C# / Go reference value 43.
   */
  PoVTokenExchange = 43,

  /**
   * NamePublish — application-layer name resolution. Sent by IDirectoryService
   * to announce a (name -> ContentDescriptor) binding to the mesh, or in
   * response to an inbound NameQuery from a peer that asked for the binding.
   * Payload is a UTF-8 JSON-encoded NamePublishPayload. Added in v1.2.0 —
   * closes Issue #60 surfaced by Wave 16.
   */
  NamePublish = 38,

  /**
   * NameQuery — application-layer name resolution. Sent by IDirectoryService
   * when resolve() misses the local cache; flooded across the mesh so any
   * node holding the binding can reply with a NamePublish carrying the
   * matching ContentDescriptor. Payload is a UTF-8 JSON-encoded
   * NameQueryPayload. Added in v1.2.0 — closes Issue #60.
   */
  NameQuery = 39,

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

  /**
   * BandwidthProbe — active probe packet sent to a peer to measure RTT and
   * delivery rate. Payload carries four timestamps per RFC 5136 §3.
   * Part of the AetherNet Bandwidth Measurement Framework (ABMF, W18-5).
   */
  BandwidthProbe = 53,

  /**
   * BandwidthAck — ACK response to a BandwidthProbe.  Carries the receiver
   * timestamps so the sender can compute RTT without clock synchronisation.
   */
  BandwidthAck = 54,

  /**
   * BandwidthGossip — gossip payload broadcast during handshake to warm-start
   * a new session's BtlBw estimate from a previously measured value.
   * Unique to AetherNet; QUIC/TCP always cold-start.
   */
  BandwidthGossip = 55,
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
    case PacketType.PoVTokenExchange:
      return "PoVTokenExchange";
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
