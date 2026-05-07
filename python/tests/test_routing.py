# SPDX-License-Identifier: MIT

"""Unit tests for the Aether routing service.

Mirror of tests/Aether.Core.Tests/RoutingServiceTests.cs and go/routing/service_test.go.
Run with: python -m unittest python/tests/test_routing.py
"""

from __future__ import annotations

import asyncio
import unittest
from datetime import datetime, timedelta
from uuid import uuid4

from aether import constants
from aether.models import RouteEntry
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.routing import (
    AcceptAllRouteReplyVerifier,
    InMemoryRouteStore,
    RoutingService,
)
from aether.routing.verifier import RouteReplyVerifier

from tests.fakes import FakeMeshSender


LOCAL = "local-uhid"


def _new_rreq(source: str, dest: str, ttl: int = constants.DEFAULT_TTL) -> MeshPacket:
    return MeshPacket(
        type=PacketType.RouteRequest,
        source_uhid=source,
        destination_uhid=dest,
        ttl=ttl,
    )


def _new_rrep(source: str, dest: str, ttl: int = constants.DEFAULT_TTL) -> MeshPacket:
    return MeshPacket(
        type=PacketType.RouteReply,
        source_uhid=source,
        destination_uhid=dest,
        ttl=ttl,
    )


def _new_svc(verifier: RouteReplyVerifier | None = None):
    sender = FakeMeshSender(LOCAL)
    store = InMemoryRouteStore()
    svc = RoutingService(sender, store, verifier or AcceptAllRouteReplyVerifier())
    return svc, sender, store


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


class RoutingServiceTests(unittest.TestCase):
    # ─── HandleRouteRequest ──────────────────────────────────

    def test_handle_rreq_drops_duplicate(self):
        svc, sender, _ = _new_svc()
        rreq = _new_rreq("alice", "bob")
        _run(svc.handle_route_request(rreq))
        sender.clear()
        _run(svc.handle_route_request(rreq))
        self.assertEqual([], sender.broadcasts)
        self.assertEqual([], sender.unicasts)

    def test_handle_rreq_ignores_self(self):
        svc, sender, store = _new_svc()
        rreq = _new_rreq(LOCAL, "bob")
        _run(svc.handle_route_request(rreq))
        self.assertEqual([], sender.broadcasts)
        self.assertEqual([], sender.unicasts)
        all_routes = _run(store.get_all())
        self.assertEqual([], all_routes)

    def test_handle_rreq_installs_reverse_route(self):
        svc, _, store = _new_svc()
        rreq = _new_rreq("alice", "bob")
        _run(svc.handle_route_request(rreq))
        route = _run(store.get("alice"))
        self.assertIsNotNone(route)
        self.assertEqual(route.next_hop_uhid, "alice")
        self.assertGreaterEqual(route.hop_count, 1)
        self.assertFalse(route.is_expired)

    def test_handle_rreq_as_destination_sends_rrep(self):
        svc, sender, _ = _new_svc()
        rreq = _new_rreq("alice", LOCAL)
        _run(svc.handle_route_request(rreq))
        self.assertEqual(1, len(sender.unicasts))
        rec = sender.unicasts[0]
        self.assertEqual(PacketType.RouteReply, rec.packet.type)
        self.assertEqual(LOCAL, rec.packet.source_uhid)
        self.assertEqual("alice", rec.packet.destination_uhid)
        self.assertEqual("alice", rec.next_hop_uhid)

    def test_handle_rreq_with_cached_route_replies_on_behalf(self):
        svc, sender, store = _new_svc()
        _run(store.save(RouteEntry(
            destination_uhid="carol",
            next_hop_uhid="carol",
            hop_count=1,
            expires_at=datetime.utcnow() + timedelta(minutes=5),
        )))
        _run(svc.find_route("carol"))  # populate cache
        sender.clear()

        rreq = _new_rreq("alice", "carol")
        _run(svc.handle_route_request(rreq))

        rrep = None
        for u in sender.unicasts:
            if u.packet.type == PacketType.RouteReply:
                rrep = u.packet
                break
        if rrep is None:
            for b in sender.broadcasts:
                if b.type == PacketType.RouteReply:
                    rrep = b
                    break
        self.assertIsNotNone(rrep, "expected an RREP to be emitted")
        self.assertEqual("carol", rrep.source_uhid)

    def test_handle_rreq_forwards_when_ttl_allows(self):
        svc, sender, _ = _new_svc()
        rreq = _new_rreq("alice", "carol", ttl=5)
        _run(svc.handle_route_request(rreq))
        self.assertEqual(1, len(sender.broadcasts))
        self.assertEqual(4, sender.broadcasts[0].ttl)

    def test_handle_rreq_drops_when_ttl_exhausted(self):
        svc, sender, _ = _new_svc()
        rreq = _new_rreq("alice", "carol", ttl=1)
        _run(svc.handle_route_request(rreq))
        self.assertEqual([], sender.broadcasts)
        self.assertEqual([], sender.unicasts)

    # ─── HandleRouteReply ────────────────────────────────────

    def test_handle_rrep_installs_forward_route(self):
        svc, _, store = _new_svc()
        rrep = _new_rrep("carol", LOCAL)
        _run(svc.handle_route_reply(rrep))
        route = _run(store.get("carol"))
        self.assertIsNotNone(route)
        self.assertEqual("carol", route.next_hop_uhid)

    def test_handle_rrep_rejects_when_verifier_fails(self):
        class Rejecting(RouteReplyVerifier):
            async def verify(self, route_reply):  # type: ignore[override]
                return False

        svc, _, store = _new_svc(verifier=Rejecting())
        rrep = _new_rrep("carol", LOCAL)
        _run(svc.handle_route_reply(rrep))
        self.assertIsNone(_run(store.get("carol")))

    def test_handle_rrep_forwards_toward_original_requester(self):
        svc, sender, store = _new_svc()
        _run(store.save(RouteEntry(
            destination_uhid="alice",
            next_hop_uhid="bob",
            hop_count=2,
            expires_at=datetime.utcnow() + timedelta(minutes=5),
        )))
        _run(svc.find_route("alice"))
        sender.clear()

        rrep = _new_rrep("carol", "alice", ttl=4)
        _run(svc.handle_route_reply(rrep))

        forwarded = next(
            (u for u in sender.unicasts
             if u.packet.type == PacketType.RouteReply and u.next_hop_uhid == "bob"),
            None,
        )
        self.assertIsNotNone(forwarded)
        self.assertEqual(3, forwarded.packet.ttl)

    # ─── FindRoute / Prune ────────────────────────────────────

    def test_find_route_returns_cached_without_broadcast(self):
        svc, sender, store = _new_svc()
        _run(store.save(RouteEntry(
            destination_uhid="bob",
            next_hop_uhid="bob",
            hop_count=1,
            expires_at=datetime.utcnow() + timedelta(minutes=5),
        )))
        route = _run(svc.find_route("bob"))
        self.assertIsNotNone(route)
        self.assertEqual("bob", route.next_hop_uhid)
        self.assertEqual([], sender.broadcasts)

    def test_find_route_returns_none_when_no_peers(self):
        svc, _, _ = _new_svc()
        self.assertIsNone(_run(svc.find_route("bob")))

    def test_prune_removes_expired_routes(self):
        svc, _, store = _new_svc()
        _run(store.save(RouteEntry(
            destination_uhid="stale",
            next_hop_uhid="stale",
            hop_count=1,
            expires_at=datetime.utcnow() - timedelta(seconds=10),
        )))
        _run(store.save(RouteEntry(
            destination_uhid="fresh",
            next_hop_uhid="fresh",
            hop_count=1,
            expires_at=datetime.utcnow() + timedelta(minutes=5),
        )))
        _run(svc.find_route("fresh"))
        _run(svc.prune())
        self.assertIsNone(_run(store.get("stale")))
        self.assertIsNotNone(_run(store.get("fresh")))


if __name__ == "__main__":
    unittest.main()
