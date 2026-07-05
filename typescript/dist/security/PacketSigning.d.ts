/**
 * Packet signing and verification
 * SPDX-License-Identifier: MIT
 */
import { MeshPacket } from "../protocol/MeshPacket.js";
import { NodeReputationService } from "../reputation.js";
export interface PacketSignature {
    nonce: Uint8Array;
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
export declare function buildSignableData(packet: MeshPacket): Uint8Array;
/**
 * Sign a packet
 */
export declare function signPacket(packet: MeshPacket, privateKey: Uint8Array): void;
/**
 * Verify a packet signature
 */
export declare function verifyPacket(packet: MeshPacket, publicKey: Uint8Array): boolean;
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
export declare class PacketDeduplicator {
    /** Composite key "senderUhid:hex(nonce)" -> ms epoch when first seen. */
    private nonces;
    private lastCleanup;
    private readonly cleanupIntervalMs;
    /** Window beyond which a nonce is considered expired. */
    private readonly maxAgeMs;
    /**
     * Build the composite dedup key. Keying by (source, nonce) — see class
     * docs for why nonce-alone is unsafe.
     */
    private static keyOf;
    /** Check if this (sender, nonce) pair has already been observed. */
    isSeen(senderUhid: string, nonce: Uint8Array): boolean;
    /** Mark this (sender, nonce) pair as observed at the current wall time. */
    mark(senderUhid: string, nonce: Uint8Array): void;
    /** Atomically check-and-mark — returns true iff the pair is fresh. */
    checkAndMark(senderUhid: string, nonce: Uint8Array): boolean;
    /** Number of dedup entries currently held. Exposed for tests. */
    get size(): number;
    /** Clear all deduplication state. */
    clear(): void;
    /**
     * Drop entries older than {@link maxAgeMs}. Bounded cost — runs at most
     * once per {@link cleanupIntervalMs}.
     */
    private cleanup;
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
export declare class PacketSigningService {
    private readonly deduplicator;
    private reputation;
    /** Attach (or detach, when null) a reputation service. */
    setReputation(rep: NodeReputationService | null): void;
    /**
     * Sign a packet in-place using the supplied Ed25519 private key.
     * Delegates to the module-level {@link signPacket} helper.
     */
    sign(packet: MeshPacket, privateKey: Uint8Array): void;
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
    verifyAndDedup(packet: MeshPacket, publicKey: Uint8Array): boolean;
    /**
     * Fire the signature-failure reputation hook.
     * Extracted as a named helper so tests can assert on it independently.
     */
    notifySignatureFailure(sourceUhid: string): void;
    /** Expose deduplicator size for tests. */
    get dedupSize(): number;
    /** Clear all deduplication state (useful in tests). */
    clearDedup(): void;
}
//# sourceMappingURL=PacketSigning.d.ts.map