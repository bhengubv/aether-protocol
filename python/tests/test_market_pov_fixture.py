# SPDX-License-Identifier: MIT

"""Cross-language Proof-of-Vicinity parity verifier.

Mirrors ../fixtures/market/pov_token_basic.json — the canonical cross-language parity source generated
from the C# reference (PoVTokenCodec.BuildSignableTokenData + Ed25519). Every language port MUST
reproduce canonical_body and witness_signature byte-for-byte. Mirrors the Go
market/pov_token_fixture_test.go suite.

Run from the python/ directory:
    python -m pytest tests/test_market_pov_fixture.py
"""

from __future__ import annotations

import json
from pathlib import Path

import nacl.signing
import pytest

from aethernet.market.pov_exchange_service import PoVTokenExchangeService
from aethernet.market.pov_token import (
    PoVToken,
    PoVTransportType,
    build_signable_token_data,
    datetime_to_ticks,
    ticks_to_datetime,
)
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


def _fixtures_dir() -> Path:
    return Path(__file__).resolve().parent.parent.parent / "fixtures"


def _load_vectors() -> dict:
    with (_fixtures_dir() / "market" / "pov_token_basic.json").open(encoding="utf-8") as fp:
        return json.load(fp)


VECTORS = _load_vectors()
CASES = VECTORS["cases"]


def test_pov_canonical_body_parity():
    """build_signable_token_data reproduces the fixture canonical_body byte-for-byte for every case
    (covers all three transports + the .NET DateTime.Ticks i64 LE field)."""
    for c in CASES:
        got = build_signable_token_data(
            c["subject_uhid"], c["timestamp_ticks"], PoVTransportType(c["transport_byte"])
        ).hex()
        assert got == c["canonical_body"], (
            f"{c['subject_uhid']}: canonical body mismatch\n got={got}\nwant={c['canonical_body']}"
        )
        # Transport enum byte must match the named transport.
        assert PoVTransportType(c["transport_byte"]).wire_name == c["transport"]


def test_pov_witness_signature_deterministic_parity():
    """A fresh Ed25519 sign from the fixture witness seed reproduces the fixture witness_signature
    exactly (Ed25519 is deterministic), and the fixture signature verifies against the fixture witness
    public key."""
    seed = bytes.fromhex(VECTORS["witness_seed"])
    assert len(seed) == 32
    signing_key = nacl.signing.SigningKey(seed)
    verify_key = signing_key.verify_key

    assert bytes(verify_key).hex() == VECTORS["witness_public_key"]

    for c in CASES:
        body = build_signable_token_data(
            c["subject_uhid"], c["timestamp_ticks"], PoVTransportType(c["transport_byte"])
        )

        sig = signing_key.sign(body).signature
        assert sig.hex() == c["witness_signature"], (
            f"{c['subject_uhid']}: witness signature mismatch\n got={sig.hex()}\nwant={c['witness_signature']}"
        )

        # The fixture witness signature verifies against the fixture public key.
        verify_key.verify(body, bytes.fromhex(c["witness_signature"]))  # raises on failure


def test_pov_token_json_round_trip():
    """A token with a witness signature survives a JSON round-trip with its canonical body intact."""
    seed = bytes.fromhex(VECTORS["witness_seed"])
    signing_key = nacl.signing.SigningKey(seed)

    for c in CASES:
        tok = PoVToken(
            witness_uhid="aether:witness:zz",
            subject_uhid=c["subject_uhid"],
            timestamp_ticks=c["timestamp_ticks"],
            transport_used=PoVTransportType(c["transport_byte"]),
        )
        tok.witness_signature = signing_key.sign(tok.signable_data()).signature

        back = PoVToken.from_json(tok.to_json())

        assert back.signable_data() == tok.signable_data()
        assert back.witness_signature == tok.witness_signature
        assert back.transport_used == tok.transport_used


def test_ticks_datetime_conversion_round_trip_microsecond():
    """The .NET ticks <-> Python datetime conversion is lossless at MICROSECOND (10-tick) resolution
    for the fixture timestamps. Python datetime cannot represent the 100ns tick precision, so the
    canonical body always uses the raw integer ticks (verified byte-identical above) and these helpers
    round-trip to within one microsecond."""
    for c in CASES:
        rt = datetime_to_ticks(ticks_to_datetime(c["timestamp_ticks"]))
        # Within 10 ticks (1 microsecond) — the datetime resolution floor.
        assert abs(rt - c["timestamp_ticks"]) < 10


# ── service-level exchange flow ───────────────────────────────────────────────


class _FakeSender:
    def __init__(self, local: str) -> None:
        self._local = local
        self.sent: list[MeshPacket] = []

    @property
    def local_uhid(self) -> str:
        return self._local

    def send(self, packet: MeshPacket, subject: str) -> bool:
        self.sent.append(packet)
        return True


class _RealIdentity:
    """Signs/verifies with real Ed25519 — the local node's identity key."""

    def __init__(self, signing_key: nacl.signing.SigningKey) -> None:
        self._sk = signing_key

    def sign_data(self, data: bytes) -> bytes:
        return self._sk.sign(data).signature

    def verify_signature(self, public_key: bytes, data: bytes, signature: bytes) -> bool:
        try:
            nacl.signing.VerifyKey(public_key).verify(data, signature)
            return True
        except Exception:  # noqa: BLE001
            return False


class _PassSigner:
    """Stamps a real Ed25519 envelope signature with the node's key and verifies fresh; replay-dedup on
    the nonce mirrors the C# IPacketSigningService contract (freshness/replay are exercised here; the
    body crypto is exercised separately)."""

    def __init__(self, signing_key: nacl.signing.SigningKey) -> None:
        self._sk = signing_key
        self._seen: set[str] = set()

    def sign_packet(self, packet: MeshPacket) -> MeshPacket:
        packet.packet_nonce = bytes([9, 9, 9, 9, 9, 9, 9, 9])
        packet.signature = self._sk.sign(
            (packet.source_uhid + ":" + packet.destination_uhid).encode("utf-8")
        ).signature
        return packet

    def verify_packet(self, packet: MeshPacket, sender_public_key: bytes) -> bool:
        key = packet.source_uhid + ":" + packet.packet_nonce.hex()
        if key in self._seen:
            return False
        self._seen.add(key)
        try:
            nacl.signing.VerifyKey(sender_public_key).verify(
                (packet.source_uhid + ":" + packet.destination_uhid).encode("utf-8"),
                packet.signature,
            )
            return True
        except Exception:  # noqa: BLE001
            return False


def test_pov_exchange_full_flow():
    """Exercises the on-mesh exchange end-to-end: the witness issues a token over packet 43; the
    subject verifies the witness Ed25519 signature, counter-signs, and records it; and BOTH signatures
    then verify against their respective keys."""
    witness_sk = nacl.signing.SigningKey.generate()
    witness_pub = bytes(witness_sk.verify_key)
    subject_sk = nacl.signing.SigningKey.generate()
    subject_pub = bytes(subject_sk.verify_key)

    witness_uhid = "aether:node:witness"
    subject_uhid = "aether:node:subject"

    # Witness side.
    w_sender = _FakeSender(witness_uhid)
    witness = PoVTokenExchangeService(w_sender, _PassSigner(witness_sk), _RealIdentity(witness_sk))

    token = witness.issue_token(subject_uhid, PoVTransportType.Ble)
    assert token is not None, "witness refused to issue a valid token"
    assert len(w_sender.sent) == 1, "expected exactly 1 directed send"

    exchange_pkt = w_sender.sent[0]
    assert exchange_pkt.type == PacketType.PoVTokenExchange
    assert exchange_pkt.ttl == 1  # one short-range hop

    # Subject side receives the witness's packet.
    s_sender = _FakeSender(subject_uhid)
    subject = PoVTokenExchangeService(s_sender, _PassSigner(subject_sk), _RealIdentity(subject_sk))

    received: list[PoVToken] = []
    subject.on_token_received = received.append

    accepted = subject.handle_token_exchange(exchange_pkt, witness_pub)
    assert accepted is True, "subject rejected a valid witness token"
    assert len(received) == 1, "on_token_received did not fire"

    # BOTH signatures must now verify over the same canonical body.
    body = received[0].signable_data()
    nacl.signing.VerifyKey(witness_pub).verify(body, received[0].witness_signature)
    nacl.signing.VerifyKey(subject_pub).verify(body, received[0].subject_signature)

    # Score reflects one unique witness for the subject.
    score = subject.get_score(subject_uhid)
    assert score.unique_witnesses == 1
    assert subject.accepted_subjects() == [subject_uhid]

    # Replaying the same packet is rejected by the signer's nonce dedup.
    replay = subject.handle_token_exchange(exchange_pkt, witness_pub)
    assert replay is False, "a replayed PoV exchange packet must be rejected"


def test_pov_exchange_rejects_self_vouch_and_remote_mint():
    """The hard invariants: no self-vouch and no non-short-range minting."""
    sk = nacl.signing.SigningKey.generate()
    sender = _FakeSender("aether:node:self")
    svc = PoVTokenExchangeService(sender, _PassSigner(sk), _RealIdentity(sk))

    # Self-vouch refused.
    assert svc.issue_token("aether:node:self", PoVTransportType.Ble) is None

    # Non-short-range refused — transport value 9 is not BLE/NFC/NearLink. Build the enum loosely since
    # 9 is not a named member.
    class _BadTransport:
        def is_short_range(self) -> bool:
            return False

        def __int__(self) -> int:
            return 9

    assert svc.issue_token("aether:node:other", _BadTransport()) is None  # type: ignore[arg-type]
    assert len(sender.sent) == 0


def test_pov_exchange_rejects_unaddressed_token():
    """A token whose subject is not the local node is ignored (we are not the one being vouched for)."""
    witness_sk = nacl.signing.SigningKey.generate()
    witness_pub = bytes(witness_sk.verify_key)

    # Witness issues a token addressed to 'someone:else'.
    w_sender = _FakeSender("aether:node:witness")
    witness = PoVTokenExchangeService(w_sender, _PassSigner(witness_sk), _RealIdentity(witness_sk))
    witness.issue_token("aether:node:someone-else", PoVTransportType.Nfc)
    pkt = w_sender.sent[0]

    # A different node (not the addressed subject) receives it.
    other_sk = nacl.signing.SigningKey.generate()
    other_sender = _FakeSender("aether:node:bystander")
    other = PoVTokenExchangeService(other_sender, _PassSigner(other_sk), _RealIdentity(other_sk))

    assert other.handle_token_exchange(pkt, witness_pub) is False


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
