# SPDX-License-Identifier: MIT

"""Cross-language ERID parity: the Python port must reproduce the C# reference vectors
(fixtures/erid/vectors.json) byte-for-byte."""

from __future__ import annotations

import json
from pathlib import Path

from aethernet.identity import (
    EridDirectory,
    derive,
    derive_for_epoch,
    derive_routing_key,
    erid_announcement_codec,
)

# fixtures/erid/vectors.json lives at the repo root: tests/ -> python/ -> repo root.
_VECTORS = json.loads(
    (Path(__file__).resolve().parents[2] / "fixtures" / "erid" / "vectors.json").read_text()
)


def test_erid_byte_parity_with_csharp_fixture() -> None:
    rk = derive_routing_key(_VECTORS["secret_ascii"].encode("ascii"))
    assert rk.hex() == _VECTORS["routing_key_hex"]

    for v in _VECTORS["erids_by_epoch"]:
        assert derive_for_epoch(rk, v["epoch"], _VECTORS["erid_length"]) == v["erid"]

    for v in _VECTORS["derive_by_unixseconds"]:
        assert (
            derive(rk, v["unix"], _VECTORS["epoch_seconds"], _VECTORS["erid_length"])
            == v["erid"]
        )

    enc = erid_announcement_codec.encode(
        rk, _VECTORS["epoch_seconds"], _VECTORS["erid_length"]
    )
    assert enc.hex() == _VECTORS["announcement_encode_hex"]

    # Round-trip the frame back through the decoder.
    dec = erid_announcement_codec.try_decode(enc)
    assert dec is not None
    assert dec.routing_key.hex() == _VECTORS["routing_key_hex"]
    assert dec.epoch_seconds == _VECTORS["epoch_seconds"]
    assert dec.erid_length == _VECTORS["erid_length"]


def test_erid_directory_resolve_and_outsider() -> None:
    a_key = derive_routing_key(b"identity-A")
    b_key = derive_routing_key(b"identity-B")
    alice = EridDirectory(a_key)
    bob = EridDirectory(b_key)
    alice.remember_peer("bob", b_key)
    bob.remember_peer("alice", a_key)
    t = 1_700_000_000

    # An established peer resolves the other's rotating address, both directions.
    assert alice.erid_for_peer("bob", t) == bob.my_erid(t)
    assert bob.resolve_peer(alice.my_erid(t), t) == "alice"

    # An outsider holding no routing key cannot.
    outsider = EridDirectory(derive_routing_key(b"identity-X"))
    assert outsider.resolve_peer(alice.my_erid(t), t) is None
