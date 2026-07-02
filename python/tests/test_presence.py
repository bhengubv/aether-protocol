# SPDX-License-Identifier: MIT

"""Unit tests for PresenceBeacon (21) / PresenceQuery (22) — the PresenceService WIRE binding.

Mirrors the Presence cases in tests/AetherNet.Core.Tests/PresenceEridAnnounceTests.cs.
Byte-identity gate against ../fixtures/presence/vectors.json (both beacon vectors + the
query vector) plus broadcast/query/handle behaviour. Uses the shared in-memory
FakeMeshSender — no transport needed.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from datetime import datetime
from pathlib import Path
from uuid import UUID

from aethernet.models import PeerInfo
from aethernet.presence import (
    PresenceBeaconPayload,
    PresenceQueryPayload,
    PresenceService,
    encode_beacon_payload,
    encode_query_payload,
)
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


LOCAL = "aether:local:01"

_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _fixtures_dir() -> Path:
    # python/tests/test_presence.py → python/tests/.. → aether-protocol/fixtures
    return Path(__file__).resolve().parent.parent.parent / "fixtures"


def _load_vectors() -> dict:
    with (_fixtures_dir() / "presence" / "vectors.json").open(encoding="utf-8") as fp:
        return json.load(fp)


def _new_svc(local: str = LOCAL, peers: int = 0):
    sender = FakeMeshSender(local)
    for i in range(peers):
        sender.add_peer(
            PeerInfo(uhid=f"aether:peer:{i:02d}", public_key=b"", last_seen=datetime.utcnow())
        )
    return PresenceService(sender), sender


class PresenceByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate (fixtures/presence/vectors.json) ─────────────

    def test_beacons_serialize_to_canonical_bytes(self):
        for vec in _load_vectors()["beacon_vectors"]:
            with self.subTest(name=vec["name"]):
                beacon = PresenceBeaconPayload(
                    erid=vec["erid"],
                    geohash=vec["geohash"],
                    capabilities=vec["capabilities"],
                    status=vec["status"],
                    sent_at_ms=vec["sent_at_ms"],
                )
                got = encode_beacon_payload(beacon).decode("utf-8")
                self.assertEqual(vec["expected_json"], got)

    def test_query_serializes_to_canonical_bytes(self):
        for vec in _load_vectors()["query_vectors"]:
            with self.subTest(name=vec["name"]):
                query = PresenceQueryPayload(
                    query_id=UUID(vec["query_id"]),
                    geohash=vec["geohash"],
                )
                got = encode_query_payload(query).decode("utf-8")
                self.assertEqual(vec["expected_json"], got)

    def test_beacon_available_matches_literal_bytes(self):
        beacon = PresenceBeaconPayload(
            erid="3B38HPPFG9JXE37Q",
            geohash="u4pru",
            capabilities=73,
            status=1,
            sent_at_ms=1700000000000,
        )
        self.assertEqual(
            '{"erid":"3B38HPPFG9JXE37Q","geohash":"u4pru","capabilities":73,'
            '"status":1,"sent_at_ms":1700000000000}',
            encode_beacon_payload(beacon).decode("utf-8"),
        )

    def test_beacon_hidden_offline_matches_literal_bytes(self):
        beacon = PresenceBeaconPayload(
            erid="0Z5BD0HB1Q7W76MY",
            geohash="",
            capabilities=0,
            status=5,
            sent_at_ms=0,
        )
        self.assertEqual(
            '{"erid":"0Z5BD0HB1Q7W76MY","geohash":"","capabilities":0,'
            '"status":5,"sent_at_ms":0}',
            encode_beacon_payload(beacon).decode("utf-8"),
        )

    def test_query_matches_literal_bytes(self):
        query = PresenceQueryPayload(
            query_id=UUID("11112222-3333-4444-5555-666677778888"),
            geohash="u4pru",
        )
        self.assertEqual(
            '{"query_id":"11112222-3333-4444-5555-666677778888","geohash":"u4pru"}',
            encode_query_payload(query).decode("utf-8"),
        )


class PresenceServiceTests(unittest.TestCase):
    # ─── Broadcast + query + handle ──────────────────────────────────────

    def test_broadcast_beacon_emits_beacon_packet_and_handle_raises_event(self):
        svc, sender = _new_svc("aether:alice:01", peers=4)
        beacon = PresenceBeaconPayload(
            erid="3B38HPPFG9JXE37Q",
            geohash="u4pru",
            capabilities=73,
            status=1,
            sent_at_ms=1700000000000,
        )

        self.assertEqual(4, _run(svc.broadcast_beacon(beacon)))
        self.assertEqual(1, len(sender.broadcasts))
        sent = sender.broadcasts[0]
        self.assertEqual(PacketType.PresenceBeacon, sent.type)

        got = {}
        svc.on_beacon_received = lambda b, src: got.update(beacon=b, uhid=src)
        sent.source_uhid = "aether:alice:01"
        self.assertTrue(_run(svc.handle(sent)))
        self.assertIn("beacon", got)
        self.assertEqual("3B38HPPFG9JXE37Q", got["beacon"].erid)
        self.assertEqual("aether:alice:01", got["uhid"])

    def test_query_emits_query_packet_and_handle_raises_event(self):
        svc, sender = _new_svc("aether:bob:02")

        qid = _run(svc.query("u4pru"))
        self.assertIsInstance(qid, UUID)
        self.assertEqual(1, len(sender.broadcasts))
        sent = sender.broadcasts[0]
        self.assertEqual(PacketType.PresenceQuery, sent.type)
        body = json.loads(sent.payload.decode("utf-8"))
        self.assertEqual(str(qid), body["query_id"])
        self.assertEqual("u4pru", body["geohash"])

        got = {}
        svc.on_query_received = lambda q, src: got.update(query=q, uhid=src)
        self.assertTrue(_run(svc.handle(sent)))
        self.assertIn("query", got)
        self.assertEqual(qid, got["query"].query_id)

    def test_handle_wrong_type_returns_false(self):
        svc, _ = _new_svc()
        self.assertFalse(_run(svc.handle(MeshPacket(type=PacketType.Data, payload=b""))))

    def test_handle_beacon_with_empty_erid_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(
            type=PacketType.PresenceBeacon,
            source_uhid="aether:x:01",
            payload=encode_beacon_payload(PresenceBeaconPayload(erid="")),
        )
        self.assertFalse(_run(svc.handle(pkt)))


if __name__ == "__main__":
    unittest.main()
