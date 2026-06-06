# SPDX-License-Identifier: MIT

"""Static :class:`DataAtRestKeyProvider` backed by pre-derived 32-byte keys.

Useful for tests, demos, and deployments that derive their key material
out of band (e.g. from the OS keychain, a hardware enclave, or a remote
KMS) and just need to inject the resulting bytes into the wrapper.

The simplest construction takes a single 32-byte key and assigns it
version 1 — sufficient for hosts that never rotate. Hosts that rotate
pass the dictionary constructor with both the previous and current
versions so that values written under the old key keep decrypting
during the rotation window.
"""

from __future__ import annotations

from typing import Mapping, Optional

from aethernet.storage.key_provider import DataAtRestKeyProvider


_KEY_LENGTH = 32  # AES-256


class StaticDataAtRestKeyProvider(DataAtRestKeyProvider):
    """Provider backed by an explicit dict of version -> key bytes.

    Two construction shapes:

    * Single-key: pass one 32-byte ``key`` and the provider serves it as
      version 1.
    * Multi-version (rotation window): pass ``keys_by_version`` and
      ``current_version``. Both must be set together; ``current_version``
      must be a key in the dict.
    """

    def __init__(
        self,
        key: Optional[bytes] = None,
        *,
        keys_by_version: Optional[Mapping[int, bytes]] = None,
        current_version: Optional[int] = None,
    ) -> None:
        if key is not None and keys_by_version is not None:
            raise ValueError(
                "Pass either 'key' (single-version) or 'keys_by_version' (multi-version), not both."
            )

        if key is not None:
            self._keys = {1: self._validate_key(key)}
            self._current_version = 1
        elif keys_by_version is not None:
            if current_version is None:
                raise ValueError(
                    "current_version is required when keys_by_version is supplied."
                )
            if current_version < 1 or current_version > 255:
                raise ValueError(
                    "current_version must fit in a single byte (1..255)."
                )
            if current_version not in keys_by_version:
                raise ValueError(
                    f"keys_by_version does not contain an entry for current_version={current_version}."
                )
            validated = {}
            for version, value in keys_by_version.items():
                if version < 1 or version > 255:
                    raise ValueError(
                        f"Key version {version} is outside the supported [1, 255] range."
                    )
                validated[version] = self._validate_key(value)
            self._keys = validated
            self._current_version = current_version
        else:
            raise ValueError(
                "Must supply either 'key' or 'keys_by_version'."
            )

    @property
    def current_version(self) -> int:
        return self._current_version

    def get_key(self, version: int) -> Optional[bytes]:
        return self._keys.get(version)

    @staticmethod
    def _validate_key(key: bytes) -> bytes:
        if key is None:
            raise ValueError("Data-at-rest key cannot be None.")
        if len(key) != _KEY_LENGTH:
            raise ValueError(
                f"Data-at-rest key must be exactly {_KEY_LENGTH} bytes (AES-256); got {len(key)}."
            )
        # Defensive copy so caller mutations don't affect cached key bytes.
        return bytes(key)
