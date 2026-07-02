# SPDX-License-Identifier: MIT

"""Unit tests for the Aether channel-message service (PacketType.ChannelMessage).

Mirrors tests/AetherNet.Core.Tests/ChannelMessageTests.cs. Uses the shared in-memory
FakeMeshSender — no transport needed.
"""

from __future__ import annotations

import asyncio
import json
import unittest
from uuid import UUID, uuid4

from aethernet.channels import (
    ChannelMessagePayload,
    ChannelMessageReceived,
    ChannelMessageService,
)
from aethernet.channels.service import _encode_channel_message_payload
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


LOCAL = "aether:local:01"


_LOOP = asyncio.new_event_loop()


def _run(coro):
    asyncio.set_event_loop(_LOOP)
    return _LOOP.run_until_complete(coro)


def _new_svc(local: str = LOCAL):
    sender = FakeMeshSender(local)
    return ChannelMessageService(sender), sender


def _channel_packet(
    channel_id: str,
    message_id: UUID,
    sender: str,
    content: str,
    sent_at_ms: int,
    ttl: int = 7,
) -> MeshPacket:
    return MeshPacket(
        type=PacketType.ChannelMessage,
        source_uhid=sender,
        destination_uhid="*",
        ttl=ttl,
        payload=_encode_channel_message_payload(
            channel_id, message_id, sender, content, sent_at_ms
        ),
    )


class ChannelMessagePayloadByteIdentityTests(unittest.TestCase):
    # ─── Byte-identity gate ──────────────────────────────

    def test_payload_serializes_to_canonical_bytes(self):
        vectors = [
            (
                "res-floor-3",
                UUID("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
                "aether:alice:01",
                "meeting at 6",
                1_700_000_000_000,
                b'{"channel_id":"res-floor-3","message_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","sender_uhid":"aether:alice:01","content":"meeting at 6","sent_at_ms":1700000000000}',
            ),
            (
                "g",
                UUID("00000000-0000-0000-0000-000000000000"),
                "n",
                "",
                0,
                b'{"channel_id":"g","message_id":"00000000-0000-0000-0000-000000000000","sender_uhid":"n","content":"","sent_at_ms":0}',
            ),
        ]
        for channel_id, message_id, sender, content, ms, expected in vectors:
            with self.subTest(channel_id=channel_id):
                self.assertEqual(
                    expected,
                    _encode_channel_message_payload(
                        channel_id, message_id, sender, content, ms
                    ),
                )


class ChannelMessageServiceTests(unittest.TestCase):
    # ─── Publish ─────────────────────────────────────────

    def test_publish_broadcasts_channel_message(self):
        svc, sender = _new_svc("aether:alice:01")

        _run(svc.publish("res-floor-3", "meeting at 6"))

        self.assertEqual(1, len(sender.broadcasts))
        pkt = sender.broadcasts[0]
        self.assertEqual(PacketType.ChannelMessage, pkt.type)
        body = json.loads(pkt.payload.decode("utf-8"))
        self.assertEqual("res-floor-3", body["channel_id"])
        self.assertEqual("meeting at 6", body["content"])
        self.assertEqual("aether:alice:01", body["sender_uhid"])

    # ─── Handle ──────────────────────────────────────────

    def test_handle_subscribed_channel_raises_event(self):
        svc, _ = _new_svc(LOCAL)
        svc.subscribe("res-floor-3")

        got = {}
        svc.on_message_received = lambda e: got.setdefault("e", e)

        ok = _run(
            svc.handle(
                _channel_packet(
                    "res-floor-3", uuid4(), "aether:bob:02", "hello floor", 1_700_000_000_000
                )
            )
        )

        self.assertTrue(ok)
        self.assertIn("e", got)
        self.assertEqual("res-floor-3", got["e"].channel_id)
        self.assertEqual("hello floor", got["e"].content)
        self.assertEqual("aether:bob:02", got["e"].sender_uhid)

    def test_handle_unsubscribed_channel_no_event_but_processed(self):
        svc, _ = _new_svc(LOCAL)
        raised = {"v": False}
        svc.on_message_received = lambda e: raised.__setitem__("v", True)

        ok = _run(
            svc.handle(_channel_packet("society-x", uuid4(), "aether:bob:02", "hi", 1))
        )

        self.assertTrue(ok)  # processed + relayed
        self.assertFalse(raised["v"])  # but not surfaced — we aren't subscribed

    def test_handle_duplicate_message_id_returns_false(self):
        svc, _ = _new_svc(LOCAL)
        svc.subscribe("res-floor-3")
        mid = uuid4()

        events = {"n": 0}
        svc.on_message_received = lambda e: events.__setitem__("n", events["n"] + 1)

        self.assertTrue(
            _run(svc.handle(_channel_packet("res-floor-3", mid, "aether:bob:02", "one", 1)))
        )
        self.assertFalse(
            _run(svc.handle(_channel_packet("res-floor-3", mid, "aether:bob:02", "one", 1)))
        )
        self.assertEqual(1, events["n"])

    def test_handle_wrong_packet_type_returns_false(self):
        svc, _ = _new_svc()
        pkt = _channel_packet("res-floor-3", uuid4(), "aether:bob:02", "x", 1)
        pkt.type = PacketType.Data
        self.assertFalse(_run(svc.handle(pkt)))

    def test_handle_malformed_payload_returns_false(self):
        svc, _ = _new_svc()
        pkt = MeshPacket(
            type=PacketType.ChannelMessage,
            source_uhid="aether:bob:02",
            destination_uhid="*",
            payload=b"not json",
        )
        self.assertFalse(_run(svc.handle(pkt)))

    def test_handle_relays_when_ttl_allows(self):
        # Not subscribed — pure relay.
        svc, relay_sender = _new_svc("aether:relay:09")
        _run(
            svc.handle(
                _channel_packet(
                    "res-floor-3", uuid4(), "aether:bob:02", "hop", 1, ttl=5
                )
            )
        )

        self.assertEqual(1, len(relay_sender.broadcasts))
        relayed = relay_sender.broadcasts[0]
        self.assertEqual(PacketType.ChannelMessage, relayed.type)
        self.assertEqual(4, relayed.ttl)

    # ─── Subscriptions ───────────────────────────────────

    def test_subscribe_unsubscribe_tracks_subscriptions(self):
        svc, _ = _new_svc()
        svc.subscribe("res-floor-3")
        svc.subscribe("society-x")
        self.assertCountEqual(["res-floor-3", "society-x"], svc.get_subscriptions())

        svc.unsubscribe("res-floor-3")
        self.assertCountEqual(["society-x"], svc.get_subscriptions())

    def test_handle_own_message_not_surfaced_or_relayed(self):
        # A subscribed node still ignores its OWN authored message flooding back.
        svc, sender = _new_svc(LOCAL)
        svc.subscribe("res-floor-3")
        raised = {"v": False}
        svc.on_message_received = lambda e: raised.__setitem__("v", True)

        ok = _run(
            svc.handle(_channel_packet("res-floor-3", uuid4(), LOCAL, "mine", 1, ttl=5))
        )

        self.assertTrue(ok)  # first sighting is processed (marked seen)
        self.assertFalse(raised["v"])  # own message never surfaced
        self.assertEqual([], sender.broadcasts)  # own message never re-flooded


if __name__ == "__main__":
    unittest.main()
