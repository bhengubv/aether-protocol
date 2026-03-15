/**
 * In-memory transport for testing and demos
 * Simulates a network using a static registry
 * SPDX-License-Identifier: MIT
 */

import { ITransportService } from "./ITransportService.js";

/**
 * In-memory transport for testing and demos. Simulates a network of nodes using a static
 * registry. Each instance represents one node; sending data to a peer delivers it directly
 * to that peer's onDataReceived callback via the in-process registry.
 */
export class InProcessTransport implements ITransportService {
  private static readonly network: Map<string, InProcessTransport> = new Map();

  private localUhid: string;
  private disposed: boolean = false;

  name: string = "InProcess";
  isAvailable: boolean = true;
  maxBandwidthBps: number = 1_000_000_000; // 1 Gbps
  maxRangeMeters: number = 0; // Not applicable
  powerCostRelative: number = 0; // No power cost
  maxConcurrentPeers: number = Number.MAX_SAFE_INTEGER;
  onDataReceived?: (senderUhid: string, data: Uint8Array) => void;

  /**
   * Creates a new in-process transport node and registers it in the simulated network.
   */
  constructor(localUhid: string) {
    if (!localUhid || localUhid.trim().length === 0) {
      throw new Error("localUhid must not be empty");
    }

    if (InProcessTransport.network.has(localUhid)) {
      throw new Error(
        `An InProcessTransport with UHID '${localUhid}' is already registered. ` +
          "Dispose the existing instance first or use a different UHID."
      );
    }

    this.localUhid = localUhid;
    InProcessTransport.network.set(localUhid, this);
    console.log(
      `[InProcess] Node '${localUhid}' joined the simulated network (${InProcessTransport.network.size} nodes total)`
    );
  }

  /**
   * Send data to a peer
   */
  async sendAsync(
    peerUhid: string,
    data: Uint8Array,
    cancellationToken?: AbortSignal
  ): Promise<boolean> {
    if (this.disposed) {
      console.warn(`[InProcess] Cannot send: node '${this.localUhid}' is disposed`);
      return false;
    }

    if (!peerUhid || peerUhid.trim().length === 0) {
      console.warn("[InProcess] SendAsync called with empty peer UHID");
      return false;
    }

    const targetNode = InProcessTransport.network.get(peerUhid);
    if (!targetNode) {
      console.debug(
        `[InProcess] Peer '${peerUhid}' not found in simulated network`
      );
      return false;
    }

    if (targetNode.disposed) {
      console.debug(`[InProcess] Peer '${peerUhid}' is disposed`);
      return false;
    }

    try {
      // Copy data to prevent mutation
      const dataCopy = new Uint8Array(data);
      targetNode.onDataReceived?.(this.localUhid, dataCopy);
      console.debug(
        `[InProcess] Delivered ${data.length} bytes from '${this.localUhid}' to '${peerUhid}'`
      );
      return true;
    } catch (error) {
      console.error(
        `[InProcess] Error delivering data from '${this.localUhid}' to '${peerUhid}':`,
        error
      );
      return false;
    }
  }

  /**
   * Send a stream to a peer
   */
  async sendStreamAsync(
    peerUhid: string,
    stream: ReadableStream<Uint8Array>,
    cancellationToken?: AbortSignal
  ): Promise<boolean> {
    if (this.disposed) {
      return false;
    }

    // Collect stream into buffer
    const chunks: Uint8Array[] = [];
    const reader = stream.getReader();

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value) chunks.push(value);
      }
    } finally {
      reader.releaseLock();
    }

    // Send accumulated data
    const totalLength = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
    const combined = new Uint8Array(totalLength);
    let offset = 0;
    for (const chunk of chunks) {
      combined.set(chunk, offset);
      offset += chunk.length;
    }

    return this.sendAsync(peerUhid, combined, cancellationToken);
  }

  /**
   * Check if connected to a peer
   */
  isConnected(peerUhid: string): boolean {
    if (this.disposed || !peerUhid || peerUhid.trim().length === 0) {
      return false;
    }

    const peer = InProcessTransport.network.get(peerUhid);
    return peer !== undefined && !peer.disposed;
  }

  /**
   * Returns the number of active nodes in the simulated network
   */
  static get activeNodeCount(): number {
    return InProcessTransport.network.size;
  }

  /**
   * Reset the simulated network
   */
  static resetNetwork(): void {
    InProcessTransport.network.clear();
    console.log("[InProcess] Network reset");
  }

  /**
   * Dispose this transport node
   */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.isAvailable = false;

    InProcessTransport.network.delete(this.localUhid);
    this.onDataReceived = undefined;

    console.log(
      `[InProcess] Node '${this.localUhid}' left the simulated network (${InProcessTransport.network.size} nodes remaining)`
    );
  }
}
