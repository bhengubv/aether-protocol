"""Binary serializer/deserializer for MeshPacket.

Wire format (all multi-byte integers are little-endian):
  [1 byte]  Protocol version
  [1 byte]  Packet type
  [16 bytes] Packet ID (GUID)
  [1 byte]  Priority
  [4 bytes] TTL (int32)
  [8 bytes] TimestampMs (int64)
  [2 bytes] SourceUhid length (uint16)
  [N bytes] SourceUhid (UTF-8)
  [2 bytes] DestinationUhid length (uint16)
  [N bytes] DestinationUhid (UTF-8)
  [2 bytes] PacketNonce length (uint16)
  [N bytes] PacketNonce
  [4 bytes] Payload length (int32)
  [N bytes] Payload
  [2 bytes] Signature length (uint16)
  [N bytes] Signature
"""

import struct
from typing import Optional
from uuid import UUID
from aether.protocol.mesh_packet import MeshPacket, PacketType
from datetime import datetime


class PacketSerializer:
    """Binary serializer/deserializer for MeshPacket."""

    @staticmethod
    def serialize(packet: MeshPacket) -> bytes:
        """
        Serialize a MeshPacket to its binary wire format.

        All multi-byte integers are in little-endian format.
        """
        if packet is None:
            raise ValueError("packet cannot be None")

        source_bytes = packet.source_uhid.encode("utf-8")
        dest_bytes = packet.destination_uhid.encode("utf-8")

        # Build the packet step by step
        parts = []

        # Protocol version (1 byte)
        parts.append(struct.pack("<B", packet.protocol_version))

        # Packet type (1 byte)
        parts.append(struct.pack("<B", packet.type))

        # Packet ID (16 bytes UUID)
        parts.append(packet.id.bytes)

        # Priority (1 byte)
        parts.append(struct.pack("<B", packet.priority))

        # TTL (4 bytes, little-endian int32)
        parts.append(struct.pack("<i", packet.ttl))

        # TimestampMs (8 bytes, little-endian int64)
        parts.append(struct.pack("<q", packet.timestamp_ms))

        # SourceUhid length (2 bytes, little-endian uint16) + data
        parts.append(struct.pack("<H", len(source_bytes)))
        parts.append(source_bytes)

        # DestinationUhid length (2 bytes, little-endian uint16) + data
        parts.append(struct.pack("<H", len(dest_bytes)))
        parts.append(dest_bytes)

        # PacketNonce length (2 bytes, little-endian uint16) + data
        parts.append(struct.pack("<H", len(packet.packet_nonce)))
        parts.append(packet.packet_nonce)

        # Payload length (4 bytes, little-endian int32) + data
        parts.append(struct.pack("<i", len(packet.payload)))
        parts.append(packet.payload)

        # Signature length (2 bytes, little-endian uint16) + data
        parts.append(struct.pack("<H", len(packet.signature)))
        parts.append(packet.signature)

        return b"".join(parts)

    @staticmethod
    def deserialize(data: bytes) -> MeshPacket:
        """
        Deserialize a MeshPacket from its binary wire format.

        Raises ValueError if the data is malformed.
        """
        if data is None:
            raise ValueError("data cannot be None")

        if len(data) < 31:
            raise ValueError(
                "Data is too short to contain a valid MeshPacket. "
                f"Got {len(data)} bytes, need at least 31."
            )

        offset = 0

        # Protocol version (1 byte)
        protocol_version = struct.unpack_from("<B", data, offset)[0]
        offset += 1

        # Packet type (1 byte)
        packet_type = PacketType(struct.unpack_from("<B", data, offset)[0])
        offset += 1

        # Packet ID (16 bytes)
        packet_id = UUID(bytes=data[offset : offset + 16])
        offset += 16

        # Priority (1 byte)
        priority = struct.unpack_from("<B", data, offset)[0]
        offset += 1

        # TTL (4 bytes, little-endian int32)
        ttl = struct.unpack_from("<i", data, offset)[0]
        offset += 4

        # TimestampMs (8 bytes, little-endian int64)
        timestamp_ms = struct.unpack_from("<q", data, offset)[0]
        offset += 8

        # SourceUhid length (2 bytes) + data
        source_len = struct.unpack_from("<H", data, offset)[0]
        offset += 2
        if offset + source_len > len(data):
            raise ValueError(
                f"Insufficient data for SourceUhid. Need {source_len}, "
                f"have {len(data) - offset} remaining."
            )
        source_uhid = data[offset : offset + source_len].decode("utf-8")
        offset += source_len

        # DestinationUhid length (2 bytes) + data
        dest_len = struct.unpack_from("<H", data, offset)[0]
        offset += 2
        if offset + dest_len > len(data):
            raise ValueError(
                f"Insufficient data for DestinationUhid. Need {dest_len}, "
                f"have {len(data) - offset} remaining."
            )
        dest_uhid = data[offset : offset + dest_len].decode("utf-8")
        offset += dest_len

        # PacketNonce length (2 bytes) + data
        nonce_len = struct.unpack_from("<H", data, offset)[0]
        offset += 2
        if offset + nonce_len > len(data):
            raise ValueError(
                f"Insufficient data for PacketNonce. Need {nonce_len}, "
                f"have {len(data) - offset} remaining."
            )
        packet_nonce = data[offset : offset + nonce_len]
        offset += nonce_len

        # Payload length (4 bytes, little-endian int32) + data
        payload_len = struct.unpack_from("<i", data, offset)[0]
        offset += 4
        if payload_len < 0:
            raise ValueError(f"Negative payload length: {payload_len}")
        if offset + payload_len > len(data):
            raise ValueError(
                f"Insufficient data for Payload. Need {payload_len}, "
                f"have {len(data) - offset} remaining."
            )
        payload = data[offset : offset + payload_len]
        offset += payload_len

        # Signature length (2 bytes) + data
        sig_len = struct.unpack_from("<H", data, offset)[0]
        offset += 2
        if offset + sig_len > len(data):
            raise ValueError(
                f"Insufficient data for Signature. Need {sig_len}, "
                f"have {len(data) - offset} remaining."
            )
        signature = data[offset : offset + sig_len]

        # Reconstruct CreatedAt from TimestampMs
        created_at = datetime.fromtimestamp(timestamp_ms / 1000)

        packet = MeshPacket(
            id=packet_id,
            type=packet_type,
            source_uhid=source_uhid,
            destination_uhid=dest_uhid,
            ttl=ttl,
            priority=priority,
            payload=payload,
            packet_nonce=packet_nonce,
            signature=signature,
            created_at=created_at,
            timestamp_ms=timestamp_ms,
            protocol_version=protocol_version,
        )

        return packet

    @staticmethod
    def try_deserialize(data: bytes) -> Optional[MeshPacket]:
        """
        Attempt to deserialize a packet, returning None on failure.
        """
        try:
            return PacketSerializer.deserialize(data)
        except (ValueError, struct.error, Exception):
            return None
