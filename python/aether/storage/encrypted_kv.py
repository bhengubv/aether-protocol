# SPDX-License-Identifier: MIT

"""Transparent encryption-at-rest wrapper for an arbitrary :class:`KeyValueStore`.

Encrypts every value on the way down and decrypts on the way up using
AES-256-GCM with a per-write random nonce. Keys are passed through
unchanged so list/range queries continue to work.

**Threat model.** Protects persisted bytes from an attacker who recovers
the underlying medium (stolen disk, recycled SD card, leaked backup)
without compromising the master-key material that the host hands to the
:class:`DataAtRestKeyProvider`. The wrapper does NOT hide write patterns,
key names, or value sizes. It does NOT defend against in-process memory
disclosure — values are plaintext while the application holds them.

**Wire format (per stored blob):** ::

   key_version (1 byte) || nonce (12 bytes) || ciphertext (N bytes) || tag (16 bytes)

The ``key_version`` byte names which key in the provider was used; the
wrapper looks it up on read, so hosts can run a rotation window with both
old and new keys loaded. Tampering with any byte fails GCM authentication
and the read returns ``None`` (treated as "not present" by callers).

**Composition:** existing adapters
(:class:`aether.security.session_store.KeyValueSignalSessionStore`,
:class:`aether.security.pre_key_store.KeyValuePreKeyStore`, etc.) consume
any :class:`KeyValueStore`, so wrapping is a one-line composition::

    inner = FileSystemKeyValueStore(root_dir)
    secure = EncryptedKeyValueStore(inner, key_provider)
    pre_keys = KeyValuePreKeyStore(secure)

The wire format is byte-identical to the C# reference
(``Aether.Storage.EncryptedKeyValueStore``) so a Python host can decrypt
blobs written by a C# host (and vice versa) given the same key material
and version registry.
"""

from __future__ import annotations

import logging
import os
from typing import AsyncIterator, Optional

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

from aether.storage.key_provider import DataAtRestKeyProvider
from aether.storage.kv import KeyValueStore


_LOGGER = logging.getLogger(__name__)


class EncryptedKeyValueStore(KeyValueStore):
    """AES-256-GCM-encrypted wrapper around any :class:`KeyValueStore`.

    Constructor arguments:

    * ``inner`` — the underlying KV store that holds encrypted bytes.
    * ``key_provider`` — supplies the master key(s) and current version.

    The wrapper never sees the source of the master key — the provider
    can derive it from a passphrase, fetch it from a hardware enclave,
    pull it from an OS keychain, etc.
    """

    KEY_SIZE = 32
    """AES-256 key length in bytes."""

    NONCE_SIZE = 12
    """AES-GCM nonce length in bytes."""

    TAG_SIZE = 16
    """AES-GCM authentication tag length in bytes."""

    VERSION_HEADER_SIZE = 1
    """Length of the version-byte header at the start of every blob."""

    MINIMUM_BLOB_SIZE = VERSION_HEADER_SIZE + NONCE_SIZE + TAG_SIZE
    """Minimum byte count for any well-formed encrypted blob."""

    def __init__(
        self,
        inner: KeyValueStore,
        key_provider: DataAtRestKeyProvider,
    ) -> None:
        if inner is None:
            raise ValueError("inner cannot be None")
        if key_provider is None:
            raise ValueError("key_provider cannot be None")
        self._inner: KeyValueStore = inner
        self._key_provider: DataAtRestKeyProvider = key_provider

    async def get(self, key: str) -> Optional[bytes]:
        if not key:
            raise ValueError("key cannot be empty")
        blob = await self._inner.get(key)
        if blob is None:
            return None

        if len(blob) < self.MINIMUM_BLOB_SIZE:
            _LOGGER.warning(
                "Encrypted blob under key=%r is smaller than the minimum %d bytes — treating as tampered/missing.",
                key, self.MINIMUM_BLOB_SIZE)
            return None

        version = blob[0]
        key_bytes = self._key_provider.get_key(version)
        if key_bytes is None:
            _LOGGER.warning(
                "No data-at-rest key registered for version=%d under key=%r — cannot decrypt.",
                version, key)
            return None

        nonce = blob[self.VERSION_HEADER_SIZE:self.VERSION_HEADER_SIZE + self.NONCE_SIZE]
        ciphertext_and_tag = blob[self.VERSION_HEADER_SIZE + self.NONCE_SIZE:]

        try:
            aes = AESGCM(key_bytes)
            return aes.decrypt(nonce, ciphertext_and_tag, None)
        except InvalidTag:
            # GCM authentication failed — wrong key or tampered blob.
            # Caller treats the value as absent (matches C# behaviour).
            _LOGGER.warning(
                "AES-GCM authentication failed reading key=%r (version=%d). "
                "Either the wrong key is configured or the blob has been tampered with.",
                key, version)
            return None

    async def put(self, key: str, value: bytes) -> None:
        if not key:
            raise ValueError("key cannot be empty")
        if value is None:
            raise ValueError("value cannot be None")

        version = self._key_provider.current_version
        if version < 1 or version > 255:
            raise RuntimeError(
                f"DataAtRestKeyProvider.current_version={version} is outside the supported [1, 255] range."
            )

        key_bytes = self._key_provider.get_key(version)
        if key_bytes is None:
            raise RuntimeError(
                f"DataAtRestKeyProvider returned None for its own current_version={version}."
            )
        if len(key_bytes) != self.KEY_SIZE:
            raise RuntimeError(
                f"DataAtRestKeyProvider returned a {len(key_bytes)}-byte key; AES-256 requires {self.KEY_SIZE} bytes."
            )

        nonce = os.urandom(self.NONCE_SIZE)
        aes = AESGCM(key_bytes)
        ciphertext_and_tag = aes.encrypt(nonce, value, None)

        # Wire layout: version (1) || nonce (12) || ciphertext || tag (16)
        # The cryptography lib already concatenates ciphertext + tag in
        # the AESGCM.encrypt return value.
        blob = bytes([version]) + nonce + ciphertext_and_tag
        await self._inner.put(key, blob)

    async def remove(self, key: str) -> bool:
        if not key:
            raise ValueError("key cannot be empty")
        return await self._inner.remove(key)

    async def contains(self, key: str) -> bool:
        if not key:
            raise ValueError("key cannot be empty")
        return await self._inner.contains(key)

    async def list_keys(self, prefix: Optional[str] = None) -> AsyncIterator[str]:
        async for k in self._inner.list_keys(prefix=prefix):
            yield k

    async def rewrap(self) -> int:
        """Re-encrypt every value in the underlying store under the
        provider's current key version.

        Use during a key-rotation window after the provider has been
        swapped out for one that holds both the old and new keys —
        values written under the old version stay readable, and after
        the rewrap completes every blob is on the new version so the
        host can retire the old key on the next deploy.

        Returns the number of values successfully rewrapped (skipping
        any that no key in the provider can decrypt).
        """
        rewrapped = 0
        keys = []
        async for k in self._inner.list_keys():
            keys.append(k)

        for k in keys:
            plaintext = await self.get(k)
            if plaintext is None:
                _LOGGER.warning(
                    "Skipping rewrap of key=%r — value could not be decrypted under any registered key version.",
                    k)
                continue
            await self.put(k, plaintext)
            rewrapped += 1
        return rewrapped
