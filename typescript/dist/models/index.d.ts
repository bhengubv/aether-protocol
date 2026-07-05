/**
 * Core data models
 * SPDX-License-Identifier: MIT
 */
/**
 * Bitfield representing node capabilities.
 */
export declare const NodeCapabilities: {
    readonly None: 0;
    readonly BLE: 1;
    readonly WifiDirect: 2;
    readonly Gateway: 4;
    readonly Relay: 8;
    readonly SOS: 16;
    readonly Streaming: 32;
    readonly Voice: 64;
    readonly DtnCarrier: 128;
    readonly NearLink: 256;
    readonly Video: 512;
};
export type NodeCapabilitiesValue = number;
/**
 * Represents a peer in the mesh network
 */
export interface PeerInfo {
    uhid: string;
    publicKey: Uint8Array;
    lastSeen: Date;
    reliabilityScore: number;
    hopCount?: number;
    geohash?: string;
    capabilities?: NodeCapabilitiesValue;
    isBlocked?: boolean;
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
export declare function isRouteExpired(route: RouteEntry, now?: Date): boolean;
/**
 * Aether node representation
 */
export interface AetherNetNode {
    uhid: string;
    publicKey: Uint8Array;
    capabilities: number;
    isOnline: boolean;
    lastHeartbeat: Date;
}
export declare enum BundleStatus {
    Pending = 0,
    InCustody = 1,
    Delivered = 2,
    Expired = 3,
    Failed = 4
}
export declare enum BundlePriority {
    Low = 0,
    Normal = 1,
    High = 2,
    Sos = 3
}
export interface DtnBundle {
    id: string;
    senderUhid: string;
    recipientUhid: string;
    encryptedPayload: Uint8Array;
    priority: BundlePriority;
    status: BundleStatus;
    copyCount: number;
    maxCopies: number;
    senderGeohash?: string;
    recipientLastGeohash?: string;
    hopCount: number;
    createdAt: Date;
    expiresAt: Date;
}
export declare function newDtnBundle(senderUhid: string, recipientUhid: string, encryptedPayload: Uint8Array, priority?: BundlePriority): DtnBundle;
export declare function isBundleExpired(bundle: DtnBundle, now?: Date): boolean;
export interface CustodyRecord {
    id: string;
    bundleId: string;
    fromUhid: string;
    toUhid: string;
    accepted: boolean;
    transferredAt: Date;
}
export interface DtnDeliveryReceipt {
    bundleId: string;
    recipientUhid: string;
    totalHops: number;
    totalCustodyTransfers: number;
    deliveredAt: Date;
}
/**
 * Event payload delivered to DtnService.onBundleReceived the moment a DTN
 * bundle addressed to the local node lands. Mirrors the C# /
 * DtnBundleReceivedEventArgs. Added in v1.2.0 — closes Issue #59.
 */
export interface DtnBundleReceivedEvent {
    bundleId: string;
    senderUhid: string;
    recipientUhid: string;
    encryptedPayload: Uint8Array;
    priority: BundlePriority;
    hopCount: number;
    receivedAtUtc: Date;
}
export interface SosAlert {
    id: string;
    senderUhid: string;
    broadcastType: string;
    message?: string;
    latitude: number;
    longitude: number;
    geohash?: string;
    receivedAt: Date;
    /**
     * Distinct UHIDs of peers that have acknowledged receiving this alert. Populated on the
     * ORIGINATING node only, as SosAck packets arrive back — it lets the sender see how many
     * devices their emergency reached. Mirrors the C# SosAlert.AcknowledgedBy.
     */
    acknowledgedBy: Set<string>;
}
/**
 * Raised on the originating node when a peer acknowledges receipt of one of its active SOS
 * alerts. Mirrors the C# SosAcknowledgement.
 */
export interface SosAcknowledgement {
    broadcastId: string;
    responderUhid: string;
    totalAcknowledgements: number;
}
//# sourceMappingURL=index.d.ts.map