/**
 * WebRTC P2P transport for AetherNet. Backend-agnostic behind the {@link PeerLink} seam: this
 * (Node) entry registers the werift backend; the browser entry registers the native-DOM backend.
 * SPDX-License-Identifier: MIT
 */

import { setDefaultPeerLinkFactory } from "./peer-link.js";
import { createWeriftPeerLink } from "./peer-link.werift.js";

// Importing this Node entry registers werift as the default WebRTC backend. The browser entry
// (peer-link.dom.ts, wired by the browser build) registers the native RTCPeerConnection instead.
setDefaultPeerLinkFactory(createWeriftPeerLink);

export {
  WebRtcTransport,
  defaultIceServers,
} from "./WebRtcTransport.js";

export {
  type PeerLink,
  type PeerLinkDeps,
  type PeerLinkFactory,
  type IceServer,
  setDefaultPeerLinkFactory,
  getDefaultPeerLinkFactory,
} from "./peer-link.js";

export { createWeriftPeerLink } from "./peer-link.werift.js";

export {
  type Signal,
  type Signaling,
  SignalType,
  InMemorySignalingBus,
} from "./signaling.js";

export {
  RelayWebRtcSignaling,
  type SignalingChannel,
  encodeSignalFrame,
  decodeSignalFrame,
} from "./RelayWebRtcSignaling.js";
