# SPDX-License-Identifier: MIT



"""Unit tests for the Aether SOS service."""



from __future__ import annotations



import asyncio

import json

import unittest

from uuid import UUID, uuid4



from aethernet import constants

from aethernet.models import SosAlert

from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from aethernet.sos import SosBroadcastService

from aethernet.sos.service import _encode_ack_payload



from tests.fakes import FakeMeshSender





LOCAL = "local"





_LOOP = asyncio.new_event_loop()


def _run(coro):

    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)





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





def _new_sos_ack_packet(broadcast_id, responder: str, received_at_ms: int = 1_700_000_000_000) -> MeshPacket:
    body = _encode_ack_payload(broadcast_id, received_at_ms)
    return MeshPacket(
        type=PacketType.SosAck,
        source_uhid=responder,
        destination_uhid="origin",
        payload=body,
    )


def _originate_sos(origin_uhid: str):
    """Originate a real SosBroadcast on a separate node; return (packet, broadcast_id)."""
    origin_sender = FakeMeshSender(origin_uhid)
    origin = SosBroadcastService(origin_sender)
    _run(origin.broadcast("medical", "help", -26.20, 28.04, geohash="ke7g"))
    return origin_sender.broadcasts[0], origin.get_active_alerts()[0].id


class SosAckTests(unittest.TestCase):
    # ─── Byte-identity gate ──────────────────────────────

    def test_ack_payload_serializes_to_canonical_bytes(self):
        vectors = [
            (
                "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
                1_700_000_000_000,
                b'{"broadcast_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","received_at_ms":1700000000000}',
            ),
            (
                "00000000-0000-0000-0000-000000000000",
                0,
                b'{"broadcast_id":"00000000-0000-0000-0000-000000000000","received_at_ms":0}',
            ),
        ]
        for guid, ms, expected in vectors:
            with self.subTest(guid=guid, ms=ms):
                self.assertEqual(expected, _encode_ack_payload(UUID(guid), ms))

    # ─── Directed ack on receive ─────────────────────────

    def test_handle_receiving_sos_sends_directed_ack_to_originator(self):
        sos, broadcast_id = _originate_sos("origin")

        receiver_sender = FakeMeshSender("receiver")
        _run(SosBroadcastService(receiver_sender).handle(sos))

        self.assertEqual(1, len(receiver_sender.unicasts))
        rec = receiver_sender.unicasts[0]
        self.assertEqual(PacketType.SosAck, rec.packet.type)
        self.assertEqual("origin", rec.next_hop_uhid)
        self.assertEqual("origin", rec.packet.destination_uhid)

        data = json.loads(rec.packet.payload.decode("utf-8"))
        self.assertEqual(str(broadcast_id), data["broadcast_id"])

    def test_handle_own_sos_does_not_ack(self):
        local_sender = FakeMeshSender(LOCAL)
        svc = SosBroadcastService(local_sender)
        _run(svc.broadcast("panic", None, 0, 0))

        # Re-handling our own broadcast must not generate an ack.
        _run(svc.handle(local_sender.broadcasts[0]))
        self.assertEqual([], local_sender.unicasts)

    # ─── handle_ack on originator ────────────────────────

    def test_handle_ack_on_originator_records_responder_and_fires_event(self):
        origin_sender = FakeMeshSender("origin")
        origin = SosBroadcastService(origin_sender)
        _run(origin.broadcast("fire", "north wing", -26.1, 28.0))
        broadcast_id = origin.get_active_alerts()[0].id

        captured = {}
        origin.on_sos_acknowledged = lambda e: captured.setdefault("e", e)

        _run(origin.handle_ack(_new_sos_ack_packet(broadcast_id, "responder-cc")))

        self.assertIn("e", captured)
        self.assertEqual(broadcast_id, captured["e"].broadcast_id)
        self.assertEqual("responder-cc", captured["e"].responder_uhid)
        self.assertEqual(1, captured["e"].total_acknowledgements)
        self.assertIn("responder-cc", origin.get_active_alerts()[0].acknowledged_by)

    def test_handle_ack_duplicate_responder_counted_once(self):
        origin_sender = FakeMeshSender("origin")
        origin = SosBroadcastService(origin_sender)
        _run(origin.broadcast("medical", None, 0, 0))
        broadcast_id = origin.get_active_alerts()[0].id

        events = {"n": 0}
        origin.on_sos_acknowledged = lambda e: events.__setitem__("n", events["n"] + 1)

        _run(origin.handle_ack(_new_sos_ack_packet(broadcast_id, "responder-cc")))
        _run(origin.handle_ack(_new_sos_ack_packet(broadcast_id, "responder-cc")))  # same responder

        self.assertEqual(1, events["n"])
        self.assertEqual(1, len(origin.get_active_alerts()[0].acknowledged_by))

    def test_handle_ack_two_distinct_responders_counts_two(self):
        origin_sender = FakeMeshSender("origin")
        origin = SosBroadcastService(origin_sender)
        _run(origin.broadcast("medical", None, 0, 0))
        broadcast_id = origin.get_active_alerts()[0].id

        _run(origin.handle_ack(_new_sos_ack_packet(broadcast_id, "responder-cc")))
        _run(origin.handle_ack(_new_sos_ack_packet(broadcast_id, "responder-dd")))

        self.assertEqual(2, len(origin.get_active_alerts()[0].acknowledged_by))

    def test_handle_ack_ignores_self_responder(self):
        origin_sender = FakeMeshSender(LOCAL)
        origin = SosBroadcastService(origin_sender)
        _run(origin.broadcast("medical", None, 0, 0))
        broadcast_id = origin.get_active_alerts()[0].id

        raised = {}
        origin.on_sos_acknowledged = lambda e: raised.setdefault("flag", True)

        # Our own ack echoed back — must be ignored.
        _run(origin.handle_ack(_new_sos_ack_packet(broadcast_id, LOCAL)))
        self.assertNotIn("flag", raised)
        self.assertEqual(0, len(origin.get_active_alerts()[0].acknowledged_by))

    def test_handle_ack_unknown_broadcast_is_noop(self):
        svc = SosBroadcastService(FakeMeshSender(LOCAL))
        raised = {}
        svc.on_sos_acknowledged = lambda e: raised.setdefault("flag", True)

        _run(svc.handle_ack(_new_sos_ack_packet(uuid4(), "responder-cc")))
        self.assertNotIn("flag", raised)

    def test_handle_ack_rejects_wrong_packet_type(self):
        svc = SosBroadcastService(FakeMeshSender(LOCAL))
        pkt = _new_sos_ack_packet(uuid4(), "responder-cc")
        pkt.type = PacketType.Data
        with self.assertRaises(ValueError):
            _run(svc.handle_ack(pkt))


if __name__ == "__main__":

    unittest.main()

