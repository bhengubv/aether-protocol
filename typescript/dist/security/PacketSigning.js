/**

 * Packet signing and verification

 * SPDX-License-Identifier: MIT

 */
import { createHash, randomBytes } from "crypto";
import { Ed25519Service } from "./Ed25519Service.js";
import { MAX_PACKET_AGE_SECONDS } from "../constants.js";
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
function constructSignableData(packet) {
    const sourceBytes = new TextEncoder().encode(packet.sourceUhid);
    const destBytes = new TextEncoder().encode(packet.destinationUhid);
    const payloadHash = createHash("sha256").update(packet.payload).digest();
    // Build signable data
    const buffers = [];
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
export function signPacket(packet, privateKey) {
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
export function verifyPacket(packet, publicKey) {
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
    nonces = new Map();
    lastCleanup = Date.now();
    cleanupIntervalMs = 60_000; // 60s — matches C#
    /** Window beyond which a nonce is considered expired. */
    maxAgeMs = MAX_PACKET_AGE_SECONDS * 1000;
    /**
  
     * Build the composite dedup key. Keying by (source, nonce) — see class
  
     * docs for why nonce-alone is unsafe.
  
     */
    static keyOf(senderUhid, nonce) {
        return `${senderUhid}:${Buffer.from(nonce).toString("hex")}`;
    }
    /** Check if this (sender, nonce) pair has already been observed. */
    isSeen(senderUhid, nonce) {
        return this.nonces.has(PacketDeduplicator.keyOf(senderUhid, nonce));
    }
    /** Mark this (sender, nonce) pair as observed at the current wall time. */
    mark(senderUhid, nonce) {
        const key = PacketDeduplicator.keyOf(senderUhid, nonce);
        this.nonces.set(key, Date.now());
        if (Date.now() - this.lastCleanup > this.cleanupIntervalMs) {
            this.cleanup();
        }
    }
    /** Atomically check-and-mark — returns true iff the pair is fresh. */
    checkAndMark(senderUhid, nonce) {
        const key = PacketDeduplicator.keyOf(senderUhid, nonce);
        if (this.nonces.has(key))
            return false;
        this.nonces.set(key, Date.now());
        if (Date.now() - this.lastCleanup > this.cleanupIntervalMs) {
            this.cleanup();
        }
        return true;
    }
    /** Number of dedup entries currently held. Exposed for tests. */
    get size() {
        return this.nonces.size;
    }
    /** Clear all deduplication state. */
    clear() {
        this.nonces.clear();
        this.lastCleanup = Date.now();
    }
    /**
  
     * Drop entries older than {@link maxAgeMs}. Bounded cost — runs at most
  
     * once per {@link cleanupIntervalMs}.
  
     */
    cleanup() {
        const cutoff = Date.now() - this.maxAgeMs;
        for (const [k, ts] of this.nonces) {
            if (ts < cutoff)
                this.nonces.delete(k);
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
    deduplicator = new PacketDeduplicator();
    reputation = null;
    /** Attach (or detach, when null) a reputation service. */
    setReputation(rep) {
        this.reputation = rep;
    }
    /**
  
     * Sign a packet in-place using the supplied Ed25519 private key.
  
     * Delegates to the module-level {@link signPacket} helper.
  
     */
    sign(packet, privateKey) {
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
    verifyAndDedup(packet, publicKey) {
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
    notifySignatureFailure(sourceUhid) {
        this.reputation?.recordSignatureFailure(sourceUhid);
    }
    /** Expose deduplicator size for tests. */
    get dedupSize() {
        return this.deduplicator.size;
    }
    /** Clear all deduplication state (useful in tests). */
    clearDedup() {
        this.deduplicator.clear();
    }
}
//# sourceMappingURL=PacketSigning.js.map