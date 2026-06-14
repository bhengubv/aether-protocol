# SPDX-License-Identifier: MIT

"""Cross-language tipping parity verifier.

Mirrors ../fixtures/tipping/tip_packet_basic.json — the canonical cross-language parity source
generated from the C# reference (TipPacketPayload.BuildCanonicalData + Ed25519). Every language port
MUST reproduce canonical_bytes and signature byte-for-byte. Mirrors the Go
incentive/tip_packet_fixture_test.go suite.

Run from the python/ directory:
    python -m pytest tests/test_tipping_fixture.py
"""

from __future__ import annotations

import json
from pathlib import Path
from uuid import UUID

import nacl.signing
import pytest

from aethernet.incentive.mesh_tip_service import MeshTipService, NoopMeshTipSettlementProvider
from aethernet.incentive.tip_packet_payload import TipPacketPayload
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


def _fixtures_dir() -> Path:
    # python/tests/test_tipping_fixture.py -> python/tests/.. -> aether-protocol/
    return Path(__file__).resolve().parent.parent.parent / "fixtures"


def _load_vectors() -> dict:
    with (_fixtures_dir() / "tipping" / "tip_packet_basic.json").open(encoding="utf-8") as fp:
        return json.load(fp)


def _case_to_payload(c: dict) -> TipPacketPayload:
    """Reconstruct a TipPacketPayload from a fixture case (without the signature)."""
    return TipPacketPayload(
        tipper_uhid=c["tipper_uhid"],
        recipient_uhid=c["recipient_uhid"],
        amount=c["amount"],
        traffic_type=c["traffic_type"],
        reference_id=UUID(c["reference_id"]) if c["reference_id"] else None,
        timestamp_unix_ms=c["timestamp_unix_ms"],
    )


VECTORS = _load_vectors()
CASES = VECTORS["cases"]


def test_tip_canonical_bytes_parity():
    """build_canonical_data reproduces the fixture canonical_bytes byte-for-byte for every case
    (covers null reference_id -> 16 zero bytes, and the .NET mixed-endian GUID byte order)."""
    for c in CASES:
        got = _case_to_payload(c).build_canonical_data().hex()
        assert got == c["canonical_bytes"], (
            f"{c['tipper_uhid']}: canonical bytes mismatch\n got={got}\nwant={c['canonical_bytes']}"
        )


def test_tip_null_reference_id_is_sixteen_zero_bytes():
    """The null-reference_id case must serialise the GUID slot as exactly 16 zero bytes (.NET
    Guid.Empty)."""
    null_case = next(c for c in CASES if c["reference_id"] is None)
    canonical = _case_to_payload(null_case).build_canonical_data()
    # The 16-byte GUID slot sits immediately before the trailing 8-byte timestamp.
    guid_slot = canonical[-24:-8]
    assert guid_slot == b"\x00" * 16


def test_tip_signature_deterministic_parity():
    """A fresh Ed25519 sign from the fixture seed reproduces the fixture signature exactly (Ed25519 is
    deterministic), and the fixture signature verifies against the fixture public key."""
    seed = bytes.fromhex(VECTORS["ed25519_seed"])
    assert len(seed) == 32
    signing_key = nacl.signing.SigningKey(seed)
    verify_key = signing_key.verify_key

    # The derived public key must match the fixture's published key.
    assert bytes(verify_key).hex() == VECTORS["public_key"]

    for c in CASES:
        canonical = _case_to_payload(c).build_canonical_data()

        # Deterministic re-sign reproduces the exact fixture signature.
        sig = signing_key.sign(canonical).signature
        assert sig.hex() == c["signature"], (
            f"{c['tipper_uhid']}: signature mismatch\n got={sig.hex()}\nwant={c['signature']}"
        )

        # The fixture signature verifies against the fixture public key.
        verify_key.verify(canonical, bytes.fromhex(c["signature"]))  # raises on failure


def test_tip_payload_json_round_trip():
    """A signed payload survives a JSON round-trip with canonical bytes, signature, amount, and
    reference_id nullity intact."""
    seed = bytes.fromhex(VECTORS["ed25519_seed"])
    signing_key = nacl.signing.SigningKey(seed)

    for c in CASES:
        p = _case_to_payload(c)
        p.signature = signing_key.sign(p.build_canonical_data()).signature

        back = TipPacketPayload.from_json(p.to_json())

        assert back.build_canonical_data() == p.build_canonical_data()
        assert back.signature == p.signature
        assert back.amount == c["amount"]
        assert (back.reference_id is None) == (p.reference_id is None)
        if p.reference_id is not None:
            assert back.reference_id == p.reference_id


# ── service-level dispatch (MeshTipService) ───────────────────────────────────


class _FakeSender:
    def __init__(self, local: str) -> None:
        self._local = local
        self.sent: list[MeshPacket] = []
        self.broadcasts: list[MeshPacket] = []

    @property
    def local_uhid(self) -> str:
        return self._local

    def send(self, packet: MeshPacket, next_hop: str) -> bool:
        self.sent.append(packet)
        return True

    def broadcast(self, packet: MeshPacket) -> int:
        self.broadcasts.append(packet)
        return 1


class _FakeSigner:
    def sign_packet(self, packet: MeshPacket) -> MeshPacket:
        packet.signature = b"envelope-sig"
        packet.packet_nonce = bytes([1, 2, 3, 4, 5, 6, 7, 8])
        return packet


class _SeedIdentity:
    def __init__(self, seed: bytes) -> None:
        self._sk = nacl.signing.SigningKey(seed)

    def sign_data(self, data: bytes) -> bytes:
        return self._sk.sign(data).signature


class _RecordingSettler:
    def __init__(self) -> None:
        self.calls: list[TipPacketPayload] = []

    def settle_mesh_tip(self, payload: TipPacketPayload) -> None:
        self.calls.append(payload)


def test_send_tip_produces_fixture_signature():
    """Wires the full MeshTipService send path with the fixture seed and confirms the signed payload
    inside the emitted TipPacket(24) carries the exact fixture signature — proving the service-level
    flow is byte-identical to C#."""
    seed = bytes.fromhex(VECTORS["ed25519_seed"])
    c = CASES[0]
    sender = _FakeSender(c["tipper_uhid"])
    svc = MeshTipService(sender, _FakeSigner(), _SeedIdentity(seed), routing=None, settle=None)

    ref = UUID(c["reference_id"])
    signed = svc.send_tip(
        c["recipient_uhid"], c["amount"], c["traffic_type"], ref, c["timestamp_unix_ms"]
    )
    assert signed.type == PacketType.TipPacket

    payload = TipPacketPayload.from_json(signed.payload)
    assert payload.signature is not None
    assert payload.signature.hex() == c["signature"], (
        f"service-emitted signature mismatch\n got={payload.signature.hex()}\nwant={c['signature']}"
    )

    # With no route resolver, the tip must have been broadcast.
    assert len(sender.broadcasts) == 1
    assert len(sender.sent) == 0


def test_handle_tip_packet_routes_to_settlement_hook():
    """An inbound TipPacket(24) is dispatched to the host settlement hook (the Python analog of
    IAetherNetIncentiveProvider.SettleMeshTip); a packet with a malformed signature is dropped before
    the hook fires."""
    seed = bytes.fromhex(VECTORS["ed25519_seed"])
    signing_key = nacl.signing.SigningKey(seed)
    c = CASES[0]

    # Local node is the addressed recipient, so no onward relay happens.
    sender = _FakeSender(c["recipient_uhid"])
    settler = _RecordingSettler()
    svc = MeshTipService(sender, _FakeSigner(), _SeedIdentity(seed), routing=None, settle=settler)

    # Build a well-formed, signed tip payload.
    p = _case_to_payload(c)
    p.signature = signing_key.sign(p.build_canonical_data()).signature
    pkt = MeshPacket(
        type=PacketType.TipPacket,
        source_uhid=c["tipper_uhid"],
        destination_uhid=c["recipient_uhid"],
        payload=p.to_json(),
    )

    assert svc.handle_tip_packet(pkt) is True
    assert len(settler.calls) == 1
    assert settler.calls[0].tipper_uhid == c["tipper_uhid"]

    # A malformed signature (wrong length) must be dropped before the hook fires.
    settler.calls.clear()
    p.signature = bytes([0x00, 0x01, 0x02])
    bad_pkt = MeshPacket(
        type=PacketType.TipPacket,
        source_uhid=c["tipper_uhid"],
        destination_uhid=c["recipient_uhid"],
        payload=p.to_json(),
    )
    assert svc.handle_tip_packet(bad_pkt) is False
    assert len(settler.calls) == 0


def test_noop_settlement_provider():
    """The default no-op settles nothing without error."""
    assert NoopMeshTipSettlementProvider().settle_mesh_tip(TipPacketPayload()) is None


def test_handle_tip_packet_relays_when_not_destination():
    """When the local node is NOT the addressed recipient, the handler relays the packet onward
    (broadcast with no route resolver) after settling."""
    seed = bytes.fromhex(VECTORS["ed25519_seed"])
    signing_key = nacl.signing.SigningKey(seed)
    c = CASES[0]

    # Local node is an intermediate relay (neither tipper nor recipient).
    sender = _FakeSender("aether:node:relay")
    settler = _RecordingSettler()
    svc = MeshTipService(sender, _FakeSigner(), _SeedIdentity(seed), routing=None, settle=settler)

    p = _case_to_payload(c)
    p.signature = signing_key.sign(p.build_canonical_data()).signature
    pkt = MeshPacket(
        type=PacketType.TipPacket,
        source_uhid=c["tipper_uhid"],
        destination_uhid=c["recipient_uhid"],
        ttl=7,
        payload=p.to_json(),
    )

    assert svc.handle_tip_packet(pkt) is True
    assert len(settler.calls) == 1
    # Relayed onward toward the recipient.
    assert len(sender.broadcasts) == 1


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
