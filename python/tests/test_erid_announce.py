# SPDX-License-Identifier: MIT

"""Unit tests for the EridAnnounce (56) mesh binding — the EridAnnounceService transport.

Mirrors the EridAnnounce cases in tests/AetherNet.Core.Tests/PresenceEridAnnounceTests.cs.
ERID-announce is opaque encrypted transport (no new fixture); its byte-identity is
re-pinned against the existing EridAnnouncementCodec vector (fixtures/erid/vectors.json,
``announcement_encode_hex`` derived from ``routing_key_hex``). Uses the shared in-memory
FakeMeshSender — no transport needed.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from pathlib import Path

from aethernet.identity import EridAnnounceService, erid_announcement_codec
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _erid_vectors() -> dict:
    # python/tests/test_erid_announce.py → python/tests/.. → aether-protocol/fixtures
    path = Path(__file__).resolve().parent.parent.parent / "fixtures" / "erid" / "vectors.json"
    with path.open(encoding="utf-8") as fp:
        return json.load(fp)


class EridAnnounceTransportTests(unittest.TestCase):
    # ─── Send + handle (directed transport) ──────────────────────────────

    def test_send_emits_directed_packet_and_handle_raises_event(self):
        sender = FakeMeshSender("aether:alice:01")
        svc = EridAnnounceService(sender)
        enc = bytes([1, 2, 3, 4, 5])  # opaque Signal-encrypted announcement

        self.assertTrue(_run(svc.send_announce("aether:bob:02", enc)))
        self.assertEqual(1, len(sender.unicasts))
        rec = sender.unicasts[0]
        self.assertEqual(PacketType.EridAnnounce, rec.packet.type)
        self.assertEqual("aether:bob:02", rec.next_hop_uhid)

        got = {}
        svc.on_announce_received = lambda blob, src: got.update(blob=blob, uhid=src)
        rec.packet.source_uhid = "aether:bob:02"
        self.assertTrue(_run(svc.handle(rec.packet)))
        self.assertIn("blob", got)
        self.assertEqual(enc, got["blob"])
        self.assertEqual("aether:bob:02", got["uhid"])

    def test_handle_wrong_type_or_empty_returns_false(self):
        svc = EridAnnounceService(FakeMeshSender("aether:local:01"))
        self.assertFalse(
            _run(svc.handle(MeshPacket(type=PacketType.Data, payload=bytes([1]))))
        )
        self.assertFalse(
            _run(svc.handle(MeshPacket(type=PacketType.EridAnnounce, payload=b"")))
        )


class EridAnnouncementCodecRepinTests(unittest.TestCase):
    # ─── Re-pin the shared frame byte-identity (fixtures/erid) ────────────

    def test_codec_matches_canonical_frame(self):
        vectors = _erid_vectors()
        routing_key = bytes.fromhex(vectors["routing_key_hex"])
        frame = erid_announcement_codec.encode(routing_key, 900, 16)
        self.assertEqual(vectors["announcement_encode_hex"], frame.hex())


if __name__ == "__main__":
    unittest.main()
