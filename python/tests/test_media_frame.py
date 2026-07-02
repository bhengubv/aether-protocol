# SPDX-License-Identifier: MIT

"""Unit tests for the VoicePtt(15) + ScreenShare(32) media-frame bindings.

Mirrors tests/AetherNet.Core.Tests/MediaFrameTests.cs. Binary frames sharing the 29-byte
header (call_id BIG-ENDIAN, sequence/timestamp LITTLE-ENDIAN, flag). Byte-identity gates
(against the SHARED fixtures/media/vectors.json) + send/handle behaviour. Directed sends
land in ``sender.unicasts`` via the shared in-memory ``FakeMeshSender``.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from pathlib import Path
from uuid import UUID

from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.media import (
    MediaFrameCodec,
    ScreenShareFrame,
    ScreenShareFrameReceived,
    ScreenShareService,
    VoicePttFrame,
    VoicePttFrameReceived,
    VoicePttService,
)

from tests.fakes import FakeMeshSender


CALL_ID = UUID("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f")


_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _fixtures_dir() -> Path:
    here = Path(__file__).resolve()
    # python/tests/test_media_frame.py → python/tests/.. → aether-protocol/
    return here.parent.parent.parent / "fixtures"


def _load_media_vectors() -> dict:
    with (_fixtures_dir() / "media" / "vectors.json").open(encoding="utf-8") as fp:
        return json.load(fp)


class MediaFrameByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gates ─────────────────────────────────────────────

    def test_voice_ptt_frame_serializes_to_canonical_bytes(self):
        f = VoicePttFrame(
            call_id=CALL_ID,
            sequence=42,
            timestamp_ms=1_700_000_000_000,
            is_silence=False,
            encoded_payload=bytes([0xAA, 0xBB, 0xCC]),
        )
        self.assertEqual(
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2a0000000068e5cf8b01000000aabbcc",
            MediaFrameCodec.serialize_voice_ptt(f).hex(),
        )

    def test_voice_ptt_silence_empty_serializes_to_canonical_bytes(self):
        f = VoicePttFrame(
            call_id=CALL_ID,
            sequence=43,
            timestamp_ms=1_700_000_000_020,
            is_silence=True,
            encoded_payload=b"",
        )
        self.assertEqual(
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2b0000001468e5cf8b01000001",
            MediaFrameCodec.serialize_voice_ptt(f).hex(),
        )

    def test_screen_share_keyframe_serializes_to_canonical_bytes(self):
        f = ScreenShareFrame(
            call_id=CALL_ID,
            sequence=7,
            timestamp_ms=1_700_000_000_000,
            is_keyframe=True,
            encoded_payload=bytes([0x11, 0x22, 0x33, 0x44]),
        )
        self.assertEqual(
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f070000000068e5cf8b0100000111223344",
            MediaFrameCodec.serialize_screen_share(f).hex(),
        )

    def test_screen_share_delta_empty_serializes_to_canonical_bytes(self):
        f = ScreenShareFrame(
            call_id=UUID(int=0),
            sequence=0,
            timestamp_ms=0,
            is_keyframe=False,
            encoded_payload=b"",
        )
        self.assertEqual(
            "0000000000000000000000000000000000000000000000000000000000",
            MediaFrameCodec.serialize_screen_share(f).hex(),
        )

    def test_all_serializations_match_shared_fixture_vectors(self):
        vectors = _load_media_vectors()
        for v in vectors["voice_ptt_vectors"]:
            with self.subTest(kind="voice_ptt", name=v["name"]):
                f = VoicePttFrame(
                    call_id=UUID(v["call_id"]),
                    sequence=v["sequence"],
                    timestamp_ms=v["timestamp_ms"],
                    is_silence=v["is_silence"],
                    encoded_payload=bytes.fromhex(v["payload_hex"]),
                )
                self.assertEqual(
                    v["expected_hex"], MediaFrameCodec.serialize_voice_ptt(f).hex()
                )
        for v in vectors["screen_share_vectors"]:
            with self.subTest(kind="screen_share", name=v["name"]):
                f = ScreenShareFrame(
                    call_id=UUID(v["call_id"]),
                    sequence=v["sequence"],
                    timestamp_ms=v["timestamp_ms"],
                    is_keyframe=v["is_keyframe"],
                    encoded_payload=bytes.fromhex(v["payload_hex"]),
                )
                self.assertEqual(
                    v["expected_hex"], MediaFrameCodec.serialize_screen_share(f).hex()
                )

    # ─── Round-trips ─────────────────────────────────────────────────────

    def test_voice_ptt_round_trips(self):
        f = VoicePttFrame(
            call_id=CALL_ID,
            sequence=99,
            timestamp_ms=123_456_789,
            is_silence=True,
            encoded_payload=bytes([1, 2, 3, 4, 5]),
        )
        back = MediaFrameCodec.deserialize_voice_ptt(MediaFrameCodec.serialize_voice_ptt(f))
        self.assertEqual(CALL_ID, back.call_id)
        self.assertEqual(99, back.sequence)
        self.assertEqual(123_456_789, back.timestamp_ms)
        self.assertTrue(back.is_silence)
        self.assertEqual(f.encoded_payload, back.encoded_payload)

    def test_screen_share_round_trips_keyframe_and_call_id_big_endian(self):
        f = ScreenShareFrame(
            call_id=CALL_ID,
            sequence=5,
            timestamp_ms=999,
            is_keyframe=True,
            encoded_payload=bytes([0xFF]),
        )
        back = MediaFrameCodec.deserialize_screen_share(
            MediaFrameCodec.serialize_screen_share(f)
        )
        self.assertEqual(CALL_ID, back.call_id)
        self.assertTrue(back.is_keyframe)
        self.assertEqual(bytes([0xFF]), back.encoded_payload)


class MediaFrameBehaviourTests(unittest.TestCase):
    # ─── Behaviour ───────────────────────────────────────────────────────

    def test_voice_ptt_send_emits_directed_frame_and_handle_raises_event(self):
        sender = FakeMeshSender("aether:alice:01")
        svc = VoicePttService(sender)
        frame = VoicePttFrame(
            call_id=CALL_ID,
            sequence=42,
            timestamp_ms=1_700_000_000_000,
            encoded_payload=bytes([0xAA, 0xBB, 0xCC]),
        )

        self.assertTrue(_run(svc.send_frame("aether:bob:02", frame)))
        self.assertEqual(1, len(sender.unicasts))
        sent = sender.unicasts[0]
        self.assertEqual(PacketType.VoicePtt, sent.packet.type)
        self.assertEqual("aether:bob:02", sent.next_hop_uhid)

        got: dict = {}
        svc.on_frame_received = lambda e: got.setdefault("e", e)
        sent.packet.source_uhid = "aether:alice:01"
        self.assertTrue(_run(svc.handle(sent.packet)))
        evt: VoicePttFrameReceived = got["e"]
        self.assertIsNotNone(evt)
        self.assertEqual(42, evt.frame.sequence)
        self.assertEqual("aether:alice:01", evt.from_uhid)
        self.assertEqual(bytes([0xAA, 0xBB, 0xCC]), evt.frame.encoded_payload)

    def test_screen_share_send_emits_directed_frame_and_handle_raises_event(self):
        sender = FakeMeshSender("aether:alice:01")
        svc = ScreenShareService(sender)
        frame = ScreenShareFrame(
            call_id=CALL_ID,
            sequence=7,
            timestamp_ms=1_700_000_000_000,
            is_keyframe=True,
            encoded_payload=bytes([0x11, 0x22, 0x33, 0x44]),
        )

        self.assertTrue(_run(svc.send_frame("aether:bob:02", frame)))
        self.assertEqual(1, len(sender.unicasts))
        sent = sender.unicasts[0]
        self.assertEqual(PacketType.ScreenShare, sent.packet.type)

        got: dict = {}
        svc.on_frame_received = lambda e: got.setdefault("e", e)
        self.assertTrue(_run(svc.handle(sent.packet)))
        evt: ScreenShareFrameReceived = got["e"]
        self.assertIsNotNone(evt)
        self.assertTrue(evt.frame.is_keyframe)
        self.assertEqual(7, evt.frame.sequence)

    def test_handle_wrong_type_returns_false(self):
        vp = VoicePttService(FakeMeshSender("aether:local:01"))
        ss = ScreenShareService(FakeMeshSender("aether:local:01"))
        self.assertFalse(
            _run(vp.handle(MeshPacket(type=PacketType.Data, payload=bytes(40))))
        )
        self.assertFalse(
            _run(ss.handle(MeshPacket(type=PacketType.Data, payload=bytes(40))))
        )

    def test_handle_short_frame_returns_false(self):
        vp = VoicePttService(FakeMeshSender("aether:local:01"))
        self.assertFalse(
            _run(vp.handle(MeshPacket(type=PacketType.VoicePtt, payload=bytes(10))))
        )


if __name__ == "__main__":
    unittest.main()
