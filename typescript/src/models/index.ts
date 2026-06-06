/**
 * Core data models
 * SPDX-License-Identifier: MIT
 */

import { DTN_MAX_COPIES, DTN_BUNDLE_TTL_HOURS } from "../constants.js";

/**
 * Bitfield representing node capabilities.
 */
export const NodeCapabilities = {
  None: 0,
  BLE: 1,
  WifiDirect: 2,
  Gateway: 4,
  Relay: 8,
  SOS: 16,
  Streaming: 32,
  Voice: 64,
  DtnCarrier: 128,
  NearLink: 256,
  Video: 512,
} as const;
export type NodeCapabilitiesValue = number;

/**
 * Represents a peer in the mesh network
 */
export interface PeerInfo {
  uhid: string;
  publicKey: Uint8Array;
  lastSeen: Date;
  reliabilityScore: number; // 0-100
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
  qualityScore: number; // 0-100
  expiresAt: Date;
}

export function isRouteExpired(route: RouteEntry, now: Date = new Date()): boolean {
  return now >= route.expiresAt;
}

/**
 * Aether node representation
 */
export interface AetherMeshNode {
  uhid: string;
  publicKey: Uint8Array;
  capabilities: number; // Bitfield (see PROTOCOL_SPEC Section 2.5)
  isOnline: boolean;
  lastHeartbeat: Date;
}

// ────────────────────────────── DTN ──────────────────────────────

export enum BundleStatus {
  Pending = 0,
  InCustody = 1,
  Delivered = 2,
  Expired = 3,
  Failed = 4,
}

export enum BundlePriority {
  Low = 0,
  Normal = 1,
  High = 2,
  Sos = 3,
}

export interface DtnBundle {
  id: string; // UUID string
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

export function newDtnBundle(
  senderUhid: string,
  recipientUhid: string,
  encryptedPayload: Uint8Array,
  priority: BundlePriority = BundlePriority.Normal,
): DtnBundle {
  return {
    id: crypto.randomUUID(),
    senderUhid,
    recipientUhid,
    encryptedPayload,
    priority,
    status: BundleStatus.Pending,
    copyCount: 1,
    maxCopies: DTN_MAX_COPIES,
    hopCount: 0,
    createdAt: new Date(),
    expiresAt: new Date(Date.now() + DTN_BUNDLE_TTL_HOURS * 3600 * 1000),
  };
}

export function isBundleExpired(bundle: DtnBundle, now: Date = new Date()): boolean {
  return now >= bundle.expiresAt;
}

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

// ────────────────────────────── SOS ──────────────────────────────

export interface SosAlert {
  id: string;
  senderUhid: string;
  broadcastType: string;
  message?: string;
  latitude: number;
  longitude: number;
  geohash?: string;
  receivedAt: Date;
}
