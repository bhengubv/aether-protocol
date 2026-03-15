/**
 * Core MeshPacket interface and factory
 * Wire format fully compatible with C# implementation
 * SPDX-License-Identifier: MIT
 */
import { PacketType } from "./PacketType.js";
export interface IMeshPacket {
    id: string;
    type: PacketType;
    sourceUhid: string;
    destinationUhid: string;
    ttl: number;
    priority: number;
    payload: Uint8Array;
    signature: Uint8Array;
    packetNonce: Uint8Array;
    timestampMs: bigint;
    protocolVersion: number;
    createdAt: Date;
}
/**
 * Mutable MeshPacket class for building packets
 */
export declare class MeshPacket implements IMeshPacket {
    id: string;
    type: PacketType;
    sourceUhid: string;
    destinationUhid: string;
    ttl: number;
    priority: number;
    payload: Uint8Array;
    signature: Uint8Array;
    packetNonce: Uint8Array;
    timestampMs: bigint;
    protocolVersion: number;
    createdAt: Date;
    constructor();
    /**
     * Check if packet has exceeded maximum allowed age
     */
    isExpired(maxAgeSeconds?: number): boolean;
    /**
     * Check if packet can still be forwarded (TTL > 0)
     */
    get canForward(): boolean;
    toString(): string;
    /**
     * Create a new packet with generated ID and current timestamp
     */
    static create(type: PacketType, sourceUhid: string): MeshPacket;
    /**
     * Deep clone this packet
     */
    clone(): MeshPacket;
}
//# sourceMappingURL=MeshPacket.d.ts.map