/**
 * Native circuit-relay-v2 TRANSPORT — the {@link ITransportService} adapter that lets
 * {@link TransportManager} auto-select the relay as its serverless last-resort fallback.
 *
 * The fixture-locked {@link Transport} engine already carries every circuit-relay-v2 role
 * (target / client / relay); this class wraps one engine + its {@link RelayLink} and exposes
 * them through the mesh's {@link ITransportService} contract so a node that cannot reach a peer
 * directly transparently routes through a third node that can reach both — the decentralised,
 * no-libp2p equivalent of libp2p circuit-relay-v2. It slots into the transport ladder next to
 * BLE / Wi-Fi Direct / WebRTC, NOT as an app-level sidecar.
 *
 * Faithful port of the C# reference (AetherNet.Transport.CircuitRelay.CircuitRelayTransportService):
 * same name, same power cost, same behaviour. The C# reference is a single class because its engine
 * already implements ITransportService; the TypeScript engine is deliberately transport-agnostic, so
 * this thin adapter supplies the ITransportService surface and delegates every role method to the
 * engine unchanged. No wire-serialization code lives here.
 *
 * SPDX-License-Identifier: MIT
 */

import { ITransportService, PerTransportMetrics } from "../transport/ITransportService.js";
import { Transport, RelayLink, RelayOptions, defaultRelayOptions } from "./Transport.js";

export class CircuitRelayTransportService implements ITransportService {
  private readonly engine: Transport;
  private disposed = false;

  /** Matches the C# reference exactly — used by TransportManager to tag the receive path. */
  readonly name: string = "Circuit Relay (v2)";
  isAvailable: boolean = true;
  /** Relayed path; conservatively below a direct link (mirrors the C# reference 5_000_000). */
  readonly maxBandwidthBps: number = 5_000_000;
  /** Internet-scope; range is not meaningful for a relayed path. */
  readonly maxRangeMeters: number = 0;
  /**
   * Relayed traffic is costly (an extra hop through a third node), so it sits just below the HTTP
   * relay's last-resort cost of 100 — TransportManager, ordering additional transports by ascending
   * power cost, therefore picks the relay only after every cheaper direct transport has failed.
   */
  readonly powerCostRelative: number = 90;
  readonly maxConcurrentPeers: number = 256;
  readonly metrics: PerTransportMetrics = new PerTransportMetrics();
  onDataReceived?: (senderUhid: string, data: Uint8Array) => void;

  /**
   * @param localUhid This node's UHID.
   * @param link One-hop link to directly-reachable nodes (typically a {@link MeshRelayLink}).
   * @param options Relay policy/tuning (defaults to {@link defaultRelayOptions}).
   * @param now Clock returning epoch ms (injectable for deterministic reservation-expiry tests).
   */
  constructor(
    localUhid: string,
    link: RelayLink,
    options: RelayOptions = defaultRelayOptions(),
    now: () => number = () => Date.now(),
  ) {
    this.engine = new Transport(localUhid, link, options, now);
    // Tunnelled data delivered to this node as an endpoint surfaces through the
    // ITransportService receive path, exactly like InProcessTransport / WebRtcTransport.
    this.engine.setOnData((sender, data) => {
      this.onDataReceived?.(sender, data);
    });
  }

  // ── ITransportService ─────────────────────────────────────────────────────────

  /**
   * Delivers `data` to `peer`, establishing a relay bridge first if a route is known.
   * Returns false (so TransportManager falls through) when no relay route is reachable.
   */
  async sendAsync(
    peerUhid: string,
    data: Uint8Array,
    _cancellationToken?: AbortSignal,
  ): Promise<boolean> {
    if (this.disposed) return false;
    return this.engine.send(peerUhid, data);
  }

  /** Buffers the stream then relays it as a single payload (mirrors the C# reference). */
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
    const total = chunks.reduce((n, c) => n + c.length, 0);
    const combined = new Uint8Array(total);
    let o = 0;
    for (const c of chunks) {
      combined.set(c, o);
      o += c.length;
    }
    return this.sendAsync(peerUhid, combined, cancellationToken);
  }

  /** True once a relay bridge to `peerUhid` has been established. */
  isConnected(peerUhid: string): boolean {
    return this.engine.isConnected(peerUhid);
  }

  // ── Relay / target role (delegated to the engine) ──────────────────────────────

  /**
   * Reserves capacity on `relayUhid` so peers can reach this node through it.
   * Resolves true once the relay confirms the reservation.
   */
  reserveAsync(relayUhid: string): Promise<boolean> {
    return this.engine.reserve(relayUhid);
  }

  /**
   * Records that `destUhid` is reachable via relay `relayUhid`. In production this is populated
   * from the directory / reservation gossip; tests set it directly.
   */
  setRoute(destUhid: string, relayUhid: string): void {
    this.engine.setRoute(destUhid, relayUhid);
  }

  /** Number of bridges this node is currently servicing as a relay (diagnostics/tests). */
  get activeBridgeCount(): number {
    return this.engine.activeBridgeCount();
  }

  /** Number of reservations this node is currently holding as a relay (diagnostics/tests). */
  get activeReservationCount(): number {
    return this.engine.activeReservationCount();
  }

  /** Marks the transport unavailable so TransportManager stops selecting it. */
  dispose(): void {
    this.disposed = true;
    this.isAvailable = false;
    this.onDataReceived = undefined;
  }
}
