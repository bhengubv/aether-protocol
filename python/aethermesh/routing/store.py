# SPDX-License-Identifier: MIT

"""Route store abstraction + in-memory default."""

from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from datetime import datetime
from typing import List, Optional

from aethermesh.models import RouteEntry


class RouteStore(ABC):
    """Persistent backing store for the routing table. Default impl is in-memory;
    hosts substitute file- or SQLite-backed implementations for durability."""

    @abstractmethod
    async def get(self, destination_uhid: str) -> Optional[RouteEntry]:
        ...

    @abstractmethod
    async def get_all(self) -> List[RouteEntry]:
        ...

    @abstractmethod
    async def save(self, route: RouteEntry) -> None:
        ...

    @abstractmethod
    async def remove(self, destination_uhid: str) -> None:
        ...

    @abstractmethod
    async def prune_expired(self) -> int:
        ...


class InMemoryRouteStore(RouteStore):
    """Process-local route store. Loses everything on restart."""

    def __init__(self) -> None:
        self._routes: dict[str, RouteEntry] = {}
        self._lock = asyncio.Lock()

    async def get(self, destination_uhid: str) -> Optional[RouteEntry]:
        async with self._lock:
            return self._routes.get(destination_uhid)

    async def get_all(self) -> List[RouteEntry]:
        async with self._lock:
            return list(self._routes.values())

    async def save(self, route: RouteEntry) -> None:
        async with self._lock:
            self._routes[route.destination_uhid] = route

    async def remove(self, destination_uhid: str) -> None:
        async with self._lock:
            self._routes.pop(destination_uhid, None)

    async def prune_expired(self) -> int:
        async with self._lock:
            now = datetime.utcnow()
            expired = [k for k, r in self._routes.items() if r.expires_at <= now]
            for k in expired:
                del self._routes[k]
            return len(expired)
