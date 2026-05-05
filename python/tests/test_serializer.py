"""Round-trip tests for PacketSerializer.

Mirror of swift/Tests/PacketSerializationTests.swift; cross-language byte
equivalence is anchored separately under fixtures/.

Run with: cd python && python -m unittest tests.test_serializer
"""

from __future__ import annotations

import unittest
from uuid import UUID, uuid4

from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.protocol.serializer import PacketSerializer


def _nonce(fill: int = 0x00) -> bytes:
    return bytes([fill] * 8)


class PacketSerializerTests(unittest.TestCase):

    def test_round_trip(self):
        pkt = MeshPacket(
            type=PacketType.Data,
            source_uhid="alice-node",
            destination_uhid="bob-node",
            ttl=7,
            priority=10,
            payload=b"Hello, Aether!",
            packet_nonce=_nonce(0xAB),
            timestamp_ms=1710528000000,
        )
        b = PacketSerializer.serialize(pkt)
        got = PacketSerializer.deserialize(b)

        self.assertEqual(got.type, pkt.type)
        self.assertEqual(got.source_uhid, pkt.source_uhid)
        self.assertEqual(got.destination_uhid, pkt.destination_uhid)
        self.assertEqual(got.ttl, pkt.ttl)
        self.assertEqual(got.priority, pkt.priority)
        self.assertEqual(got.payload, pkt.payload)
        self.assertEqual(got.packet_nonce, pkt.packet_nonce)
        self.assertEqual(got.protocol_version, pkt.protocol_version)

    def test_empty_destination_uhid(self):
        pkt = MeshPacket(
            type=PacketType.SosBroadcast,
            source_uhid="node-1",
            destination_uhid="",
            packet_nonce=_nonce(),
        )
        got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
        self.assertEqual(got.source_uhid, "node-1")
        self.assertEqual(got.destination_uhid, "")

    def test_empty_payload(self):
        pkt = MeshPacket(
            type=PacketType.Heartbeat,
            source_uhid="node-1",
            packet_nonce=_nonce(),
            payload=b"",
        )
        got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
        self.assertEqual(len(got.payload), 0)

    def test_large_payload(self):
        payload = bytes([0xFF] * 262144)
        pkt = MeshPacket(
            type=PacketType.ChunkData,
            source_uhid="node-1",
            destination_uhid="node-2",
            packet_nonce=_nonce(),
            payload=payload,
        )
        got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
        self.assertEqual(len(got.payload), 262144)
        self.assertEqual(got.payload[0], 0xFF)
        self.assertEqual(got.payload[-1], 0xFF)

    def test_uuid_round_trip(self):
        expected = UUID("550e8400-e29b-41d4-a716-446655440000")
        pkt = MeshPacket(
            id=expected, type=PacketType.Data, source_uhid="node-1",
            packet_nonce=_nonce(),
        )
        got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
        self.assertEqual(got.id, expected)

    def test_uuid_wire_order_is_rfc4122_big_endian(self):
        expected = UUID("550e8400-e29b-41d4-a716-446655440000")
        pkt = MeshPacket(
            id=expected, type=PacketType.Data, source_uhid="n",
            packet_nonce=_nonce(),
        )
        b = PacketSerializer.serialize(pkt)
        want = bytes([
            0x55, 0x0e, 0x84, 0x00, 0xe2, 0x9b, 0x41, 0xd4,
            0xa7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x00,
        ])
        self.assertEqual(b[2:18], want)

    def test_too_short_raises(self):
        with self.assertRaises(ValueError):
            PacketSerializer.deserialize(b"\x01\x02")

    def test_try_deserialize_returns_none_on_garbage(self):
        self.assertIsNone(PacketSerializer.try_deserialize(b"\xff"))

    def test_all_packet_types_round_trip(self):
        for ty in PacketType:
            pkt = MeshPacket(
                type=ty, source_uhid=f"node-{ty.value}",
                packet_nonce=_nonce(),
            )
            got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
            self.assertEqual(got.type, ty)

    def test_timestamp_preserved_to_ms(self):
        ts = 1710528000000
        pkt = MeshPacket(
            type=PacketType.Data, source_uhid="node-1",
            timestamp_ms=ts, packet_nonce=_nonce(),
        )
        got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
        self.assertEqual(got.timestamp_ms, ts)

    def test_unicode_uhids(self):
        pkt = MeshPacket(
            type=PacketType.Data,
            source_uhid="노드-1",
            destination_uhid="узел-2",
            packet_nonce=_nonce(),
        )
        got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
        self.assertEqual(got.source_uhid, "노드-1")
        self.assertEqual(got.destination_uhid, "узел-2")

    def test_signature_preserved(self):
        sig = bytes([0xAB] * 64)
        pkt = MeshPacket(
            type=PacketType.Data, source_uhid="node-1",
            packet_nonce=_nonce(), signature=sig,
        )
        got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
        self.assertEqual(got.signature, sig)

    def test_ttl_full_int32_range_preserved(self):
        # > UInt8 max — would have wrapped to 0 under the pre-2026-05-02 bug.
        pkt = MeshPacket(
            type=PacketType.Data, source_uhid="n",
            ttl=256, packet_nonce=_nonce(),
        )
        got = PacketSerializer.deserialize(PacketSerializer.serialize(pkt))
        self.assertEqual(got.ttl, 256)


if __name__ == "__main__":
    unittest.main()
