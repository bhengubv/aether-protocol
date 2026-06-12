# SPDX-License-Identifier: MIT

"""Tests for the Ephemeral Routing Id (ERID) identity primitive.

Run with:
    python -m pytest tests/test_ephemeral_routing_id.py -v
"""

from __future__ import annotations

import pytest

from aethernet.identity import ephemeral_routing_id as erid

# ---------------------------------------------------------------------------
# Canonical cross-language parity vectors.
#
# GROUND TRUTH, derived from the C# reference
# (src/AetherNet.Core/Identity/EphemeralRoutingId.cs). Every language port MUST
# reproduce these byte-for-byte. Do not edit without regenerating from C#.
# ---------------------------------------------------------------------------

ROUTING_KEY_VECTORS = {
    "node-secret-A": "206f67e52afa8de0624fd3a2efc5bd68c65879ab623141811c996f0d416345e3",
    "node-B": "b071f5176536876b74a8927a242decea37aba390df06ec0019b711122c05384b",
    "n": "44874ed0e4e94dc12ea647a9460644feb1495f7dd348e583fcd3c5399388819a",
}

ERID_VECTORS = [
    ("node-secret-A", 0, "Q3AN7RWEGZBPZ5WM"),
    ("node-secret-A", 1, "N1HGBC2VC72W0A7E"),
    ("node-secret-A", 100, "KYF9JXYE3XJGFK26"),
    ("node-secret-A", 12345, "ZFM5AZMY6K0TGEK0"),
    ("node-secret-A", 1371, "N080TN3W537B27ZE"),
    ("node-B", 0, "61V5RVS7BVEBTV39"),
    ("node-B", 1, "6NQ731EA0HNGAN3C"),
    ("node-B", 100, "PDEMCT481QBWQN9P"),
    ("node-B", 12345, "H2D11G5JJY5EQ0PW"),
    ("node-B", 1371, "003WA1T3KDQVSDET"),
    ("n", 0, "GGY1T8FKNWCFXS71"),
    ("n", 1, "76AA5GEDFJ669RQS"),
    ("n", 100, "CFSM7DAP0Z1QT2KT"),
    ("n", 12345, "MJT2C0EYGYVRF4KN"),
    ("n", 1371, "39MYY8R0ZA292MPD"),
]


def _key(secret: str) -> bytes:
    return erid.derive_routing_key(secret.encode("ascii"))


# ---------------------------------------------------------------------------
# Canonical-vector parity
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("secret,want_hex", list(ROUTING_KEY_VECTORS.items()))
def test_routing_key_matches_canonical_vectors(secret: str, want_hex: str) -> None:
    assert _key(secret).hex() == want_hex


@pytest.mark.parametrize("secret,epoch,want_erid", ERID_VECTORS)
def test_erid_matches_canonical_vectors(secret: str, epoch: int, want_erid: str) -> None:
    assert erid.derive_for_epoch(_key(secret), epoch) == want_erid


# ---------------------------------------------------------------------------
# Behavioural properties
# ---------------------------------------------------------------------------


def test_deterministic_for_same_key_and_epoch() -> None:
    k = _key("node-secret-A")
    assert erid.derive_for_epoch(k, 12345) == erid.derive_for_epoch(k, 12345)


def test_rotates_across_consecutive_epochs() -> None:
    k = _key("node-secret-A")
    assert erid.derive_for_epoch(k, 100) != erid.derive_for_epoch(k, 101)


def test_differs_by_node_in_same_epoch() -> None:
    assert erid.derive_for_epoch(_key("node-A"), 7) != erid.derive_for_epoch(_key("node-B"), 7)


def test_length_and_alphabet() -> None:
    out = erid.derive_for_epoch(_key("n"), 1)
    assert len(out) == erid.DEFAULT_LENGTH
    assert all(c in "0123456789ABCDEFGHJKMNPQRSTVWXYZ" for c in out)


@pytest.mark.parametrize(
    "unix_seconds,epoch_seconds,expected",
    [
        (0, 900, 0),
        (899, 900, 0),
        (900, 900, 1),
        (1800, 900, 2),
        (1234567, 900, 1371),
        (-50, 900, 0),  # negative clamps to 0
    ],
)
def test_epoch_for(unix_seconds: int, epoch_seconds: int, expected: int) -> None:
    assert erid.epoch_for(unix_seconds, epoch_seconds) == expected


def test_derive_stable_within_window_changes_at_boundary() -> None:
    k = _key("n")
    # 1000 and 1500 both fall inside window 1 → same ERID.
    assert erid.derive(k, 1000) == erid.derive(k, 1500)
    # 2000 falls in window 2 → a different ERID.
    assert erid.derive(k, 1000) != erid.derive(k, 2000)


def test_routing_key_is_deterministic_256bit_distinct_from_seed() -> None:
    seed = b"ed25519-private-key-material-seed"
    k1 = erid.derive_routing_key(seed)
    k2 = erid.derive_routing_key(seed)
    assert k1 == k2
    assert len(k1) == 32
    assert k1 != seed
    assert erid.derive_routing_key(b"a-different-identity") != k1


def test_rejects_empty_inputs() -> None:
    with pytest.raises(ValueError):
        erid.derive_routing_key(b"")
    with pytest.raises(ValueError):
        erid.derive_for_epoch(b"", 1)
