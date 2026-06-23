/**
 * Direct peer-to-peer transport for AetherNet over a WebRTC data channel (werift — pure TypeScript,
 * no native dependency).
 *
 * NAT traversal is handled by ICE/STUN; the SDP/ICE handshake (trickled) rides an injected
 * {@link Signaling} channel, so no central signalling server is required. Implements
 * {@link ITransportService} so the transport ladder can rank it between the radio mesh (cheap,
 * proximity) and the relay (last resort): a direct internet path is used when one can be negotiated.
 *
 * Mirrors the C# `WebRtcTransportService` / `WebRtcPeerLink` (SIPSorcery) and the Go `WebRtcTransport`
 * / `peerLink` (pion). Received bytes are surfaced via {@link onDataReceived}, exactly as
 * {@link InProcessTransport} does.
 *
 * SPDX-License-Identifier: MIT
 */

import {
  RTCPeerConnection,
  type RTCDataChannel,
  type RTCIceServer,
} from "werift";

import { ITransportService, PerTransportMetrics } from "../ITransportService.js";
import { Signal, Signaling, SignalType } from "./signaling.js";

/** Data-channel label used by every AetherNet WebRTC link. */
const DATA_CHANNEL_LABEL = "aether";

/** How long {@link WebRtcTransport.sendAsync} waits for the data channel to open. */
const CONNECT_TIMEOUT_MS = 20_000;

/** The public STUN baseline used when a caller passes `undefined` ICE servers. */
export function defaultIceServers(): RTCIceServer[] {
  return [{ urls: "stun:stun.l.google.com:19302" }];
}

/**
 * Direct P2P transport over a WebRTC `RTCDataChannel` (werift).
 *
 * Pass `undefined` `iceServers` for the STUN default, or an explicit (possibly empty) list to
 * control ICE — an **empty** list forces host-candidate-only ICE, with no network dependency.
 */
export class WebRtcTransport implements ITransportService {
  private readonly localUhid: string;
  private readonly signaling: Signaling;
  private readonly iceServers: RTCIceServer[];
  private readonly peers = new Map<string, PeerLink>();
  private disposed = false;

  readonly name: string = "WebRTC P2P";
  maxBandwidthBps: number = 100_000_000; // direct link — bounded by the local NIC
  maxRangeMeters: number = 0;            // internet — unbounded
  powerCostRelative: number = 5;         // dearer than local radio on the 1-10 scale
  maxConcurrentPeers: number = 256;
  readonly metrics: PerTransportMetrics = new PerTransportMetrics();
  onDataReceived?: (senderUhid: string, data: Uint8Array) => void;

  /**
   * @param localUhid  This node's UHID.
   * @param signaling  The channel carrying SDP/ICE between peers.
   * @param iceServers `undefined` => STUN default; an explicit (even empty) list is respected
   *                   verbatim — pass `[]` to force host-candidate-only ICE.
   */
  constructor(localUhid: string, signaling: Signaling, iceServers?: RTCIceServer[]) {
    if (!localUhid || localUhid.trim().length === 0) {
      throw new Error("WebRtcTransport: localUhid must not be empty");
    }
    if (!signaling) {
      throw new Error("WebRtcTransport: signaling is required");
    }
    this.localUhid = localUhid;
    this.signaling = signaling;
    this.iceServers = iceServers ?? defaultIceServers();
    this.signaling.onSignal((signal) => {
      void this.handleSignal(signal);
    });
  }

  get isAvailable(): boolean {
    return !this.disposed;
  }

  // ── ITransportService ───────────────────────────────────────────────────────

  async sendAsync(
    peerUhid: string,
    data: Uint8Array,
    _cancellationToken?: AbortSignal,
  ): Promise<boolean> {
    if (this.disposed || !peerUhid || peerUhid.trim().length === 0) return false;
    if (data.length === 0) return false;

    const link = await this.getOrCreateLink(peerUhid, true);
    if (link === undefined) return false;

    const start = Date.now();
    const ok = await link.send(data, CONNECT_TIMEOUT_MS);
    const elapsed = Date.now() - start;
    this.metrics.recordSample(elapsed, ok, ok ? data.length : 0);
    return ok;
  }

  async sendStreamAsync(
    peerUhid: string,
    stream: ReadableStream<Uint8Array>,
    cancellationToken?: AbortSignal,
  ): Promise<boolean> {
    if (this.disposed) return false;

    const chunks: Uint8Array[] = [];
    const reader = stream.getReader();
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value) chunks.push(value);
      }
    } finally {
      reader.releaseLock();
    }

    const total = chunks.reduce((sum, c) => sum + c.length, 0);
    const combined = new Uint8Array(total);
    let offset = 0;
    for (const chunk of chunks) {
      combined.set(chunk, offset);
      offset += chunk.length;
    }
    return this.sendAsync(peerUhid, combined, cancellationToken);
  }

  isConnected(peerUhid: string): boolean {
    if (this.disposed || !peerUhid) return false;
    const link = this.peers.get(peerUhid);
    return link !== undefined && link.isOpen;
  }

  /** Tears down all peer connections. */
  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    const links = [...this.peers.values()];
    this.peers.clear();
    for (const link of links) {
      await link.close();
    }
    this.onDataReceived = undefined;
  }

  // ── Signalling inbound ──────────────────────────────────────────────────────

  private async handleSignal(signal: Signal): Promise<void> {
    if (this.disposed || signal.toUhid !== this.localUhid) return;
    try {
      switch (signal.type) {
        case SignalType.Offer: {
          const link = await this.getOrCreateLink(signal.fromUhid, false);
          if (link !== undefined && signal.sdp !== undefined) {
            await link.acceptOffer(signal.sdp);
          }
          break;
        }
        case SignalType.Answer: {
          const link = this.peers.get(signal.fromUhid);
          if (link !== undefined && signal.sdp !== undefined) {
            await link.acceptAnswer(signal.sdp);
          }
          break;
        }
        case SignalType.Candidate: {
          const link = this.peers.get(signal.fromUhid);
          if (link !== undefined) {
            await link.addRemoteCandidate(signal);
          }
          break;
        }
      }
    } catch {
      // A signalling failure must not crash the transport; ICE re-gathers on reconnect.
    }
  }

  private async getOrCreateLink(
    peerUhid: string,
    asInitiator: boolean,
  ): Promise<PeerLink | undefined> {
    if (this.disposed) return undefined;

    const existing = this.peers.get(peerUhid);
    if (existing !== undefined && !existing.isClosed) {
      if (asInitiator) await existing.waitOpen(CONNECT_TIMEOUT_MS);
      return existing;
    }

    const link = new PeerLink(
      this.localUhid,
      peerUhid,
      this.iceServers,
      this.signaling,
      (from, data) => this.onPeerData(from, data),
    );
    this.peers.set(peerUhid, link);
    link.onClosed = () => {
      if (this.peers.get(peerUhid) === link) this.peers.delete(peerUhid);
    };

    await link.start(asInitiator);
    if (asInitiator) await link.waitOpen(CONNECT_TIMEOUT_MS);
    return link;
  }

  private onPeerData(peerUhid: string, data: Uint8Array): void {
    this.onDataReceived?.(peerUhid, data);
  }
}

/**
 * One WebRTC connection to a single peer: an `RTCPeerConnection` plus its `RTCDataChannel`, driving
 * the offer/answer/ICE handshake over a {@link Signaling} channel and surfacing received bytes.
 */
class PeerLink {
  private readonly localUhid: string;
  private readonly peerUhid: string;
  private readonly signaling: Signaling;
  private readonly onData: (peerUhid: string, data: Uint8Array) => void;
  private readonly pc: RTCPeerConnection;

  private channel?: RTCDataChannel;
  private closed = false;
  private opened = false;
  private readonly openWaiters: Array<(open: boolean) => void> = [];

  /** Invoked once when this link transitions to a terminal (closed/failed) state. */
  onClosed?: () => void;

  constructor(
    localUhid: string,
    peerUhid: string,
    iceServers: RTCIceServer[],
    signaling: Signaling,
    onData: (peerUhid: string, data: Uint8Array) => void,
  ) {
    this.localUhid = localUhid;
    this.peerUhid = peerUhid;
    this.signaling = signaling;
    this.onData = onData;

    this.pc = new RTCPeerConnection({ iceServers });

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

  /** Begins the handshake. The initiator creates the data channel and sends the offer. */
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

  /** Resolves `true` once the data channel is open, or `false` on timeout / terminal close. */
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
