# SPDX-License-Identifier: MIT

"""Cross-language Reed-Solomon vault parity verifier.

Mirrors ../fixtures/vault/reed_solomon_basic.json — the canonical cross-language parity source
generated from the C# reference (AetherNet.Vault.ReedSolomonCodec, GF(2^8) poly 0x11D, alpha=2,
K=10/M=4). Every language port MUST reproduce every shard and every recovery byte-for-byte. Mirrors
the Go vault/reed_solomon_fixture_test.go suite.

Run from the python/ directory:
    python -m pytest tests/test_vault_reed_solomon_fixture.py
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from aethernet.vault.reed_solomon import ReedSolomonCodec


def _fixtures_dir() -> Path:
    return Path(__file__).resolve().parent.parent.parent / "fixtures"


def _load_vectors() -> dict:
    with (_fixtures_dir() / "vault" / "reed_solomon_basic.json").open(encoding="utf-8") as fp:
        return json.load(fp)


VECTORS = _load_vectors()


def test_fixture_params_are_expected():
    assert VECTORS["k"] == 10
    assert VECTORS["m"] == 4
    assert VECTORS["n"] == 14
    assert VECTORS["field"]["primitive_polynomial"] == "0x11D"
    assert VECTORS["field"]["alpha"] == 2
    assert VECTORS["field"]["gf_bits"] == 8


def test_reed_solomon_shard_parity():
    """The Python encoder reproduces every C# shard (systematic data + Cauchy parity) byte-for-byte."""
    inp = bytes.fromhex(VECTORS["input"])
    assert len(inp) == VECTORS["input_size"]

    codec = ReedSolomonCodec(VECTORS["k"], VECTORS["m"])
    shards = codec.encode_data(inp)

    assert len(shards) == VECTORS["n"]
    assert len(shards[0]) == VECTORS["shard_size"]

    for want in VECTORS["shards"]:
        got = bytes(shards[want["index"]]).hex()
        assert got == want["hex"], (
            f"shard {want['index']} mismatch\n got={got}\nwant={want['hex']}"
        )


def test_reed_solomon_recovery_parity():
    """Every recovery subset decodes to the fixture input byte-for-byte (covers the systematic
    fast-path, the all-parity path, and a data+parity mix)."""
    inp = bytes.fromhex(VECTORS["input"])
    codec = ReedSolomonCodec(VECTORS["k"], VECTORS["m"])
    shards = codec.encode_data(inp)

    for rec in VECTORS["recovery"]:
        available = {idx: shards[idx] for idx in rec["survivor_indices"]}
        recovered = codec.reconstruct_data(available, VECTORS["input_size"])

        assert recovered.hex() == rec["recovered"], f"recovery {rec['note']!r}: bytes mismatch"
        # The recovered blob must equal the original input.
        assert recovered == inp, f"recovery {rec['note']!r}: recovered != original input"


def test_reed_solomon_k_minus_one_fails():
    """Only K-1 survivors is unrecoverable (the fixture's should_fail case). Ports MUST treat this as a
    failure."""
    inp = bytes.fromhex(VECTORS["input"])
    codec = ReedSolomonCodec(VECTORS["k"], VECTORS["m"])
    shards = codec.encode_data(inp)

    should_fail = VECTORS["should_fail"]["survivor_indices"]
    assert len(should_fail) == VECTORS["k"] - 1

    available = {idx: shards[idx] for idx in should_fail}
    with pytest.raises(ValueError):
        codec.reconstruct_data(available, VECTORS["input_size"])


def test_reed_solomon_parity_only_round_trip():
    """Recovery works from data[M..K-1] plus all M parity shards (= K total) — exercising the general
    matrix-inversion path with the maximum number of parity rows the code can use."""
    inp = bytes.fromhex(VECTORS["input"])
    k, m, n = VECTORS["k"], VECTORS["m"], VECTORS["n"]
    codec = ReedSolomonCodec(k, m)
    shards = codec.encode_data(inp)

    # Drop the first M data shards; survive on data[M..K-1] + all M parity shards.
    available = {i: shards[i] for i in range(m, k)}
    available.update({i: shards[i] for i in range(k, n)})

    recovered = codec.reconstruct_data(available, VECTORS["input_size"])
    assert recovered == inp


def test_reed_solomon_every_minimal_subset_decodes():
    """Exhaustive MDS check: EVERY size-K subset of the N shards reconstructs the original input. This
    is the strongest possible statement of the MDS guarantee for these fixture params."""
    from itertools import combinations

    inp = bytes.fromhex(VECTORS["input"])
    k, n = VECTORS["k"], VECTORS["n"]
    codec = ReedSolomonCodec(k, VECTORS["m"])
    shards = codec.encode_data(inp)

    for subset in combinations(range(n), k):
        available = {i: shards[i] for i in subset}
        recovered = codec.reconstruct_data(available, VECTORS["input_size"])
        assert recovered == inp, f"subset {subset} failed to reconstruct"


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
