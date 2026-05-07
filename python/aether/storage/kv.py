# SPDX-License-Identifier: MIT

"""Generic byte-array-keyed-by-string persistence primitive used as the
foundation for every Aether store that needs to survive a process restart.

Implementations are responsible for atomicity and durability guarantees;
the protocol layer just reads and writes opaque bytes. Two reference
implementations ship with this package:

* :class:`aether.storage.InMemoryKeyValueStore` — volatile, process-local.
* :class:`aether.storage.FileSystemKeyValueStore` — one file per key, atomic
  via temp file + ``os.rename``.

Hosts that need richer guarantees (transactions, encrypted-at-rest,
network-attached) supply their own implementation by subclassing
:class:`KeyValueStore`.

Async surface mirrors the C# ``IKeyValueStore`` contract so cross-language
adapters use the same call shapes.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import AsyncIterator, Optional


class KeyValueStore(ABC):
    """Abstract async key-value store.

    All values are opaque bytes — serialisation is the caller's concern.
    Keys are arbitrary strings; implementations that map keys onto a
    backing medium (such as :class:`FileSystemKeyValueStore`) are responsible
    for handling characters that are unsafe for that medium.
    """

    @abstractmethod
    async def get(self, key: str) -> Optional[bytes]:
        """Return the bytes stored under ``key``, or ``None`` if absent."""

    @abstractmethod
    async def put(self, key: str, value: bytes) -> None:
        """Insert or replace the bytes stored under ``key``."""

    @abstractmethod
    async def remove(self, key: str) -> bool:
        """Remove the entry under ``key``. Return ``True`` if a value was removed."""

    @abstractmethod
    async def contains(self, key: str) -> bool:
        """Return ``True`` if a value exists under ``key``."""

    @abstractmethod
    def list_keys(self, prefix: Optional[str] = None) -> AsyncIterator[str]:
        """Asynchronously enumerate every key currently in the store.

        If ``prefix`` is given, only keys that start with ``prefix`` are
        yielded. The order is implementation-defined.

        Note: this is a synchronous method that returns an async iterator —
        ``async for k in store.list_keys(): ...`` is the intended call shape.
        """
