# SPDX-License-Identifier: MIT
"""aether-space: geo-pinned community noticeboards (Phase-2 extension).

Nodes drop breadcrumbs at geohash coordinates; passing devices auto-pull and
re-host them for other passersby — fully offline. Port of the C# reference
(AetherNet.Space). Wire format: JSON, transmitted as PacketType.SpaceBreadcrumb (40).
"""
from __future__ import annotations

import enum
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from typing import Callable, Optional

EMERGENCY_TTL_HOURS = 720
MIN_TTL_HOURS = 1
MAX_TTL_HOURS = 168


class BreadcrumbType(enum.IntEnum):
    """Category of a geo-pinned breadcrumb."""

    NOTICE = 0
    EMERGENCY = 1
    COMMERCE = 2
    EVENT = 3
    JOB_POSTING = 4


def _now_utc() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class SpaceBreadcrumb:
    """A geo-pinned digital notice dropped at a physical location."""

    content_hash: str = ""
    geo_hash: str = ""
    anchor_uhid: str = ""
    created_at_utc: datetime = field(default_factory=_now_utc)
    ttl_hours: int = 72
    type: BreadcrumbType = BreadcrumbType.NOTICE
    signature: bytes = b""

    @property
    def expires_at_utc(self) -> datetime:
        return self.created_at_utc + timedelta(hours=self.ttl_hours)

    @property
    def is_expired(self) -> bool:
        return _now_utc() >= self.expires_at_utc


def _clamp(value: int, lo: int, hi: int) -> int:
    return lo if value < lo else hi if value > hi else value


class ISpaceService(ABC):
    """The aether-space breadcrumb store."""

    @abstractmethod
    async def drop(
        self,
        geo_hash: str,
        content_hash: str,
        anchor_uhid: str,
        type: BreadcrumbType = BreadcrumbType.NOTICE,
        ttl_hours: int = 72,
    ) -> SpaceBreadcrumb: ...

    @abstractmethod
    async def scan(self, center_geo_hash: str, radius_cells: int = 1) -> list[SpaceBreadcrumb]: ...

    @abstractmethod
    async def pin(self, breadcrumb: SpaceBreadcrumb) -> None: ...

    @abstractmethod
    async def delete(self, breadcrumb: SpaceBreadcrumb, requestor_uhid: str) -> bool: ...

    @abstractmethod
    def prune_expired(self) -> int: ...


class InMemorySpaceService(ISpaceService):
    """In-memory ISpaceService for testing / single-node use; state lost on restart.

    Proximity matching uses a geohash-prefix heuristic.
    """

    def __init__(self) -> None:
        self._store: dict[str, SpaceBreadcrumb] = {}  # key = content_hash
        self.on_breadcrumb_received: Optional[Callable[[SpaceBreadcrumb], None]] = None
        self.on_breadcrumb_expired: Optional[Callable[[SpaceBreadcrumb], None]] = None

    async def drop(
        self,
        geo_hash: str,
        content_hash: str,
        anchor_uhid: str,
        type: BreadcrumbType = BreadcrumbType.NOTICE,
        ttl_hours: int = 72,
    ) -> SpaceBreadcrumb:
        effective_ttl = (
            EMERGENCY_TTL_HOURS
            if type == BreadcrumbType.EMERGENCY
            else _clamp(ttl_hours, MIN_TTL_HOURS, MAX_TTL_HOURS)
        )
        crumb = SpaceBreadcrumb(
            content_hash=content_hash,
            geo_hash=geo_hash,
            anchor_uhid=anchor_uhid,
            created_at_utc=_now_utc(),
            ttl_hours=effective_ttl,
            type=type,
        )
        self._store[content_hash] = crumb
        if self.on_breadcrumb_received is not None:
            self.on_breadcrumb_received(crumb)
        return crumb

    async def scan(self, center_geo_hash: str, radius_cells: int = 1) -> list[SpaceBreadcrumb]:
        # Prefix-based proximity: match the first (6 - radius_cells) chars.
        prefix_len = _clamp(6 - radius_cells, 1, 6)
        prefix = (
            center_geo_hash[:prefix_len] if len(center_geo_hash) >= prefix_len else center_geo_hash
        ).lower()
        return [
            c
            for c in self._store.values()
            if not c.is_expired and c.geo_hash.lower().startswith(prefix)
        ]

    async def pin(self, breadcrumb: SpaceBreadcrumb) -> None:
        self._store[breadcrumb.content_hash] = breadcrumb
        if self.on_breadcrumb_received is not None:
            self.on_breadcrumb_received(breadcrumb)

    async def delete(self, breadcrumb: SpaceBreadcrumb, requestor_uhid: str) -> bool:
        stored = self._store.get(breadcrumb.content_hash)
        if stored is None:
            return False
        if stored.anchor_uhid != requestor_uhid:
            return False  # creator-only delete
        del self._store[breadcrumb.content_hash]
        return True

    def prune_expired(self) -> int:
        expired = [c for c in self._store.values() if c.is_expired]
        for crumb in expired:
            del self._store[crumb.content_hash]
            if self.on_breadcrumb_expired is not None:
                self.on_breadcrumb_expired(crumb)
        return len(expired)
