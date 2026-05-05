"""Unit tests for the Aether DTN service."""

from __future__ import annotations

import asyncio
import json
import unittest
from datetime import datetime, timedelta
from uuid import UUID, uuid4

from aether import constants
from aether.dtn import DtnService, InMemoryBundleStore
from aether.models import (
    BundlePriority,
    BundleStatus,
    DtnBundle,
    DtnDeliveryReceipt,
    NodeCapabilities,
    PeerInfo,
)
from aether.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender

LOCAL = "local"


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


def _new_svc():
    sender = FakeMeshSender(LOCAL)
    store = InMemoryBundleStore()
    svc = DtnService(sender, store)
    return svc, sender, store


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


class DtnServiceTests(unittest.TestCase):
    # ─── CreateBundle ──────────────────────────────────────

    def test_create_bundle_persists_and_attempts_delivery(self):
        svc, _, store = _new_svc()
        bundle = _run(svc.create_bundle("recipient", b"\x01\x02\x03"))
        self.assertIsNotNone(bundle)
        self.assertEqual("recipient", bundle.recipient_uhid)
        self.assertEqual(BundleStatus.PENDING, bundle.status)
        active = _run(store.get_active())
        self.assertEqual(1, len(active))

    def test_create_bundle_with_direct_peer_delivers_immediately(self):
        svc, sender, _ = _new_svc()
        sender.add_peer(PeerInfo(
            uhid="recipient",
            public_key=b"",
            last_seen=datetime.utcnow(),
            capabilities=int(NodeCapabilities.DTN_CARRIER),
        ))
        bundle = _run(svc.create_bundle("recipient", b"\x01\x02\x03"))
        self.assertEqual(BundleStatus.DELIVERED, bundle.status)
        hit = any(u.next_hop_uhid == "recipient" and u.packet.type == PacketType.DtnBundle
                  for u in sender.unicasts)
        self.assertTrue(hit)

    # ─── HandleAsync — DtnBundle ──────────────────────────

    def test_handle_as_recipient_marks_delivered_and_sends_receipt(self):
        svc, sender, store = _new_svc()
        bundle = DtnBundle(
            sender_uhid="alice",
            recipient_uhid=LOCAL,
            encrypted_payload=b"\x09",
        )
        pkt = _build_bundle_packet("alice", bundle)
        _run(svc.handle(pkt))
        stored = _run(store.get(bundle.id))
        self.assertIsNotNone(stored)
        self.assertEqual(BundleStatus.DELIVERED, stored.status)
        hit = any(u.packet.type == PacketType.DtnDeliveryReceipt and u.next_hop_uhid == "alice"
                  for u in sender.unicasts)
        self.assertTrue(hit, "expected delivery receipt to alice")

    def test_handle_not_recipient_with_capacity_accepts_custody(self):
        svc, sender, store = _new_svc()
        bundle = DtnBundle(
            sender_uhid="alice",
            recipient_uhid="bob",
            encrypted_payload=b"\x01",
        )
        pkt = _build_bundle_packet("alice", bundle)
        _run(svc.handle(pkt))
        stored = _run(store.get(bundle.id))
        self.assertEqual(BundleStatus.IN_CUSTODY, stored.status)
        self.assertEqual(1, stored.hop_count)
        hit = any(u.packet.type == PacketType.DtnCustodyAck and u.next_hop_uhid == "alice"
                  for u in sender.unicasts)
        self.assertTrue(hit, "expected custody-ack to alice")

    def test_handle_at_capacity_refuses_custody(self):
        svc, sender, store = _new_svc()
        for _ in range(constants.DTN_MAX_BUNDLES_PER_NODE):
            _run(store.save(DtnBundle(
                sender_uhid="x",
                recipient_uhid="y",
                encrypted_payload=b"",
                status=BundleStatus.IN_CUSTODY,
                expires_at=datetime.utcnow() + timedelta(hours=1),
            )))
        sender.clear()

        bundle = DtnBundle(
            sender_uhid="alice",
            recipient_uhid="bob",
            encrypted_payload=b"",
            expires_at=datetime.utcnow() + timedelta(hours=1),
        )
        pkt = _build_bundle_packet("alice", bundle)
        _run(svc.handle(pkt))

        ack = next(
            (u for u in sender.unicasts if u.packet.type == PacketType.DtnCustodyAck),
            None,
        )
        self.assertIsNotNone(ack)
        body = json.loads(ack.packet.payload.decode("utf-8"))
        self.assertFalse(body["accepted"])

    # ─── DtnCustodyAck ─────────────────────────────────────

    def test_handle_positive_custody_ack_increments_copy_count(self):
        svc, _, store = _new_svc()
        bundle = _run(svc.create_bundle("recipient", b"\x01"))
        initial = bundle.copy_count

        body = json.dumps({"bundle_id": str(bundle.id), "accepted": True}).encode("utf-8")
        pkt = MeshPacket(
            type=PacketType.DtnCustodyAck,
            source_uhid="carrier",
            destination_uhid=LOCAL,
            payload=body,
        )
        _run(svc.handle(pkt))
        stored = _run(store.get(bundle.id))
        self.assertEqual(initial + 1, stored.copy_count)

    def test_handle_negative_custody_ack_does_not_increment(self):
        svc, _, store = _new_svc()
        bundle = _run(svc.create_bundle("recipient", b"\x01"))
        initial = bundle.copy_count

        body = json.dumps({"bundle_id": str(bundle.id), "accepted": False}).encode("utf-8")
        pkt = MeshPacket(
            type=PacketType.DtnCustodyAck,
            source_uhid="carrier",
            destination_uhid=LOCAL,
            payload=body,
        )
        _run(svc.handle(pkt))
        stored = _run(store.get(bundle.id))
        self.assertEqual(initial, stored.copy_count)

    # ─── DtnDeliveryReceipt ────────────────────────────────

    def test_handle_delivery_receipt_marks_delivered_and_fires_callback(self):
        svc, _, store = _new_svc()
        bundle = _run(svc.create_bundle("recipient", b"\x01"))

        observed = {}
        svc.on_bundle_delivered = lambda r: observed.setdefault("r", r)

        receipt_payload = {
            "bundle_id": str(bundle.id),
            "recipient_uhid": "recipient",
            "total_hops": 3,
            "total_custody_transfers": 2,
            "delivered_at_ms": int(datetime.utcnow().timestamp() * 1000),
        }
        pkt = MeshPacket(
            type=PacketType.DtnDeliveryReceipt,
            source_uhid="recipient",
            destination_uhid=LOCAL,
            payload=json.dumps(receipt_payload).encode("utf-8"),
        )
        _run(svc.handle(pkt))
        stored = _run(store.get(bundle.id))
        self.assertEqual(BundleStatus.DELIVERED, stored.status)
        self.assertIn("r", observed)
        self.assertEqual(3, observed["r"].total_hops)

    # ─── ExpireStale ───────────────────────────────────────

    def test_expire_stale_flips_status_for_expired_bundles(self):
        svc, _, store = _new_svc()
        expired = DtnBundle(
            sender_uhid="a",
            recipient_uhid="b",
            encrypted_payload=b"",
            status=BundleStatus.PENDING,
            expires_at=datetime.utcnow() - timedelta(minutes=1),
        )
        fresh = DtnBundle(
            sender_uhid="a",
            recipient_uhid="b",
            encrypted_payload=b"",
            status=BundleStatus.PENDING,
            expires_at=datetime.utcnow() + timedelta(hours=1),
        )
        _run(store.save(expired))
        _run(store.save(fresh))

        n = _run(svc.expire_stale())
        self.assertEqual(1, n)
        fresh_after = _run(store.get(fresh.id))
        self.assertEqual(BundleStatus.PENDING, fresh_after.status)


if __name__ == "__main__":
    unittest.main()
