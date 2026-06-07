# SPDX-License-Identifier: MIT

"""Unit tests for the DirectoryService (v1.2.0, Issue #60)."""

from __future__ import annotations

import asyncio
import json
import unittest
from uuid import uuid4

from aethernet.content import (
    ContentDescriptor,
    DirectoryEntryAnnouncedEvent,
    DirectoryService,
    NamePublishPayload,
    NameQueryPayload,
)
from datetime import datetime

from aethernet.models import PeerInfo
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

from tests.fakes import FakeMeshSender


def _run(coro):
    return asyncio.get_event_loop().run_until_complete(coro)


def _sample_descriptor(root_hash: str = "deadbeef") -> ContentDescriptor:
    return ContentDescriptor(
        root_hash=root_hash,
        name="ignored-publisher-hint",
        total_bytes=1024,
        chunk_size_bytes=256,
        chunk_count=4,
        chunk_hashes=["h0", "h1", "h2", "h3"],
        content_type="audio/flac",
    )


class DirectoryServicePublishTests(unittest.TestCase):
    # ─── publish ─────────────────────────────────────────────

    def test_publish_stores_locally_and_broadcasts_name_publish(self):
        sender = FakeMeshSender("publisher")
        sender.add_peer(PeerInfo(uhid="peer-1", public_key=b"", last_seen=datetime.utcnow()))
        sender.add_peer(PeerInfo(uhid="peer-2", public_key=b"", last_seen=datetime.utcnow()))
        dir_svc = DirectoryService(sender)

        _run(dir_svc.publish("podcast:abc", _sample_descriptor("root-abc")))

        hit = _run(dir_svc.resolve("podcast:abc"))
        self.assertIsNotNone(hit)
        self.assertEqual("root-abc", hit.root_hash)

        # Broadcast went out.
        self.assertEqual(1, len(sender.broadcasts))
        self.assertEqual(PacketType.NamePublish, sender.broadcasts[0].type)

        # Wire payload uses snake_case keys + cross-language-stable shape.
        wire = json.loads(sender.broadcasts[0].payload.decode("utf-8"))
        self.assertIn("name", wire)
        self.assertIn("descriptor", wire)
        self.assertIn("in_response_to_query_id", wire)
        self.assertIsNone(wire["in_response_to_query_id"])
        self.assertEqual("podcast:abc", wire["name"])
        self.assertEqual("root-abc", wire["descriptor"]["root_hash"])

    def test_publish_empty_name_raises(self):
        sender = FakeMeshSender("local")
        dir_svc = DirectoryService(sender)
        with self.assertRaises(ValueError):
            _run(dir_svc.publish("", _sample_descriptor()))


class DirectoryServiceResolveTests(unittest.TestCase):
    # ─── resolve ─────────────────────────────────────────────

    def test_resolve_local_catalogue_hit_returns_immediately_no_broadcast(self):
        sender = FakeMeshSender("local")
        sender.add_peer(PeerInfo(uhid="peer-1", public_key=b"", last_seen=datetime.utcnow()))
        dir_svc = DirectoryService(sender)

        _run(dir_svc.publish("track:xyz", _sample_descriptor("root-xyz")))
        sender.clear()

        hit = _run(dir_svc.resolve("track:xyz"))

        self.assertIsNotNone(hit)
        self.assertEqual("root-xyz", hit.root_hash)
        # No NameQuery broadcast - local hit.
        self.assertEqual(0, len(sender.broadcasts))

    def test_resolve_miss_and_timeout_returns_none(self):
        sender = FakeMeshSender("local")
        sender.add_peer(PeerInfo(uhid="peer-1", public_key=b"", last_seen=datetime.utcnow()))
        dir_svc = DirectoryService(sender)

        hit = _run(dir_svc.resolve("unknown-name", timeout=0.05))

        self.assertIsNone(hit)
        # A NameQuery WAS broadcast - we tried.
        self.assertEqual(1, len(sender.broadcasts))
        self.assertEqual(PacketType.NameQuery, sender.broadcasts[0].type)

    def test_resolve_waiting_completes_when_matching_name_publish_arrives(self):
        sender = FakeMeshSender("local")
        sender.add_peer(PeerInfo(uhid="peer-1", public_key=b"", last_seen=datetime.utcnow()))
        dir_svc = DirectoryService(sender)

        async def scenario():
            # Start the resolve in the background.
            resolve_task = asyncio.ensure_future(
                dir_svc.resolve("podcast:remote", timeout=2.0)
            )
            # Yield so the broadcast happens.
            await asyncio.sleep(0)
            await asyncio.sleep(0)

            assert len(sender.broadcasts) == 1
            assert sender.broadcasts[0].type == PacketType.NameQuery
            query = NameQueryPayload.from_wire_dict(
                json.loads(sender.broadcasts[0].payload.decode("utf-8"))
            )

            # Simulate a peer responding.
            descriptor = _sample_descriptor("remote-root")
            response_payload = NamePublishPayload(
                name="podcast:remote",
                descriptor=descriptor,
                in_response_to_query_id=query.query_id,
            )
            response_packet = MeshPacket(
                type=PacketType.NamePublish,
                source_uhid="peer-1",
                payload=json.dumps(response_payload.to_wire_dict()).encode("utf-8"),
            )
            await dir_svc.handle(response_packet)
            return await resolve_task

        result = _run(scenario())
        self.assertIsNotNone(result)
        self.assertEqual("remote-root", result.root_hash)


class DirectoryServiceHandleTests(unittest.TestCase):
    # ─── handle(NamePublish) ────────────────────────────────

    def test_handle_inbound_name_publish_populates_catalogue_and_fires_event(self):
        sender = FakeMeshSender("local")
        dir_svc = DirectoryService(sender)

        captured: list[DirectoryEntryAnnouncedEvent] = []
        dir_svc.on_entry_announced = captured.append

        descriptor = _sample_descriptor("from-peer")
        publish_payload = NamePublishPayload(
            name="reel:hello",
            descriptor=descriptor,
            in_response_to_query_id=None,
        )
        broadcast = MeshPacket(
            type=PacketType.NamePublish,
            source_uhid="peer-publisher",
            payload=json.dumps(publish_payload.to_wire_dict()).encode("utf-8"),
        )
        _run(dir_svc.handle(broadcast))

        # Catalogue now has the entry.
        hit = _run(dir_svc.resolve("reel:hello"))
        self.assertIsNotNone(hit)
        self.assertEqual("from-peer", hit.root_hash)

        # Event fired.
        self.assertEqual(1, len(captured))
        self.assertEqual("reel:hello", captured[0].name)
        self.assertEqual("peer-publisher", captured[0].source_uhid)
        self.assertEqual("from-peer", captured[0].descriptor.root_hash)

    # ─── handle(NameQuery) hit ──────────────────────────────

    def test_handle_query_with_matching_name_unicasts_name_publish_response(self):
        holder_sender = FakeMeshSender("holder")
        holder_sender.add_peer(PeerInfo(uhid="asker", public_key=b"", last_seen=datetime.utcnow()))
        holder = DirectoryService(holder_sender)

        _run(holder.publish("album:test", _sample_descriptor("album-root")))
        holder_sender.clear()

        query_id = uuid4()
        query_payload = NameQueryPayload(name="album:test", query_id=query_id)
        query_packet = MeshPacket(
            type=PacketType.NameQuery,
            source_uhid="asker",
            payload=json.dumps(query_payload.to_wire_dict()).encode("utf-8"),
        )

        _run(holder.handle(query_packet))

        # Holder unicasts back a NamePublish with in_response_to_query_id set.
        self.assertEqual(1, len(holder_sender.unicasts))
        record = holder_sender.unicasts[0]
        self.assertEqual("asker", record.next_hop_uhid)
        self.assertEqual(PacketType.NamePublish, record.packet.type)

        response_body = NamePublishPayload.from_wire_dict(
            json.loads(record.packet.payload.decode("utf-8"))
        )
        self.assertEqual("album:test", response_body.name)
        self.assertEqual("album-root", response_body.descriptor.root_hash)
        self.assertEqual(query_id, response_body.in_response_to_query_id)

    # ─── handle(NameQuery) miss ─────────────────────────────

    def test_handle_query_for_unknown_name_does_nothing(self):
        sender = FakeMeshSender("local")
        sender.add_peer(PeerInfo(uhid="asker", public_key=b"", last_seen=datetime.utcnow()))
        dir_svc = DirectoryService(sender)

        query_payload = NameQueryPayload(name="nothing-here", query_id=uuid4())
        query_packet = MeshPacket(
            type=PacketType.NameQuery,
            source_uhid="asker",
            payload=json.dumps(query_payload.to_wire_dict()).encode("utf-8"),
        )

        _run(dir_svc.handle(query_packet))

        self.assertEqual(0, len(sender.unicasts))
        self.assertEqual(0, len(sender.broadcasts))


class DirectoryServiceListNamesTests(unittest.TestCase):

    def test_list_names_returns_catalogue_snapshot(self):
        sender = FakeMeshSender("local")
        dir_svc = DirectoryService(sender)

        _run(dir_svc.publish("a", _sample_descriptor("hash-a")))
        _run(dir_svc.publish("b", _sample_descriptor("hash-b")))
        _run(dir_svc.publish("c", _sample_descriptor("hash-c")))

        names = _run(dir_svc.list_names())

        self.assertEqual(3, len(names))
        self.assertIn("a", names)
        self.assertIn("b", names)
        self.assertIn("c", names)


if __name__ == "__main__":
    unittest.main()
