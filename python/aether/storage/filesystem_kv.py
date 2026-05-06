"""Durable :class:`KeyValueStore` backed by one file per entry in a
configurable root directory.

Writes are atomic on the local file system: bytes go to a tempfile inside
the same directory and are then renamed over the target. Keys are sanitised
to a hex SHA-256 hash (with the original key recoverable from a sidecar
manifest) so arbitrary key strings — including paths, slashes, and Unicode —
round-trip safely on every host OS.

This is a simple reference impl, not a database: it doesn't compact, doesn't
transact across multiple keys, and has no encryption-at-rest. Hosts that
need any of those compose :class:`aether.storage.EncryptedKeyValueStore`
on top, or supply their own :class:`KeyValueStore` implementation.
"""

from __future__ import annotations

import hashlib
import os
import tempfile
from pathlib import Path
from typing import AsyncIterator, Optional

from aether.storage.kv import KeyValueStore


_ENTRY_SUFFIX = ".kv"
_KEY_MANIFEST_SUFFIX = ".key"


class FileSystemKeyValueStore(KeyValueStore):
    """File-per-key durable KV store.

    Args:
        root_directory: The directory the store writes into. Created if it
            does not exist.
        namespace: Optional sub-directory under ``root_directory``. Multiple
            stores can share a root with disjoint namespaces.
    """

    def __init__(self, root_directory: str, namespace: Optional[str] = None) -> None:
        if not root_directory:
            raise ValueError("root_directory cannot be empty")
        root = Path(root_directory) if not namespace else Path(root_directory) / namespace
        root.mkdir(parents=True, exist_ok=True)
        self._root: Path = root

    async def get(self, key: str) -> Optional[bytes]:
        if not key:
            raise ValueError("key cannot be empty")
        path = self._entry_path(key)
        if not path.exists():
            return None
        try:
            return path.read_bytes()
        except FileNotFoundError:
            return None

    async def put(self, key: str, value: bytes) -> None:
        if not key:
            raise ValueError("key cannot be empty")
        if value is None:
            raise ValueError("value cannot be None")

        entry = self._entry_path(key)
        # Atomic write: tempfile in the same directory + os.replace.
        # ``delete=False`` so we own the path after the context exits.
        fd, temp_path = tempfile.mkstemp(
            prefix=entry.name + ".",
            suffix=".tmp",
            dir=str(entry.parent),
        )
        try:
            with os.fdopen(fd, "wb") as f:
                f.write(value)
            os.replace(temp_path, entry)
        except BaseException:
            # Best-effort cleanup of the temp on exception.
            try:
                os.unlink(temp_path)
            except FileNotFoundError:
                pass
            raise

        # Write the key manifest sidecar (stores the original, un-hashed
        # key) on first write. Subsequent puts skip the I/O if it already
        # exists.
        manifest = self._manifest_path(key)
        if not manifest.exists():
            manifest.write_text(key, encoding="utf-8")

    async def remove(self, key: str) -> bool:
        if not key:
            raise ValueError("key cannot be empty")
        entry = self._entry_path(key)
        existed = entry.exists()
        if existed:
            try:
                entry.unlink()
            except FileNotFoundError:
                existed = False
            manifest = self._manifest_path(key)
            try:
                manifest.unlink()
            except FileNotFoundError:
                pass
        return existed

    async def contains(self, key: str) -> bool:
        if not key:
            raise ValueError("key cannot be empty")
        return self._entry_path(key).exists()

    async def list_keys(self, prefix: Optional[str] = None) -> AsyncIterator[str]:
        if not self._root.exists():
            return
        # Iterate manifests so we recover the original (un-hashed) key
        # strings. The "*.kv.key" glob avoids matching unrelated files.
        for manifest in self._root.glob(f"*{_ENTRY_SUFFIX}{_KEY_MANIFEST_SUFFIX}"):
            try:
                original = manifest.read_text(encoding="utf-8")
            except (FileNotFoundError, OSError):
                continue
            if prefix is None or original.startswith(prefix):
                yield original

    def _entry_path(self, key: str) -> Path:
        return self._root / (self._hash_key(key) + _ENTRY_SUFFIX)

    def _manifest_path(self, key: str) -> Path:
        return self._root / (self._hash_key(key) + _ENTRY_SUFFIX + _KEY_MANIFEST_SUFFIX)

    @staticmethod
    def _hash_key(key: str) -> str:
        # SHA-256 -> lowercase hex makes a filesystem-safe, fixed-length
        # filename for any input. Matches the C# ``HashKey`` helper exactly.
        return hashlib.sha256(key.encode("utf-8")).hexdigest()
