# SPDX-License-Identifier: MIT

"""Cross-language PeerID parity: the Python port must reproduce the cross-language
fixtures/peerid corpus exactly. Those expected values are real js-libp2p output, so passing
proves both cross-language byte-identity AND interoperability with the real libp2p network."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from aethernet.identity import from_ed25519_public_key

# fixtures/peerid lives at the repo root: tests/ -> python/ -> repo root.
_PEERID_DIR = Path(__file__).resolve().parents[2] / "fixtures" / "peerid"
_INPUTS = json.loads((_PEERID_DIR / "inputs.json").read_text())


def test_peerid_byte_parity_with_libp2p_fixture() -> None:
    assert _INPUTS, "no inputs"
    for case in _INPUTS:
        name = case["name"]
        pub = bytes.fromhex(case["pubkey_hex"])
        expected = (_PEERID_DIR / "expected" / f"{name}.txt").read_text().strip()

        actual = from_ed25519_public_key(pub)

        assert actual == expected, f"{name}: got {actual} want {expected}"
        assert actual.startswith("12D3Koo"), f"{name}: expected 12D3Koo prefix, got {actual}"


def test_peerid_rejects_wrong_length() -> None:
    with pytest.raises(ValueError):
        from_ed25519_public_key(bytes(31))
    with pytest.raises(ValueError):
        from_ed25519_public_key(bytes(33))
