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

export { Transport, defaultRelayOptions } from "./Transport.js";
export type { RelayLink, RelayOptions } from "./Transport.js";

export { MeshRelayLink, MeshCircuitRelay } from "./MeshRelayLink.js";
export type { SendOneHop, CanReachFn } from "./MeshRelayLink.js";
export { CircuitRelayTransportService } from "./CircuitRelayTransportService.js";
