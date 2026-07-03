# SPDX-License-Identifier: MIT

"""Bluetooth-LE tracking protection: a rotating Service UUID and IRK-based
Resolvable Private Addresses (RPA), so a mesh node is discoverable by its peers
without exposing a stable, trackable Bluetooth fingerprint on the air.

- The Service UUID rotates every 15 minutes, HMAC-SHA256-derived from a shared
  rotation key and the current time window. Every node in the same window derives
  the same UUID, so peers still find each other -- but a passive scanner sees an
  identifier that changes and cannot be linked over time.
- The node's stable id is removed from the advertisement; a peer that holds the
  node's 128-bit Identity Resolving Key (IRK) resolves its rotating 6-byte RPA
  instead (the BLE "ah" function).

The window-based operations are deterministic and byte-identical across every
AetherNet SDK (verified against fixtures/bleprivacy/vectors.json). The time
window is encoded as a little-endian int64.

Port of ``src/AetherNet.Security/Privacy/BlePrivacy.cs``.
"""

from __future__ import annotations

import hashlib
import hmac
import struct

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

# Rotation period in seconds (15 minutes).
ROTATION_SECONDS = 900


def window_for(unix_seconds: int) -> int:
    """The rotation window index for a Unix-seconds timestamp."""
    return unix_seconds // ROTATION_SECONDS


def service_uuid(rotation_key: bytes, window: int) -> str:
    """The rotating BLE Service UUID for a rotation key and time window.

    Every node sharing the rotation key derives the same UUID within the window,
    enabling mutual discovery with no static identifier on the air.
    """
    if rotation_key is None:
        raise ValueError("rotation_key cannot be None")
    mac = hmac.new(rotation_key, _window_bytes(window), hashlib.sha256).digest()
    return _format_uuid(mac[0:16])


def resolvable_address(irk: bytes, window: int) -> bytes:
    """A 6-byte Resolvable Private Address for a 16-byte IRK and time window.

    ``hash(3) || prand(3)``, where prand is HMAC-derived (with the RPA
    address-type bits set) and hash = AES-128(IRK, prand-block). Rotates every
    window; only a peer holding the IRK can link successive addresses.
    """
    if irk is None:
        raise ValueError("irk cannot be None")
    if len(irk) != 16:
        raise ValueError("IRK must be 16 bytes.")

    prand = bytearray(
        hmac.new(irk, _window_bytes(window), hashlib.sha256).digest()[0:3]
    )
    prand[0] = (prand[0] & 0x3F) | 0x40  # RPA address-type bits (0b01)
    prand = bytes(prand)

    hash_ = _ah(irk, prand)
    return hash_[0:3] + prand[0:3]


def resolve_address(irk: bytes, rpa: bytes) -> bool:
    """True if ``rpa`` was generated from ``irk`` -- i.e. this node recognises
    the peer behind the rotating address."""
    if irk is None or rpa is None:
        return False
    if len(irk) != 16 or len(rpa) != 6:
        return False

    prand = rpa[3:6]
    return _ah(irk, prand)[0:3] == rpa[0:3]


def _ah(irk: bytes, prand: bytes) -> bytes:
    """BLE "ah" hash: AES-128-ECB(irk, 0^13 || prand), keep the first 3 bytes."""
    block = bytearray(16)
    block[13:16] = prand

    encryptor = Cipher(algorithms.AES(irk), modes.ECB()).encryptor()
    ct = encryptor.update(bytes(block)) + encryptor.finalize()
    return ct[0:3]


def _window_bytes(window: int) -> bytes:
    """The time window encoded as a little-endian int64 (8 bytes)."""
    return struct.pack("<q", window)


def _format_uuid(b: bytes) -> str:
    """Format 16 bytes as a lowercase canonical UUID string
    (bytes 0-3, 4-5, 6-7, 8-9, 10-15)."""
    return (
        f"{b[0:4].hex()}-{b[4:6].hex()}-{b[6:8].hex()}-"
        f"{b[8:10].hex()}-{b[10:16].hex()}"
    )
