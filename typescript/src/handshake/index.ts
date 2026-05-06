/**
 * Handshake module barrel.
 * SPDX-License-Identifier: MIT
 */

export {
  HandshakeService,
  DEFAULT_CAPABILITIES,
  DEFAULT_IMPLEMENTATION,
  type HandshakeServiceOptions,
  type PeerNegotiatedListener,
  type IncompatiblePeerListener,
} from "./HandshakeService.js";
export type {
  HelloPayload,
  PeerCapabilities,
  IncompatiblePeerEvent,
} from "./models.js";
