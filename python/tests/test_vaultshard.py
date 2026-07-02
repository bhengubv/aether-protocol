# SPDX-License-Identifier: MIT

"""Unit tests for the VaultShardRequest (42) WIRE binding.

Mirrors the VaultShardRequest cases in tests/AetherNet.Core.Tests/WirePacketsTests.cs. Byte-identity gate
against ../fixtures/vaultshard/vectors.json + request/handle behaviour. Uses the shared in-memory
FakeMeshSender — no transport needed.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from datetime import datetime
from pathlib import Path

from aethernet.models import PeerInfo
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.vault import VaultShardRequest, VaultShardRequestService

from tests.fakes import FakeMeshSender


LOCAL = "aether:bob:02"

_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _fixtures_dir() -> Path:
    # python/tests/test_vaultshard.py → python/tests/.. → aether-protocol/fixtures
    return Path(__file__).resolve().parent.parent.parent / "fixtures"


def _load_vectors() -> list[dict]:
    with (_fixtures_dir() / "vaultshard" / "vectors.json").open(encoding="utf-8") as fp:
        return json.load(fp)["vectors"]


def _new_svc(local: str = LOCAL, peers: int = 0):
    sender = FakeMeshSender(local)
    for i in range(peers):
        sender.add_peer(
            PeerInfo(uhid=f"aether:peer:{i:02d}", public_key=b"", last_seen=datetime.utcnow())
        )
    return VaultShardRequestService(sender), sender


class VaultShardRequestByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate (fixtures/vaultshard/vectors.json) ────────

    def test_payload_serializes_to_canonical_bytes(self):
        for vec in _load_vectors():
            with self.subTest(name=vec["name"]):
                req = VaultShardRequest(
                    shard_hash=vec["shard_hash"],
                    requester_uhid=vec["requester_uhid"],
                )
                self.assertEqual(vec["expected_json"], req.to_json().decode("utf-8"))

    def test_basic_matches_literal_bytes(self):
        req = VaultShardRequest(shard_hash="QmShardHash789", requester_uhid="aether:bob:02")
        self.assertEqual(
            '{"shard_hash":"QmShardHash789","requester_uhid":"aether:bob:02"}',
            req.to_json().decode("utf-8"),
        )


class VaultShardRequestServiceTests(unittest.TestCase):
    # ─── Request + handle ────────────────────────────────────────────

    def test_request_emits_shard_request_packet_and_handle_raises_event(self):
        svc, sender = _new_svc("aether:bob:02", peers=2)

        reached = _run(svc.request_shard("QmShardHash789"))
        self.assertEqual(2, reached)
        self.assertEqual(1, len(sender.broadcasts))
        sent = sender.broadcasts[0]
        self.assertEqual(PacketType.VaultShardRequest, sent.type)
        body = json.loads(sent.payload.decode("utf-8"))
        self.assertEqual("QmShardHash789", body["shard_hash"])
        self.assertEqual("aether:bob:02", body["requester_uhid"])

        got = {}
        svc.on_shard_requested = lambda r: got.setdefault("r", r)
        self.assertTrue(_run(svc.handle(sent)))
        self.assertIn("r", got)
        self.assertEqual("QmShardHash789", got["r"].shard_hash)
        self.assertEqual("aether:bob:02", got["r"].requester_uhid)

    def test_handle_wrong_type_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(type=PacketType.Data, payload=b"")
        self.assertFalse(_run(svc.handle(pkt)))

    def test_handle_malformed_payload_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(type=PacketType.VaultShardRequest, source_uhid="aether:alice:01", payload=b"not json")
        self.assertFalse(_run(svc.handle(pkt)))


if __name__ == "__main__":
    unittest.main()
