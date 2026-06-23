/**
 * Real WebRTC P2P transport for AetherNet (werift — pure TypeScript, no native dependency).
 * SPDX-License-Identifier: MIT
 */

export {
  WebRtcTransport,
  defaultIceServers,
} from "./WebRtcTransport.js";

export {
  type Signal,
  type Signaling,
  SignalType,
  InMemorySignalingBus,
} from "./signaling.js";
