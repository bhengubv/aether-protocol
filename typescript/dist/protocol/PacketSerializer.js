/**
 * Binary serializer/deserializer for MeshPacket
 * Wire format fully compatible with C# implementation
 * All multi-byte integers are little-endian
 * SPDX-License-Identifier: MIT
 */
import { MeshPacket } from "./MeshPacket.js";
/**
 * Wire format:
 *
 *   [1 byte]   Protocol version
 *   [1 byte]   Packet type
 *   [16 bytes] Packet ID (UUID)
 *   [1 byte]   Priority
 *   [4 bytes]  TTL (int32, little-endian)
 *   [8 bytes]  TimestampMs (int64, little-endian)
 *   [2 bytes]  SourceUhid length (uint16, little-endian)
 *   [N bytes]  SourceUhid (UTF-8)
 *   [2 bytes]  DestinationUhid length (uint16, little-endian)
 *   [N bytes]  DestinationUhid (UTF-8)
 *   [2 bytes]  PacketNonce length (uint16, little-endian)
 *   [N bytes]  PacketNonce
 *   [4 bytes]  Payload length (int32, little-endian)
 *   [N bytes]  Payload
 *   [2 bytes]  Signature length (uint16, little-endian)
 *   [N bytes]  Signature
 */
export class PacketSerializer {
    /**
     * Serialize a MeshPacket to its binary wire format
     */
    static serialize(packet) {
        const sourceBytes = new TextEncoder().encode(packet.sourceUhid);
        const destBytes = new TextEncoder().encode(packet.destinationUhid);
        // Calculate total size
        let totalSize = 1 + // protocol version
            1 + // packet type
            16 + // guid
            1 + // priority
            4 + // ttl
            8 + // timestamp
            2 +
            sourceBytes.length +
            2 +
            destBytes.length +
            2 +
            packet.packetNonce.length +
            4 +
            packet.payload.length +
            2 +
            packet.signature.length;
        const buffer = new Uint8Array(totalSize);
        const dv = new DataView(buffer.buffer);
        let offset = 0;
        // Protocol version
        buffer[offset++] = packet.protocolVersion;
        // Packet type
        buffer[offset++] = packet.type;
        // Packet ID (UUID as 16 bytes)
        const uuidBytes = this.uuidStringToBytes(packet.id);
        buffer.set(uuidBytes, offset);
        offset += 16;
        // Priority
        buffer[offset++] = packet.priority;
        // TTL (int32, little-endian)
        dv.setInt32(offset, packet.ttl, true);
        offset += 4;
        // TimestampMs (int64, little-endian)
        dv.setBigInt64(offset, packet.timestampMs, true);
        offset += 8;
        // SourceUhid (length-prefixed, uint16 LE)
        dv.setUint16(offset, sourceBytes.length, true);
        offset += 2;
        buffer.set(sourceBytes, offset);
        offset += sourceBytes.length;
        // DestinationUhid (length-prefixed, uint16 LE)
        dv.setUint16(offset, destBytes.length, true);
        offset += 2;
        buffer.set(destBytes, offset);
        offset += destBytes.length;
        // PacketNonce (length-prefixed, uint16 LE)
        dv.setUint16(offset, packet.packetNonce.length, true);
        offset += 2;
        buffer.set(packet.packetNonce, offset);
        offset += packet.packetNonce.length;
        // Payload (length-prefixed, int32 LE)
        dv.setInt32(offset, packet.payload.length, true);
        offset += 4;
        buffer.set(packet.payload, offset);
        offset += packet.payload.length;
        // Signature (length-prefixed, uint16 LE)
        dv.setUint16(offset, packet.signature.length, true);
        offset += 2;
        buffer.set(packet.signature, offset);
        return buffer;
    }
    /**
     * Deserialize a MeshPacket from its binary wire format
     */
    static deserialize(data) {
        if (data.length < 31) {
            throw new Error("Data is too short to contain a valid MeshPacket");
        }
        const dv = new DataView(data.buffer, data.byteOffset, data.length);
        let offset = 0;
        const packet = new MeshPacket();
        // Protocol version
        packet.protocolVersion = data[offset++];
        // Packet type
        packet.type = data[offset++];
        // Packet ID (16 bytes UUID)
        packet.id = this.bytesToUuidString(data.slice(offset, offset + 16));
        offset += 16;
        // Priority
        packet.priority = data[offset++];
        // TTL (int32, little-endian)
        packet.ttl = dv.getInt32(offset, true);
        offset += 4;
        // TimestampMs (int64, little-endian)
        packet.timestampMs = dv.getBigInt64(offset, true);
        offset += 8;
        // SourceUhid
        const sourceLen = dv.getUint16(offset, true);
        offset += 2;
        this.ensureRemaining(data, offset, sourceLen);
        packet.sourceUhid = new TextDecoder().decode(data.slice(offset, offset + sourceLen));
        offset += sourceLen;
        // DestinationUhid
        const destLen = dv.getUint16(offset, true);
        offset += 2;
        this.ensureRemaining(data, offset, destLen);
        packet.destinationUhid = new TextDecoder().decode(data.slice(offset, offset + destLen));
        offset += destLen;
        // PacketNonce
        const nonceLen = dv.getUint16(offset, true);
        offset += 2;
        this.ensureRemaining(data, offset, nonceLen);
        packet.packetNonce = data.slice(offset, offset + nonceLen);
        offset += nonceLen;
        // Payload
        const payloadLen = dv.getInt32(offset, true);
        offset += 4;
        if (payloadLen < 0) {
            throw new Error("Negative payload length");
        }
        this.ensureRemaining(data, offset, payloadLen);
        packet.payload = data.slice(offset, offset + payloadLen);
        offset += payloadLen;
        // Signature
        const sigLen = dv.getUint16(offset, true);
        offset += 2;
        this.ensureRemaining(data, offset, sigLen);
        packet.signature = data.slice(offset, offset + sigLen);
        // Reconstruct CreatedAt from TimestampMs
        packet.createdAt = new Date(Number(packet.timestampMs));
        return packet;
    }
    /**
     * Try to deserialize, return null on failure
     */
    static tryDeserialize(data) {
        try {
            return this.deserialize(data);
        }
        catch {
            return null;
        }
    }
    static ensureRemaining(data, offset, required) {
        if (offset + required > data.length) {
            throw new Error(`Insufficient data: need ${required} bytes at offset ${offset}, but only ${data.length - offset} remain`);
        }
    }
    /**
     * Convert UUID string (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx) to 16-byte array
     */
    static uuidStringToBytes(uuidStr) {
        const bytes = new Uint8Array(16);
        const parts = uuidStr.replace(/-/g, "");
        for (let i = 0; i < 16; i++) {
            bytes[i] = parseInt(parts.substr(i * 2, 2), 16);
        }
        return bytes;
    }
    /**
     * Convert 16-byte array to UUID string format
     */
    static bytesToUuidString(bytes) {
        if (bytes.length !== 16) {
            throw new Error("UUID bytes must be 16 bytes long");
        }
        const hex = Array.from(bytes)
            .map((b) => b.toString(16).padStart(2, "0"))
            .join("");
        return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    }
}
//# sourceMappingURL=PacketSerializer.js.map