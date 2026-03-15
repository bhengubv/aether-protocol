/**
 * Core data models
 * SPDX-License-Identifier: MIT
 */
/**
 * Represents a peer in the mesh network
 */
export interface PeerInfo {
    uhid: string;
    publicKey: Uint8Array;
    lastSeen: Date;
    reliabilityScore: number;
    hopCount?: number;
}
/**
 * Route table entry
 */
export interface RouteEntry {
    destinationUhid: string;
    nextHopUhid: string;
    hopCount: number;
    qualityScore: number;
    expiresAt: Date;
}
/**
 * Aether node representation
 */
export interface AetherNode {
    uhid: string;
    publicKey: Uint8Array;
    capabilities: number;
    isOnline: boolean;
    lastHeartbeat: Date;
}
//# sourceMappingURL=index.d.ts.map