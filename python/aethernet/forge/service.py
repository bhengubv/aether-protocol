# SPDX-License-Identifier: MIT
"""aether-forge: a mesh-native package cache proxy (Phase-2 extension).

The first internet pull of a package is cached as Aether content; subsequent
pulls by anyone in the mesh are served locally at mesh speeds. Port of the C#
reference (AetherNet.Forge). Ecosystems: npm, pip, cargo, go, nuget, git.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Callable, Optional


def _now_utc() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class ForgeEntry:
    """Metadata record for one cached package artifact."""

    content_hash: str = ""
    package_id: str = ""  # "ecosystem:name@version", e.g. "npm:react@18.2.0"
    fetched_at_utc: datetime = field(default_factory=_now_utc)
    size_bytes: int = 0
    download_count: int = 0


@dataclass
class ForgeStats:
    """Aggregate statistics for the local Forge cache."""

    total_bytes_saved: int = 0
    total_peers_served: int = 0
    catalogue_size: int = 0
    top_packages: list[ForgeEntry] = field(default_factory=list)


class IForgeService(ABC):
    """The mesh-native package cache."""

    @abstractmethod
    async def query(self, package_id: str) -> Optional[ForgeEntry]: ...

    @abstractmethod
    async def cache(self, package_id: str, content_hash: str, size_bytes: int) -> ForgeEntry: ...

    @abstractmethod
    async def fetch(self, package_id: str) -> Optional[ForgeEntry]: ...

    @abstractmethod
    async def get_stats(self) -> ForgeStats: ...


class InMemoryForgeService(IForgeService):
    """In-memory IForgeService for testing / single-node use; state lost on restart."""

    def __init__(self) -> None:
        self._store: dict[str, ForgeEntry] = {}  # key = package_id
        self.on_new_entry_announced: Optional[Callable[[ForgeEntry], None]] = None

    async def query(self, package_id: str) -> Optional[ForgeEntry]:
        return self._store.get(package_id)

    async def cache(self, package_id: str, content_hash: str, size_bytes: int) -> ForgeEntry:
        existing = self._store.get(package_id)
        if existing is not None:
            return existing  # idempotent — first write wins
        entry = ForgeEntry(
            content_hash=content_hash,
            package_id=package_id,
            fetched_at_utc=_now_utc(),
            size_bytes=size_bytes,
            download_count=0,
        )
        self._store[package_id] = entry
        if self.on_new_entry_announced is not None:
            self.on_new_entry_announced(entry)
        return entry

    async def fetch(self, package_id: str) -> Optional[ForgeEntry]:
        entry = self._store.get(package_id)
        if entry is None:
            return None
        entry.download_count += 1
        return entry

    async def get_stats(self) -> ForgeStats:
        entries = list(self._store.values())
        total_bytes_saved = sum(e.download_count * e.size_bytes for e in entries)
        top_packages = sorted(entries, key=lambda e: e.download_count, reverse=True)[:10]
        return ForgeStats(
            total_bytes_saved=total_bytes_saved,
            total_peers_served=0,
            catalogue_size=len(entries),
            top_packages=top_packages,
        )
