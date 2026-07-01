# SPDX-License-Identifier: MIT

"""Cross-language circuit-relay-v2 wire-format verifier. Serializes each input
case and asserts byte-equality with fixtures/circuit-relay/expected/<name>.bin
(the Go oracle output, committed to the repo) and round-trips every field.

Run from the python/ directory:
    python -m pytest tests/test_circuitrelay_fixture.py -q
"""

from __future__ import annotations

import json
from pathlib import Path
from uuid import UUID

import pytest

from aethernet.circuitrelay.frame import (
    MessageType,
    RelayFrame,
    Status,
    deserialize,
    serialize,
)

_NIL_UUID = UUID(int=0)


def _fixtures_dir() -> Path:
    d = Path(__file__).resolve()
    for _ in range(10):
        if (d / "fixtures" / "circuit-relay" / "inputs.json").exists():
            return d / "fixtures" / "circuit-relay"
        if d.parent == d:
            break
        d = d.parent
    raise FileNotFoundError("fixtures/circuit-relay/inputs.json not found")


def _load_inputs() -> list[dict]:
    return json.loads((_fixtures_dir() / "inputs.json").read_text(encoding="utf-8"))


def _payload_for(inp: dict) -> bytes:
    if inp.get("payload_len", 0) > 0:
        return bytes(i % 256 for i in range(inp["payload_len"]))
    return bytes.fromhex(inp.get("payload_hex", ""))


def _conn_id(inp: dict) -> UUID:
    cid = inp.get("connection_id", "")
    return UUID(cid) if cid else _NIL_UUID


def _frame_for(inp: dict) -> RelayFrame:
    return RelayFrame(
        type=MessageType(inp["type"]),
        status=Status(inp.get("status", 0)),
        source_uhid=inp.get("source_uhid", ""),
        destination_uhid=inp.get("destination_uhid", ""),
        relay_uhid=inp.get("relay_uhid", ""),
        connection_id=_conn_id(inp),
        reservation_expires_at_ms=inp.get("reservation_expires_at_ms", 0),
        limit_duration_seconds=inp.get("limit_duration_seconds", 0),
        limit_data_bytes=inp.get("limit_data_bytes", 0),
        payload=_payload_for(inp),
    )


@pytest.mark.parametrize("inp", _load_inputs(), ids=lambda x: x["name"])
def test_serialize_matches_expected(inp: dict) -> None:
    got = serialize(_frame_for(inp))
    expected = (_fixtures_dir() / "expected" / f"{inp['name']}.bin").read_bytes()
    assert got == expected


@pytest.mark.parametrize("inp", _load_inputs(), ids=lambda x: x["name"])
def test_deserialize_roundtrip(inp: dict) -> None:
    data = (_fixtures_dir() / "expected" / f"{inp['name']}.bin").read_bytes()
    f = deserialize(data)
    assert int(f.type) == inp["type"]
    assert int(f.status) == inp.get("status", 0)
    assert f.source_uhid == inp.get("source_uhid", "")
    assert f.destination_uhid == inp.get("destination_uhid", "")
    assert f.relay_uhid == inp.get("relay_uhid", "")
    assert f.connection_id == _conn_id(inp)
    assert f.reservation_expires_at_ms == inp.get("reservation_expires_at_ms", 0)
    assert f.limit_duration_seconds == inp.get("limit_duration_seconds", 0)
    assert f.limit_data_bytes == inp.get("limit_data_bytes", 0)
    assert f.payload == _payload_for(inp)


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
