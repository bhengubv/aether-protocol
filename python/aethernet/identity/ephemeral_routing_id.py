# SPDX-License-Identifier: MIT

"""Ephemeral Routing Id (ERID) — a rotating, key-derived wire address designed to
replace the stable, phone-derived UHID on the public wire.

The problem it solves
---------------------
A node's UHID is ``SHA-256(phone : deviceId : publicKey)`` — stable for the life of
the install and carried in cleartext on every packet. A passive observer who never
breaks any encryption can therefore (a) follow any node indefinitely across time and
place, and (b) — because the value is phone-derived — attempt to confirm a suspected
phone number by recomputing the hash. That is a surveillance and targeting primitive,
independent of the fact that message *contents* are end-to-end encrypted.

The design
----------
``ERID(epoch) = base32( HMAC-SHA256(routing_key, epoch) )[0:length]``

* ``routing_key`` is SECRET — derived from the node's identity secret via
  :func:`derive_routing_key`. It is NEVER derived from the public key.
* ``epoch = floor(unix_seconds / epoch_seconds)`` — a 15-minute window by default.
* Two ERIDs from the same node in different epochs are cryptographically uncorrelated
  to an outside observer — no cross-time linkage, no phone recovery.

The epoch is encoded big-endian (8-byte signed int64) so every language port produces
byte-identical input to the HMAC.
"""

from __future__ import annotations

import hashlib
import hmac as _hmac

from cryptography.hazmat.backends import default_backend
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

# Same Crockford base-32 alphabet as AetherNetTag (no I/L/O/U — visually unambiguous).
_ALPHABET: str = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

# HKDF domain-separation label. Must match the C# reference (and every other port).
_ROUTING_KEY_INFO: bytes = b"aether-erid-routing-key-v1"

#: Default rotation window: 15 minutes, expressed in seconds.
DEFAULT_EPOCH_SECONDS: int = 900

#: Default ERID length in base-32 characters (16 chars × 5 bits = 80 bits of entropy).
DEFAULT_LENGTH: int = 16


def derive_routing_key(identity_secret: bytes) -> bytes:
    """Derive the 32-byte SECRET routing key from a node's identity secret.

    Domain-separated via HKDF-SHA256 (RFC 5869, no salt). MUST be fed a secret —
    never a public value, or the rotation schedule becomes computable by anyone.

    Parameters
    ----------
    identity_secret:
        The node's identity secret (e.g. its Ed25519 private-key bytes).

    Raises
    ------
    ValueError
        If ``identity_secret`` is empty.
    """
    if not identity_secret:
        raise ValueError("identity_secret cannot be empty")
    kdf = HKDF(
        algorithm=hashes.SHA256(),
        length=32,
        salt=None,
        info=_ROUTING_KEY_INFO,
        backend=default_backend(),
    )
    return kdf.derive(bytes(identity_secret))


def epoch_for(unix_seconds: int, epoch_seconds: int = DEFAULT_EPOCH_SECONDS) -> int:
    """Return the epoch (rotation-window index) that contains the given Unix time.

    Negative ``unix_seconds`` clamp to 0.

    Raises
    ------
    ValueError
        If ``epoch_seconds`` is not positive.
    """
    if epoch_seconds <= 0:
        raise ValueError("epoch_seconds must be positive")
    if unix_seconds < 0:
        unix_seconds = 0
    return unix_seconds // epoch_seconds


def derive(
    routing_key: bytes,
    unix_seconds: int,
    epoch_seconds: int = DEFAULT_EPOCH_SECONDS,
    length: int = DEFAULT_LENGTH,
) -> str:
    """Derive the ERID for the epoch that contains ``unix_seconds``."""
    return derive_for_epoch(routing_key, epoch_for(unix_seconds, epoch_seconds), length)


def derive_for_epoch(
    routing_key: bytes,
    epoch: int,
    length: int = DEFAULT_LENGTH,
) -> str:
    """Derive the ERID for an explicit epoch number.

    The epoch is encoded big-endian (8-byte signed int64) so every language port
    produces byte-identical input to the HMAC.

    Raises
    ------
    ValueError
        If ``routing_key`` is empty or ``length`` is outside ``1..51``.
    """
    if not routing_key:
        raise ValueError("routing_key cannot be empty")
    if length < 1 or length > 51:
        raise ValueError(
            "length must be 1..51 (SHA-256 is 256 bits = 51 base-32 chars)"
        )

    # 8-byte big-endian *signed* int64 — matches BinaryPrimitives.WriteInt64BigEndian.
    epoch_bytes = (epoch & 0xFFFFFFFFFFFFFFFF).to_bytes(8, byteorder="big", signed=False)

    mac = _hmac.new(bytes(routing_key), epoch_bytes, hashlib.sha256).digest()
    return _base32(mac, length)


def _base32(data: bytes, length: int) -> str:
    """Encode the first ``length * 5`` bits of *data* as Crockford base-32, MSB first."""
    chars = []
    bit_pos = 0
    for _ in range(length):
        byte_index = bit_pos >> 3
        bit_offset = bit_pos & 7
        hi = data[byte_index]
        lo = data[byte_index + 1] if byte_index + 1 < len(data) else 0
        window = (hi << 8) | lo
        val = (window >> (11 - bit_offset)) & 0x1F
        chars.append(_ALPHABET[val])
        bit_pos += 5
    return "".join(chars)
