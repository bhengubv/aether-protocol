# SPDX-License-Identifier: MIT

"""Unit tests for StreamingService."""

from __future__ import annotations

import asyncio
import json
import struct
import time
import unittest
import uuid

from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.streaming.service import StreamingService

from tests.fakes import FakeMeshSender


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


def _make_svc(uhid: str = "alice"):
    sender = FakeMeshSender(uhid)
    svc = StreamingService(sender, uhid)
    return svc, sender


def _announce_packet(from_uhid: str, stream_id: uuid.UUID, state: str, title: str = "test") -> MeshPacket:
    body = json.dumps({
        "stream_id": str(stream_id),
        "title": title,
        "content_type": "video/h264",
        "codec": "h264",
        "segment_duration_ms": 1000,
        "state": state,
        "started_at_ms": int(time.time() * 1000),
    }).encode("utf-8")
    return MeshPacket(
        type=PacketType.StreamAnnounce,
        source_uhid=from_uhid,
        destination_uhid="",
        ttl=7,
        priority=32,
        payload=body,
    )


def _subscribe_packet(from_uhid: str, to_uhid: str, stream_id: uuid.UUID) -> MeshPacket:
    return MeshPacket(
        type=PacketType.StreamSubscribe,
        source_uhid=from_uhid,
        destination_uhid=to_uhid,
        ttl=7,
        priority=32,
        payload=json.dumps({"stream_id": str(stream_id), "live_only": False}).encode("utf-8"),
    )


def _unsubscribe_packet(from_uhid: str, to_uhid: str, stream_id: uuid.UUID) -> MeshPacket:
    return MeshPacket(
        type=PacketType.StreamUnsubscribe,
        source_uhid=from_uhid,
        destination_uhid=to_uhid,
        ttl=7,
        priority=32,
        payload=json.dumps({"stream_id": str(stream_id)}).encode("utf-8"),
    )


# ── startStream ───────────────────────────────────────────────────────────────

class StartStreamTests(unittest.TestCase):

    def test_broadcasts_stream_announce_with_state_live(self):
        svc, sender = _make_svc()
        stream_id = _run(svc.start_stream("My Stream", "video/h264", "h264", 2000))
        self.assertEqual(1, len(sender.broadcasts))
        pkt = sender.broadcasts[0]
        self.assertEqual(PacketType.StreamAnnounce, pkt.type)
        body = json.loads(pkt.payload.decode("utf-8"))
        self.assertEqual("live", body["state"])
        self.assertEqual("My Stream", body["title"])
        self.assertEqual(str(stream_id), body["stream_id"])

    def test_returns_unique_stream_id(self):
        svc, _ = _make_svc()
        sid1 = _run(svc.start_stream("A", "video/h264", "h264", 1000))
        sid2 = _run(svc.start_stream("B", "video/h264", "h264", 1000))
        self.assertNotEqual(sid1, sid2)


# ── endStream ─────────────────────────────────────────────────────────────────

class EndStreamTests(unittest.TestCase):

    def test_broadcasts_ended_announce(self):
        svc, sender = _make_svc()
        stream_id = _run(svc.start_stream("T", "video/h264", "h264", 1000))
        sender.clear()
        _run(svc.end_stream(stream_id))
        self.assertGreaterEqual(len(sender.broadcasts), 1)
        last_body = json.loads(sender.broadcasts[-1].payload.decode("utf-8"))
        self.assertEqual("ended", last_body["state"])
        self.assertEqual(str(stream_id), last_body["stream_id"])


# ── subscribe / unsubscribe ───────────────────────────────────────────────────

class SubscribeTests(unittest.TestCase):

    def test_subscribe_sends_stream_subscribe_unicast(self):
        svc, sender = _make_svc("alice")
        fake_stream_id = uuid.uuid4()
        _run(svc.subscribe(fake_stream_id, "bob", False))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertEqual(1, len(to_bob))
        self.assertEqual(PacketType.StreamSubscribe, to_bob[0].packet.type)

    def test_unsubscribe_sends_stream_unsubscribe_unicast(self):
        svc, sender = _make_svc("alice")
        fake_stream_id = uuid.uuid4()
        _run(svc.subscribe(fake_stream_id, "bob", False))
        sender.clear()
        _run(svc.unsubscribe(fake_stream_id, "bob"))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertEqual(1, len(to_bob))
        self.assertEqual(PacketType.StreamUnsubscribe, to_bob[0].packet.type)


# ── publishSegment ────────────────────────────────────────────────────────────

class PublishSegmentTests(unittest.TestCase):

    def test_inbound_subscribe_then_publish_reaches_subscriber(self):
        svc, sender = _make_svc("alice")
        stream_id = _run(svc.start_stream("T", "video/h264", "h264", 1000))
        sender.clear()
        _run(svc.handle_packet(_subscribe_packet("bob", "alice", stream_id)))
        _run(svc.publish_segment(stream_id, bytes([1, 2, 3, 4]), True))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0, "expected StreamSegment unicast to bob")
        self.assertEqual(PacketType.StreamSegment, to_bob[0].packet.type)

    def test_fans_out_to_multiple_subscribers(self):
        svc, sender = _make_svc("alice")
        stream_id = _run(svc.start_stream("T", "video/h264", "h264", 1000))
        _run(svc.handle_packet(_subscribe_packet("bob", "alice", stream_id)))
        _run(svc.handle_packet(_subscribe_packet("carol", "alice", stream_id)))
        sender.clear()
        _run(svc.publish_segment(stream_id, bytes([1, 2, 3]), False))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        to_carol = [u for u in sender.unicasts if u.next_hop_uhid == "carol"]
        self.assertTrue(len(to_bob) > 0, "bob should receive segment")
        self.assertTrue(len(to_carol) > 0, "carol should receive segment")

    def test_unsubscribed_peer_receives_no_segments(self):
        svc, sender = _make_svc("alice")
        stream_id = _run(svc.start_stream("T", "video/h264", "h264", 1000))
        _run(svc.handle_packet(_subscribe_packet("bob", "alice", stream_id)))
        _run(svc.handle_packet(_unsubscribe_packet("bob", "alice", stream_id)))
        sender.clear()
        _run(svc.publish_segment(stream_id, bytes([1, 2, 3]), False))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertEqual(0, len(to_bob), "unsubscribed bob must not receive segments")


# ── handlePacket — announce flow ──────────────────────────────────────────────

class HandleAnnounceTests(unittest.TestCase):

    def test_live_announce_fires_on_stream_announced(self):
        svc, _ = _make_svc("alice")
        remote_id = uuid.uuid4()
        announced = []
        svc.on_stream_announced = lambda *args: announced.append(args)
        _run(svc.handle_packet(_announce_packet("bob", remote_id, "live", "Bob's Stream")))
        self.assertEqual(1, len(announced))
        # announced[0] = (stream_id, publisher_uhid, title, content_type, codec, seg_dur, state, started_at)
        self.assertEqual(remote_id, announced[0][0])
        self.assertEqual("bob", announced[0][1])
        self.assertEqual("Bob's Stream", announced[0][2])

    def test_ended_announce_fires_on_stream_ended(self):
        svc, _ = _make_svc("alice")
        remote_id = uuid.uuid4()
        ended = []
        svc.on_stream_ended = lambda sid: ended.append(sid)
        _run(svc.handle_packet(_announce_packet("bob", remote_id, "ended")))
        self.assertEqual([remote_id], ended)


if __name__ == "__main__":
    unittest.main()
