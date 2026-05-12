# SPDX-License-Identifier: MIT

"""AetherTag — human-readable identity address derived from an Ed25519 public key.

Algorithm
---------
SHA-256(publicKey) → extract first 50 bits → encode as 10 Crockford base-32 chars
→ format as "XXXXX-XXXXX"

Crockford base-32 alphabet (removes I, L, O, U):
    0123456789ABCDEFGHJKMNPQRSTVWXYZ

Bit packing (50 bits from the first 7 bytes of the SHA-256 digest):
    bits = (h[0]<<42) | (h[1]<<34) | (h[2]<<26) | (h[3]<<18)
           | (h[4]<<10) | (h[5]<<2) | (h[6]>>6 & 0x3)

Each of the 10 Crockford symbols encodes 5 bits, extracted MSB-first.
"""

from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from typing import Optional

# ---------------------------------------------------------------------------
# Crockford base-32 alphabet
# ---------------------------------------------------------------------------

_ALPHABET: str = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
assert len(_ALPHABET) == 32, "Crockford alphabet must have exactly 32 characters"

# Build a fast decode table: ord(char) → 5-bit value (case-insensitive)
_DECODE: dict[int, int] = {}
for _idx, _ch in enumerate(_ALPHABET):
    _DECODE[ord(_ch)] = _idx
    _DECODE[ord(_ch.lower())] = _idx

# Valid characters (upper + lower) for quick membership tests
_VALID_CHARS: frozenset[str] = frozenset(_ALPHABET + _ALPHABET.lower())

# Pre-compiled regex for the canonical XXXXX-XXXXX format
_TAG_RE = re.compile(r"^[0-9A-Za-z]{5}-[0-9A-Za-z]{5}$")


# ---------------------------------------------------------------------------
# Core encoding helpers
# ---------------------------------------------------------------------------

def _encode(public_key: bytes) -> str:
    """Return the raw 10-character Crockford base-32 string for *public_key*."""
    if len(public_key) != 32:
        raise ValueError(
            f"public_key must be exactly 32 bytes, got {len(public_key)}"
        )

    digest = hashlib.sha256(public_key).digest()

    # Pack 50 bits from the first 7 bytes of the digest.
    # Bytes 0-5 contribute 6×8 = 48 bits; byte 6 contributes its top 2 bits.
    bits: int = (
        (digest[0] << 42)
        | (digest[1] << 34)
        | (digest[2] << 26)
        | (digest[3] << 18)
        | (digest[4] << 10)
        | (digest[5] << 2)
        | ((digest[6] >> 6) & 0x3)
    )

    # Extract 10 groups of 5 bits, MSB-first
    chars: list[str] = []
    for shift in range(45, -5, -5):  # 45, 40, 35, …, 0
        chars.append(_ALPHABET[(bits >> shift) & 0x1F])

    return "".join(chars)


def _validate_raw(raw: str) -> bool:
    """Return True if *raw* is a valid 10-character Crockford string."""
    if len(raw) != 10:
        return False
    return all(ch in _VALID_CHARS for ch in raw)


# ---------------------------------------------------------------------------
# AetherTag
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class AetherTag:
    """Human-readable, shareable Aether identity address.

    The canonical string form is ``XXXXX-XXXXX`` where every character is
    from the Crockford base-32 alphabet (uppercase by convention).

    Attributes
    ----------
    value:
        The canonical ``XXXXX-XXXXX`` string representation.
    """

    value: str

    # ------------------------------------------------------------------
    # Construction
    # ------------------------------------------------------------------

    @staticmethod
    def from_public_key(public_key: bytes) -> "AetherTag":
        """Derive an :class:`AetherTag` from a 32-byte Ed25519 public key.

        Parameters
        ----------
        public_key:
            The raw 32-byte Ed25519 public key.

        Returns
        -------
        AetherTag
            The deterministic tag for *public_key*.

        Raises
        ------
        ValueError
            If *public_key* is not exactly 32 bytes.
        """
        raw = _encode(public_key)
        return AetherTag(f"{raw[:5]}-{raw[5:]}")

    # ------------------------------------------------------------------
    # Parsing
    # ------------------------------------------------------------------

    @staticmethod
    def parse(tag: str) -> "AetherTag":
        """Parse a tag string into an :class:`AetherTag`.

        Accepts the ``XXXXX-XXXXX`` form with or without the separator, and
        is case-insensitive.

        Raises
        ------
        ValueError
            If *tag* has the wrong length, contains invalid characters, or is
            otherwise malformed.
        """
        if not isinstance(tag, str) or not tag:
            raise ValueError("tag must be a non-empty string")

        # Normalise: strip whitespace, uppercase, remove any embedded hyphen
        normalised = tag.strip().upper().replace("-", "")

        if len(normalised) != 10:
            raise ValueError(
                f"AetherTag must be 10 Crockford characters (got {len(normalised)!r} "
                f"after stripping separator)"
            )

        invalid = [ch for ch in normalised if ch not in _VALID_CHARS]
        if invalid:
            raise ValueError(
                f"AetherTag contains invalid Crockford character(s): "
                f"{', '.join(repr(c) for c in invalid)}"
            )

        return AetherTag(f"{normalised[:5]}-{normalised[5:]}")

    @staticmethod
    def try_parse(tag: str) -> Optional["AetherTag"]:
        """Like :meth:`parse` but returns ``None`` instead of raising."""
        try:
            return AetherTag.parse(tag)
        except (ValueError, AttributeError):
            return None

    # ------------------------------------------------------------------
    # Verification
    # ------------------------------------------------------------------

    @staticmethod
    def verify(tag: str, public_key: bytes) -> bool:
        """Return ``True`` if *tag* is the correct :class:`AetherTag` for *public_key*.

        Parameters
        ----------
        tag:
            Any parseable tag string (case-insensitive, with or without ``-``).
        public_key:
            The 32-byte Ed25519 public key to check against.
        """
        try:
            parsed = AetherTag.parse(tag)
            expected = AetherTag.from_public_key(public_key)
            return parsed == expected
        except (ValueError, Exception):
            return False

    # ------------------------------------------------------------------
    # Validation
    # ------------------------------------------------------------------

    def is_valid(self) -> bool:
        """Return ``True`` if this instance holds a well-formed tag."""
        if not isinstance(self.value, str):
            return False
        if not _TAG_RE.match(self.value):
            return False
        raw = self.value.replace("-", "")
        return all(ch in _VALID_CHARS for ch in raw)

    # ------------------------------------------------------------------
    # Dunder helpers
    # ------------------------------------------------------------------

    def __str__(self) -> str:
        return self.value

    def __repr__(self) -> str:
        return f"AetherTag({self.value!r})"

    # __eq__ and __hash__ are provided automatically by @dataclass(frozen=True)
