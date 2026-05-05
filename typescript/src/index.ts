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
export { SignalProtocol } from "./security/SignalProtocol.js";
export {
  signPacket,
  verifyPacket,
  PacketDeduplicator,
} from "./security/PacketSigning.js";

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

// Constants
export * from "./constants.js";
