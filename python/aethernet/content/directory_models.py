# SPDX-License-Identifier: MIT

"""Wire payloads for IDirectoryService NamePublish / NameQuery.

Serialized as JSON with snake_case property names for cross-language byte
equality with the C# / Go / Kotlin / Swift / TypeScript ports.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional
from uuid import UUID, uuid4

from aethernet.content.content_descriptor import ContentDescriptor


@dataclass
class NamePublishPayload:
    """Wire payload for PacketType.NamePublish.

    Two modes:
        * Unsolicited broadcast: the publisher emits this on
          DirectoryService.publish. ``in_response_to_query_id`` is None.
        * Query response: a peer that holds the name emits this in unicast back
          to a querier. ``in_response_to_query_id`` carries the query's
          correlation id.
    """

    name: str = ""
    descriptor: ContentDescriptor = field(default_factory=ContentDescriptor)
    in_response_to_query_id: Optional[UUID] = None

    def to_wire_dict(self) -> dict:
        return {
            "name": self.name,
            "descriptor": self.descriptor.to_wire_dict(),
            "in_response_to_query_id": (
                str(self.in_response_to_query_id)
                if self.in_response_to_query_id is not None
                else None
            ),
        }

    @staticmethod
    def from_wire_dict(d: dict) -> "NamePublishPayload":
        raw_query = d.get("in_response_to_query_id")
        query_id: Optional[UUID] = None
        if isinstance(raw_query, str) and raw_query:
            try:
                query_id = UUID(raw_query)
            except ValueError:
                query_id = None
        descriptor_raw = d.get("descriptor") or {}
        return NamePublishPayload(
            name=str(d.get("name", "")),
            descriptor=ContentDescriptor.from_wire_dict(descriptor_raw),
            in_response_to_query_id=query_id,
        )


@dataclass
class NameQueryPayload:
    """Wire payload for PacketType.NameQuery.

    A broadcast request asking peers to send a NamePublishPayload for the
    named entry back to the sender, correlated by ``query_id``.
    """

    name: str = ""
    query_id: UUID = field(default_factory=uuid4)

    def to_wire_dict(self) -> dict:
        return {
            "name": self.name,
            "query_id": str(self.query_id),
        }

    @staticmethod
    def from_wire_dict(d: dict) -> "NameQueryPayload":
        raw_query = d.get("query_id")
        try:
            query_id = UUID(str(raw_query)) if raw_query else uuid4()
        except (ValueError, TypeError):
            query_id = uuid4()
        return NameQueryPayload(
            name=str(d.get("name", "")),
            query_id=query_id,
        )


@dataclass
class DirectoryEntryAnnouncedEvent:
    """Event payload for DirectoryService.on_entry_announced.

    Raised when a NamePublish packet arrives and the local catalogue learns a
    new (or replaced) name -> descriptor binding.
    """

    name: str
    descriptor: ContentDescriptor
    source_uhid: str
    announced_at_utc: datetime = field(default_factory=datetime.utcnow)
