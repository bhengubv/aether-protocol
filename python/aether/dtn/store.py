# SPDX-License-Identifier: MIT

"""DTN bundle store abstraction + in-memory default."""

from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from typing import List, Optional
from uuid import UUID

from aether.models import BundleStatus, CustodyRecord, DtnBundle


class BundleStore(ABC):
    """Persistent backing store for DTN bundles + custody records."""

    @abstractmethod
    async def get(self, bundle_id: UUID) -> Optional[DtnBundle]: ...

    @abstractmethod
    async def get_active(self) -> List[DtnBundle]: ...

    @abstractmethod
    async def save(self, bundle: DtnBundle) -> None: ...

    @abstractmethod
    async def remove(self, bundle_id: UUID) -> None: ...

    @abstractmethod
    async def get_active_count(self) -> int: ...

    @abstractmethod
    async def save_custody(self, record: CustodyRecord) -> None: ...

    @abstractmethod
    async def get_custody_records(self, bundle_id: UUID) -> List[CustodyRecord]: ...

    @abstractmethod
    async def expire_stale(self) -> int: ...


class InMemoryBundleStore(BundleStore):
    """Process-local DTN store. Suitable for tests."""

    def __init__(self) -> None:
        self._bundles: dict[UUID, DtnBundle] = {}
        self._custody: dict[UUID, CustodyRecord] = {}
        self._lock = asyncio.Lock()

    async def get(self, bundle_id: UUID) -> Optional[DtnBundle]:
        async with self._lock:
            return self._bundles.get(bundle_id)

    async def get_active(self) -> List[DtnBundle]:
        async with self._lock:
            return [
                b
                for b in self._bundles.values()
                if not b.is_expired
                and b.status in (BundleStatus.PENDING, BundleStatus.IN_CUSTODY)
            ]

    async def save(self, bundle: DtnBundle) -> None:
        async with self._lock:
            self._bundles[bundle.id] = bundle

    async def remove(self, bundle_id: UUID) -> None:
        async with self._lock:
            self._bundles.pop(bundle_id, None)

    async def get_active_count(self) -> int:
        async with self._lock:
            return sum(
                1
                for b in self._bundles.values()
                if not b.is_expired
                and b.status in (BundleStatus.PENDING, BundleStatus.IN_CUSTODY)
            )

    async def save_custody(self, record: CustodyRecord) -> None:
        async with self._lock:
            self._custody[record.id] = record

    async def get_custody_records(self, bundle_id: UUID) -> List[CustodyRecord]:
        async with self._lock:
            return [r for r in self._custody.values() if r.bundle_id == bundle_id]

    async def expire_stale(self) -> int:
        async with self._lock:
            expired = 0
            for b in self._bundles.values():
                if b.is_expired and b.status != BundleStatus.EXPIRED:
                    b.status = BundleStatus.EXPIRED
                    expired += 1
            return expired
