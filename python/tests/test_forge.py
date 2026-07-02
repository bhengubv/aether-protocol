# SPDX-License-Identifier: MIT

"""Unit tests for the ForgeAnnounce (41) WIRE binding.

Mirrors the ForgeAnnounce cases in tests/AetherNet.Core.Tests/WirePacketsTests.cs. Byte-identity gate
against ../fixtures/forge/vectors.json + broadcast/handle behaviour. Uses the shared in-memory
FakeMeshSender — no transport needed.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from datetime import datetime
from pathlib import Path

from aethernet.forge import ForgeAnnouncePayload, ForgeAnnounceService
from aethernet.models import PeerInfo
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


LOCAL = "aether:alice:01"

_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _fixtures_dir() -> Path:
    # python/tests/test_forge.py → python/tests/.. → aether-protocol/fixtures
    return Path(__file__).resolve().parent.parent.parent / "fixtures"


def _load_vectors() -> list[dict]:
    with (_fixtures_dir() / "forge" / "vectors.json").open(encoding="utf-8") as fp:
        return json.load(fp)["vectors"]


def _new_svc(local: str = LOCAL, peers: int = 0):
    sender = FakeMeshSender(local)
    for i in range(peers):
        sender.add_peer(
            PeerInfo(uhid=f"aether:peer:{i:02d}", public_key=b"", last_seen=datetime.utcnow())
        )
    return ForgeAnnounceService(sender), sender


class ForgeAnnounceByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate (fixtures/forge/vectors.json) ─────────────

    def test_payload_serializes_to_canonical_bytes(self):
        for vec in _load_vectors():
            with self.subTest(name=vec["name"]):
                payload = ForgeAnnouncePayload(
                    package_id=vec["package_id"],
                    content_hash=vec["content_hash"],
                    size_bytes=vec["size_bytes"],
                    announced_at_ms=vec["announced_at_ms"],
                )
                self.assertEqual(vec["expected_json"], payload.to_json().decode("utf-8"))

    def test_basic_matches_literal_bytes(self):
        payload = ForgeAnnouncePayload(
            package_id="npm:react@18.2.0",
            content_hash="QmForgeHash456",
            size_bytes=294912,
            announced_at_ms=1_700_000_000_000,
        )
        self.assertEqual(
            '{"package_id":"npm:react@18.2.0","content_hash":"QmForgeHash456",'
            '"size_bytes":294912,"announced_at_ms":1700000000000}',
            payload.to_json().decode("utf-8"),
        )


class ForgeAnnounceServiceTests(unittest.TestCase):
    # ─── Broadcast + handle ──────────────────────────────────────────

    def test_broadcast_emits_announce_packet_and_handle_raises_event(self):
        svc, sender = _new_svc("aether:alice:01", peers=2)

        reached = _run(svc.broadcast("npm:react@18.2.0", "QmForgeHash456", 294912, 1_700_000_000_000))
        self.assertEqual(2, reached)
        self.assertEqual(1, len(sender.broadcasts))
        sent = sender.broadcasts[0]
        self.assertEqual(PacketType.ForgeAnnounce, sent.type)

        got = {}
        svc.on_announce_received = lambda p: got.setdefault("p", p)
        self.assertTrue(_run(svc.handle(sent)))
        self.assertIn("p", got)
        self.assertEqual("npm:react@18.2.0", got["p"].package_id)
        self.assertEqual(294912, got["p"].size_bytes)

    def test_handle_wrong_type_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(type=PacketType.Data, payload=b"")
        self.assertFalse(_run(svc.handle(pkt)))

    def test_handle_malformed_payload_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(type=PacketType.ForgeAnnounce, source_uhid="aether:bob:02", payload=b"not json")
        self.assertFalse(_run(svc.handle(pkt)))


if __name__ == "__main__":
    unittest.main()
