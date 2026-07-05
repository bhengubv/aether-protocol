/**
 * Browser implementation of the {@link PeerLink} seam — the direct P2P WebRTC link over the
 * browser's NATIVE `RTCPeerConnection` + `RTCDataChannel` (the DOM API, no werift). This is exactly
 * what the mission calls "swap werift for the DOM API": the same offer/answer/ICE dance and the same
 * {@link Signaling} wire, driven through `onicecandidate` / `ondatachannel` / `onmessage` instead of
 * werift's event emitters, and `ArrayBuffer` instead of `Buffer`.
 *
 * Requires the DOM lib — this file is part of the browser build only and is excluded from the Node
 * `tsc` build (see tsconfig `exclude`).
 *
 * SPDX-License-Identifier: MIT
 */

import type { IceServer, PeerLink, PeerLinkDeps, PeerLinkFactory } from "./peer-link.js";
import { Signal, SignalType, type Signaling } from "./signaling.js";

/** Data-channel label used by every AetherNet WebRTC link (matches werift/pion/SIPSorcery). */
const DATA_CHANNEL_LABEL = "aether";

/**
 * One WebRTC connection to a single peer over the browser's native `RTCPeerConnection`.
 */
class DomPeerLink implements PeerLink {
  private readonly localUhid: string;
  private readonly peerUhid: string;
  private readonly signaling: Signaling;
  private readonly onData: (peerUhid: string, data: Uint8Array) => void;
  private readonly pc: RTCPeerConnection;

  private channel?: RTCDataChannel;
  private closed = false;
  private opened = false;
  private readonly openWaiters: Array<(open: boolean) => void> = [];

  onClosed?: () => void;

  constructor(
    localUhid: string,
    peerUhid: string,
    iceServers: IceServer[],
    signaling: Signaling,
    onData: (peerUhid: string, data: Uint8Array) => void,
  ) {
    this.localUhid = localUhid;
    this.peerUhid = peerUhid;
    this.signaling = signaling;
    this.onData = onData;

    // The native browser RTCPeerConnection accepts { urls: string | string[] } directly.
    this.pc = new RTCPeerConnection({ iceServers });

    // Trickle local ICE candidates. DOM wraps the candidate in the event; event.candidate is null
    // at end-of-gathering (the wrapper werift does NOT have — this is the core of the swap).
    this.pc.onicecandidate = (event) => {
      const candidate = event.candidate;
      if (!candidate) return;
      const json = candidate.toJSON();
      void this.signaling.sendSignal(this.peerUhid, {
        fromUhid: this.localUhid,
        toUhid: this.peerUhid,
        type: SignalType.Candidate,
        candidate: json.candidate ?? undefined,
        sdpMid: json.sdpMid ?? undefined,
        sdpMLineIndex: json.sdpMLineIndex ?? undefined,
      });
    };

    // The responder receives the channel the initiator created.
    this.pc.ondatachannel = (event) => this.attach(event.channel);

    this.pc.onconnectionstatechange = () => {
      const state = this.pc.connectionState;
      if (state === "failed" || state === "closed" || state === "disconnected") {
        this.markClosed();
      }
    };
  }

  get isOpen(): boolean {
    return this.channel !== undefined && this.channel.readyState === "open";
  }

  get isClosed(): boolean {
    return this.closed;
  }

  async start(asInitiator: boolean): Promise<void> {
    if (!asInitiator) return; // responder waits for the inbound offer (acceptOffer)

    const channel = this.pc.createDataChannel(DATA_CHANNEL_LABEL);
    this.attach(channel);

    const offer = await this.pc.createOffer();
    await this.pc.setLocalDescription(offer);
    await this.signaling.sendSignal(this.peerUhid, {
      fromUhid: this.localUhid,
      toUhid: this.peerUhid,
      type: SignalType.Offer,
      sdp: offer.sdp,
    });
  }

  async acceptOffer(sdp: string): Promise<void> {
    await this.pc.setRemoteDescription({ type: "offer", sdp });
    const answer = await this.pc.createAnswer();
    await this.pc.setLocalDescription(answer);
    await this.signaling.sendSignal(this.peerUhid, {
      fromUhid: this.localUhid,
      toUhid: this.peerUhid,
      type: SignalType.Answer,
      sdp: answer.sdp,
    });
  }

  async acceptAnswer(sdp: string): Promise<void> {
    await this.pc.setRemoteDescription({ type: "answer", sdp });
  }

  async addRemoteCandidate(signal: Signal): Promise<void> {
    if (!signal.candidate) return;
    await this.pc.addIceCandidate({
      candidate: signal.candidate,
      sdpMid: signal.sdpMid,
      sdpMLineIndex: signal.sdpMLineIndex,
    });
  }

  private attach(channel: RTCDataChannel): void {
    this.channel = channel;
    channel.binaryType = "arraybuffer";
    channel.onopen = () => {
      this.opened = true;
      this.resolveWaiters(true);
    };
    channel.onclose = () => this.markClosed();
    channel.onmessage = (event) => {
      const data: unknown = event.data;
      const bytes =
        typeof data === "string"
          ? new TextEncoder().encode(data)
          : new Uint8Array(data as ArrayBuffer);
      this.onData(this.peerUhid, bytes);
    };
    if (channel.readyState === "open") {
      this.opened = true;
      this.resolveWaiters(true);
    }
  }

  private markClosed(): void {
    if (this.closed) return;
    this.closed = true;
    this.resolveWaiters(false);
    this.onClosed?.();
  }

  private resolveWaiters(open: boolean): void {
    while (this.openWaiters.length > 0) {
      this.openWaiters.shift()!(open);
    }
  }

  waitOpen(timeoutMs: number): Promise<boolean> {
    if (this.isOpen || this.opened) return Promise.resolve(true);
    if (this.closed) return Promise.resolve(false);
    return new Promise<boolean>((resolve) => {
      let settled = false;
      const settle = (open: boolean): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(open);
      };
      const timer = setTimeout(() => settle(false), timeoutMs);
      this.openWaiters.push(settle);
    });
  }

  async send(data: Uint8Array, openTimeoutMs: number): Promise<boolean> {
    if (!(await this.waitOpen(openTimeoutMs))) return false;
    const channel = this.channel;
    if (channel === undefined) return false;
    try {
      // Copy into a plain ArrayBuffer: DOM `send` wants an ArrayBuffer, not a possibly
      // SharedArrayBuffer-backed view (TS 5.7 distinguishes them). One copy per send.
      const buffer = new ArrayBuffer(data.byteLength);
      new Uint8Array(buffer).set(data);
      channel.send(buffer);
      return true;
    } catch {
      return false;
    }
  }

  async close(): Promise<void> {
    this.markClosed();
    try {
      this.channel?.close();
    } catch {
      // best effort
    }
    try {
      this.pc.close();
    } catch {
      // best effort
    }
  }
}

/** Factory for the browser (native DOM) backend — register with `setDefaultPeerLinkFactory`. */
export const createDomPeerLink: PeerLinkFactory = (deps: PeerLinkDeps): PeerLink =>
  new DomPeerLink(deps.localUhid, deps.peerUhid, deps.iceServers, deps.signaling, deps.onData);
