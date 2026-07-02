# SPDX-License-Identifier: MIT

"""Unit tests for the SpaceBreadcrumb (40) WIRE binding.

Mirrors the SpaceBreadcrumb cases in tests/AetherNet.Core.Tests/WirePacketsTests.cs. Byte-identity gate
against ../fixtures/space/vectors.json + broadcast/handle behaviour. Uses the shared in-memory
FakeMeshSender — no transport needed.
"""

from __future__ import annotations

import asyncio
import base64
import json
import unittest
from datetime import datetime, timezone
from pathlib import Path

from aethernet.models import PeerInfo
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.space import (
    BreadcrumbType,
    SpaceBreadcrumb,
    SpaceBreadcrumbService,
    encode_space_breadcrumb_payload,
)

from tests.fakes import FakeMeshSender


LOCAL = "aether:alice:01"

_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _fixtures_dir() -> Path:
    # python/tests/test_space.py → python/tests/.. → aether-protocol/fixtures
    return Path(__file__).resolve().parent.parent.parent / "fixtures"


def _load_vectors() -> list[dict]:
    with (_fixtures_dir() / "space" / "vectors.json").open(encoding="utf-8") as fp:
        return json.load(fp)["vectors"]


def _new_svc(local: str = LOCAL, peers: int = 0):
    sender = FakeMeshSender(local)
    for i in range(peers):
        sender.add_peer(
            PeerInfo(uhid=f"aether:peer:{i:02d}", public_key=b"", last_seen=datetime.utcnow())
        )
    return SpaceBreadcrumbService(sender), sender


class SpaceBreadcrumbByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate (fixtures/space/vectors.json) ─────────────

    def test_payload_serializes_to_canonical_bytes(self):
        for vec in _load_vectors():
            with self.subTest(name=vec["name"]):
                crumb = SpaceBreadcrumb(
                    content_hash=vec["content_hash"],
                    geo_hash=vec["geo_hash"],
                    anchor_uhid=vec["anchor_uhid"],
                    created_at_utc=datetime.fromtimestamp(
                        vec["created_at_ms"] / 1000, tz=timezone.utc
                    ),
                    ttl_hours=vec["ttl_hours"],
                    type=BreadcrumbType(vec["type"]),
                    signature=base64.b64decode(vec["signature"]) if vec["signature"] else b"",
                )
                got = encode_space_breadcrumb_payload(crumb).decode("utf-8")
                self.assertEqual(vec["expected_json"], got)

    def test_emergency_signed_matches_literal_bytes(self):
        crumb = SpaceBreadcrumb(
            content_hash="QmContentHashExample123",
            geo_hash="u4pruy",
            anchor_uhid="aether:alice:01",
            created_at_utc=datetime.fromtimestamp(1_700_000_000_000 / 1000, tz=timezone.utc),
            ttl_hours=720,
            type=BreadcrumbType.EMERGENCY,
            signature=bytes([0x99]) * 64,
        )
        self.assertEqual(
            '{"content_hash":"QmContentHashExample123","geo_hash":"u4pruy",'
            '"anchor_uhid":"aether:alice:01","created_at_ms":1700000000000,'
            '"ttl_hours":720,"type":1,'
            '"signature":"mZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmQ=="}',
            encode_space_breadcrumb_payload(crumb).decode("utf-8"),
        )

    def test_notice_unsigned_matches_literal_bytes(self):
        crumb = SpaceBreadcrumb(
            content_hash="QmNotice777",
            geo_hash="gcpvj0",
            anchor_uhid="aether:bob:02",
            created_at_utc=datetime.fromtimestamp(0, tz=timezone.utc),
            ttl_hours=72,
            type=BreadcrumbType.NOTICE,
            signature=b"",
        )
        self.assertEqual(
            '{"content_hash":"QmNotice777","geo_hash":"gcpvj0","anchor_uhid":"aether:bob:02",'
            '"created_at_ms":0,"ttl_hours":72,"type":0,"signature":""}',
            encode_space_breadcrumb_payload(crumb).decode("utf-8"),
        )


class SpaceBreadcrumbServiceTests(unittest.TestCase):
    # ─── Broadcast + handle ──────────────────────────────────────────

    def test_broadcast_emits_breadcrumb_packet_and_handle_raises_event(self):
        svc, sender = _new_svc("aether:alice:01", peers=2)

        crumb = SpaceBreadcrumb(
            content_hash="QmX",
            geo_hash="u4pruy",
            anchor_uhid="aether:alice:01",
            created_at_utc=datetime.fromtimestamp(1_700_000_000_000 / 1000, tz=timezone.utc),
            ttl_hours=720,
            type=BreadcrumbType.EMERGENCY,
            signature=bytes([0x99]) * 64,
        )
        reached = _run(svc.broadcast(crumb))
        self.assertEqual(2, reached)
        self.assertEqual(1, len(sender.broadcasts))
        sent = sender.broadcasts[0]
        self.assertEqual(PacketType.SpaceBreadcrumb, sent.type)

        got = {}
        svc.on_breadcrumb_received = lambda c: got.setdefault("c", c)
        ok = _run(svc.handle(sent))
        self.assertTrue(ok)
        self.assertIn("c", got)
        self.assertEqual("u4pruy", got["c"].geo_hash)
        self.assertEqual(BreadcrumbType.EMERGENCY, got["c"].type)
        self.assertEqual(720, got["c"].ttl_hours)
        self.assertEqual(64, len(got["c"].signature))

    def test_handle_wrong_type_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(type=PacketType.Data, payload=b"")
        self.assertFalse(_run(svc.handle(pkt)))

    def test_handle_malformed_payload_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(type=PacketType.SpaceBreadcrumb, source_uhid="aether:bob:02", payload=b"not json")
        self.assertFalse(_run(svc.handle(pkt)))


if __name__ == "__main__":
    unittest.main()
