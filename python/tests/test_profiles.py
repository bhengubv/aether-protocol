# SPDX-License-Identifier: MIT

"""Unit tests for the Aether profile service (PacketType.ProfileSync).

Mirrors tests/AetherNet.Core.Tests/ProfileSyncTests.cs. Directed exchange — the shared
in-memory FakeMeshSender captures the directed send.
"""

from __future__ import annotations

import asyncio
import json
import unittest

from aethernet.profiles import ProfileService, ProfileSyncPayload
from aethernet.profiles.service import _encode_profile_sync_payload
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


LOCAL = "aether:local:01"


_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _new_svc(local: str = LOCAL):
    sender = FakeMeshSender(local)
    return ProfileService(sender), sender


def _profile_packet(
    uhid: str, name: str, avatar: str, status: str, updated_at_ms: int
) -> MeshPacket:
    return MeshPacket(
        type=PacketType.ProfileSync,
        source_uhid=uhid,
        destination_uhid=LOCAL,
        payload=_encode_profile_sync_payload(
            ProfileSyncPayload(
                uhid=uhid,
                display_name=name,
                avatar_ref=avatar,
                status_message=status,
                updated_at_ms=updated_at_ms,
            )
        ),
    )


class ProfileSyncPayloadByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate ──────────────────────────────

    def test_payload_serializes_to_canonical_bytes(self):
        vectors = [
            (
                "aether:alice:01",
                "Alice",
                "blake3:abc",
                "available",
                1_700_000_000_000,
                b'{"uhid":"aether:alice:01","display_name":"Alice","avatar_ref":"blake3:abc","status_message":"available","updated_at_ms":1700000000000}',
            ),
            (
                "n",
                "",
                "",
                "",
                0,
                b'{"uhid":"n","display_name":"","avatar_ref":"","status_message":"","updated_at_ms":0}',
            ),
        ]
        for uhid, name, avatar, status, ms, expected in vectors:
            with self.subTest(uhid=uhid):
                self.assertEqual(
                    expected,
                    _encode_profile_sync_payload(
                        ProfileSyncPayload(
                            uhid=uhid,
                            display_name=name,
                            avatar_ref=avatar,
                            status_message=status,
                            updated_at_ms=ms,
                        )
                    ),
                )


class ProfileServiceTests(unittest.TestCase):
    # ─── PublishProfileTo ────────────────────────────────

    def test_publish_profile_to_sends_directed_profile_to_peer(self):
        svc, sender = _new_svc("aether:alice:01")
        svc.set_local_profile("Alice", "blake3:abc", "available")

        ok = _run(svc.publish_profile_to("aether:bob:02"))

        self.assertTrue(ok)
        self.assertEqual(1, len(sender.unicasts))
        sent = sender.unicasts[0]
        self.assertEqual(PacketType.ProfileSync, sent.packet.type)
        self.assertEqual("aether:bob:02", sent.next_hop_uhid)
        body = json.loads(sent.packet.payload.decode("utf-8"))
        self.assertEqual("aether:alice:01", body["uhid"])
        self.assertEqual("Alice", body["display_name"])

    # ─── Handle ──────────────────────────────────────────

    def test_handle_caches_peer_profile_and_raises_event(self):
        svc, _ = _new_svc(LOCAL)
        updated = {}
        svc.on_profile_updated = lambda p: updated.setdefault("p", p)

        ok = _run(
            svc.handle(
                _profile_packet(
                    "aether:bob:02", "Bob", "blake3:xyz", "busy", 1_700_000_000_000
                )
            )
        )

        self.assertTrue(ok)
        self.assertIn("p", updated)
        self.assertEqual("Bob", updated["p"].display_name)

        cached = svc.get_profile("aether:bob:02")
        self.assertIsNotNone(cached)
        self.assertEqual("busy", cached.status_message)
        self.assertEqual(1, len(svc.get_known_profiles()))

    def test_handle_refreshes_existing_profile(self):
        svc, _ = _new_svc()
        _run(svc.handle(_profile_packet("aether:bob:02", "Bob", "", "here", 1000)))
        _run(svc.handle(_profile_packet("aether:bob:02", "Bob", "", "away", 2000)))

        cached = svc.get_profile("aether:bob:02")
        self.assertEqual("away", cached.status_message)
        self.assertEqual(1, len(svc.get_known_profiles()))

    def test_handle_own_profile_is_ignored(self):
        svc, _ = _new_svc(LOCAL)
        ok = _run(svc.handle(_profile_packet(LOCAL, "Me", "", "", 1)))
        self.assertFalse(ok)
        self.assertEqual([], svc.get_known_profiles())

    def test_handle_wrong_packet_type_returns_false(self):
        svc, _ = _new_svc()
        pkt = _profile_packet("aether:bob:02", "Bob", "", "", 1)
        pkt.type = PacketType.Data
        self.assertFalse(_run(svc.handle(pkt)))

    def test_handle_malformed_payload_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(
            type=PacketType.ProfileSync,
            source_uhid="aether:bob:02",
            destination_uhid=LOCAL,
            payload=b"not json",
        )
        self.assertFalse(_run(svc.handle(pkt)))
        self.assertEqual([], svc.get_known_profiles())

    # ─── Local profile ───────────────────────────────────

    def test_set_local_profile_populates_fields_and_uhid(self):
        svc, _ = _new_svc("aether:alice:01")
        svc.set_local_profile("Alice", "blake3:abc", "available")

        local = svc.get_local_profile()
        self.assertEqual("aether:alice:01", local.uhid)
        self.assertEqual("Alice", local.display_name)
        self.assertEqual("blake3:abc", local.avatar_ref)
        self.assertEqual("available", local.status_message)
        self.assertGreater(local.updated_at_ms, 0)


if __name__ == "__main__":
    unittest.main()
