# SPDX-License-Identifier: MIT

"""Cross-language BLE privacy parity: the Python port must reproduce the C#
reference vectors (fixtures/bleprivacy/vectors.json) byte-for-byte."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from aethernet.security.ble_privacy import (
    ROTATION_SECONDS,
    resolvable_address,
    resolve_address,
    service_uuid,
    window_for,
)

# fixtures/bleprivacy/vectors.json lives at the repo root: tests/ -> python/ -> repo root.
_VECTORS = json.loads(
    (
        Path(__file__).resolve().parents[2]
        / "fixtures"
        / "bleprivacy"
        / "vectors.json"
    ).read_text()
)


def test_ble_privacy_byte_parity_with_csharp_fixture() -> None:
    rotation_key = bytes.fromhex(_VECTORS["rotation_key"])
    irk = bytes.fromhex(_VECTORS["irk"])
    wrong_irk = bytes.fromhex(_VECTORS["wrong_irk"])

    assert _VECTORS["uuid_vectors"]
    for v in _VECTORS["uuid_vectors"]:
        assert service_uuid(rotation_key, v["window"]) == v["uuid"], v["window"]

    assert _VECTORS["rpa_vectors"]
    for v in _VECTORS["rpa_vectors"]:
        rpa = resolvable_address(irk, v["window"])
        assert rpa.hex() == v["rpa"], v["window"]
        # The holder of the IRK resolves its own rotating address ...
        assert resolve_address(irk, rpa) is True, v["window"]
        # ... but a different IRK does not.
        assert resolve_address(wrong_irk, rpa) is False, v["window"]


def test_rotation_seconds_matches_fixture() -> None:
    assert ROTATION_SECONDS == _VECTORS["rotation_seconds"]


def test_window_for_boundary() -> None:
    assert window_for(899) == 0
    assert window_for(900) == 1


def test_fifteen_byte_irk_rejected() -> None:
    fifteen = bytes(15)
    with pytest.raises(ValueError):
        resolvable_address(fifteen, 0)
