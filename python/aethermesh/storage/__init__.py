# SPDX-License-Identifier: MIT

"""Storage layer for Aether: a generic key-value abstraction (:class:`KeyValueStore`)
plus the reference implementations that protocol-layer adapters compose on top of.

Two reference implementations ship with this package:

* :class:`InMemoryKeyValueStore` — volatile, process-local. Suitable for tests
  and demos. Loses everything on process exit.
* :class:`FileSystemKeyValueStore` — durable, file-per-key, atomic via
  tempfile + ``os.rename``. Suitable for hosts that need to survive a
  process restart.

The :class:`EncryptedKeyValueStore` wrapper turns any of the above into an
encrypted-at-rest store via AES-256-GCM with a per-write random nonce. Master
keys are supplied through an :class:`DataAtRestKeyProvider` — either a static
byte string (:class:`StaticDataAtRestKeyProvider`) or one derived from a
passphrase via PBKDF2-HMAC-SHA256 (:class:`DerivedDataAtRestKeyProvider`).

Mirrors the ``AetherMesh.Storage`` namespace in the C# reference implementation —
the on-disk wire format for encrypted blobs is byte-identical to allow a
Python host to read what a C# host wrote (and vice versa).
"""

from aethermesh.storage.kv import KeyValueStore
from aethermesh.storage.in_memory_kv import InMemoryKeyValueStore
from aethermesh.storage.filesystem_kv import FileSystemKeyValueStore
from aethermesh.storage.encrypted_kv import EncryptedKeyValueStore
from aethermesh.storage.key_provider import DataAtRestKeyProvider
from aethermesh.storage.static_key_provider import StaticDataAtRestKeyProvider
from aethermesh.storage.derived_key_provider import DerivedDataAtRestKeyProvider

__all__ = [
    "KeyValueStore",
    "InMemoryKeyValueStore",
    "FileSystemKeyValueStore",
    "EncryptedKeyValueStore",
    "DataAtRestKeyProvider",
    "StaticDataAtRestKeyProvider",
    "DerivedDataAtRestKeyProvider",
]
