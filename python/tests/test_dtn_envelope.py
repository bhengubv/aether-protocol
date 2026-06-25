# SPDX-License-Identifier: MIT

"""Cross-language DTN-envelope wire-format verifier. Serializes each input case
and asserts byte-equality with fixtures/dtn/expected/<name>.bin (the Go oracle
output, committed to the repo) and round-trips every field."""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path
from uuid import UUID

import pytest

from aethernet.models import BundlePriority, BundleStatus, DtnBundle
from aethernet.dtn.envelope import (
    deserialize_bundle,
    deserialize_custody_ack,
    deserialize_delivery_receipt,
    serialize_bundle,
    serialize_custody_ack,
    serialize_delivery_receipt,
)


def _fixtures_dir() -> Path:
    d = Path(__file__).resolve()
    for _ in range(10):
        if (d / "fixtures" / "dtn" / "inputs.json").exists():
            return d / "fixtures" / "dtn"
        if d.parent == d:
            break
        d = d.parent
    raise FileNotFoundError("fixtures/dtn/inputs.json not found")


def _load_inputs() -> list[dict]:
    return json.loads((_fixtures_dir() / "inputs.json").read_text(encoding="utf-8"))


def _payload_for(inp: dict) -> bytes:
    if inp.get("encrypted_payload_len", 0) > 0:
        return bytes(i % 256 for i in range(inp["encrypted_payload_len"]))
    return bytes.fromhex(inp.get("encrypted_payload_hex", ""))


def _dt(ms: int) -> datetime:
    return datetime.utcfromtimestamp(ms / 1000)


def _serialize(inp: dict) -> bytes:
    kind = inp["kind"]
    if kind == "bundle":
        bundle = DtnBundle(
            id=UUID(inp["id"]),
            sender_uhid=inp.get("sender_uhid", ""),
            recipient_uhid=inp.get("recipient_uhid", ""),
            encrypted_payload=_payload_for(inp),
            priority=BundlePriority(inp.get("priority", 0)),
            status=BundleStatus(inp.get("status", 0)),
            copy_count=inp.get("copy_count", 0),
            max_copies=inp.get("max_copies", 0),
            sender_geohash=inp.get("sender_geohash"),
            recipient_last_geohash=inp.get("recipient_last_geohash"),
            hop_count=inp.get("hop_count", 0),
            created_at=_dt(inp["created_at_ms"]),
            expires_at=_dt(inp["expires_at_ms"]),
        )
        return serialize_bundle(bundle)
    if kind == "custody_ack":
        return serialize_custody_ack(UUID(inp["bundle_id"]), inp.get("accepted", False))
    if kind == "delivery_receipt":
        return serialize_delivery_receipt(
            UUID(inp["bundle_id"]),
            inp.get("recipient_uhid", ""),
            inp.get("total_hops", 0),
            inp.get("total_custody_transfers", 0),
            inp["delivered_at_ms"],
        )
    raise ValueError(f"unknown kind {kind}")


def _ms(dt: datetime) -> int:
    return int(dt.replace(tzinfo=timezone.utc).timestamp() * 1000)


@pytest.mark.parametrize("inp", _load_inputs(), ids=lambda x: x["name"])
def test_serialize_matches_expected(inp: dict) -> None:
    got = _serialize(inp)
    expected = (_fixtures_dir() / "expected" / f"{inp['name']}.bin").read_bytes()
    assert got == expected


@pytest.mark.parametrize("inp", _load_inputs(), ids=lambda x: x["name"])
def test_deserialize_roundtrip(inp: dict) -> None:
    data = (_fixtures_dir() / "expected" / f"{inp['name']}.bin").read_bytes()
    kind = inp["kind"]
    if kind == "bundle":
        b = deserialize_bundle(data)
        assert str(b.id) == inp["id"]
        assert int(b.priority) == inp["priority"]
        assert int(b.status) == inp["status"]
        assert b.copy_count == inp["copy_count"]
        assert b.max_copies == inp["max_copies"]
        assert b.hop_count == inp["hop_count"]
        assert _ms(b.created_at) == inp["created_at_ms"]
        assert _ms(b.expires_at) == inp["expires_at_ms"]
        assert b.sender_uhid == inp.get("sender_uhid", "")
        assert b.recipient_uhid == inp.get("recipient_uhid", "")
        assert (b.sender_geohash or "") == (inp.get("sender_geohash") or "")
        assert (b.recipient_last_geohash or "") == (inp.get("recipient_last_geohash") or "")
        assert b.encrypted_payload == _payload_for(inp)
    elif kind == "custody_ack":
        bundle_id, accepted = deserialize_custody_ack(data)
        assert str(bundle_id) == inp["bundle_id"]
        assert accepted == inp.get("accepted", False)
    else:
        bundle_id, recipient, hops, transfers, delivered = deserialize_delivery_receipt(data)
        assert str(bundle_id) == inp["bundle_id"]
        assert recipient == inp.get("recipient_uhid", "")
        assert hops == inp["total_hops"]
        assert transfers == inp["total_custody_transfers"]
        assert delivered == inp["delivered_at_ms"]
