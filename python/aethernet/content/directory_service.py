# SPDX-License-Identifier: MIT

"""Application-layer name -> ContentDescriptor resolver.

Closes the Wave-16 protocol gap: the content service is content-addressed
(``root_hash``-keyed) - consumers that want to fetch content by an
application-layer name (e.g. ``"podcast:abc123"``, ``"reel:hash"``,
``"album:artist/title"``) cannot do so via the content service alone because
they do not know the ``root_hash`` upfront. That's precisely what they're
trying to discover.

This service maintains a local name catalogue, broadcasts a
:data:`~aethernet.protocol.mesh_packet.PacketType.NamePublish` when the local
node publishes a binding, emits
:data:`~aethernet.protocol.mesh_packet.PacketType.NameQuery` when the local
node needs to resolve an unknown name, and unicasts a
:data:`~aethernet.protocol.mesh_packet.PacketType.NamePublish` response when a
peer's query matches an entry we hold.

Added in v1.2.0. Mirrors C#'s ``AetherNet.Content.IDirectoryService`` and the
Go / Kotlin / Swift / TypeScript ports.
"""

from __future__ import annotations

import asyncio
import json
import logging
from datetime import datetime
from typing import Callable, Dict, List, Optional
from uuid import UUID, uuid4

from aethernet import constants
from aethernet.content.content_descriptor import ContentDescriptor
from aethernet.content.directory_models import (
    DirectoryEntryAnnouncedEvent,
    NamePublishPayload,
    NameQueryPayload,
)
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)

DEFAULT_QUERY_TIMEOUT_SECONDS = 5.0


class DirectoryService:
    """In-process catalogue with broadcast publish + query/response correlation.

    Persistence is the host's responsibility (rehydrate via :meth:`publish` on
    startup if you want a non-volatile catalogue).
    """

    def __init__(self, sender: MeshSender) -> None:
        if sender is None:
            raise ValueError("sender must not be None")
        self._sender = sender
        self._catalogue: Dict[str, ContentDescriptor] = {}
        self._pending_queries: Dict[UUID, "asyncio.Future[Optional[ContentDescriptor]]"] = {}

        # Callable event hook - matches the on_bundle_delivered idiom used by
        # DtnService. Default None means "no subscriber". Setting to a callable
        # makes it fire on every NamePublish that updates the local catalogue.
        self.on_entry_announced: Optional[Callable[[DirectoryEntryAnnouncedEvent], None]] = None

    # ---- publish / resolve ----

    async def publish(self, name: str, descriptor: ContentDescriptor) -> None:
        """Store the binding locally and broadcast a NamePublish to peers."""
        if not name:
            raise ValueError("name must not be empty")
        if descriptor is None:
            raise ValueError("descriptor must not be None")

        self._catalogue[name] = descriptor

        payload = NamePublishPayload(
            name=name,
            descriptor=descriptor,
            in_response_to_query_id=None,
        )
        body = json.dumps(payload.to_wire_dict()).encode("utf-8")
        packet = MeshPacket(
            type=PacketType.NamePublish,
            source_uhid=self._sender.local_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )
        delivered = await self._sender.broadcast(packet)
        _LOG.debug(
            "directory: published name %s to %s peers (root=%s)",
            name,
            delivered,
            descriptor.root_hash,
        )

    async def resolve(
        self,
        name: str,
        timeout: float = DEFAULT_QUERY_TIMEOUT_SECONDS,
    ) -> Optional[ContentDescriptor]:
        """Resolve a name to its descriptor.

        Returns the local-catalogue hit immediately if present. Otherwise
        broadcasts a NameQuery and awaits a matching NamePublish response up to
        ``timeout`` seconds. Returns ``None`` on timeout.
        """
        if not name:
            raise ValueError("name must not be empty")

        cached = self._catalogue.get(name)
        if cached is not None:
            return cached

        loop = asyncio.get_event_loop()
        query = NameQueryPayload(name=name, query_id=uuid4())
        future: "asyncio.Future[Optional[ContentDescriptor]]" = loop.create_future()
        self._pending_queries[query.query_id] = future

        try:
            body = json.dumps(query.to_wire_dict()).encode("utf-8")
            packet = MeshPacket(
                type=PacketType.NameQuery,
                source_uhid=self._sender.local_uhid,
                ttl=constants.DEFAULT_TTL,
                payload=body,
            )
            await self._sender.broadcast(packet)

            try:
                return await asyncio.wait_for(future, timeout=timeout)
            except asyncio.TimeoutError:
                return None
        finally:
            self._pending_queries.pop(query.query_id, None)

    async def list_names(self) -> List[str]:
        """Snapshot of every name currently in the local catalogue."""
        return list(self._catalogue.keys())

    # ---- inbound packet pump ----

    async def handle(self, packet: MeshPacket) -> None:
        """Pump inbound NamePublish / NameQuery packets into the service."""
        if packet is None:
            raise ValueError("packet must not be None")
        if packet.type == PacketType.NamePublish:
            self._handle_publish(packet)
        elif packet.type == PacketType.NameQuery:
            await self._handle_query(packet)
        # Other packet types: silently ignore - matches C# behaviour.

    def _handle_publish(self, packet: MeshPacket) -> None:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            _LOG.warning("directory: failed to deserialize NamePublish packet %s: %s", packet.id, exc)
            return
        if not isinstance(data, dict):
            return
        payload = NamePublishPayload.from_wire_dict(data)
        if not payload.name:
            return

        self._catalogue[payload.name] = payload.descriptor

        # Query-response correlation: if the payload references a pending
        # query, complete it with the freshly-learned descriptor.
        if payload.in_response_to_query_id is not None:
            future = self._pending_queries.pop(payload.in_response_to_query_id, None)
            if future is not None and not future.done():
                future.set_result(payload.descriptor)

        if self.on_entry_announced is not None:
            self.on_entry_announced(DirectoryEntryAnnouncedEvent(
                name=payload.name,
                descriptor=payload.descriptor,
                source_uhid=packet.source_uhid,
                announced_at_utc=datetime.utcnow(),
            ))

    async def _handle_query(self, packet: MeshPacket) -> None:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            _LOG.warning("directory: failed to deserialize NameQuery packet %s: %s", packet.id, exc)
            return
        if not isinstance(data, dict):
            return
        query = NameQueryPayload.from_wire_dict(data)
        if not query.name:
            return

        descriptor = self._catalogue.get(query.name)
        if descriptor is None:
            # We don't hold this name - silently ignore. Other peers may answer.
            return

        response = NamePublishPayload(
            name=query.name,
            descriptor=descriptor,
            in_response_to_query_id=query.query_id,
        )
        body = json.dumps(response.to_wire_dict()).encode("utf-8")
        response_packet = MeshPacket(
            type=PacketType.NamePublish,
            source_uhid=self._sender.local_uhid,
            destination_uhid=packet.source_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )
        await self._sender.send(response_packet, packet.source_uhid)
        _LOG.debug("directory: answered query for %s from %s", query.name, packet.source_uhid)
