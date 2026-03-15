/**
 * Packet signing and verification
 * SPDX-License-Identifier: MIT
 */

import { createHash, randomBytes } from "crypto";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { Ed25519Service } from "./Ed25519Service.js";
import { MAX_PACKET_AGE_SECONDS } from "../constants.js";

export interface PacketSignature {
  nonce: Uint8Array; // 8-byte random nonce
  timestamp: bigint;
  signature: Uint8Array;
}

/**
 * Construct signable data per PROTOCOL_SPEC Section 2.3
 * Format:
 *   PacketNonce (8 bytes)
 *   || TimestampMs (8 bytes, little-endian int64)
 *   || Type (4 bytes, little-endian int32)
 *   || SourceUhidLength (4 bytes, little-endian int32)
 *   || SourceUhid (UTF-8 bytes)
 *   || DestinationUhidLength (4 bytes, little-endian int32)
 *   || DestinationUhid (UTF-8 bytes)
 *   || SHA-256(Payload) (32 bytes)
 *   || Ttl (4 bytes, little-endian int32)
 *   || Priority (4 bytes, little-endian int32)
 */
function constructSignableData(packet: MeshPacket): Uint8Array {
  const sourceBytes = new TextEncoder().encode(packet.sourceUhid);
  const destBytes = new TextEncoder().encode(packet.destinationUhid);
  const payloadHash = createHash("sha256").update(packet.payload).digest();

  // Build signable data
  const buffers: Uint8Array[] = [];

  // PacketNonce (8 bytes)
  buffers.push(packet.packetNonce);

  // TimestampMs (8 bytes, little-endian int64)
  const tsBuffer = Buffer.allocUnsafe(8);
  const tsView = new DataView(tsBuffer.buffer);
  tsView.setBigInt64(0, packet.timestampMs, true);
  buffers.push(new Uint8Array(tsBuffer));

  // Type (4 bytes, little-endian int32)
  const typeBuffer = Buffer.allocUnsafe(4);
  new DataView(typeBuffer.buffer).setInt32(0, packet.type, true);
  buffers.push(new Uint8Array(typeBuffer));

  // SourceUhidLength (4 bytes, little-endian int32)
  const srcLenBuffer = Buffer.allocUnsafe(4);
  new DataView(srcLenBuffer.buffer).setInt32(0, sourceBytes.length, true);
  buffers.push(new Uint8Array(srcLenBuffer));

  // SourceUhid (UTF-8)
  buffers.push(sourceBytes);

  // DestinationUhidLength (4 bytes, little-endian int32)
  const dstLenBuffer = Buffer.allocUnsafe(4);
  new DataView(dstLenBuffer.buffer).setInt32(0, destBytes.length, true);
  buffers.push(new Uint8Array(dstLenBuffer));

  // DestinationUhid (UTF-8)
  buffers.push(destBytes);

  // SHA-256(Payload) (32 bytes)
  buffers.push(new Uint8Array(payloadHash));

  // Ttl (4 bytes, little-endian int32)
  const ttlBuffer = Buffer.allocUnsafe(4);
  new DataView(ttlBuffer.buffer).setInt32(0, packet.ttl, true);
  buffers.push(new Uint8Array(ttlBuffer));

  // Priority (4 bytes, little-endian int32)
  const priBuffer = Buffer.allocUnsafe(4);
  new DataView(priBuffer.buffer).setInt32(0, packet.priority, true);
  buffers.push(new Uint8Array(priBuffer));

  // Concatenate all buffers
  const totalLength = buffers.reduce((sum, buf) => sum + buf.length, 0);
  const result = new Uint8Array(totalLength);
  let offset = 0;
  for (const buf of buffers) {
    result.set(buf, offset);
    offset += buf.length;
  }

  return result;
}

/**
 * Sign a packet
 */
export function signPacket(
  packet: MeshPacket,
  privateKey: Uint8Array
): void {
  // Generate 8-byte random nonce
  const nonce = new Uint8Array(randomBytes(8));
  packet.packetNonce = nonce;

  // Construct signable data
  const signableData = constructSignableData(packet);

  // Sign with Ed25519
  packet.signature = Ed25519Service.sign(privateKey, signableData);
}

/**
 * Verify a packet signature
 */
export function verifyPacket(
  packet: MeshPacket,
  publicKey: Uint8Array
): boolean {
  // Check timestamp freshness
  const nowMs = BigInt(Date.now());
  const ageMs = nowMs - packet.timestampMs;
  if (ageMs > BigInt(MAX_PACKET_AGE_SECONDS * 1000)) {
    return false;
  }

  // Construct signable data
  const signableData = constructSignableData(packet);

  // Verify signature
  return Ed25519Service.verify(publicKey, signableData, packet.signature);
}

/**
 * Non-cryptographic deduplication with timestamp-based cleanup
 */
export class PacketDeduplicator {
  private nonces: Map<string, Set<string>> = new Map(); // senderUhid -> set of nonce strings
  private lastCleanup: number = Date.now();
  private readonly cleanupIntervalMs: number = 60000; // 1 minute

  /**
   * Check if a nonce is already seen for this sender
   */
  isSeen(senderUhid: string, nonce: Uint8Array): boolean {
    const nonceStr = Buffer.from(nonce).toString("hex");
    const senderNonces = this.nonces.get(senderUhid);
    return senderNonces ? senderNonces.has(nonceStr) : false;
  }

  /**
   * Mark a nonce as seen
   */
  mark(senderUhid: string, nonce: Uint8Array): void {
    const nonceStr = Buffer.from(nonce).toString("hex");
    if (!this.nonces.has(senderUhid)) {
      this.nonces.set(senderUhid, new Set());
    }
    this.nonces.get(senderUhid)!.add(nonceStr);

    // Periodic cleanup
    if (Date.now() - this.lastCleanup > this.cleanupIntervalMs) {
      this.cleanup();
    }
  }

  /**
   * Clear all deduplication state
   */
  clear(): void {
    this.nonces.clear();
    this.lastCleanup = Date.now();
  }

  /**
   * Internal cleanup (in real implementation, would respect packet timestamp TTL)
   */
  private cleanup(): void {
    // For now, just clear everything older than 5 minutes
    // In production, respect MAX_PACKET_AGE_SECONDS
    this.nonces.clear();
    this.lastCleanup = Date.now();
  }
}
