/**
 * Aether Mesh Networking Protocol - TypeScript Implementation
 * SPDX-License-Identifier: MIT
 */

// Protocol
export { PacketType, packetTypeToString } from "./protocol/PacketType.js";
export { MeshPacket } from "./protocol/MeshPacket.js";
export { PacketSerializer } from "./protocol/PacketSerializer.js";

// Security
export { Ed25519Service } from "./security/Ed25519Service.js";
export {
  SignalProtocol,
  DEFAULT_OPK_POOL_SIZE,
  DEFAULT_SPK_ROTATION_OPTIONS,
} from "./security/SignalProtocol.js";
export type {
  SignalProtocolOptions,
  SignedPreKeyRotationOptions,
  PreKeyBundle,
  EncryptedPayload,
  OpkPoolStatus,
} from "./security/SignalProtocol.js";
export {
  InMemorySignalSessionStore,
  KeyValueSignalSessionStore,
  serializeSignalSession,
  deserializeSignalSession,
} from "./security/SignalSessionStore.js";
export type {
  SignalSessionStore,
  StoredSignalSession,
} from "./security/SignalSessionStore.js";
export {
  InMemoryPreKeyStore,
  KeyValuePreKeyStore,
} from "./security/PreKeyStore.js";
export type {
  PreKeyStore,
  StoredIdentityKeys,
  StoredSignedPreKey,
  StoredSignedPreKeyHistory,
  StoredOneTimePreKey,
} from "./security/PreKeyStore.js";
export {
  signPacket,
  verifyPacket,
  PacketDeduplicator,
} from "./security/PacketSigning.js";

// Storage
export * from "./storage/index.js";

// Transport
export { InProcessTransport } from "./transport/InProcessTransport.js";
export * from "./transport/webrtc/index.js";

// Models (extended)
export * from "./models/index.js";

// Extensibility seams
export * from "./extensibility.js";

// Routing
export * from "./routing/index.js";

// DTN
export * from "./dtn/index.js";

// Circuit relay (native circuit-relay-v2 wire frame)
export * from "./circuitrelay/index.js";

// SOS
export * from "./sos/index.js";

// Heartbeat — liveness beacons (PacketType.Heartbeat = 10)
export * from "./heartbeat/index.js";

// Channels — named-channel pub/sub (PacketType.ChannelMessage = 7)
export * from "./channels/index.js";

// Video call-control — directed ring/accept/decline/hangup (PacketType.VideoCall = 27)
export * from "./videocall/index.js";

// PreKey exchange — directed transport of a PreKeyBundle (PacketType.PreKeyRequest = 25 / PreKeyResponse = 26)
export * from "./prekey/index.js";

// Profiles — directed peer-profile exchange (PacketType.ProfileSync = 23)
export * from "./profiles/index.js";

// Presence — "I'm here" beacon + "who's around?" query (PacketType.PresenceBeacon = 21 / PresenceQuery = 22)
export * from "./presence/index.js";

// ERID-announce — directed transport of an already-encrypted ERID announcement (PacketType.EridAnnounce = 56)
export * from "./eridannounce/index.js";

// Handshake
export * from "./handshake/index.js";

// Voice
export * from "./voice/index.js";

// Streaming, video, watch-together
export * from "./streaming/index.js";

// Identity
export * from "./identity/index.js";

// URI scheme
export * from "./uri/index.js";

// Content (descriptors + directory)
export * from "./content/index.js";

// Constants
export * from "./constants.js";

// Bandwidth Measurement Framework (ABMF, W18-5)
export * from "./bandwidth/index.js";

// Incentive — generic mesh tipping (PacketType.TipPacket = 24)
export * from "./incentive/index.js";

// Vault — systematic Cauchy-Reed-Solomon erasure coding (GF(2⁸), 0x11D, α=2)
export * from "./vault/index.js";

// Vault shard-request WIRE binding (PacketType.VaultShardRequest = 42)
export * from "./vaultshard/index.js";

// Space — geo-pinned noticeboard + WIRE binding (PacketType.SpaceBreadcrumb = 40)
export * from "./space/index.js";

// Forge — mesh-native package cache + WIRE binding (PacketType.ForgeAnnounce = 41)
export * from "./forge/index.js";

// Market — Proof-of-Vicinity tokens + on-mesh exchange (PacketType.PoVTokenExchange = 43)
export * from "./market/index.js";

// Reputation gossip
export {
  ReputationGossipService,
  REPUTATION_UPDATE_TYPE,
} from "./gossip.js";
export type {
  ReputationUpdatePayload,
  Packet,
  MeshSender,
  PacketSigner,
} from "./gossip.js";
