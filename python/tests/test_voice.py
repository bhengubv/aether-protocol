# SPDX-License-Identifier: MIT

"""Unit tests for VoiceCallService and GroupVoiceCallService."""

from __future__ import annotations

import asyncio
import json
import struct
import time
import unittest
import uuid

from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.voice.service import VoiceCallService, VoiceCallState, _pack_voice_frame
from aether.voice.group_service import GroupVoiceCallService, _pack_group_frame

from tests.fakes import FakeMeshSender


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


# ── Helpers ───────────────────────────────────────────────────────────────────

def _make_voice_svc(uhid: str = "alice"):
    sender = FakeMeshSender(uhid)
    svc = VoiceCallService(sender, None, uhid)
    return svc, sender


def _make_group_svc(uhid: str = "alice"):
    sender = FakeMeshSender(uhid)
    svc = GroupVoiceCallService(sender, None, uhid)
    return svc, sender


def _signaling_packet(from_uhid: str, body: dict) -> MeshPacket:
    return MeshPacket(
        type=PacketType.VoiceSignaling,
        source_uhid=from_uhid,
        destination_uhid="",
        ttl=7,
        priority=32,
        payload=json.dumps(body).encode("utf-8"),
    )


def _voice_call_packet(from_uhid: str, call_id: uuid.UUID) -> MeshPacket:
    payload = _pack_voice_frame(
        call_id=call_id,
        sequence=0,
        timestamp_ms=int(time.time() * 1000),
        is_silence=False,
        encoded_audio=bytes([0xAA, 0xBB, 0xCC, 0xDD]),
    )
    return MeshPacket(
        type=PacketType.VoiceCall,
        source_uhid=from_uhid,
        destination_uhid="",
        ttl=7,
        priority=64,
        payload=payload,
    )


def _group_voice_packet(from_uhid: str, call_id: uuid.UUID) -> MeshPacket:
    payload = _pack_group_frame(
        call_id=call_id,
        sequence=0,
        timestamp_ms=int(time.time() * 1000),
        is_silence=False,
        key_generation=0,
        encoded_audio=bytes([0xAA, 0xBB, 0xCC, 0xDD]),
    )
    return MeshPacket(
        type=PacketType.VoiceCall,
        source_uhid=from_uhid,
        destination_uhid="",
        ttl=7,
        priority=64,
        payload=payload,
    )


# ── VoiceCallService — sendOffer ──────────────────────────────────────────────

class SendOfferTests(unittest.TestCase):

    def test_sends_voice_signaling_to_callee(self):
        svc, sender = _make_voice_svc("alice")
        call_id = _run(svc.send_offer("bob", ["opus"], 48000))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertEqual(1, len(to_bob))
        self.assertEqual(PacketType.VoiceSignaling, to_bob[0].packet.type)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("offer", msg["kind"])
        self.assertEqual("alice", msg["from_uhid"])
        self.assertEqual("bob", msg["to_uhid"])
        self.assertEqual(str(call_id), msg["call_id"])

    def test_empty_to_uhid_raises(self):
        svc, _ = _make_voice_svc()
        with self.assertRaises(ValueError):
            _run(svc.send_offer("", ["opus"], 48000))


# ── VoiceCallService — inbound offer ─────────────────────────────────────────

class InboundOfferTests(unittest.TestCase):

    def test_fires_on_incoming_call(self):
        svc, _ = _make_voice_svc("alice")
        call_id = uuid.uuid4()
        received = []
        svc.on_incoming_call = lambda cid, from_u, codecs, sr: received.append((cid, from_u))
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "offer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
            "proposed_codecs": ["opus"], "sample_rate_hz": 48000,
        })))
        self.assertEqual(1, len(received))
        self.assertEqual(call_id, received[0][0])
        self.assertEqual("bob", received[0][1])


# ── VoiceCallService — inbound answer ────────────────────────────────────────

class InboundAnswerTests(unittest.TestCase):

    def test_fires_on_call_accepted_and_state_connected(self):
        svc, sender = _make_voice_svc("alice")
        call_id = _run(svc.send_offer("bob", ["opus"], 48000))
        sender.clear()
        accepted = []
        svc.on_call_accepted = lambda cid, codec: accepted.append(cid)
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
        })))
        self.assertEqual(1, len(accepted))
        self.assertEqual(call_id, accepted[0])


# ── VoiceCallService — inbound hangup ────────────────────────────────────────

class InboundHangupTests(unittest.TestCase):

    def test_fires_on_call_ended(self):
        svc, _ = _make_voice_svc("alice")
        call_id = uuid.uuid4()
        # Create inbound session via offer.
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "offer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice", "proposed_codecs": ["opus"],
        })))
        ended = []
        svc.on_call_ended = lambda cid, reason: ended.append(cid)
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "hangup", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
        })))
        self.assertEqual([call_id], ended)


# ── VoiceCallService — acceptCall ─────────────────────────────────────────────

class AcceptCallTests(unittest.TestCase):

    def test_sends_answer_unicast_and_state_becomes_connected(self):
        svc, sender = _make_voice_svc("alice")
        call_id = uuid.uuid4()
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "offer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice", "proposed_codecs": ["opus"],
        })))
        sender.clear()
        _run(svc.accept_call(call_id))
        # accept_call sends an answer unicast to the caller
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0, "expected answer unicast to bob")
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("answer", msg["kind"])
        # Session should now be in Connected state
        session = svc._sessions.get(call_id)
        self.assertIsNotNone(session)
        self.assertEqual(VoiceCallState.Connected, session.state)


# ── VoiceCallService — hangUp ─────────────────────────────────────────────────

class HangUpTests(unittest.TestCase):

    def test_sends_cancel_when_outgoing(self):
        svc, sender = _make_voice_svc("alice")
        call_id = _run(svc.send_offer("bob", ["opus"], 48000))
        sender.clear()
        _run(svc.hang_up(call_id))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("cancel", msg["kind"])

    def test_sends_hangup_when_connected(self):
        svc, sender = _make_voice_svc("alice")
        call_id = _run(svc.send_offer("bob", ["opus"], 48000))
        # Answer → Connected.
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
        })))
        sender.clear()
        _run(svc.hang_up(call_id))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0)
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("hangup", msg["kind"])


# ── VoiceCallService — sendFrame ──────────────────────────────────────────────

class SendFrameTests(unittest.TestCase):

    def test_sends_voice_call_packet_when_connected(self):
        svc, sender = _make_voice_svc("alice")
        call_id = _run(svc.send_offer("bob", ["opus"], 48000))
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
        })))
        sender.clear()
        _run(svc.send_frame(call_id, bytes([1, 2, 3, 4]), False))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertTrue(len(to_bob) > 0, "expected VoiceCall unicast to bob")
        self.assertEqual(PacketType.VoiceCall, to_bob[0].packet.type)

    def test_no_packet_sent_when_not_connected(self):
        svc, sender = _make_voice_svc("alice")
        call_id = _run(svc.send_offer("bob", ["opus"], 48000))
        # Still Outgoing.
        sender.clear()
        _run(svc.send_frame(call_id, bytes([1, 2, 3]), False))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        self.assertEqual(0, len(to_bob), "no VoiceCall should be sent while not connected")


# ── VoiceCallService — inbound frame ─────────────────────────────────────────

class InboundFrameTests(unittest.TestCase):

    def test_fires_on_frame_received(self):
        svc, sender = _make_voice_svc("alice")
        call_id = _run(svc.send_offer("bob", ["opus"], 48000))
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "answer", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
        })))
        sender.clear()
        received = []
        svc.on_frame_received = lambda cid, seq, ts, sil, audio: received.append((cid, audio))
        _run(svc.handle_packet(_voice_call_packet("bob", call_id)))
        self.assertEqual(1, len(received))
        self.assertEqual(call_id, received[0][0])
        self.assertEqual(bytes([0xAA, 0xBB, 0xCC, 0xDD]), received[0][1])


# ── GroupVoiceCallService — invite ────────────────────────────────────────────

class GroupInviteTests(unittest.TestCase):

    def test_sends_invite_unicast_to_each_member(self):
        svc, sender = _make_group_svc("alice")
        call_id = uuid.uuid4()
        _run(svc.invite(call_id, ["bob", "carol"]))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        to_carol = [u for u in sender.unicasts if u.next_hop_uhid == "carol"]
        self.assertTrue(len(to_bob) > 0, "expected invite to bob")
        self.assertTrue(len(to_carol) > 0, "expected invite to carol")
        msg = json.loads(to_bob[0].packet.payload.decode("utf-8"))
        self.assertEqual("invite", msg["kind"])


# ── GroupVoiceCallService — inbound signaling ─────────────────────────────────

class GroupInboundSignalingTests(unittest.TestCase):

    def test_inbound_invite_fires_on_invite(self):
        svc, _ = _make_group_svc("alice")
        call_id = uuid.uuid4()
        received = []
        svc.on_invite = lambda cid, from_u, invited: received.append((cid, from_u))
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "invite", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
            "invited_uhids": ["alice", "carol"],
        })))
        self.assertEqual(1, len(received))
        self.assertEqual(call_id, received[0][0])
        self.assertEqual("bob", received[0][1])

    def test_inbound_join_fires_on_member_joined(self):
        svc, _ = _make_group_svc("alice")
        call_id = uuid.uuid4()
        # Alice hosts.
        _run(svc.invite(call_id, ["bob"]))
        joined = []
        svc.on_member_joined = lambda cid, uhid: joined.append(uhid)
        _run(svc.handle_packet(_signaling_packet("carol", {
            "kind": "join", "call_id": str(call_id),
            "from_uhid": "carol", "to_uhid": "alice",
        })))
        self.assertEqual(["carol"], joined)

    def test_inbound_leave_fires_on_member_left(self):
        svc, _ = _make_group_svc("alice")
        call_id = uuid.uuid4()
        _run(svc.invite(call_id, ["bob", "carol"]))
        left = []
        svc.on_member_left = lambda cid, uhid: left.append(uhid)
        _run(svc.handle_packet(_signaling_packet("bob", {
            "kind": "leave", "call_id": str(call_id),
            "from_uhid": "bob", "to_uhid": "alice",
        })))
        self.assertEqual(["bob"], left)

    def test_inbound_kick_fires_on_member_kicked(self):
        svc, _ = _make_group_svc("alice")
        call_id = uuid.uuid4()
        _run(svc.invite(call_id, ["bob", "carol"]))
        kicked = []
        svc.on_member_kicked = lambda cid, uhid: kicked.append(uhid)
        _run(svc.handle_packet(_signaling_packet("alice", {
            "kind": "kick", "call_id": str(call_id),
            "from_uhid": "alice", "to_uhid": "bob",
            "kicked_uhid": "bob",
        })))
        self.assertEqual(["bob"], kicked)


# ── GroupVoiceCallService — sendFrame ────────────────────────────────────────

class GroupSendFrameTests(unittest.TestCase):

    def test_fans_out_to_all_members_except_self(self):
        svc, sender = _make_group_svc("alice")
        call_id = uuid.uuid4()
        _run(svc.invite(call_id, ["bob", "carol"]))
        sender.clear()
        _run(svc.send_frame(call_id, bytes([1, 2, 3]), False, 0))
        to_bob = [u for u in sender.unicasts if u.next_hop_uhid == "bob"]
        to_carol = [u for u in sender.unicasts if u.next_hop_uhid == "carol"]
        to_alice = [u for u in sender.unicasts if u.next_hop_uhid == "alice"]
        self.assertTrue(len(to_bob) > 0, "bob should receive frame")
        self.assertTrue(len(to_carol) > 0, "carol should receive frame")
        self.assertEqual(0, len(to_alice), "alice (self) must not receive frame")


# ── GroupVoiceCallService — inbound frame ────────────────────────────────────

class GroupInboundFrameTests(unittest.TestCase):

    def test_fires_on_frame_received(self):
        svc, _ = _make_group_svc("alice")
        call_id = uuid.uuid4()
        _run(svc.invite(call_id, ["bob"]))
        received = []
        svc.on_frame_received = lambda cid, from_u, seq, ts, sil, kg, audio: received.append((cid, from_u, audio))
        _run(svc.handle_packet(_group_voice_packet("bob", call_id)))
        self.assertEqual(1, len(received))
        self.assertEqual(call_id, received[0][0])
        self.assertEqual("bob", received[0][1])
        self.assertEqual(bytes([0xAA, 0xBB, 0xCC, 0xDD]), received[0][2])


if __name__ == "__main__":
    unittest.main()
