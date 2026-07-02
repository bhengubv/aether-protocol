# SPDX-License-Identifier: MIT

"""Unit tests for the Aether heartbeat service (PacketType.Heartbeat).

Mirrors tests/AetherNet.Core.Tests/HeartbeatTests.cs. Uses the shared in-memory
FakeMeshSender — no transport needed.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from datetime import datetime

from aethernet.heartbeat import HeartbeatPayload, HeartbeatService, PeerLiveness
from aethernet.heartbeat.service import _encode_heartbeat_payload
from aethernet.models import PeerInfo
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


LOCAL = "aether:local:01"


_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _new_svc(local: str = LOCAL):
    sender = FakeMeshSender(local)
    return HeartbeatService(sender), sender


def _heartbeat_from(source: str, sequence: int, sent_at_ms: int) -> MeshPacket:
    return MeshPacket(
        type=PacketType.Heartbeat,
        source_uhid=source,
        destination_uhid="*",
        payload=_encode_heartbeat_payload(sequence, sent_at_ms),
    )


class HeartbeatPayloadByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate ──────────────────────────────

    def test_payload_serializes_to_canonical_bytes(self):
        vectors = [
            (1, 1_700_000_000_000, b'{"sequence":1,"sent_at_ms":1700000000000}'),
            (0, 0, b'{"sequence":0,"sent_at_ms":0}'),
        ]
        for sequence, ms, expected in vectors:
            with self.subTest(sequence=sequence, ms=ms):
                self.assertEqual(expected, _encode_heartbeat_payload(sequence, ms))


class HeartbeatServiceTests(unittest.TestCase):
    # ─── Send ────────────────────────────────────────────

    def test_send_broadcasts_heartbeat_with_incrementing_sequence(self):
        svc, sender = _new_svc()

        _run(svc.send_heartbeat())
        _run(svc.send_heartbeat())

        self.assertEqual(2, len(sender.broadcasts))
        for pkt in sender.broadcasts:
            self.assertEqual(PacketType.Heartbeat, pkt.type)
            self.assertEqual(1, pkt.ttl)
            self.assertEqual("*", pkt.destination_uhid)

        first = json.loads(sender.broadcasts[0].payload.decode("utf-8"))
        second = json.loads(sender.broadcasts[1].payload.decode("utf-8"))
        self.assertEqual(1, first["sequence"])
        self.assertEqual(2, second["sequence"])

    def test_send_returns_delivered_peer_count(self):
        svc, sender = _new_svc()
        sender.add_peer(
            PeerInfo(uhid="aether:peer:aa", public_key=b"", last_seen=datetime.utcnow())
        )
        delivered = _run(svc.send_heartbeat())
        self.assertEqual(1, delivered)

    # ─── Handle ──────────────────────────────────────────

    def test_handle_records_peer_and_fires_event(self):
        svc, _ = _new_svc(LOCAL)
        seen = {}
        svc.on_peer_seen = lambda p: seen.setdefault("p", p)

        ok = _run(svc.handle(_heartbeat_from("aether:peer:aa", 7, 1_700_000_000_000)))

        self.assertTrue(ok)
        self.assertIn("p", seen)
        self.assertEqual("aether:peer:aa", seen["p"].uhid)
        self.assertEqual(7, seen["p"].last_sequence)
        self.assertEqual(1_700_000_000_000, seen["p"].last_sent_at_ms)

        known = svc.get_known_peers()
        self.assertEqual(1, len(known))
        self.assertEqual("aether:peer:aa", known[0].uhid)

    def test_handle_refreshes_existing_peer(self):
        svc, _ = _new_svc()
        _run(svc.handle(_heartbeat_from("aether:peer:aa", 1, 1000)))
        _run(svc.handle(_heartbeat_from("aether:peer:aa", 2, 2000)))

        known = svc.get_known_peers()
        self.assertEqual(1, len(known))
        self.assertEqual(2, known[0].last_sequence)

    def test_handle_own_heartbeat_is_ignored(self):
        svc, _ = _new_svc(LOCAL)
        ok = _run(svc.handle(_heartbeat_from(LOCAL, 1, 1000)))
        self.assertFalse(ok)
        self.assertEqual([], svc.get_known_peers())

    def test_handle_wrong_packet_type_returns_false(self):
        svc, _ = _new_svc()
        pkt = _heartbeat_from("aether:peer:aa", 1, 1000)
        pkt.type = PacketType.Data
        self.assertFalse(_run(svc.handle(pkt)))
        self.assertEqual([], svc.get_known_peers())

    def test_handle_malformed_payload_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(
            type=PacketType.Heartbeat,
            source_uhid="aether:peer:aa",
            destination_uhid="*",
            payload=b"not json",
        )
        self.assertFalse(_run(svc.handle(pkt)))
        self.assertEqual([], svc.get_known_peers())

    # ─── GetLivePeers ────────────────────────────────────

    def test_get_live_peers_includes_recently_seen_peer(self):
        svc, _ = _new_svc()
        _run(svc.handle(_heartbeat_from("aether:peer:aa", 1, 1000)))

        # A just-received heartbeat is live within any generous window.
        live = svc.get_live_peers(within_seconds=3600)
        self.assertEqual(1, len(live))
        self.assertEqual("aether:peer:aa", live[0].uhid)

        # A negative window pushes the recency horizon into the future, so it excludes
        # even a just-seen peer — a deterministic proof the filter filters.
        self.assertEqual([], svc.get_live_peers(within_seconds=-1))


if __name__ == "__main__":
    unittest.main()
