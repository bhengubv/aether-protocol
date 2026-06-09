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

// Models (extended)
export * from "./models/index.js";

// Extensibility seams
export * from "./extensibility.js";

// Routing
export * from "./routing/index.js";

// DTN
export * from "./dtn/index.js";

// SOS
export * from "./sos/index.js";

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
