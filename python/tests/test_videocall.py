# SPDX-License-Identifier: MIT

"""Unit tests for the Aether video call-control service (PacketType.VideoCall).

Mirrors tests/AetherNet.Core.Tests/VideoCallControlTests.cs. Uses the shared in-memory
FakeMeshSender — no transport needed. Directed signalling, so sends land in
``sender.unicasts`` (not broadcasts).
"""

from __future__ import annotations

import asyncio
import json
import unittest
from uuid import UUID, uuid4

from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.videocall import (
    VideoCallControlPayload,
    VideoCallControlService,
    VideoCallStateChanged,
)
from aethernet.videocall.service import _encode_video_call_control_payload

from tests.fakes import FakeMeshSender


LOCAL = "aether:local:01"


_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _new_svc(local: str = LOCAL):
    sender = FakeMeshSender(local)
    return VideoCallControlService(sender), sender


def _control_packet(call_id: UUID, action: str, from_uhid: str, sent_at_ms: int = 1) -> MeshPacket:
    return MeshPacket(
        type=PacketType.VideoCall,
        source_uhid=from_uhid,
        destination_uhid=LOCAL,
        payload=_encode_video_call_control_payload(call_id, action, sent_at_ms),
    )


class VideoCallControlPayloadByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate (fixtures/videocall/vectors.json) ────────────

    def test_payload_serializes_to_canonical_bytes(self):
        vectors = [
            (
                UUID("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
                "ring",
                1_700_000_000_000,
                b'{"call_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","action":"ring","sent_at_ms":1700000000000}',
            ),
            (
                UUID("00000000-0000-0000-0000-000000000000"),
                "hangup",
                0,
                b'{"call_id":"00000000-0000-0000-0000-000000000000","action":"hangup","sent_at_ms":0}',
            ),
        ]
        for call_id, action, ms, expected in vectors:
            with self.subTest(action=action):
                self.assertEqual(
                    expected,
                    _encode_video_call_control_payload(call_id, action, ms),
                )


class VideoCallControlServiceTests(unittest.TestCase):
    # ─── Ring ────────────────────────────────────────────

    def test_ring_sends_directed_ring_to_peer_and_returns_call_id(self):
        svc, sender = _new_svc("aether:alice:01")

        call_id = _run(svc.ring("aether:bob:02"))

        self.assertNotEqual(UUID(int=0), call_id)
        self.assertEqual(1, len(sender.unicasts))
        sent = sender.unicasts[0]
        self.assertEqual(PacketType.VideoCall, sent.packet.type)
        self.assertEqual("aether:bob:02", sent.next_hop_uhid)
        body = json.loads(sent.packet.payload.decode("utf-8"))
        self.assertEqual("ring", body["action"])
        self.assertEqual(str(call_id), body["call_id"])

    def test_ring_empty_peer_raises(self):
        svc, _ = _new_svc()
        with self.assertRaises(ValueError):
            _run(svc.ring(""))

    # ─── Accept / Decline / Hangup ───────────────────────

    def test_respond_sends_directed_action_to_peer(self):
        for action in ("accept", "decline", "hangup"):
            with self.subTest(action=action):
                svc, sender = _new_svc()
                call_id = uuid4()

                if action == "accept":
                    ok = _run(svc.accept(call_id, "aether:bob:02"))
                elif action == "decline":
                    ok = _run(svc.decline(call_id, "aether:bob:02"))
                else:
                    ok = _run(svc.hangup(call_id, "aether:bob:02"))

                self.assertTrue(ok)
                self.assertEqual(1, len(sender.unicasts))
                sent = sender.unicasts[0]
                self.assertEqual("aether:bob:02", sent.next_hop_uhid)
                body = json.loads(sent.packet.payload.decode("utf-8"))
                self.assertEqual(action, body["action"])
                self.assertEqual(str(call_id), body["call_id"])

    # ─── Handle ──────────────────────────────────────────

    def test_handle_raises_call_state_changed(self):
        svc, _ = _new_svc(LOCAL)
        got = {}
        svc.on_call_state_changed = lambda e: got.setdefault("e", e)

        call_id = uuid4()
        ok = _run(svc.handle(_control_packet(call_id, "ring", "aether:bob:02")))

        self.assertTrue(ok)
        self.assertIn("e", got)
        self.assertEqual(call_id, got["e"].call_id)
        self.assertEqual("ring", got["e"].action)
        self.assertEqual("aether:bob:02", got["e"].from_uhid)

    def test_handle_wrong_packet_type_returns_false(self):
        svc, _ = _new_svc()
        pkt = _control_packet(uuid4(), "ring", "aether:bob:02")
        pkt.type = PacketType.Data
        self.assertFalse(_run(svc.handle(pkt)))

    def test_handle_malformed_payload_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(
            type=PacketType.VideoCall,
            source_uhid="aether:bob:02",
            destination_uhid=LOCAL,
            payload=b"not json",
        )
        self.assertFalse(_run(svc.handle(pkt)))

    def test_handle_missing_action_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(
            type=PacketType.VideoCall,
            source_uhid="aether:bob:02",
            destination_uhid=LOCAL,
            payload=json.dumps({"call_id": str(uuid4())}, separators=(",", ":")).encode("utf-8"),
        )
        self.assertFalse(_run(svc.handle(pkt)))


if __name__ == "__main__":
    unittest.main()
