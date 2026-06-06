# SPDX-License-Identifier: MIT

"""Unit tests for the Aether SOS service."""

from __future__ import annotations

import asyncio
import json
import unittest
from uuid import uuid4

from aethernet import constants
from aethernet.models import SosAlert
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.sos import SosBroadcastService

from tests.fakes import FakeMeshSender


LOCAL = "local"


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


def _new_svc():
    sender = FakeMeshSender(LOCAL)
    svc = SosBroadcastService(sender)
    return svc, sender


def _new_sos_packet(source: str, ttl: int = constants.SOS_TTL) -> MeshPacket:
    body = json.dumps({
        "broadcast_id": str(uuid4()),
        "broadcast_type": "sos",
        "message": "help",
        "latitude": -33.9,
        "longitude": 18.4,
        "geohash": None,
    }).encode("utf-8")
    return MeshPacket(
        type=PacketType.SosBroadcast,
        source_uhid=source,
        destination_uhid="",
        ttl=ttl,
        priority=constants.SOS_PRIORITY,
        payload=body,
    )


class SosBroadcastServiceTests(unittest.TestCase):
    # ─── Broadcast ────────────────────────────────────────

    def test_broadcast_floods_and_stores_alert(self):
        svc, sender = _new_svc()
        ok = _run(svc.broadcast("sos", "help", -33.9, 18.4))
        self.assertTrue(ok)
        self.assertEqual(1, len(sender.broadcasts))
        pkt = sender.broadcasts[0]
        self.assertEqual(PacketType.SosBroadcast, pkt.type)
        self.assertEqual(constants.SOS_TTL, pkt.ttl)
        self.assertEqual(constants.SOS_PRIORITY, pkt.priority)
        self.assertEqual(1, len(svc.get_active_alerts()))

    def test_broadcast_rate_limited_after_max(self):
        svc, _ = _new_svc()
        for _ in range(constants.MAX_SOS_BROADCASTS_PER_HOUR):
            self.assertTrue(_run(svc.broadcast("sos", "h", 0, 0)))
        self.assertFalse(_run(svc.broadcast("sos", "h", 0, 0)))

    def test_broadcast_rejects_empty_type(self):
        svc, _ = _new_svc()
        with self.assertRaises(ValueError):
            _run(svc.broadcast("", "help", 0, 0))

    # ─── Handle ──────────────────────────────────────────

    def test_handle_drops_duplicate_packet_id(self):
        svc, sender = _new_svc()
        pkt = _new_sos_packet("alice")
        _run(svc.handle(pkt))
        sender.clear()
        alerts_after = len(svc.get_active_alerts())

        _run(svc.handle(pkt))
        self.assertEqual([], sender.broadcasts)
        self.assertEqual(alerts_after, len(svc.get_active_alerts()))

    def test_handle_ignores_self_originated(self):
        svc, sender = _new_svc()
        pkt = _new_sos_packet(LOCAL)
        _run(svc.handle(pkt))
        self.assertEqual([], sender.broadcasts)

    def test_handle_raises_sos_received(self):
        svc, _ = _new_svc()
        observed = {}
        svc.on_sos_received = lambda a: observed.setdefault("a", a)

        pkt = _new_sos_packet("alice")
        _run(svc.handle(pkt))
        self.assertIn("a", observed)
        self.assertEqual("alice", observed["a"].sender_uhid)

    def test_handle_rebroadcasts_when_ttl_allows(self):
        svc, sender = _new_svc()
        pkt = _new_sos_packet("alice", ttl=5)
        _run(svc.handle(pkt))
        self.assertEqual(1, len(sender.broadcasts))
        self.assertEqual(4, sender.broadcasts[0].ttl)

    def test_handle_does_not_rebroadcast_when_ttl_exhausted(self):
        svc, sender = _new_svc()
        pkt = _new_sos_packet("alice", ttl=1)
        _run(svc.handle(pkt))
        self.assertEqual([], sender.broadcasts)

    def test_handle_rejects_wrong_packet_type(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(type=PacketType.Data, source_uhid="alice")
        with self.assertRaises(ValueError):
            _run(svc.handle(pkt))

    # ─── Resolve ─────────────────────────────────────────

    def test_resolve_removes_alert_and_fires_callback(self):
        svc, _ = _new_svc()
        resolved = {}
        svc.on_sos_resolved = lambda i: resolved.setdefault("id", i)

        _run(svc.broadcast("sos", "h", 0, 0))
        alert = svc.get_active_alerts()[0]
        _run(svc.resolve(alert.id))

        self.assertEqual([], svc.get_active_alerts())
        self.assertEqual(alert.id, resolved.get("id"))

    def test_resolve_unknown_id_is_noop(self):
        svc, _ = _new_svc()
        called = {}
        svc.on_sos_resolved = lambda i: called.setdefault("flag", True)

        _run(svc.resolve(uuid4()))
        self.assertNotIn("flag", called)


if __name__ == "__main__":
    unittest.main()
