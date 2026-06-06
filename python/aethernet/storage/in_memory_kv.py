# SPDX-License-Identifier: MIT

"""Process-local, volatile :class:`KeyValueStore` backed by a dict.

Suitable for tests and demos. Loses everything on process exit. All
mutations are guarded by a single :class:`asyncio.Lock`, so concurrent
writers see a consistent state even though Python's dict is already
thread-safe — the lock is required because we offer ``list_keys`` as an
async iterator and want a stable snapshot semantics rather than a
"keys may appear or disappear mid-iteration" race.
"""

from __future__ import annotations

import asyncio
from typing import AsyncIterator, Dict, Optional

from aethernet.storage.kv import KeyValueStore


class InMemoryKeyValueStore(KeyValueStore):
    """Volatile in-memory KV store.

    All values are stored as defensive copies so the caller cannot mutate
    the stored bytes after writing them. Likewise, every ``get`` returns a
    fresh copy so the caller cannot mutate the in-memory entry through a
    returned reference.
    """

    def __init__(self) -> None:
        self._entries: Dict[str, bytes] = {}
        self._lock: asyncio.Lock = asyncio.Lock()

    async def get(self, key: str) -> Optional[bytes]:
        if not key:
            raise ValueError("key cannot be empty")
        async with self._lock:
            v = self._entries.get(key)
            return bytes(v) if v is not None else None

    async def put(self, key: str, value: bytes) -> None:
        if not key:
            raise ValueError("key cannot be empty")
        if value is None:
            raise ValueError("value cannot be None")
        async with self._lock:
            self._entries[key] = bytes(value)

    async def remove(self, key: str) -> bool:
        if not key:
            raise ValueError("key cannot be empty")
        async with self._lock:
            if key in self._entries:
                del self._entries[key]
                return True
            return False

    async def contains(self, key: str) -> bool:
        if not key:
            raise ValueError("key cannot be empty")
        async with self._lock:
            return key in self._entries

    async def list_keys(self, prefix: Optional[str] = None) -> AsyncIterator[str]:
        # Snapshot the key set under the lock so the iteration is stable
        # even if writers concurrently mutate the dict.
        async with self._lock:
            keys = list(self._entries.keys())
        for k in keys:
            if prefix is None or k.startswith(prefix):
                yield k
