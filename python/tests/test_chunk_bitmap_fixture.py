# SPDX-License-Identifier: MIT
"""
Cross-language ChunkBitmap wire-format fixture verifier — Python runner.

Reads fixtures/content/chunk_bitmap_vectors.json and verifies that this
implementation produces bit-identical bitsets and JSON payloads for each
pinned test vector.
"""
import base64
import json
import os
from pathlib import Path
from typing import Any

import pytest


# ── Inline implementations (spec-compliance check, not library re-use) ────────

def _bitset_encode(chunk_count: int, have_indices) -> bytes:
    if chunk_count <= 0:
        return b""
    buf = bytearray((chunk_count + 7) // 8)
    for i in have_indices:
        buf[i >> 3] |= 1 << (i & 7)
    return bytes(buf)


def _bitset_decode(bitset: bytes, chunk_count: int) -> list[int]:
    result = []
    limit = min(chunk_count, len(bitset) * 8)
    for i in range(limit):
        if bitset[i >> 3] & (1 << (i & 7)):
            result.append(i)
    return result


def _marshal_json(root_hash: str, chunk_count: int, have_bitset: bytes, generation: int) -> str:
    b64 = base64.b64encode(have_bitset).decode("ascii")
    parts = [
        f'"root_hash":{json.dumps(root_hash)}',
        f'"chunk_count":{chunk_count}',
        f'"have_bitset":{json.dumps(b64)}',
        f'"generation":{generation}',
    ]
    return "{" + ",".join(parts) + "}"


# ── Fixture loader ─────────────────────────────────────────────────────────────

def _find_fixtures() -> Path:
    here = Path(__file__).resolve()
    for _ in range(12):
        candidate = here / "fixtures" / "content" / "chunk_bitmap_vectors.json"
        if candidate.exists():
            return candidate
        here = here.parent
    raise FileNotFoundError("Could not locate fixtures/content/chunk_bitmap_vectors.json")


def _load_vectors() -> list[dict[str, Any]]:
    path = _find_fixtures()
    return json.loads(path.read_text(encoding="utf-8"))


VECTORS: list[dict[str, Any]] = _load_vectors()
VECTOR_NAMES = [v["name"] for v in VECTORS]


def _get(name: str) -> dict[str, Any]:
    return next(v for v in VECTORS if v["name"] == name)


# ── Tests ──────────────────────────────────────────────────────────────────────

@pytest.mark.parametrize("vector_name", VECTOR_NAMES)
def test_encode_produces_correct_bitset(vector_name: str) -> None:
    v = _get(vector_name)
    bitset = _bitset_encode(v["chunk_count"], v["have_indices"])
    assert bitset.hex() == v["have_bitset_hex"].lower()
    assert base64.b64encode(bitset).decode("ascii") == v["have_bitset_base64"]


@pytest.mark.parametrize("vector_name", VECTOR_NAMES)
def test_decode_recovers_correct_indices(vector_name: str) -> None:
    v = _get(vector_name)
    bitset = base64.b64decode(v["have_bitset_base64"])
    recovered = _bitset_decode(bitset, v["chunk_count"])
    assert sorted(recovered) == sorted(v["have_indices"])


@pytest.mark.parametrize("vector_name", VECTOR_NAMES)
def test_json_serialize_matches_expected(vector_name: str) -> None:
    v = _get(vector_name)
    bitset = _bitset_encode(v["chunk_count"], v["have_indices"])
    actual = _marshal_json(v["root_hash"], v["chunk_count"], bitset, v["generation"])
    assert actual == v["expected_json"]


@pytest.mark.parametrize("vector_name", VECTOR_NAMES)
def test_bitset_length_is_ceil_div8(vector_name: str) -> None:
    v = _get(vector_name)
    bitset = _bitset_encode(v["chunk_count"], v["have_indices"])
    expected_len = (v["chunk_count"] + 7) // 8
    assert len(bitset) == expected_len


@pytest.mark.parametrize("vector_name", VECTOR_NAMES)
def test_trailing_bits_are_zero(vector_name: str) -> None:
    v = _get(vector_name)
    bitset = _bitset_encode(v["chunk_count"], v["have_indices"])
    if len(bitset) == 0:
        return
    trailing_bits = v["chunk_count"] % 8
    if trailing_bits == 0:
        return
    last_byte = bitset[-1]
    valid_mask = (1 << trailing_bits) - 1
    assert (last_byte & ~valid_mask) == 0
