# SPDX-License-Identifier: MIT

"""Master-key provider for :class:`aether.storage.EncryptedKeyValueStore`.

Two responsibilities:

* :attr:`DataAtRestKeyProvider.current_version` tells the wrapper which
  key version to stamp onto every newly written blob. Hosts increment
  this to roll the key.
* :meth:`DataAtRestKeyProvider.get_key` hands back the 32-byte AES-256
  key for a given version on read. During a key-rotation window, the
  provider keeps both the old and new key so previously written blobs
  continue to decrypt.

Hosts derive these bytes however they like — from a passphrase via
PBKDF2 (:class:`StaticDataAtRestKeyProvider`), from the OS keychain
(DPAPI / Keychain Services / Android Keystore), from a hardware enclave,
or from a remote KMS. The wrapper never sees the source.

All keys returned by :meth:`get_key` MUST be exactly 32 bytes (AES-256).
Implementations are responsible for keeping the key material in memory
only as long as needed; the wrapper does not pin or wipe.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Optional


class DataAtRestKeyProvider(ABC):
    """Abstract supplier of AES-256 master key(s) for encryption-at-rest.

    Mirrors C# ``IDataAtRestKeyProvider`` field-for-field — the on-disk
    blob format is byte-identical between languages, so a Python host
    can decrypt blobs written by a C# host (and vice versa) given the
    same key material and version registry.
    """

    @property
    @abstractmethod
    def current_version(self) -> int:
        """The key version stamped onto every blob written via this provider.

        Must be in the range [1, 255] so it fits in the single-byte version
        header of the encrypted blob format.
        """

    @abstractmethod
    def get_key(self, version: int) -> Optional[bytes]:
        """Return the 32-byte AES-256 key for the given ``version``.

        Returns ``None`` if the provider has no key for that version (the
        blob was written under a key that has since been retired). The
        wrapper treats a ``None`` result as "cannot decrypt — return None
        to caller".
        """
