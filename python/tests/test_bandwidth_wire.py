# SPDX-License-Identifier: MIT

"""Unit tests for the ABMF WIRE bindings: BandwidthProbe(53), BandwidthAck(54),
BandwidthGossip(55).

Mirrors tests/AetherNet.Core.Tests/BandwidthWireTests.cs (10 tests) plus a
byte-identity gate against ../fixtures/bandwidth/vectors.json. Binary little-endian
byte-identity gates + send/handle behaviour. Uses the shared in-memory
FakeMeshSender — no transport needed.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from pathlib import Path

from aethernet.bandwidth import (
    BandwidthConfidence,
    BandwidthGossipPayload,
    BandwidthProbe,
    BandwidthProbeAck,
    BandwidthProbeReceived,
    BandwidthWireService,
    deserialize_ack,
    serialize_ack,
    serialize_gossip,
    serialize_probe,
)
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


LOCAL = "aether:local:01"

_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _fixtures_dir() -> Path:
    # python/tests/test_bandwidth_wire.py → python/tests/.. → aether-protocol/fixtures
    return Path(__file__).resolve().parent.parent.parent / "fixtures"


def _load_vectors() -> list[dict]:
    with (_fixtures_dir() / "bandwidth" / "vectors.json").open(encoding="utf-8") as fp:
        return json.load(fp)["vectors"]


def _new_svc(local: str = LOCAL):
    sender = FakeMeshSender(local)
    return BandwidthWireService(sender), sender


class BandwidthWireByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gates (fixtures/bandwidth/vectors.json) ─────────

    def test_probe_serializes_to_canonical_bytes(self):
        self.assertEqual(
            "2a00000000401e18240a0600",
            serialize_probe(BandwidthProbe(sequence=42, sender_send_us=1_700_000_000_000_000)).hex(),
        )

    def test_ack_serializes_to_canonical_bytes(self):
        # sender_receive_us (999) is local-only and must NOT change the wire bytes.
        ack = BandwidthProbeAck(
            sequence=42,
            sender_send_us=1_700_000_000_000_000,
            receiver_receive_us=1_700_000_000_012_345,
            receiver_send_us=1_700_000_000_013_000,
            sender_receive_us=999,
            probe_bytes=1200,
        )
        self.assertEqual(
            "2a00000000401e18240a060039701e18240a0600c8721e18240a0600b0040000",
            serialize_ack(ack).hex(),
        )

    def test_gossip_serializes_to_canonical_bytes(self):
        # peer_uhid / transport_name / measured_at are not on the wire.
        g = BandwidthGossipPayload(
            peer_uhid="peer",
            transport_name="tp",
            btlbw_bps=5_000_000,
            rt_prop_us=25_000,
            confidence=BandwidthConfidence.Medium,
            measured_at=0.0,
        )
        self.assertEqual("404b4c0000000000a861000002", serialize_gossip(g).hex())

    def test_all_fixture_vectors_match(self):
        for vec in _load_vectors():
            with self.subTest(name=vec["name"]):
                kind = vec["kind"]
                if kind == "probe":
                    body = serialize_probe(
                        BandwidthProbe(
                            sequence=vec["sequence"], sender_send_us=vec["sender_send_us"]
                        )
                    )
                elif kind == "ack":
                    body = serialize_ack(
                        BandwidthProbeAck(
                            sequence=vec["sequence"],
                            sender_send_us=vec["sender_send_us"],
                            receiver_receive_us=vec["receiver_receive_us"],
                            receiver_send_us=vec["receiver_send_us"],
                            sender_receive_us=0,
                            probe_bytes=vec["probe_bytes"],
                        )
                    )
                elif kind == "gossip":
                    body = serialize_gossip(
                        BandwidthGossipPayload(
                            peer_uhid="",
                            transport_name="",
                            btlbw_bps=vec["btlbw_bps"],
                            rt_prop_us=vec["rtprop_us"],
                            confidence=BandwidthConfidence(vec["confidence"]),
                            measured_at=0.0,
                        )
                    )
                else:
                    raise AssertionError(f"unknown vector kind {kind!r}")
                self.assertEqual(vec["expected_hex"], body.hex())

    def test_ack_round_trips_sender_receive_us_zeroed(self):
        back = deserialize_ack(
            serialize_ack(
                BandwidthProbeAck(
                    sequence=7,
                    sender_send_us=100,
                    receiver_receive_us=200,
                    receiver_send_us=300,
                    sender_receive_us=400,
                    probe_bytes=512,
                )
            )
        )
        self.assertEqual(7, back.sequence)
        self.assertEqual(100, back.sender_send_us)
        self.assertEqual(200, back.receiver_receive_us)
        self.assertEqual(300, back.receiver_send_us)
        self.assertEqual(0, back.sender_receive_us)  # not on wire
        self.assertEqual(512, back.probe_bytes)


class BandwidthWireServiceTests(unittest.TestCase):
    # ─── Behaviour ────────────────────────────────────────────────────

    def test_send_probe_emits_directed_probe(self):
        svc, sender = _new_svc("aether:a:01")
        self.assertTrue(
            _run(svc.send_probe("aether:b:02", BandwidthProbe(sequence=42, sender_send_us=1_700_000_000_000_000)))
        )
        self.assertEqual(1, len(sender.unicasts))
        rec = sender.unicasts[0]
        self.assertEqual(PacketType.BandwidthProbe, rec.packet.type)
        self.assertEqual("aether:b:02", rec.next_hop_uhid)

    def test_send_ack_emits_directed_ack(self):
        svc, sender = _new_svc()
        ack = BandwidthProbeAck(
            sequence=1,
            sender_send_us=2,
            receiver_receive_us=3,
            receiver_send_us=4,
            sender_receive_us=5,
            probe_bytes=6,
        )
        self.assertTrue(_run(svc.send_ack("aether:b:02", ack)))
        self.assertEqual(1, len(sender.unicasts))
        self.assertEqual(PacketType.BandwidthAck, sender.unicasts[0].packet.type)

    def test_broadcast_gossip_emits_gossip_and_handle_raises_event_with_source_peer(self):
        svc, sender = _new_svc()
        for i in range(3):
            from aethernet.models import PeerInfo
            from datetime import datetime

            sender.add_peer(
                PeerInfo(uhid=f"aether:peer:{i:02d}", public_key=b"", last_seen=datetime.utcnow())
            )
        g = BandwidthGossipPayload(
            peer_uhid="",
            transport_name="",
            btlbw_bps=5_000_000,
            rt_prop_us=25_000,
            confidence=BandwidthConfidence.Medium,
            measured_at=0.0,
        )
        self.assertEqual(3, _run(svc.broadcast_gossip(g)))
        self.assertEqual(1, len(sender.broadcasts))
        sent = sender.broadcasts[0]
        self.assertEqual(PacketType.BandwidthGossip, sent.type)

        got = {}
        svc.on_gossip_received = lambda e: got.setdefault("g", e)
        sent.source_uhid = "aether:peer:09"
        self.assertTrue(_run(svc.handle(sent)))
        self.assertIn("g", got)
        self.assertEqual(5_000_000, got["g"].btlbw_bps)
        self.assertEqual(25_000, got["g"].rt_prop_us)
        self.assertEqual(BandwidthConfidence.Medium, got["g"].confidence)
        self.assertEqual("aether:peer:09", got["g"].peer_uhid)

    def test_handle_probe_raises_probe_received_with_source(self):
        svc, _ = _new_svc()
        got = {}
        svc.on_probe_received = lambda e: got.setdefault("p", e)
        pkt = MeshPacket(
            type=PacketType.BandwidthProbe,
            source_uhid="aether:x:01",
            payload=serialize_probe(BandwidthProbe(sequence=9, sender_send_us=123)),
        )
        self.assertTrue(_run(svc.handle(pkt)))
        self.assertIn("p", got)
        self.assertIsInstance(got["p"], BandwidthProbeReceived)
        self.assertEqual(9, got["p"].probe.sequence)
        self.assertEqual("aether:x:01", got["p"].from_uhid)

    def test_handle_ack_raises_ack_received(self):
        svc, _ = _new_svc()
        got = {}
        svc.on_ack_received = lambda e: got.setdefault("a", e)
        pkt = MeshPacket(
            type=PacketType.BandwidthAck,
            source_uhid="aether:x:01",
            payload=serialize_ack(
                BandwidthProbeAck(
                    sequence=3,
                    sender_send_us=10,
                    receiver_receive_us=20,
                    receiver_send_us=30,
                    sender_receive_us=0,
                    probe_bytes=64,
                )
            ),
        )
        self.assertTrue(_run(svc.handle(pkt)))
        self.assertIn("a", got)
        self.assertEqual(3, got["a"].sequence)
        self.assertEqual(64, got["a"].probe_bytes)

    def test_handle_wrong_type_returns_false(self):
        svc, _ = _new_svc()
        self.assertFalse(_run(svc.handle(MeshPacket(type=PacketType.Data, payload=b""))))

    def test_handle_short_payload_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(
            type=PacketType.BandwidthProbe,
            source_uhid="aether:x:01",
            payload=b"\x01\x02\x03",  # < 12 bytes
        )
        self.assertFalse(_run(svc.handle(pkt)))


if __name__ == "__main__":
    unittest.main()
