# SPDX-License-Identifier: MIT

"""Unit tests for the DtnService.on_bundle_received event (v1.2.0, Issue #59)."""

from __future__ import annotations

import asyncio
import json
import unittest
from datetime import datetime

from aethernet.dtn import DtnService, DtnBundleReceivedEvent, InMemoryBundleStore
from aethernet.models import BundlePriority, BundleStatus, DtnBundle, NodeCapabilities, PeerInfo
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


def _build_bundle_packet(source: str, bundle: DtnBundle) -> MeshPacket:
    payload = {
        "id": str(bundle.id),
        "sender_uhid": bundle.sender_uhid,
        "recipient_uhid": bundle.recipient_uhid,
        "encrypted_payload": list(bundle.encrypted_payload),
        "priority": int(bundle.priority),
        "status": int(bundle.status),
        "copy_count": bundle.copy_count,
        "max_copies": bundle.max_copies,
        "sender_geohash": bundle.sender_geohash,
        "recipient_last_geohash": bundle.recipient_last_geohash,
        "hop_count": bundle.hop_count,
        "created_at_ms": int(bundle.created_at.timestamp() * 1000),
        "expires_at_ms": int(bundle.expires_at.timestamp() * 1000),
    }
    return MeshPacket(
        type=PacketType.DtnBundle,
        source_uhid=source,
        destination_uhid=bundle.recipient_uhid,
        payload=json.dumps(payload).encode("utf-8"),
    )


class DtnBundleReceivedTests(unittest.TestCase):

    def test_inbound_bundle_addressed_to_local_fires_on_bundle_received(self):
        sender = FakeMeshSender("recipient")
        svc = DtnService(sender, InMemoryBundleStore())

        captured: list[DtnBundleReceivedEvent] = []
        svc.on_bundle_received = captured.append

        bundle = DtnBundle(
            sender_uhid="remote-sender",
            recipient_uhid="recipient",  # matches local
            encrypted_payload=b"\x01\x02\x03\x04",
            priority=BundlePriority.HIGH,
            hop_count=2,
        )
        packet = _build_bundle_packet("carrier", bundle)
        _run(svc.handle(packet))

        self.assertEqual(1, len(captured))
        event = captured[0]
        self.assertEqual(bundle.id, event.bundle_id)
        self.assertEqual("remote-sender", event.sender_uhid)
        self.assertEqual("recipient", event.recipient_uhid)
        self.assertEqual(b"\x01\x02\x03\x04", event.encrypted_payload)
        self.assertEqual(BundlePriority.HIGH, event.priority)
        self.assertEqual(2, event.hop_count)
        self.assertIsInstance(event.received_at_utc, datetime)

    def test_inbound_bundle_for_other_node_does_not_fire_on_bundle_received(self):
        sender = FakeMeshSender("carrier")
        sender.add_peer(PeerInfo(
            uhid="peer-z",
            public_key=b"",
            last_seen=datetime.utcnow(),
            capabilities=int(NodeCapabilities.DTN_CARRIER),
        ))
        svc = DtnService(sender, InMemoryBundleStore())

        captured: list[DtnBundleReceivedEvent] = []
        svc.on_bundle_received = captured.append

        bundle = DtnBundle(
            sender_uhid="remote-sender",
            recipient_uhid="someone-else",  # NOT local
            encrypted_payload=b"\xff",
            priority=BundlePriority.NORMAL,
        )
        packet = _build_bundle_packet("remote-sender", bundle)
        _run(svc.handle(packet))

        self.assertEqual(0, len(captured),
                         "on_bundle_received must fire ONLY when local node is the final recipient")

    def test_on_bundle_received_unset_does_not_raise(self):
        """DtnService must not raise when no callback has been attached."""
        sender = FakeMeshSender("recipient")
        svc = DtnService(sender, InMemoryBundleStore())
        # on_bundle_received stays None

        bundle = DtnBundle(
            sender_uhid="alice",
            recipient_uhid="recipient",
            encrypted_payload=b"\x09",
        )
        _run(svc.handle(_build_bundle_packet("alice", bundle)))
        # Reaching here without exception is the assertion


if __name__ == "__main__":
    unittest.main()
