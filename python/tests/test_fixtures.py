# SPDX-License-Identifier: MIT

"""Cross-language wire-format fixture verifier.

Reads ../fixtures/inputs.json and ../fixtures/expected/*.bin and asserts that
this language's PacketSerializer produces identical bytes for each canonical
input. See fixtures/README.md.

Run from the python/ directory:
    python -m unittest tests.test_fixtures
"""

from __future__ import annotations

import json
import os
import unittest
from pathlib import Path
from uuid import UUID

from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.protocol.serializer import PacketSerializer


def _fixtures_dir() -> Path:
    here = Path(__file__).resolve()
    # python/tests/test_fixtures.py → python/tests/.. → aether-protocol/
    return here.parent.parent.parent / "fixtures"


def _load_inputs() -> list[dict]:
    with (_fixtures_dir() / "inputs.json").open(encoding="utf-8") as fp:
        return json.load(fp)


def _packet_from(input_dict: dict) -> MeshPacket:
    return MeshPacket(
        id=UUID(input_dict["id"]),
        type=PacketType(input_dict["type"]),
        source_uhid=input_dict["source_uhid"],
        destination_uhid=input_dict["destination_uhid"],
        ttl=input_dict["ttl"],
        priority=input_dict["priority"],
        payload=bytes.fromhex(input_dict["payload_hex"]),
        packet_nonce=bytes.fromhex(input_dict["packet_nonce_hex"]),
        signature=bytes.fromhex(input_dict["signature_hex"]),
        timestamp_ms=input_dict["timestamp_ms"],
        protocol_version=input_dict["protocol_version"],
    )


class FixtureTests(unittest.TestCase):
    def test_serialize_matches_expected_bytes(self):
        for case in _load_inputs():
            with self.subTest(name=case["name"]):
                pkt = _packet_from(case)
                got = PacketSerializer.serialize(pkt)
                expected_path = _fixtures_dir() / "expected" / f"{case['name']}.bin"
                expected = expected_path.read_bytes()
                self.assertEqual(
                    got, expected,
                    f"{case['name']}: bytes diverge — see fixtures/README.md",
                )

    def test_deserialize_from_expected_matches_input_fields(self):
        for case in _load_inputs():
            with self.subTest(name=case["name"]):
                expected_path = _fixtures_dir() / "expected" / f"{case['name']}.bin"
                got = PacketSerializer.deserialize(expected_path.read_bytes())

                self.assertEqual(got.id, UUID(case["id"]))
                self.assertEqual(got.type, PacketType(case["type"]))
                self.assertEqual(got.source_uhid, case["source_uhid"])
                self.assertEqual(got.destination_uhid, case["destination_uhid"])
                self.assertEqual(got.ttl, case["ttl"])
                self.assertEqual(got.priority, case["priority"])
                self.assertEqual(got.payload, bytes.fromhex(case["payload_hex"]))
                self.assertEqual(got.packet_nonce, bytes.fromhex(case["packet_nonce_hex"]))
                self.assertEqual(got.signature, bytes.fromhex(case["signature_hex"]))
                self.assertEqual(got.timestamp_ms, case["timestamp_ms"])
                self.assertEqual(got.protocol_version, case["protocol_version"])


if __name__ == "__main__":
    unittest.main()
