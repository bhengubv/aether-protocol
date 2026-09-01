# SPDX-License-Identifier: MIT

"""Two phones agreeing where to meet, before either radio has done anything.

Port of the C# reference ``AetherNet.Rendezvous.Meeting`` / ``GroupRole``. The derivation is the
cross-language contract: order the two tags, HKDF-SHA256 them under a fixed label, and both phones
independently land on the same rendezvous — and on opposite host roles — without a word passing
between them. Every value here must match ``fixtures/meeting/meeting_basic.json`` byte-for-byte.
"""

from __future__ import annotations

import hashlib
import uuid as _uuid
from dataclasses import dataclass

from cryptography.hazmat.backends import default_backend
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

# Ties this derivation to this purpose, so the same tags used elsewhere yield nothing here.
_INFO = "aether-meeting-v1"

# Crockford's alphabet: no I, L, O or U, so it cannot be misread down a phone line.
_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

# How many characters a rendezvous carries. Longer than the widest radio needs.
LENGTH = 25


def hosts_the_group(my_tag: str | None, their_tag: str | None) -> bool:
    """Does this phone host the group it would share with ``their_tag``?

    Ordinal comparison — the two phones compare code units rather than anything a locale could
    disagree about. A tag against itself hosts nothing.
    """
    if not my_tag or not their_tag:
        return False
    return my_tag < their_tag


def _encode(data: bytes) -> str:
    """Bytes as Crockford base32, five bits at a time — the same bit walk as the C# reference."""
    chars = []
    total_chars = len(data) * 8 // 5
    bit = 0
    for _ in range(total_chars):
        value = 0
        for _ in range(5):
            source = data[bit // 8]
            taken = (source >> (7 - (bit % 8))) & 1
            value = (value << 1) | taken
            bit += 1
        chars.append(_ALPHABET[value])
    return "".join(chars)


@dataclass(frozen=True)
class Meeting:
    """A meeting point derived from two tags: who you are meeting, where, and which of you opens."""

    peer_tag: str
    rendezvous: str
    i_start: bool

    @classmethod
    def with_tags(cls, my_tag: str | None, their_tag: str | None) -> "Meeting | None":
        """Work out where two phones meet, from their tags alone.

        Returns ``None`` when either tag is missing or they are the same phone (tags are
        case-insensitive, so two case-variants are one identity and do not meet).
        """
        if not my_tag or not my_tag.strip() or not their_tag or not their_tag.strip():
            return None
        if my_tag.upper() == their_tag.upper():
            return None

        # Ordered, so both phones feed the derivation the same bytes in the same order.
        first, second = (my_tag, their_tag) if my_tag < their_tag else (their_tag, my_tag)

        kdf = HKDF(
            algorithm=hashes.SHA256(),
            length=16,
            salt=b"",  # C# passes ReadOnlySpan<byte>.Empty; empty and absent salt are equivalent in HKDF.
            info=_INFO.encode("utf-8"),
            backend=default_backend(),
        )
        derived = kdf.derive((first + "\n" + second).encode("utf-8"))

        return cls(their_tag, _encode(derived)[:LENGTH], hosts_the_group(my_tag, their_tag))

    def where(self, characters: int) -> str:
        """As much of the rendezvous as a radio can use, from the front."""
        if characters <= 0:
            return ""
        if characters >= len(self.rendezvous):
            return self.rendezvous
        return self.rendezvous[:characters]

    def uuid(self) -> _uuid.UUID:
        """The meeting as a UUID, for a radio that finds people by advertising one.

        Built to match the .NET reference: the raw hash bytes carry the version/variant, and the
        16 bytes are read in .NET's mixed-endian Guid layout (``bytes_le``), so ``str()`` and
        ``.bytes_le`` agree with C#'s ``Guid.ToString()`` / ``Guid.ToByteArray()``.
        """
        digest = bytearray(hashlib.sha256((_INFO + "-uuid\n" + self.rendezvous).encode("utf-8")).digest()[:16])
        digest[7] = (digest[7] & 0x0F) | 0x40  # version 4
        digest[8] = (digest[8] & 0x3F) | 0x80  # variant 1
        return _uuid.UUID(bytes_le=bytes(digest))

    def address(self, bits: int) -> int:
        """The meeting as a small number, for a radio whose address space is tiny."""
        if bits < 1 or bits > 32:
            raise ValueError("bits must be between 1 and 32")
        digest = hashlib.sha256((_INFO + "-addr\n" + self.rendezvous).encode("utf-8")).digest()
        whole = int.from_bytes(digest[:4], "big")
        return whole if bits == 32 else whole & ((1 << bits) - 1)
