/**
 * Browser entry point for the AetherNet TypeScript SDK. Registers the native-DOM WebRTC backend
 * (the browser's `RTCPeerConnection`) instead of werift, then re-exports the browser-safe transport
 * surface. Consumed via the package's `"browser"` export condition — bundlers (esbuild/vite/webpack)
 * pick this over the Node entry automatically. The Node entry (`index.ts`) registers werift instead.
 *
 * SPDX-License-Identifier: MIT
 */

import { setDefaultPeerLinkFactory } from "./transport/webrtc/peer-link.js";
import { createDomPeerLink } from "./transport/webrtc/peer-link.dom.js";

// Importing this browser entry registers the native RTCPeerConnection backend for WebRtcTransport.
setDefaultPeerLinkFactory(createDomPeerLink);

// ── WebRTC transport (native DOM backend) ──────────────────────────────────────
export { WebRtcTransport, defaultIceServers } from "./transport/webrtc/WebRtcTransport.js";
export { createDomPeerLink } from "./transport/webrtc/peer-link.dom.js";
export {
  setDefaultPeerLinkFactory,
  getDefaultPeerLinkFactory,
} from "./transport/webrtc/peer-link.js";
export type {
  PeerLink,
  PeerLinkDeps,
  PeerLinkFactory,
  IceServer,
} from "./transport/webrtc/peer-link.js";

// ── Signalling ─────────────────────────────────────────────────────────────────
export { InMemorySignalingBus, SignalType } from "./transport/webrtc/signaling.js";
export type { Signal, Signaling } from "./transport/webrtc/signaling.js";
export {
  RelayWebRtcSignaling,
  encodeSignalFrame,
  decodeSignalFrame,
} from "./transport/webrtc/RelayWebRtcSignaling.js";
export type { SignalingChannel } from "./transport/webrtc/RelayWebRtcSignaling.js";
