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
export declare class PacketSerializer {
    /**
     * Serialize a MeshPacket to its binary wire format
     */
    static serialize(packet: MeshPacket): Uint8Array;
    /**
     * Deserialize a MeshPacket from its binary wire format
     */
    static deserialize(data: Uint8Array): MeshPacket;
    /**
     * Try to deserialize, return null on failure
     */
    static tryDeserialize(data: Uint8Array): MeshPacket | null;
    private static ensureRemaining;
    /**
     * Convert UUID string (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx) to 16-byte array
     */
    private static uuidStringToBytes;
    /**
     * Convert 16-byte array to UUID string format
     */
    private static bytesToUuidString;
}
//# sourceMappingURL=PacketSerializer.d.ts.map