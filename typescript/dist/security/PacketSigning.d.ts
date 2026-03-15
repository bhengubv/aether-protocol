/**
 * Packet signing and verification
 * SPDX-License-Identifier: MIT
 */
import { MeshPacket } from "../protocol/MeshPacket.js";
export interface PacketSignature {
    nonce: Uint8Array;
    timestamp: bigint;
    signature: Uint8Array;
}
/**
 * Sign a packet
 */
export declare function signPacket(packet: MeshPacket, privateKey: Uint8Array): void;
/**
 * Verify a packet signature
 */
export declare function verifyPacket(packet: MeshPacket, publicKey: Uint8Array): boolean;
/**
 * Non-cryptographic deduplication with timestamp-based cleanup
 */
export declare class PacketDeduplicator {
    private nonces;
    private lastCleanup;
    private readonly cleanupIntervalMs;
    /**
     * Check if a nonce is already seen for this sender
     */
    isSeen(senderUhid: string, nonce: Uint8Array): boolean;
    /**
     * Mark a nonce as seen
     */
    mark(senderUhid: string, nonce: Uint8Array): void;
    /**
     * Clear all deduplication state
     */
    clear(): void;
    /**
     * Internal cleanup (in real implementation, would respect packet timestamp TTL)
     */
    private cleanup;
}
//# sourceMappingURL=PacketSigning.d.ts.map