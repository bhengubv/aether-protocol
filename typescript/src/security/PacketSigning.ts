/**
 * Packet signing and verification
 * SPDX-License-Identifier: MIT
 */

import { createHash, randomBytes } from "crypto";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { Ed25519Service } from "./Ed25519Service.js";
import { MAX_PACKET_AGE_SECONDS } from "../constants.js";
import { NodeReputationService } from "../reputation.js";

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
// Exported as the single source of the canonical signable layout so other
// verifiers (e.g. the routing-layer Ed25519 RREP verifier) sign/verify over the
// EXACT same bytes every language implementation shares — never a re-derived
// layout. Do not change this without regenerating the cross-language fixtures.
export function buildSignableData(packet: MeshPacket): Uint8Array {
  const sourceBytes = new TextEncoder().encode(packet.sourceUhid);
  const destBytes = new TextEncoder().encode(packet.destinationUhid);
  const payloadHash = createHash("sha256").update(packet.payload).digest();

  // Build signable data
  const buffers: Uint8Array[] = [];

  // PacketNonce (8 bytes)
  buffers.push(packet.packetNonce);

  // Fixed-width little-endian encoders. IMPORTANT: use `Buffer.alloc` (zero-
  // filled, pool-independent) rather than `allocUnsafe`, and the Buffer's own
  // writeInt32LE/writeBigInt64LE. A previous `new DataView(buf.buffer).setInt32`
  // was WRONG: `Buffer.allocUnsafe(n)` hands back a view into Node's shared pool
  // (buf.byteOffset ≠ 0, buf.buffer = the whole pool), so writing at DataView
  // offset 0 hit offset 0 of the POOL, not the buffer — producing garbage,
  // non-deterministic signable bytes that made Ed25519 verify fail under load.
  // Byte LAYOUT is identical (same fields, widths, little-endian order), so the
  // wire format and cross-language fixtures are unchanged.
  const le32 = (v: number): Uint8Array => {
    const b = Buffer.alloc(4);
    b.writeInt32LE(v | 0, 0);
    return new Uint8Array(b);
  };

  // TimestampMs (8 bytes, little-endian int64)
  const tsBuffer = Buffer.alloc(8);
  tsBuffer.writeBigInt64LE(packet.timestampMs, 0);
  buffers.push(new Uint8Array(tsBuffer));

  // Type (4 bytes, little-endian int32)
  buffers.push(le32(packet.type));

  // SourceUhidLength (4 bytes, little-endian int32)
  buffers.push(le32(sourceBytes.length));

  // SourceUhid (UTF-8)
  buffers.push(sourceBytes);

  // DestinationUhidLength (4 bytes, little-endian int32)
  buffers.push(le32(destBytes.length));

  // DestinationUhid (UTF-8)
  buffers.push(destBytes);

  // SHA-256(Payload) (32 bytes)
  buffers.push(new Uint8Array(payloadHash));

  // Ttl (4 bytes, little-endian int32)
  buffers.push(le32(packet.ttl));

  // Priority (4 bytes, little-endian int32)
  buffers.push(le32(packet.priority));

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
  const signableData = buildSignableData(packet);

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
  const signableData = buildSignableData(packet);

  // Verify signature
  return Ed25519Service.verify(publicKey, signableData, packet.signature);
}

/**
 * Non-cryptographic deduplication keyed by (senderUhid, nonce).
 *
 * The composite key is critical: keying by nonce alone (the pre-2026-05-05
 * design in C#) had two failure modes that this implementation defends
 * against:
 *
 *   1. A random 8-byte nonce collision across two unrelated senders would
 *      drop the legitimate sender's first packet.
 *   2. An attacker who pre-registered a chosen nonce against the recipient
 *      could block a legitimate sender's first packet by reserving its
 *      nonce slot.
 *
 * Both go away when the key is (source, nonce): two senders with the same
 * random nonce hash to different cells, and an attacker would have to know
 * both the target sender's UHID AND predict their next random nonce to
 * effect a denial.
 *
 * Cleanup: each (sender, nonce) pair is tracked with the timestamp it was
 * first seen. Periodic sweep (default every 60s) drops entries older than
 * {@link MAX_PACKET_AGE_SECONDS} — matching the C# `FreshnessWindowMs`
 * (5 minutes) so we don't blanket-clear recent legitimate nonces.
 *
 * Mirrors the C# `PacketSigningService._seenNonces` design at
 * src/AetherNet.Security/Services/PacketSigningService.cs.
 */
export class PacketDeduplicator {
  /** Composite key "senderUhid:hex(nonce)" -> ms epoch when first seen. */
  private nonces: Map<string, number> = new Map();
  private lastCleanup: number = Date.now();
  private readonly cleanupIntervalMs: number = 60_000; // 60s — matches C#

  /** Window beyond which a nonce is considered expired. */
  private readonly maxAgeMs: number = MAX_PACKET_AGE_SECONDS * 1000;

  /**
   * Build the composite dedup key. Keying by (source, nonce) — see class
   * docs for why nonce-alone is unsafe.
   */
  private static keyOf(senderUhid: string, nonce: Uint8Array): string {
    return `${senderUhid}:${Buffer.from(nonce).toString("hex")}`;
  }

  /** Check if this (sender, nonce) pair has already been observed. */
  isSeen(senderUhid: string, nonce: Uint8Array): boolean {
    return this.nonces.has(PacketDeduplicator.keyOf(senderUhid, nonce));
  }

  /** Mark this (sender, nonce) pair as observed at the current wall time. */
  mark(senderUhid: string, nonce: Uint8Array): void {
    const key = PacketDeduplicator.keyOf(senderUhid, nonce);
    this.nonces.set(key, Date.now());

    if (Date.now() - this.lastCleanup > this.cleanupIntervalMs) {
      this.cleanup();
    }
  }

  /** Atomically check-and-mark — returns true iff the pair is fresh. */
  checkAndMark(senderUhid: string, nonce: Uint8Array): boolean {
    const key = PacketDeduplicator.keyOf(senderUhid, nonce);
    if (this.nonces.has(key)) return false;
    this.nonces.set(key, Date.now());
    if (Date.now() - this.lastCleanup > this.cleanupIntervalMs) {
      this.cleanup();
    }
    return true;
  }

  /** Number of dedup entries currently held. Exposed for tests. */
  get size(): number {
    return this.nonces.size;
  }

  /** Clear all deduplication state. */
  clear(): void {
    this.nonces.clear();
    this.lastCleanup = Date.now();
  }

  /**
   * Drop entries older than {@link maxAgeMs}. Bounded cost — runs at most
   * once per {@link cleanupIntervalMs}.
   */
  private cleanup(): void {
    const cutoff = Date.now() - this.maxAgeMs;
    for (const [k, ts] of this.nonces) {
      if (ts < cutoff) this.nonces.delete(k);
    }
    this.lastCleanup = Date.now();
  }
}

/**
 * Stateful packet-signing service that combines deduplication and signature
 * verification with optional reputation signalling.
 *
 * Mirrors the C# `PacketSigningService` at
 * src/AetherNet.Security/Services/PacketSigningService.cs — specifically the
 * two hooks added in Item 21:
 *
 *   - `reputation?.RecordReplayAttemptAsync(packet.SourceUhid)` when the
 *     nonce-replay cache detects a duplicate (sourceUhid, nonce) pair.
 *   - `reputation?.RecordSignatureFailureAsync(packet.SourceUhid)` when
 *     Ed25519 signature verification returns false.
 *
 * The reputation field is nullable so callers that do not yet have a
 * `NodeReputationService` wired up incur no error — all calls use optional
 * chaining (`this.reputation?.…`).
 */
export class PacketSigningService {
  private readonly deduplicator: PacketDeduplicator = new PacketDeduplicator();
  private reputation: NodeReputationService | null = null;

  /** Attach (or detach, when null) a reputation service. */
  setReputation(rep: NodeReputationService | null): void {
    this.reputation = rep;
  }

  /**
   * Sign a packet in-place using the supplied Ed25519 private key.
   * Delegates to the module-level {@link signPacket} helper.
   */
  sign(packet: MeshPacket, privateKey: Uint8Array): void {
    signPacket(packet, privateKey);
  }

  /**
   * Verify the packet's timestamp, signature, and nonce freshness in one
   * call, firing reputation hooks on failure.
   *
   * Returns `true` iff all three checks pass.
   *
   * Hook behaviour (Item 21):
   *  - Duplicate nonce → `reputation?.recordReplayAttempt(sourceUhid)`
   *  - Bad signature   → `reputation?.recordSignatureFailure(sourceUhid)`
   */
  verifyAndDedup(packet: MeshPacket, publicKey: Uint8Array): boolean {
    // Nonce deduplication — fires before the expensive crypto verify.
    if (!this.deduplicator.checkAndMark(packet.sourceUhid, packet.packetNonce)) {
      this.reputation?.recordReplayAttempt(packet.sourceUhid);
      return false;
    }

    // Signature + timestamp verification.
    if (!verifyPacket(packet, publicKey)) {
      this.notifySignatureFailure(packet.sourceUhid);
      return false;
    }

    return true;
  }

  /**
   * Fire the signature-failure reputation hook.
   * Extracted as a named helper so tests can assert on it independently.
   */
  notifySignatureFailure(sourceUhid: string): void {
    this.reputation?.recordSignatureFailure(sourceUhid);
  }

  /** Expose deduplicator size for tests. */
  get dedupSize(): number {
    return this.deduplicator.size;
  }

  /** Clear all deduplication state (useful in tests). */
  clearDedup(): void {
    this.deduplicator.clear();
  }
}
