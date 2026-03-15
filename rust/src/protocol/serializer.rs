// SPDX-License-Identifier: MIT

use crate::protocol::MeshPacket;
use std::io::{self, Read, Write};
use uuid::Uuid;

/// Serializes and deserializes MeshPackets to/from binary wire format.
///
/// Wire format (all multi-byte integers are little-endian):
///   [1 byte]  Protocol version
///   [1 byte]  Packet type
///   [16 bytes] Packet ID (GUID)
///   [1 byte]  Priority
///   [4 bytes] TTL (int32)
///   [8 bytes] TimestampMs (int64)
///   [2 bytes] SourceUhid length (u16)
///   [N bytes] SourceUhid (UTF-8)
///   [2 bytes] DestinationUhid length (u16)
///   [N bytes] DestinationUhid (UTF-8)
///   [2 bytes] PacketNonce length (u16)
///   [N bytes] PacketNonce
///   [4 bytes] Payload length (i32)
///   [N bytes] Payload
///   [2 bytes] Signature length (u16)
///   [N bytes] Signature
pub struct PacketSerializer;

impl PacketSerializer {
    /// Serializes a MeshPacket to its binary wire format
    pub fn serialize(packet: &MeshPacket) -> io::Result<Vec<u8>> {
        let mut buffer = Vec::new();

        // Protocol version
        buffer.write_all(&[packet.protocol_version])?;

        // Packet type
        buffer.write_all(&[packet.packet_type.as_byte()])?;

        // Packet ID (16 bytes)
        buffer.write_all(packet.id.as_bytes())?;

        // Priority
        buffer.write_all(&[packet.priority])?;

        // TTL (4 bytes, little-endian i32)
        buffer.write_all(&packet.ttl.to_le_bytes())?;

        // TimestampMs (8 bytes, little-endian i64)
        buffer.write_all(&packet.timestamp_ms.to_le_bytes())?;

        // SourceUhid (length-prefixed, u16 LE)
        let source_bytes = packet.source_uhid.as_bytes();
        buffer.write_all(&(source_bytes.len() as u16).to_le_bytes())?;
        buffer.write_all(source_bytes)?;

        // DestinationUhid (length-prefixed, u16 LE)
        let dest_bytes = packet.destination_uhid.as_bytes();
        buffer.write_all(&(dest_bytes.len() as u16).to_le_bytes())?;
        buffer.write_all(dest_bytes)?;

        // PacketNonce (length-prefixed, u16 LE)
        buffer.write_all(&(packet.packet_nonce.len() as u16).to_le_bytes())?;
        buffer.write_all(&packet.packet_nonce)?;

        // Payload (length-prefixed, i32 LE)
        buffer.write_all(&(packet.payload.len() as i32).to_le_bytes())?;
        buffer.write_all(&packet.payload)?;

        // Signature (length-prefixed, u16 LE)
        buffer.write_all(&(packet.signature.len() as u16).to_le_bytes())?;
        buffer.write_all(&packet.signature)?;

        Ok(buffer)
    }

    /// Deserializes a MeshPacket from binary wire format
    pub fn deserialize(data: &[u8]) -> io::Result<MeshPacket> {
        let mut cursor = &data[..];

        // Minimum valid packet size check
        if data.len() < 43 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Data is too short to contain a valid MeshPacket",
            ));
        }

        // Protocol version
        let mut version_buf = [0u8; 1];
        cursor.read_exact(&mut version_buf)?;
        let protocol_version = version_buf[0];

        // Packet type
        let mut type_buf = [0u8; 1];
        cursor.read_exact(&mut type_buf)?;
        let packet_type = crate::protocol::PacketType::from_byte(type_buf[0])
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "Invalid packet type"))?;

        // Packet ID (16 bytes)
        let mut id_buf = [0u8; 16];
        cursor.read_exact(&mut id_buf)?;
        let id = Uuid::from_bytes(id_buf);

        // Priority
        let mut priority_buf = [0u8; 1];
        cursor.read_exact(&mut priority_buf)?;
        let priority = priority_buf[0];

        // TTL (4 bytes, i32 LE)
        let mut ttl_buf = [0u8; 4];
        cursor.read_exact(&mut ttl_buf)?;
        let ttl = i32::from_le_bytes(ttl_buf);

        // TimestampMs (8 bytes, i64 LE)
        let mut ts_buf = [0u8; 8];
        cursor.read_exact(&mut ts_buf)?;
        let timestamp_ms = i64::from_le_bytes(ts_buf);

        // SourceUhid
        let mut len_buf = [0u8; 2];
        cursor.read_exact(&mut len_buf)?;
        let source_len = u16::from_le_bytes(len_buf) as usize;
        let mut source_bytes = vec![0u8; source_len];
        cursor.read_exact(&mut source_bytes)?;
        let source_uhid = String::from_utf8(source_bytes)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "Invalid UTF-8 in source UHID"))?;

        // DestinationUhid
        cursor.read_exact(&mut len_buf)?;
        let dest_len = u16::from_le_bytes(len_buf) as usize;
        let mut dest_bytes = vec![0u8; dest_len];
        cursor.read_exact(&mut dest_bytes)?;
        let destination_uhid = String::from_utf8(dest_bytes)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "Invalid UTF-8 in destination UHID"))?;

        // PacketNonce
        cursor.read_exact(&mut len_buf)?;
        let nonce_len = u16::from_le_bytes(len_buf) as usize;
        let mut packet_nonce = vec![0u8; nonce_len];
        cursor.read_exact(&mut packet_nonce)?;

        // Payload
        let mut payload_len_buf = [0u8; 4];
        cursor.read_exact(&mut payload_len_buf)?;
        let payload_len = i32::from_le_bytes(payload_len_buf);
        if payload_len < 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Negative payload length",
            ));
        }
        let mut payload = vec![0u8; payload_len as usize];
        cursor.read_exact(&mut payload)?;

        // Signature
        cursor.read_exact(&mut len_buf)?;
        let sig_len = u16::from_le_bytes(len_buf) as usize;
        let mut signature = vec![0u8; sig_len];
        cursor.read_exact(&mut signature)?;

        Ok(MeshPacket {
            id,
            packet_type,
            source_uhid,
            destination_uhid,
            ttl,
            priority,
            payload,
            timestamp_ms,
            protocol_version,
            signature,
            packet_nonce,
        })
    }

    /// Attempts to deserialize a packet, returning None on failure
    pub fn try_deserialize(data: &[u8]) -> Option<MeshPacket> {
        Self::deserialize(data).ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::PacketType;

    #[test]
    fn test_serialize_deserialize_roundtrip() {
        let mut packet = MeshPacket::new(PacketType::Data, "node-a".to_string());
        packet.destination_uhid = "node-b".to_string();
        packet.payload = vec![1, 2, 3, 4, 5];
        packet.ttl = 5;
        packet.priority = 10;
        packet.packet_nonce = vec![1, 2, 3, 4, 5, 6, 7, 8];
        packet.signature = vec![0x42; 64];

        let serialized = PacketSerializer::serialize(&packet).unwrap();
        let deserialized = PacketSerializer::deserialize(&serialized).unwrap();

        assert_eq!(deserialized.id, packet.id);
        assert_eq!(deserialized.packet_type, packet.packet_type);
        assert_eq!(deserialized.source_uhid, packet.source_uhid);
        assert_eq!(deserialized.destination_uhid, packet.destination_uhid);
        assert_eq!(deserialized.ttl, packet.ttl);
        assert_eq!(deserialized.priority, packet.priority);
        assert_eq!(deserialized.payload, packet.payload);
        assert_eq!(deserialized.signature, packet.signature);
        assert_eq!(deserialized.packet_nonce, packet.packet_nonce);
    }

    #[test]
    fn test_serialize_empty_packet() {
        let packet = MeshPacket::new(PacketType::Heartbeat, "node-1".to_string());
        let serialized = PacketSerializer::serialize(&packet).unwrap();
        let deserialized = PacketSerializer::deserialize(&serialized).unwrap();

        assert_eq!(deserialized.source_uhid, "node-1");
        assert_eq!(deserialized.payload.len(), 0);
        assert_eq!(deserialized.signature.len(), 0);
    }

    #[test]
    fn test_deserialize_invalid_data() {
        let result = PacketSerializer::deserialize(&[1, 2, 3]);
        assert!(result.is_err());
    }
}
