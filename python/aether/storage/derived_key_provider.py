"""PBKDF2-derived :class:`DataAtRestKeyProvider`.

Derives a 32-byte AES-256 key from a passphrase and a salt using
PBKDF2-HMAC-SHA256. The derived key is cached for the lifetime of the
provider so the (relatively expensive) PBKDF2 computation runs exactly
once per passphrase/version pair.

Production iteration count: **600,000**. This matches the OWASP 2023
recommendation for PBKDF2-HMAC-SHA256 and is the default if no count is
supplied. Tests pass a smaller count to keep the suite fast — never
lower the default in production code.

The salt is required, must be at least 16 bytes, and MUST be unique to
this device (or this trust boundary). Reusing the same passphrase + salt
across devices would let an attacker who recovered the salt from one
device decrypt blobs from another — domain-separate by appending an
install-id, hardware-id, or randomly generated per-device value.
"""

from __future__ import annotations

from typing import Dict, Optional

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC

from aether.storage.key_provider import DataAtRestKeyProvider


_KEY_LENGTH = 32  # AES-256
_MINIMUM_SALT_LENGTH = 16
DEFAULT_ITERATIONS = 600_000  # OWASP 2023 PBKDF2-HMAC-SHA256 recommendation.


class DerivedDataAtRestKeyProvider(DataAtRestKeyProvider):
    """Provider that derives keys from a passphrase via PBKDF2-HMAC-SHA256.

    To rotate the key, call :meth:`with_rotation` to construct a new
    provider that adds a freshly-derived key under the new version while
    keeping every existing version available for decryption — values
    written under the old version keep decrypting during the rotation
    window, and ``current_version`` switches to the new version for new
    writes.
    """

    def __init__(
        self,
        passphrase: str,
        salt: bytes,
        iterations: int = DEFAULT_ITERATIONS,
    ) -> None:
        self._validate_inputs(passphrase, salt, iterations)
        self._iterations = iterations
        self._derived_keys: Dict[int, bytes] = {
            1: self._derive(passphrase, salt, iterations),
        }
        self._current_version = 1

    @classmethod
    def _from_existing(
        cls,
        derived_keys: Dict[int, bytes],
        current_version: int,
        iterations: int,
    ) -> "DerivedDataAtRestKeyProvider":
        instance = cls.__new__(cls)
        instance._derived_keys = derived_keys
        instance._current_version = current_version
        instance._iterations = iterations
        return instance

    @property
    def current_version(self) -> int:
        return self._current_version

    @property
    def iterations(self) -> int:
        """The PBKDF2 iteration count this provider was constructed with."""
        return self._iterations

    def get_key(self, version: int) -> Optional[bytes]:
        return self._derived_keys.get(version)

    def with_rotation(
        self,
        new_version: int,
        new_passphrase: str,
        new_salt: bytes,
        iterations: Optional[int] = None,
    ) -> "DerivedDataAtRestKeyProvider":
        """Add a freshly-derived key under ``new_version`` (which becomes
        the new :attr:`current_version`) while keeping every existing
        version available for decryption.
        """
        if new_version < 1 or new_version > 255:
            raise ValueError(
                "Key version must fit in a single byte (1..255)."
            )
        if new_version in self._derived_keys:
            raise ValueError(
                f"Version {new_version} already exists in this provider."
            )

        iters = iterations if iterations is not None else self._iterations
        self._validate_inputs(new_passphrase, new_salt, iters)

        next_keys = dict(self._derived_keys)
        next_keys[new_version] = self._derive(new_passphrase, new_salt, iters)
        return self._from_existing(next_keys, new_version, iters)

    @staticmethod
    def _validate_inputs(passphrase: str, salt: bytes, iterations: int) -> None:
        if not passphrase:
            raise ValueError("passphrase cannot be empty")
        if salt is None:
            raise ValueError("salt cannot be None")
        if len(salt) < _MINIMUM_SALT_LENGTH:
            raise ValueError(
                f"salt must be at least {_MINIMUM_SALT_LENGTH} bytes (got {len(salt)})."
            )
        if iterations < 1:
            raise ValueError(
                f"iterations must be positive (got {iterations})."
            )

    @staticmethod
    def _derive(passphrase: str, salt: bytes, iterations: int) -> bytes:
        # Defensive copy of salt before derivation so caller mutations
        # don't affect the cached key.
        salt_copy = bytes(salt)
        kdf = PBKDF2HMAC(
            algorithm=hashes.SHA256(),
            length=_KEY_LENGTH,
            salt=salt_copy,
            iterations=iterations,
        )
        return kdf.derive(passphrase.encode("utf-8"))
