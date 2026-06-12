# SPDX-License-Identifier: MIT

"""Frames the in-session ERID announcement — the message a node sends a peer INSIDE an
established Signal session to share its secret routing key (plus the rotation parameters
it uses), so the peer can resolve its rotating wire address via :class:`EridDirectory`.

The bytes are carried *encrypted* by the Signal session, so this is framing only — no
encryption of its own. A 4-byte magic sentinel + version lets a receiver tell an ERID
announcement apart from other in-session application data before trying to parse it.

Layout: magic ``AERD`` (4) + version (1) + ``epoch_seconds`` (int32 BE) +
``erid_length`` (int32 BE) + ``routing_key_len`` (int32 BE) + ``routing_key``. Integer
fields are big-endian so every language port frames byte-identically. Port of the C#
reference (``src/AetherNet.Core/Identity/EridAnnouncementCodec.cs``).
"""

from __future__ import annotations

import struct
from typing import NamedTuple

from aethernet.identity.ephemeral_routing_id import DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH

_MAGIC = b"AERD"  # 0x41 0x45 0x52 0x44
_VERSION = 1
# magic(4) + version(1) + epoch_seconds(4) + erid_length(4) + routing_key_len(4) = 17.
_HEADER_LENGTH = 17


class EridAnnouncement(NamedTuple):
    """A decoded in-session ERID announcement."""

    routing_key: bytes
    epoch_seconds: int
    erid_length: int


def encode(
    routing_key: bytes,
    epoch_seconds: int = DEFAULT_EPOCH_SECONDS,
    erid_length: int = DEFAULT_LENGTH,
) -> bytes:
    """Frame an announcement carrying ``routing_key`` and the rotation params.

    Raises
    ------
    ValueError
        If ``routing_key`` is empty, ``epoch_seconds`` is not positive, or
        ``erid_length`` is outside ``1..51``.
    """
    if not routing_key:
        raise ValueError("routing_key cannot be empty")
    if epoch_seconds <= 0:
        raise ValueError("epoch_seconds must be positive")
    if erid_length < 1 or erid_length > 51:
        raise ValueError("erid_length must be 1..51")

    # '>' = big-endian; B = version (uint8); three signed int32 fields, matching the C#
    # BinaryPrimitives.WriteInt32BigEndian calls byte-for-byte.
    header = _MAGIC + struct.pack(
        ">Biii", _VERSION, epoch_seconds, erid_length, len(routing_key)
    )
    return header + bytes(routing_key)


def try_decode(data: bytes) -> EridAnnouncement | None:
    """Parse an announcement.

    Returns ``None`` (rather than raising) when the bytes are not a well-formed ERID
    announcement, so a receiver can cheaply test an arbitrary decrypted in-session
    payload against the magic.
    """
    if len(data) < _HEADER_LENGTH:
        return None
    if data[0:4] != _MAGIC:
        return None
    if data[4] != _VERSION:
        return None

    epoch_seconds, erid_length, key_len = struct.unpack(">iii", data[5:17])

    if epoch_seconds <= 0:
        return None
    if erid_length < 1 or erid_length > 51:
        return None
    if key_len <= 0 or _HEADER_LENGTH + key_len > len(data):
        return None

    return EridAnnouncement(
        routing_key=bytes(data[_HEADER_LENGTH : _HEADER_LENGTH + key_len]),
        epoch_seconds=epoch_seconds,
        erid_length=erid_length,
    )
