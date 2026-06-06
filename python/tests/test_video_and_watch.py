# SPDX-License-Identifier: MIT

"""Unit tests for VideoCallService and WatchTogetherService."""

from __future__ import annotations

import asyncio
import json
import struct
import time
import unittest
import uuid

from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.streaming.video_service import VideoCallService, VideoCallState, _pack_video_frame
from aethermesh.streaming.watch_together import WatchTogetherService

from tests.fakes import FakeMeshSender


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


# ── Helpers ───────────────────────────────────────────────────────────────────

def _make_video_svc(uhid: str = "alice"):
    sender = FakeMeshSender(uhid)
    svc = VideoCallService(sender, None, uhid)
    return svc, sender


def _make_watch_svc(uhid: str = "alice"):
    sender = FakeMeshSender(uhid)
    svc = WatchTogetherService(sender, uhid)
    return svc, sender


def _video_signaling_packet(from_uhid: str, body: dict) -> MeshPacket:
    return MeshPacket(
        type=PacketType.VideoSignaling,
        source_uhid=from_uhid,
        destination_uhid="",
        ttl=7,
        priority=32,
        payload=json.dumps(body).encode("utf-8"),
    )


def _watch_sync_packet(from_uhid: str, body: dict) -> MeshPacket:
    return MeshPacket(
        type=PacketType.WatchSync,
        source_uhid=from_uhid,
        destination_uhid="",
        ttl=7,
        priority=32,
        payload=json.dumps(body).encode("utf-8"),
    )


def _watch_reaction_packet(from_uhid: str, session_id: uuid.UUID, reaction: str) -> MeshPacket:
    return MeshPacket(
        type=PacketType.WatchReaction,
        source_uhid=from_uhid,
        destination_uhid="",
        ttl=7,
        priority=32,
        payload=json.dumps({"session_id": str(session_id), "reaction": reaction}).encode("utf-8"),
    )


def _video_frame_packet(from_uhid: str, call_id: uuid.UUID) -> MeshPacket:
    payload = _pack_video_frame(
        call_id=call_id,
        sequence=0,
        timestamp_ms=int(time.time() * 1000),
        is_keyframe=True,
        encoded_video=bytes([0x11, 0x22, 0x33, 0x44]),
    )
    return MeshPacket(
        type=PacketType.VideoFrame,
        source_uhid=from_uhid,
        destination_uhid="",
        ttl=7,
        priority=64,
        payload=payload,
    )


# ── VideoCallService — sendOffer ──────────────────────────────────────────────

class VideoSendOfferTests(unittest.TestCase):

    def test_sends_video_signaling_to_callee(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertEqual(1, len(to_bob))
        self.assertEqual(PacketType.VideoSignaling, to_bob[0].packet.type)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("offer", msg["kind"])
        self.assertEqual("alice", msg["from_uhid"])
        self.assertEqual("bob", msg["to_uhid"])
        self.assertEqual(str(call_id), msg["call_id"])

    def test_empty_to_uhid_raises(self):
        svc, _ = _make_video_svc()
        with self.assertRaises(ValueError):
            _run(svc.send_offer("", ["h264"], 1280, 720, 30, 2000))


# ── VideoCallService — inbound offer ─────────────────────────────────────────

class VideoInboundOfferTests(unittest.TestCase):

    def test_fires_on_incoming_call(self):
        svc, _ = _make_video_svc("alice")
        call_id = uuid.uuid4()
        received = []
        svc.on_incoming_call = lambda cid, from_u, codecs, w, h, f, b: received.append((cid, from_u))
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "offer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
            "proposed_codecs": ["h264"], "width": 1280, "height": 720,
            "fps": 30, "bitrate_kbps": 2000,
        })))
        self.assertEqual(1, len(received))
        self.assertEqual(call_id, received[0][0])
        self.assertEqual("bob", received[0][1])


# ── VideoCallService — inbound answer ────────────────────────────────────────

class VideoInboundAnswerTests(unittest.TestCase):

    def test_fires_on_call_accepted_and_state_connected(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        sender.clear()
        accepted = []
        svc.on_call_accepted = lambda cid, codec: accepted.append(cid)
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
            "selected_codec": "h264",
        })))
        self.assertEqual(1, len(accepted))
        self.assertEqual(call_id, accepted[0])


# ── VideoCallService — inbound hangup ────────────────────────────────────────

class VideoInboundHangupTests(unittest.TestCase):

    def test_fires_on_call_ended(self):
        svc, _ = _make_video_svc("alice")
        call_id = uuid.uuid4()
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "offer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
            "proposed_codecs": ["h264"], "width": 1280, "height": 720, "fps": 30, "bitrate_kbps": 2000,
        })))
        ended = []
        svc.on_call_ended = lambda cid, reason: ended.append(cid)
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "hangup", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
        })))
        self.assertEqual([call_id], ended)


# ── VideoCallService — acceptCall ─────────────────────────────────────────────

class VideoAcceptCallTests(unittest.TestCase):

    def test_sends_answer_unicast_and_state_becomes_connected(self):
        svc, sender = _make_video_svc("alice")
        call_id = uuid.uuid4()
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "offer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
            "proposed_codecs": ["h264"], "width": 1280, "height": 720, "fps": 30, "bitrate_kbps": 2000,
        })))
        sender.clear()
        _run(svc.accept_call(call_id))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0, "expected answer unicast to bob")
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("answer", msg["kind"])
        session = svc._sessions.get(call_id)
        self.assertIsNotNone(session)
        self.assertEqual(VideoCallState.Connected, session.state)


# ── VideoCallService — hangUp ─────────────────────────────────────────────────

class VideoHangUpTests(unittest.TestCase):

    def test_sends_cancel_when_outgoing(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        sender.clear()
        _run(svc.hang_up(call_id))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("cancel", msg["kind"])

    def test_sends_hangup_when_connected(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice", "selected_codec": "h264",
        })))
        sender.clear()
        _run(svc.hang_up(call_id))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("hangup", msg["kind"])


# ── VideoCallService — sendFrame ──────────────────────────────────────────────

class VideoSendFrameTests(unittest.TestCase):

    def test_sends_video_frame_packet_when_connected(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice", "selected_codec": "h264",
        })))
        sender.clear()
        _run(svc.send_frame(call_id, bytes([1, 2, 3, 4]), True))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0, "expected VideoFrame unicast to bob")
        self.assertEqual(PacketType.VideoFrame, to_bob[0].packet.type)

    def test_no_packet_sent_when_not_connected(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        sender.clear()
        _run(svc.send_frame(call_id, bytes([1, 2, 3]), False))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertEqual(0, len(to_bob), "no VideoFrame should be sent while not connected")


# ── VideoCallService — inbound frame ─────────────────────────────────────────

class VideoInboundFrameTests(unittest.TestCase):

    def test_fires_on_frame_received(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice", "selected_codec": "h264",
        })))
        sender.clear()
        received = []
        svc.on_frame_received = lambda cid, seq, ts, kf, video: received.append((cid, kf, video))
        _run(svc.handle_packet(_video_frame_packet("bob", call_id)))
        self.assertEqual(1, len(received))
        self.assertEqual(call_id, received[0][0])
        self.assertTrue(received[0][1])  # is_keyframe=True
        self.assertEqual(bytes([0x11, 0x22, 0x33, 0x44]), received[0][2])


# ── VideoCallService — keyframeRequest ───────────────────────────────────────

class VideoKeyframeTests(unittest.TestCase):

    def test_request_keyframe_sends_signaling(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice", "selected_codec": "h264",
        })))
        sender.clear()
        _run(svc.request_keyframe(call_id))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("keyframe_request", msg["kind"])

    def test_inbound_keyframe_request_fires_callback(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice", "selected_codec": "h264",
        })))
        fired = []
        svc.on_keyframe_requested = lambda cid: fired.append(cid)
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "keyframe_request", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
        })))
        self.assertEqual([call_id], fired)


# ── VideoCallService — qualityChange ─────────────────────────────────────────

class VideoQualityChangeTests(unittest.TestCase):

    def test_inbound_quality_change_fires_callback(self):
        svc, sender = _make_video_svc("alice")
        call_id = _run(svc.send_offer("bob", ["h264"], 1280, 720, 30, 2000))
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice", "selected_codec": "h264",
        })))
        received_quality = []
        svc.on_quality_changed = lambda cid, w, h, f, b: received_quality.append((w, h, f, b))
        _run(svc.handle_packet(_video_signaling_packet("bob", {
            "kind": "quality_change", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
            "width": 640, "height": 360, "fps": 15, "bitrate_kbps": 500,
        })))
        self.assertEqual(1, len(received_quality))
        self.assertEqual((640, 360, 15, 500), received_quality[0])


# ── WatchTogetherService — invite ─────────────────────────────────────────────

class WatchInviteTests(unittest.TestCase):

    def test_invite_sends_unicast_to_each_member(self):
        svc, sender = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        _run(svc.invite_to_session(session_id, "movie:123", ["bob", "carol"]))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        to_carol = [u for u in sender.unicasts if u.next_hop_uhid == "carol"]
        self.assertTrue(len(to_bob) > 0, "expected invite to bob")
        self.assertTrue(len(to_carol) > 0, "expected invite to carol")
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("invite", msg["kind"])
        self.assertEqual(PacketType.WatchSync, to_bob[0].packet.type)


# ── WatchTogetherService — inbound invite ────────────────────────────────────

class WatchInboundInviteTests(unittest.TestCase):

    def test_fires_on_invited(self):
        svc, _ = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        received = []
        svc.on_invited = lambda sid, cid, from_u: received.append((sid, cid, from_u))
        _run(svc.handle_packet(_watch_sync_packet("bob", {
            "session_id": str(session_id),
            "kind": "invite",
            "content_id": "movie:123",
            "sent_at_ms": int(time.time() * 1000),
        })))
        self.assertEqual(1, len(received))
        self.assertEqual(session_id, received[0][0])
        self.assertEqual("movie:123", received[0][1])
        self.assertEqual("bob", received[0][2])


# ── WatchTogetherService — play/pause/seek ───────────────────────────────────

class WatchPlaybackTests(unittest.TestCase):

    def test_play_sends_sync_to_members(self):
        svc, sender = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        _run(svc.invite_to_session(session_id, "movie:123", ["bob"]))
        sender.clear()
        _run(svc.play(session_id, 5000))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("play", msg["kind"])
        self.assertEqual(5000, msg["position_ms"])
        self.assertEqual(PacketType.WatchSync, to_bob[0].packet.type)

    def test_pause_sends_sync_to_members(self):
        svc, sender = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        _run(svc.invite_to_session(session_id, "movie:123", ["bob"]))
        sender.clear()
        _run(svc.pause(session_id, 12000))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("pause", msg["kind"])

    def test_seek_sends_sync_to_members(self):
        svc, sender = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        _run(svc.invite_to_session(session_id, "movie:123", ["bob"]))
        sender.clear()
        _run(svc.seek(session_id, 60000))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("seek", msg["kind"])
        self.assertEqual(60000, msg["position_ms"])

    def test_set_speed_sends_sync_to_members(self):
        svc, sender = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        _run(svc.invite_to_session(session_id, "movie:123", ["bob"]))
        sender.clear()
        _run(svc.set_speed(session_id, 1.5))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("speed", msg["kind"])
        self.assertAlmostEqual(1.5, msg["playback_speed"])


# ── WatchTogetherService — sendReaction ──────────────────────────────────────

class WatchReactionTests(unittest.TestCase):

    def test_send_reaction_unicasts_to_all_except_self(self):
        svc, sender = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        _run(svc.invite_to_session(session_id, "movie:123", ["bob", "carol"]))
        sender.clear()
        _run(svc.send_reaction(session_id, "👍"))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        to_carol = [u for u in sender.unicasts if u.next_hop_uhid == "carol"]
        to_alice = [u for u in sender.unicasts if u.next_hop_uhid == "alice"]
        self.assertTrue(len(to_bob) > 0, "bob should receive reaction")
        self.assertTrue(len(to_carol) > 0, "carol should receive reaction")
        self.assertEqual(0, len(to_alice), "alice (self) must not receive own reaction")
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertIn("👍", msg["reaction"])


# ── WatchTogetherService — inbound sync events ───────────────────────────────

class WatchInboundSyncTests(unittest.TestCase):

    def _make_with_session(self):
        svc, sender = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        _run(svc.invite_to_session(session_id, "movie:123", ["bob"]))
        sender.clear()
        return svc, sender, session_id

    def test_inbound_play_fires_on_play(self):
        svc, _, session_id = self._make_with_session()
        fired = []
        svc.on_play = lambda sid, pos: fired.append((sid, pos))
        now = int(time.time() * 1000)
        _run(svc.handle_packet(_watch_sync_packet("bob", {
            "session_id": str(session_id),
            "kind": "play",
            "position_ms": 5000,
            "sent_at_ms": now,
        })))
        self.assertEqual(1, len(fired))
        self.assertEqual(session_id, fired[0][0])
        # Position should be approximately 5000 (with possible tiny RTT)
        self.assertGreaterEqual(fired[0][1], 5000)

    def test_inbound_pause_fires_on_pause(self):
        svc, _, session_id = self._make_with_session()
        fired = []
        svc.on_pause = lambda sid, pos: fired.append((sid, pos))
        now = int(time.time() * 1000)
        _run(svc.handle_packet(_watch_sync_packet("bob", {
            "session_id": str(session_id),
            "kind": "pause",
            "position_ms": 12000,
            "sent_at_ms": now,
        })))
        self.assertEqual(1, len(fired))
        self.assertEqual(session_id, fired[0][0])
        self.assertEqual(12000, fired[0][1])  # pause position is NOT compensated

    def test_inbound_seek_fires_on_seek(self):
        svc, _, session_id = self._make_with_session()
        fired = []
        svc.on_seek = lambda sid, pos: fired.append((sid, pos))
        now = int(time.time() * 1000)
        _run(svc.handle_packet(_watch_sync_packet("bob", {
            "session_id": str(session_id),
            "kind": "seek",
            "position_ms": 60000,
            "sent_at_ms": now,
        })))
        self.assertEqual(1, len(fired))
        self.assertGreaterEqual(fired[0][1], 60000)

    def test_inbound_speed_fires_on_speed_change(self):
        svc, _, session_id = self._make_with_session()
        fired = []
        svc.on_speed_change = lambda sid, spd: fired.append((sid, spd))
        _run(svc.handle_packet(_watch_sync_packet("bob", {
            "session_id": str(session_id),
            "kind": "speed",
            "playback_speed": 1.5,
            "sent_at_ms": int(time.time() * 1000),
        })))
        self.assertEqual(1, len(fired))
        self.assertAlmostEqual(1.5, fired[0][1])


# ── WatchTogetherService — inbound reaction ───────────────────────────────────

class WatchInboundReactionTests(unittest.TestCase):

    def test_inbound_reaction_fires_on_reaction(self):
        svc, _ = _make_watch_svc("alice")
        session_id = uuid.uuid4()
        received = []
        svc.on_reaction = lambda sid, from_u, reaction: received.append((sid, from_u, reaction))
        _run(svc.handle_packet(_watch_reaction_packet("bob", session_id, "❤️")))
        self.assertEqual(1, len(received))
        self.assertEqual(session_id, received[0][0])
        self.assertEqual("bob", received[0][1])
        self.assertEqual("❤️", received[0][2])


if __name__ == "__main__":
    unittest.main()
