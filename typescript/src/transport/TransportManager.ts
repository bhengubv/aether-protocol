/**
 * Multi-transport manager that routes each send through the best available transport and,
 * failing that, falls through the remaining transports until one succeeds.
 *
 * Selection order mirrors the C# reference (AetherNet.Transport.Services.TransportManager):
 * typed fast-paths (NearLink → small-payload BLE → Wi-Fi Direct → CircleLink → large-payload BLE)
 * are tried first, then **additional transports sorted by ascending `powerCostRelative`**. The
 * native circuit-relay-v2 transport advertises cost 90 (just below the HTTP relay's 100), so it is
 * auto-selected as the LAST-RESORT serverless fallback — chosen only after every cheaper direct
 * transport has declined, and never hand-wired by the caller.
 *
 * The TypeScript {@link ITransportService} contract is leaner than the C#/Go one (no separate
 * IBle/IWifiDirect/INearLink/ICircleLink marker interfaces, and receive is a single
 * `onDataReceived` callback rather than a multicast event). This manager therefore takes every
 * transport through the `additionalTransports` list, orders them by power cost, and chains each
 * transport's receive callback so delivered data surfaces via {@link TransportManager.onDataReceived}
 * tagged with the transport's `name` — proving which transport the manager selected.
 *
 * SPDX-License-Identifier: MIT
 */

import { ITransportService } from "./ITransportService.js";

/** Aggregate diagnostics for sends routed through the manager. */
export interface TransportManagerMetrics {
  /** Successful sends per transport, keyed by transport name. */
  sendCountByTransport: Map<string, number>;
  /** Bytes sent per transport, keyed by transport name. */
  bytesSentByTransport: Map<string, number>;
  /** Sends that no available transport could satisfy. */
  totalFailures: number;
}

/**
 * Routes sends across a set of transports and surfaces their inbound data through one callback.
 * Construct with the transports this node runs; the manager orders them by ascending power cost so
 * cheap direct links are preferred and the circuit relay (cost 90) is the automatic fallback.
 */
export class TransportManager {
  private readonly transports: ITransportService[];
  private readonly sendCount = new Map<string, number>();
  private readonly bytesSent = new Map<string, number>();
  private totalFailures = 0;
  private disposed = false;

  /** Fired when data arrives via any managed transport: (sender, data, viaTransportName). */
  onDataReceived?: (senderUhid: string, data: Uint8Array, via: string) => void;

  /**
   * @param additionalTransports The transports this node runs. Ordered internally by ascending
   *   `powerCostRelative`, so the cheapest usable transport is tried first and the relay last.
   */
  constructor(additionalTransports: ITransportService[] = []) {
    // Stable sort by ascending power cost (relay's 90 sorts after cheaper direct transports).
    this.transports = [...additionalTransports].sort(
      (a, b) => a.powerCostRelative - b.powerCostRelative,
    );
    this.subscribeToDataEvents();
  }

  /**
   * Sends `data` to `peerUhid`, trying each available transport in ascending-power-cost order until
   * one succeeds. Returns false only if every transport declined.
   */
  async sendAsync(
    peerUhid: string,
    data: Uint8Array,
    cancellationToken?: AbortSignal,
  ): Promise<boolean> {
    for (const transport of this.transports) {
      if (!transport.isAvailable) continue;
      if (await transport.sendAsync(peerUhid, data, cancellationToken)) {
        this.bump(this.sendCount, transport.name, 1);
        this.bump(this.bytesSent, transport.name, data.length);
        return true;
      }
    }
    this.totalFailures += 1;
    return false;
  }

  /**
   * Sends a stream to `peerUhid`, trying each available transport in ascending-power-cost order.
   */
  async sendStreamAsync(
    peerUhid: string,
    stream: ReadableStream<Uint8Array>,
    cancellationToken?: AbortSignal,
  ): Promise<boolean> {
    for (const transport of this.transports) {
      if (!transport.isAvailable) continue;
      if (await transport.sendStreamAsync(peerUhid, stream, cancellationToken)) {
        this.bump(this.sendCount, transport.name, 1);
        return true;
      }
    }
    this.totalFailures += 1;
    return false;
  }

  /** True if any managed transport reports an active connection to `peerUhid`. */
  isConnected(peerUhid: string): boolean {
    return this.transports.some((t) => t.isConnected(peerUhid));
  }

  /** Snapshot of per-transport send counters and total failures. */
  getMetrics(): TransportManagerMetrics {
    return {
      sendCountByTransport: new Map(this.sendCount),
      bytesSentByTransport: new Map(this.bytesSent),
      totalFailures: this.totalFailures,
    };
  }

  /** Detaches from every managed transport's receive callback. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const t of this.transports) t.onDataReceived = undefined;
    this.onDataReceived = undefined;
  }

  // ── internals ──────────────────────────────────────────────────────────────────

  private subscribeToDataEvents(): void {
    for (const transport of this.transports) {
      // Preserve any handler already attached to the transport, then tag with its name.
      const prior = transport.onDataReceived;
      transport.onDataReceived = (sender, data) => {
        prior?.(sender, data);
        this.onDataReceived?.(sender, data, transport.name);
      };
    }
  }

  private bump(map: Map<string, number>, key: string, by: number): void {
    map.set(key, (map.get(key) ?? 0) + by);
  }
}
