/**
 * Node/werift implementation of the {@link PeerLink} seam — the direct P2P WebRTC link over a
 * werift `RTCPeerConnection` + `RTCDataChannel` (pure TypeScript, no native build step, Node only).
 * This is the ONLY module that imports werift, so the browser build never pulls the Node-native
 * stack. The browser build supplies {@link PeerLink} from `peer-link.dom.ts` instead.
 *
 * SPDX-License-Identifier: MIT
 */

import { RTCPeerConnection, type RTCDataChannel } from "werift";

import type { IceServer, PeerLink, PeerLinkDeps, PeerLinkFactory } from "./peer-link.js";
import { Signal, SignalType, type Signaling } from "./signaling.js";

/** Data-channel label used by every AetherNet WebRTC link. */
const DATA_CHANNEL_LABEL = "aether";

/**
 * One WebRTC connection to a single peer over werift: an `RTCPeerConnection` plus its
 * `RTCDataChannel`, driving the offer/answer/ICE handshake over a {@link Signaling} channel and
 * surfacing received bytes.
 */
class WeriftPeerLink implements PeerLink {
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

    // werift types RTCIceServer.urls as a single string; expand any string[] into one entry per URL.
    const weriftIceServers = iceServers.flatMap((server) =>
      (Array.isArray(server.urls) ? server.urls : [server.urls]).map((url) => ({
        urls: url,
        username: server.username,
        credential: server.credential,
      })),
    );
    this.pc = new RTCPeerConnection({ iceServers: weriftIceServers });

    // Trickle local ICE candidates out over the signalling channel.
    this.pc.onIceCandidate.subscribe((candidate) => {
      // werift emits the RTCIceCandidate directly (undefined marks end-of-gathering) — NOT a
      // browser-style {candidate} event wrapper. Reading event.candidate dropped every candidate.
      if (!candidate) return;
      const json = candidate.toJSON();
      void this.signaling.sendSignal(this.peerUhid, {
        fromUhid: this.localUhid,
        toUhid: this.peerUhid,
        type: SignalType.Candidate,
        candidate: json.candidate,
        sdpMid: json.sdpMid ?? undefined,
        sdpMLineIndex: json.sdpMLineIndex ?? undefined,
      });
    });

    // The responder receives the channel the initiator created.
    this.pc.onDataChannel.subscribe((channel) => this.attach(channel));

    this.pc.connectionStateChange.subscribe((state) => {
      if (state === "failed" || state === "closed" || state === "disconnected") {
        this.markClosed();
      }
    });
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
    channel.stateChanged.subscribe((state) => {
      if (state === "open") {
        this.opened = true;
        this.resolveWaiters(true);
      } else if (state === "closed") {
        this.markClosed();
      }
    });
    channel.onMessage.subscribe((data) => {
      const bytes =
        typeof data === "string"
          ? new TextEncoder().encode(data)
          : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
      this.onData(this.peerUhid, bytes);
    });
    // The channel may already be open by the time we subscribe.
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
      if (typeof timer === "object" && "unref" in timer) {
        (timer as { unref: () => void }).unref();
      }
      this.openWaiters.push(settle);
    });
  }

  async send(data: Uint8Array, openTimeoutMs: number): Promise<boolean> {
    if (!(await this.waitOpen(openTimeoutMs))) return false;
    const channel = this.channel;
    if (channel === undefined) return false;
    try {
      channel.send(Buffer.from(data.buffer, data.byteOffset, data.byteLength));
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
      await this.pc.close();
    } catch {
      // best effort
    }
  }
}

/** Factory for the werift (Node) backend — pass to {@link WebRtcTransport} or register as default. */
export const createWeriftPeerLink: PeerLinkFactory = (deps: PeerLinkDeps): PeerLink =>
  new WeriftPeerLink(
    deps.localUhid,
    deps.peerUhid,
    deps.iceServers,
    deps.signaling,
    deps.onData,
  );
