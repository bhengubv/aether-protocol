/**
 * In-memory transport for testing and demos
 * Simulates a network using a static registry
 * SPDX-License-Identifier: MIT
 */
import { PerTransportMetrics } from "./ITransportService.js";
/**
 * In-memory transport for testing and demos. Simulates a network of nodes using a static
 * registry. Each instance represents one node; sending data to a peer delivers it directly
 * to that peer's onDataReceived callback via the in-process registry.
 */
export class InProcessTransport {
    static network = new Map();
    localUhid;
    disposed = false;
    name = "InProcess";
    isAvailable = true;
    maxBandwidthBps = 1_000_000_000; // 1 Gbps
    maxRangeMeters = 0; // Not applicable
    powerCostRelative = 0; // No power cost
    maxConcurrentPeers = Number.MAX_SAFE_INTEGER;
    metrics = new PerTransportMetrics();
    onDataReceived;
    /**
     * Creates a new in-process transport node and registers it in the simulated network.
     */
    constructor(localUhid) {
        if (!localUhid || localUhid.trim().length === 0) {
            throw new Error("localUhid must not be empty");
        }
        if (InProcessTransport.network.has(localUhid)) {
            throw new Error(`An InProcessTransport with UHID '${localUhid}' is already registered. ` +
                "Dispose the existing instance first or use a different UHID.");
        }
        this.localUhid = localUhid;
        InProcessTransport.network.set(localUhid, this);
        console.log(`[InProcess] Node '${localUhid}' joined the simulated network (${InProcessTransport.network.size} nodes total)`);
    }
    /**
     * Send data to a peer
     */
    async sendAsync(peerUhid, data, cancellationToken) {
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
            console.debug(`[InProcess] Peer '${peerUhid}' not found in simulated network`);
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
            // Record a successful delivery sample. In-process delivery is
            // synchronous (rttMs = 0), so only sampleCount and lossRate update.
            this.metrics.recordSample(0, true, data.length);
            console.debug(`[InProcess] Delivered ${data.length} bytes from '${this.localUhid}' to '${peerUhid}'`);
            return true;
        }
        catch (error) {
            console.error(`[InProcess] Error delivering data from '${this.localUhid}' to '${peerUhid}':`, error);
            return false;
        }
    }
    /**
     * Send a stream to a peer
     */
    async sendStreamAsync(peerUhid, stream, cancellationToken) {
        if (this.disposed) {
            return false;
        }
        // Collect stream into buffer
        const chunks = [];
        const reader = stream.getReader();
        try {
            while (true) {
                const { done, value } = await reader.read();
                if (done)
                    break;
                if (value)
                    chunks.push(value);
            }
        }
        finally {
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
    isConnected(peerUhid) {
        if (this.disposed || !peerUhid || peerUhid.trim().length === 0) {
            return false;
        }
        const peer = InProcessTransport.network.get(peerUhid);
        return peer !== undefined && !peer.disposed;
    }
    /**
     * Returns the number of active nodes in the simulated network
     */
    static get activeNodeCount() {
        return InProcessTransport.network.size;
    }
    /**
     * Reset the simulated network
     */
    static resetNetwork() {
        InProcessTransport.network.clear();
        console.log("[InProcess] Network reset");
    }
    /**
     * Dispose this transport node
     */
    dispose() {
        if (this.disposed)
            return;
        this.disposed = true;
        this.isAvailable = false;
        InProcessTransport.network.delete(this.localUhid);
        this.onDataReceived = undefined;
        console.log(`[InProcess] Node '${this.localUhid}' left the simulated network (${InProcessTransport.network.size} nodes remaining)`);
    }
}
//# sourceMappingURL=InProcessTransport.js.map