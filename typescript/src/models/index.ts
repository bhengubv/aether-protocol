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
  reliabilityScore: number; // 0-100
  hopCount?: number;
}

/**
 * Route table entry
 */
export interface RouteEntry {
  destinationUhid: string;
  nextHopUhid: string;
  hopCount: number;
  qualityScore: number; // 0-100
  expiresAt: Date;
}

/**
 * Aether node representation
 */
export interface AetherNode {
  uhid: string;
  publicKey: Uint8Array;
  capabilities: number; // Bitfield (see PROTOCOL_SPEC Section 2.5)
  isOnline: boolean;
  lastHeartbeat: Date;
}
