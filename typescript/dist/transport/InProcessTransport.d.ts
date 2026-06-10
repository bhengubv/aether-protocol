/**
 * In-memory transport for testing and demos
 * Simulates a network using a static registry
 * SPDX-License-Identifier: MIT
 */
import { ITransportService, PerTransportMetrics } from "./ITransportService.js";
/**
 * In-memory transport for testing and demos. Simulates a network of nodes using a static
 * registry. Each instance represents one node; sending data to a peer delivers it directly
 * to that peer's onDataReceived callback via the in-process registry.
 */
export declare class InProcessTransport implements ITransportService {
    private static readonly network;
    private localUhid;
    private disposed;
    name: string;
    isAvailable: boolean;
    maxBandwidthBps: number;
    maxRangeMeters: number;
    powerCostRelative: number;
    maxConcurrentPeers: number;
    readonly metrics: PerTransportMetrics;
    onDataReceived?: (senderUhid: string, data: Uint8Array) => void;
    /**
     * Creates a new in-process transport node and registers it in the simulated network.
     */
    constructor(localUhid: string);
    /**
     * Send data to a peer
     */
    sendAsync(peerUhid: string, data: Uint8Array, cancellationToken?: AbortSignal): Promise<boolean>;
    /**
     * Send a stream to a peer
     */
    sendStreamAsync(peerUhid: string, stream: ReadableStream<Uint8Array>, cancellationToken?: AbortSignal): Promise<boolean>;
    /**
     * Check if connected to a peer
     */
    isConnected(peerUhid: string): boolean;
    /**
     * Returns the number of active nodes in the simulated network
     */
    static get activeNodeCount(): number;
    /**
     * Reset the simulated network
     */
    static resetNetwork(): void;
    /**
     * Dispose this transport node
     */
    dispose(): void;
}
//# sourceMappingURL=InProcessTransport.d.ts.map