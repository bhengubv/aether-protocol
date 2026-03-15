/**
 * Core MeshPacket interface and factory
 * Wire format fully compatible with C# implementation
 * SPDX-License-Identifier: MIT
 */
import { v4 as uuidv4 } from "uuid";
import { PacketType, packetTypeToString } from "./PacketType.js";
/**
 * Mutable MeshPacket class for building packets
 */
export class MeshPacket {
    id;
    type;
    sourceUhid;
    destinationUhid;
    ttl;
    priority;
    payload;
    signature;
    packetNonce;
    timestampMs;
    protocolVersion;
    createdAt;
    constructor() {
        this.id = uuidv4();
        this.type = PacketType.Data;
        this.sourceUhid = "";
        this.destinationUhid = "";
        this.ttl = 7;
        this.priority = 0;
        this.payload = new Uint8Array();
        this.signature = new Uint8Array();
        this.packetNonce = new Uint8Array();
        this.timestampMs = BigInt(Date.now());
        this.protocolVersion = 2; // Current signed version
        this.createdAt = new Date();
    }
    /**
     * Check if packet has exceeded maximum allowed age
     */
    isExpired(maxAgeSeconds = 300) {
        const nowMs = BigInt(Date.now());
        const ageMs = nowMs - this.timestampMs;
        return ageMs > BigInt(maxAgeSeconds * 1000);
    }
    /**
     * Check if packet can still be forwarded (TTL > 0)
     */
    get canForward() {
        return this.ttl > 0;
    }
    toString() {
        return `[${packetTypeToString(this.type)}] ${this.id} src=${this.sourceUhid} dst=${this.destinationUhid} ttl=${this.ttl} pri=${this.priority} ver=${this.protocolVersion}`;
    }
    /**
     * Create a new packet with generated ID and current timestamp
     */
    static create(type, sourceUhid) {
        const packet = new MeshPacket();
        packet.type = type;
        packet.sourceUhid = sourceUhid;
        packet.timestampMs = BigInt(Date.now());
        packet.createdAt = new Date();
        return packet;
    }
    /**
     * Deep clone this packet
     */
    clone() {
        const cloned = new MeshPacket();
        cloned.id = this.id;
        cloned.type = this.type;
        cloned.sourceUhid = this.sourceUhid;
        cloned.destinationUhid = this.destinationUhid;
        cloned.ttl = this.ttl;
        cloned.priority = this.priority;
        cloned.payload = new Uint8Array(this.payload);
        cloned.signature = new Uint8Array(this.signature);
        cloned.packetNonce = new Uint8Array(this.packetNonce);
        cloned.timestampMs = this.timestampMs;
        cloned.protocolVersion = this.protocolVersion;
        cloned.createdAt = new Date(this.createdAt);
        return cloned;
    }
}
//# sourceMappingURL=MeshPacket.js.map