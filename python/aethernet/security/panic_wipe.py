# SPDX-License-Identifier: MIT

"""Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.

A duress PIN (or panic button) irreversibly destroys the node's key material, so
a seized device reveals nothing and looks like a fresh install. This module is
the protocol-level core -- deterministic and portable across every AetherNet SDK:

- ``duress_pin_hash`` / ``verify_duress_pin`` -- recognise the duress PIN
  (SHA-256, constant-time compare); the PIN itself is never stored.
- ``secure_erase`` -- best-effort in-memory erase of key material (overwrite with
  random, then zero).
- ``IDENTITY_KEY_NAMES`` + ``pre_key_name`` / ``signed_pre_key_name`` -- the
  canonical set of key-store entries a wipe must destroy.

Destroying the hosting app's local database, platform keychain entries and any
decoy store is the app's job -- it owns that storage. This module gives the app
the crypto trigger, the secure-erase primitive, and the manifest of what to
remove, so every app wipes the same identity material the same way.

The deterministic parts (``duress_pin_hash``, ``IDENTITY_KEY_NAMES``, the pre-key
name patterns, ``MAX_PRE_KEYS``) are byte-identical across every AetherNet SDK,
verified against fixtures/panicwipe/vectors.json. ``secure_erase`` (overwrite
random + zero) is behavioural and tested per language.

Port of ``src/AetherNet.Security/Privacy/PanicWipe.cs``.
"""

from __future__ import annotations

import hashlib
import hmac
import os

# Number of one-time / signed pre-key slots a wipe sweeps (0..N-1).
MAX_PRE_KEYS = 200

# The key-store entry names that together constitute an AetherNet identity --
# everything a panic-wipe must destroy, besides the numbered pre-keys.
IDENTITY_KEY_NAMES = (
    "aether_identity_pub",
    "aether_identity_priv",
    "aether_identity_generated",
    "aether_device_salt",
    "aether_drk",
    "aether_ble_rotation_key",
    "aether_ble_irk",
)


def pre_key_name(index: int) -> str:
    """Key-store name of the i-th one-time pre-key."""
    return f"prekey_{index}"


def signed_pre_key_name(index: int) -> str:
    """Key-store name of the i-th signed pre-key."""
    return f"signed_prekey_{index}"


def duress_pin_hash(pin: str) -> bytes:
    """The duress-PIN hash: SHA-256 of the UTF-8 PIN (32 bytes).

    Stored at setup and compared on unlock -- the PIN is only ever kept as this
    hash.
    """
    if pin is None:
        raise ValueError("pin cannot be None")
    return hashlib.sha256(pin.encode("utf-8")).digest()


def verify_duress_pin(pin: str, stored_hash: bytes) -> bool:
    """Constant-time check of whether ``pin`` matches a stored ``duress_pin_hash``
    -- i.e. whether unlocking should trigger a wipe."""
    if pin is None:
        raise ValueError("pin cannot be None")
    if stored_hash is None:
        raise ValueError("stored_hash cannot be None")
    if len(stored_hash) != 32:
        return False
    return hmac.compare_digest(duress_pin_hash(pin), stored_hash)


def secure_erase(buf: bytearray) -> None:
    """Best-effort secure erase of in-memory key material: overwrite with random
    bytes, then zero.

    Call on every buffer holding a secret before releasing it. Defence in depth
    -- the runtime or OS may still hold copies, but this removes the obvious one
    and leaves no plaintext secret in the buffer.
    """
    if buf is None:
        return
    n = len(buf)
    if n == 0:
        return
    buf[:] = os.urandom(n)
    for i in range(n):
        buf[i] = 0
