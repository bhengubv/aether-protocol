/**
 * Native circuit-relay-v2 — decentralised any-node relaying on the Aether mesh.
 * SPDX-License-Identifier: MIT
 */

export {
  RelayMessageType,
  RelayStatus,
  RELAY_FRAME_VERSION,
  serializeRelayFrame,
  deserializeRelayFrame,
} from "./RelayFrame.js";
export type { RelayFrame } from "./RelayFrame.js";
