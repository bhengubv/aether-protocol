/**
 * Direct peer-to-peer transport for AetherNet over a WebRTC data channel. The WebRTC backend is
 * pluggable behind the {@link PeerLink} seam: Node uses werift (`peer-link.werift.ts`), the browser
 * uses the native `RTCPeerConnection` (`peer-link.dom.ts`). This class holds only the transport
 * logic — peer bookkeeping and signalling routing — so the identical code runs on both platforms.
 *
 * Serverless by default: with the default (no ICE servers) a node never contacts a STUN/TURN
 * server — host-candidate-only ICE forms a direct link on the same LAN or when a peer has a public
 * address. STUN/TURN are OPTIONAL (opted into by passing an explicit ICE-server list) and help
 * traverse NATs that host candidates alone can't. The SDP/ICE handshake (trickled) rides an
 * injected {@link Signaling} channel, so no central signalling server is required either. Implements
 * {@link ITransportService} so the transport ladder can rank it between the radio mesh (cheap,
 * proximity) and the relay (last resort): a direct internet path is used when one can be negotiated.
 *
 * Mirrors the C# `WebRtcTransportService` (SIPSorcery) and the Go `WebRtcTransport` (pion).
 *
 * SPDX-License-Identifier: MIT
 */

import { ITransportService, PerTransportMetrics } from "../ITransportService.js";
import {
  getDefaultPeerLinkFactory,
  type IceServer,
  type PeerLink,
  type PeerLinkFactory,
} from "./peer-link.js";
import { Signal, Signaling, SignalType } from "./signaling.js";

/** How long {@link WebRtcTransport.sendAsync} waits for the data channel to open. */
const CONNECT_TIMEOUT_MS = 20_000;

/**
 * Serverless default: NO ICE servers — a peer using the default never contacts a STUN/TURN
 * server. Direct links form on the same LAN or when a peer has a public address; for NAT
 * traversal without a server, route through the circuit-relay-v2 transport (peers relay for
 * peers). Opt into STUN/TURN by passing an explicit list, e.g.
 * `[{ urls: "stun:stun.l.google.com:19302" }]`.
 */
export function defaultIceServers(): IceServer[] {
  return [];
}

/**
 * Direct P2P transport over a WebRTC `RTCDataChannel`, backend-agnostic. The backend is werift on
 * Node and the native `RTCPeerConnection` in the browser — selected by the registered default (the
 * entry point you import) or an explicit `peerLinkFactory` constructor argument.
 *
 * With `undefined` `iceServers` the transport uses the serverless default of NO ICE servers
 * (host-candidate-only ICE) — it never contacts a STUN/TURN server, and links form on the same LAN
 * or when a peer has a public address. For NAT traversal without a server, route through the
 * circuit-relay-v2 transport. Pass an explicit list to opt into STUN/TURN.
 */
export class WebRtcTransport implements ITransportService {
  private readonly localUhid: string;
  private readonly signaling: Signaling;
  private readonly iceServers: IceServer[];
  private readonly peerLinkFactory?: PeerLinkFactory;
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
   * @param iceServers `undefined` => the serverless default (NO ICE servers; host-candidate-only,
   *                   no STUN/TURN). An explicit list is respected verbatim.
   * @param peerLinkFactory Optional WebRTC backend override. Defaults to the registered backend
   *                   (werift on Node, native DOM in the browser).
   */
  constructor(
    localUhid: string,
    signaling: Signaling,
    iceServers?: IceServer[],
    peerLinkFactory?: PeerLinkFactory,
  ) {
    if (!localUhid || localUhid.trim().length === 0) {
      throw new Error("WebRtcTransport: localUhid must not be empty");
    }
    if (!signaling) {
      throw new Error("WebRtcTransport: signaling is required");
    }
    this.localUhid = localUhid;
    this.signaling = signaling;
    this.iceServers = iceServers ?? defaultIceServers();
    this.peerLinkFactory = peerLinkFactory;
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

    const factory = this.peerLinkFactory ?? getDefaultPeerLinkFactory();
    const link = factory({
      localUhid: this.localUhid,
      peerUhid,
      iceServers: this.iceServers,
      signaling: this.signaling,
      onData: (from, bytes) => this.onPeerData(from, bytes),
    });
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
