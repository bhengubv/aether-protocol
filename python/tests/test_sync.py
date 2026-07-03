# SPDX-License-Identifier: MIT

"""Cross-language parity gate for decentralised multi-device sync. Every case is
driven by ``fixtures/sync/vectors.json`` (the shared oracle, committed to the
repo) — SyncRecord serialization + round-trip, deterministic last-write-wins
reconciliation (both orderings), and Ed25519-signed DeviceLink
serialize/verify/round-trip. These bytes are byte-identical to the C# reference
and every other AetherNet SDK."""

from __future__ import annotations

import json
from pathlib import Path
from uuid import UUID

import pytest

from aethernet.sync import (
    DeviceLink,
    SyncOp,
    SyncRecord,
    device_link_create,
    device_link_deserialize,
    device_link_serialize,
    device_link_signed_body,
    device_link_verify,
    deserialize_record,
    merge,
    serialize_record,
    winner,
)


def _fixtures_dir() -> Path:
    d = Path(__file__).resolve()
    for _ in range(10):
        if (d / "fixtures" / "sync" / "vectors.json").exists():
            return d / "fixtures" / "sync"
        if d.parent == d:
            break
        d = d.parent
    raise FileNotFoundError("fixtures/sync/vectors.json not found")


def _vectors() -> dict:
    return json.loads((_fixtures_dir() / "vectors.json").read_text(encoding="utf-8"))


_VEC = _vectors()


def _record_from_json(j: dict) -> SyncRecord:
    return SyncRecord(
        record_id=UUID(j["record_id"]),
        device_id=j["device_id"],
        op=SyncOp(j["op"]),
        item_id=j["item_id"],
        logical_clock=j["logical_clock"],
        created_at_ms=j["created_at_ms"],
        encrypted_payload=bytes.fromhex(j.get("payload_hex", "")),
    )


# ── SyncRecord serialization + round-trip ─────────────────────────────────────


@pytest.mark.parametrize("case", _VEC["sync_records"], ids=lambda c: c["record_id"])
def test_sync_record_serialize_matches_fixture(case: dict) -> None:
    record = _record_from_json(case)
    assert serialize_record(record).hex() == case["serialized_hex"]


@pytest.mark.parametrize("case", _VEC["sync_records"], ids=lambda c: c["record_id"])
def test_sync_record_deserialize_roundtrip(case: dict) -> None:
    got = deserialize_record(bytes.fromhex(case["serialized_hex"]))
    assert got.record_id == UUID(case["record_id"])
    assert got.device_id == case["device_id"]
    assert int(got.op) == case["op"]
    assert got.item_id == case["item_id"]
    assert got.logical_clock == case["logical_clock"]
    assert got.created_at_ms == case["created_at_ms"]
    assert got.encrypted_payload == bytes.fromhex(case.get("payload_hex", ""))
    # Serializing the round-tripped record reproduces the exact bytes.
    assert serialize_record(got).hex() == case["serialized_hex"]


# ── Reconciliation (deterministic LWW, order-independent) ─────────────────────


@pytest.mark.parametrize("case", _VEC["reconcile"], ids=lambda c: c["name"])
def test_reconcile_winner(case: dict) -> None:
    records = [_record_from_json(r) for r in case["records"]]
    expected = UUID(case["winner_record_id"])

    assert winner(records).record_id == expected
    # Same set, reversed order → identical winner (determinism / order-independence).
    assert winner(list(reversed(records))).record_id == expected


@pytest.mark.parametrize("case", _VEC["reconcile"], ids=lambda c: c["name"])
def test_reconcile_merge(case: dict) -> None:
    records = [_record_from_json(r) for r in case["records"]]
    expected = UUID(case["winner_record_id"])
    merged = merge(records)
    # Every fixture case is a single item; the merged winner matches winner().
    item_id = records[0].item_id
    assert item_id in merged
    assert merged[item_id].record_id == expected
    assert merge(list(reversed(records)))[item_id].record_id == expected


# ── DeviceLink signing / serialization / verification ─────────────────────────


@pytest.mark.parametrize("case", _VEC["device_links"], ids=lambda c: c["device_id"])
def test_device_link_signed_body_matches_fixture(case: dict) -> None:
    body = device_link_signed_body(
        case["device_id"],
        bytes.fromhex(case["device_public_key"]),
        case["issued_at_ms"],
    )
    assert body.hex() == case["signed_body_hex"]


@pytest.mark.parametrize("case", _VEC["device_links"], ids=lambda c: c["device_id"])
def test_device_link_deterministic_signature(case: dict) -> None:
    identity_private = bytes.fromhex(_VEC["identity_private"])
    link = device_link_create(
        case["device_id"],
        bytes.fromhex(case["device_public_key"]),
        case["issued_at_ms"],
        identity_private,
    )
    assert link.signature.hex() == case["signature_hex"]
    assert device_link_serialize(link).hex() == case["serialized_hex"]


@pytest.mark.parametrize("case", _VEC["device_links"], ids=lambda c: c["device_id"])
def test_device_link_verify(case: dict) -> None:
    link = device_link_deserialize(bytes.fromhex(case["serialized_hex"]))
    identity_public = bytes.fromhex(_VEC["identity_public"])
    wrong_public = bytes.fromhex(_VEC["wrong_identity_public"])

    assert device_link_verify(link, identity_public) is True
    assert device_link_verify(link, wrong_public) is False


@pytest.mark.parametrize("case", _VEC["device_links"], ids=lambda c: c["device_id"])
def test_device_link_deserialize_roundtrip(case: dict) -> None:
    data = bytes.fromhex(case["serialized_hex"])
    link = device_link_deserialize(data)
    assert link.device_id == case["device_id"]
    assert link.device_public_key == bytes.fromhex(case["device_public_key"])
    assert link.issued_at_ms == case["issued_at_ms"]
    assert link.signature.hex() == case["signature_hex"]
    # Re-serializing the parsed link reproduces the exact bytes.
    assert device_link_serialize(link).hex() == case["serialized_hex"]
