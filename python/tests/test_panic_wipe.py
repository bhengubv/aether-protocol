# SPDX-License-Identifier: MIT

"""Cross-language panic-wipe parity: the Python port must reproduce the C#
reference vectors (fixtures/panicwipe/vectors.json) byte-for-byte for the
deterministic parts, and secure_erase is verified behaviourally."""

from __future__ import annotations

import json
from pathlib import Path

from aethernet.security.panic_wipe import (
    IDENTITY_KEY_NAMES,
    MAX_PRE_KEYS,
    duress_pin_hash,
    pre_key_name,
    secure_erase,
    signed_pre_key_name,
    verify_duress_pin,
)

# fixtures/panicwipe/vectors.json lives at the repo root: tests/ -> python/ -> repo root.
_VECTORS = json.loads(
    (
        Path(__file__).resolve().parents[2]
        / "fixtures"
        / "panicwipe"
        / "vectors.json"
    ).read_text()
)


def test_duress_pin_hash_byte_parity_with_csharp_fixture() -> None:
    assert _VECTORS["duress_pin_hashes"]
    for v in _VECTORS["duress_pin_hashes"]:
        pin = v["pin"]
        expected = v["sha256"]
        h = duress_pin_hash(pin)
        assert len(h) == 32, pin
        assert h.hex() == expected, pin
        # The right PIN verifies constant-time-true ...
        assert verify_duress_pin(pin, h) is True, pin
        # ... a different PIN does not.
        assert verify_duress_pin(pin + "x", h) is False, pin


def test_identity_key_names_match_fixture() -> None:
    assert list(IDENTITY_KEY_NAMES) == _VECTORS["identity_key_names"]


def test_max_prekeys_matches_fixture() -> None:
    assert MAX_PRE_KEYS == _VECTORS["max_prekeys"]


def test_pre_key_name_matches_fixture() -> None:
    v = _VECTORS["prekey_name"]
    assert pre_key_name(v["index"]) == v["expected"]


def test_signed_pre_key_name_matches_fixture() -> None:
    v = _VECTORS["signed_prekey_name"]
    assert signed_pre_key_name(v["index"]) == v["expected"]


def test_secure_erase_zeroes_buffer() -> None:
    buf = bytearray(b"\x01\x02\x03\x04\x05\x06\x07\x08")
    secure_erase(buf)
    assert buf == bytearray(len(buf))  # all zero
    # Empty buffer is a no-op (must not raise).
    secure_erase(bytearray())


def test_verify_duress_pin_wrong_length_hash_is_false() -> None:
    # A 16-byte hash can never match (SHA-256 is 32 bytes) -> False, not a raise.
    assert verify_duress_pin("1234", bytes(16)) is False
