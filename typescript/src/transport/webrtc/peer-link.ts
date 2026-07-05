/**
 * Backend-agnostic WebRTC peer-link seam. One AetherNet WebRTC connection to a single peer — an
 * RTCPeerConnection plus its RTCDataChannel — driving the offer/answer/ICE handshake over a
 * {@link Signaling} channel and surfacing received bytes. Two implementations satisfy it:
 * `peer-link.werift.ts` (Node, werift) and `peer-link.dom.ts` (browser, the native
 * RTCPeerConnection). {@link WebRtcTransport} depends only on this interface, so the identical
 * transport logic runs on both platforms.
 *
 * SPDX-License-Identifier: MIT
 */

import type { Signal, Signaling } from "./signaling.js";

/** ICE server config, in the shape both werift and the browser's RTCPeerConnection accept. */
export interface IceServer {
  urls: string | string[];
  username?: string;
  credential?: string;
}

/** One WebRTC connection to a single peer. */
export interface PeerLink {
  /** True once the data channel is open. */
  readonly isOpen: boolean;
  /** True once this link has reached a terminal (closed/failed) state. */
  readonly isClosed: boolean;
  /** Invoked once when this link transitions to a terminal state. */
  onClosed?: () => void;
  /** Begins the handshake. The initiator creates the data channel and sends the offer. */
  start(asInitiator: boolean): Promise<void>;
  /** Handles an inbound SDP offer (responder side) and replies with an answer. */
  acceptOffer(sdp: string): Promise<void>;
  /** Handles an inbound SDP answer (initiator side). */
  acceptAnswer(sdp: string): Promise<void>;
  /** Adds a trickled remote ICE candidate. */
  addRemoteCandidate(signal: Signal): Promise<void>;
  /** Resolves `true` once the data channel is open, or `false` on timeout / terminal close. */
  waitOpen(timeoutMs: number): Promise<boolean>;
  /** Waits for the channel to open (up to `openTimeoutMs`) then sends `data`. */
  send(data: Uint8Array, openTimeoutMs: number): Promise<boolean>;
  /** Tears the connection down. */
  close(): Promise<void>;
}

/** Everything a backend needs to build a {@link PeerLink} for one peer. */
export interface PeerLinkDeps {
  readonly localUhid: string;
  readonly peerUhid: string;
  readonly iceServers: IceServer[];
  readonly signaling: Signaling;
  readonly onData: (peerUhid: string, data: Uint8Array) => void;
}

/** Builds a {@link PeerLink} for one peer — werift on Node, the native DOM API in the browser. */
export type PeerLinkFactory = (deps: PeerLinkDeps) => PeerLink;

let defaultFactory: PeerLinkFactory | undefined;

/**
 * Registers the platform's WebRTC backend. The Node entry point registers the werift backend; the
 * browser entry point registers the native-DOM backend. Idempotent; last registration wins.
 */
export function setDefaultPeerLinkFactory(factory: PeerLinkFactory): void {
  defaultFactory = factory;
}

/**
 * Returns the registered backend, or throws if no entry point has registered one. A caller can also
 * bypass this by passing an explicit `peerLinkFactory` to {@link WebRtcTransport}.
 */
export function getDefaultPeerLinkFactory(): PeerLinkFactory {
  if (defaultFactory === undefined) {
    throw new Error(
      "AetherNet WebRTC: no PeerLink backend registered. Import the package's Node entry " +
        "('@bhengubv/aethernet-protocol') or its browser entry, or pass an explicit " +
        "peerLinkFactory to WebRtcTransport.",
    );
  }
  return defaultFactory;
}
