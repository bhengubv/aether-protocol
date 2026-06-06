# SPDX-License-Identifier: MIT

"""Tests for PacketSigningService — sign / verify / nonce dedup behaviour.

Regression coverage for the (source, nonce) dedup keying. The dedup
MUST NOT key by nonce alone — see the class-level docstring on
``PacketSigningService`` for the full rationale. These tests pin that
behaviour so it cannot silently regress to the older nonce-only keying
that introduced cross-sender drop and pre-registration attacks.
"""

from __future__ import annotations

import hashlib
import time
from datetime import datetime, timedelta

import pytest

from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.security.ed25519_service import Ed25519SigningService
from aethermesh.security.packet_signing import PacketSigningService


# ─── helpers ───────────────────────────────────────────────────────────


def _make_packet(
    source_uhid: str,
    nonce: bytes = b"\x01\x02\x03\x04\x05\x06\x07\x08",
    payload: bytes = b"hello",
) -> MeshPacket:
    """Build a MeshPacket with a deterministic nonce for tests."""
    return MeshPacket(
        type=PacketType.Data,
        source_uhid=source_uhid,
        destination_uhid="bob",
        payload=payload,
        packet_nonce=nonce,
        timestamp_ms=int(time.time() * 1000),
        protocol_version=2,
    )


def _signed(svc: PacketSigningService, pkt: MeshPacket, priv: bytes) -> MeshPacket:
    svc.sign_packet(pkt, priv)
    return pkt


# ─── round trip ────────────────────────────────────────────────────────


def test_sign_and_verify_round_trip():
    priv, pub = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()

    pkt = _make_packet("alice")
    svc.sign_packet(pkt, priv)
    assert len(pkt.signature) == 64
    assert svc.verify_packet(pkt, pub) is True


def test_verify_fails_on_tampered_payload():
    priv, pub = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()

    pkt = _make_packet("alice", payload=b"original")
    svc.sign_packet(pkt, priv)
    pkt.payload = b"tampered"

    assert svc.verify_packet(pkt, pub) is False


# ─── (source, nonce) dedup keying ──────────────────────────────────────


def test_same_sender_same_nonce_is_dropped_as_replay():
    """The legitimate replay-detection case: a single sender reusing its
    own nonce within the freshness window MUST be dropped.
    """
    priv, pub = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()

    nonce = b"\x10" * 8
    pkt1 = _make_packet("alice", nonce=nonce)
    svc.sign_packet(pkt1, priv)
    assert svc.verify_packet(pkt1, pub) is True

    # Build a SECOND packet with the same source and nonce. The signature
    # over its (different) payload is independently valid.
    pkt2 = _make_packet("alice", nonce=nonce, payload=b"different")
    svc.sign_packet(pkt2, priv)
    # Signature is fine, but the dedup must reject it.
    assert svc.verify_packet(pkt2, pub) is False


def test_different_senders_with_same_nonce_both_succeed():
    """The Tier-1 interop fix: two DIFFERENT senders happening to pick
    the same 8-byte random nonce must NOT collide. Keying by nonce
    alone (the broken pre-fix behaviour) would have dropped the second
    one.
    """
    priv_a, pub_a = Ed25519SigningService.generate_keypair()
    priv_b, pub_b = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()

    shared_nonce = b"\xaa" * 8

    pkt_alice = _make_packet("alice", nonce=shared_nonce, payload=b"from-alice")
    svc.sign_packet(pkt_alice, priv_a)
    assert svc.verify_packet(pkt_alice, pub_a) is True

    pkt_bob = _make_packet("bob", nonce=shared_nonce, payload=b"from-bob")
    svc.sign_packet(pkt_bob, priv_b)
    # Bob's packet must NOT be flagged as a replay just because its
    # nonce happens to match Alice's.
    assert svc.verify_packet(pkt_bob, pub_b) is True


def test_pre_registered_nonce_does_not_block_legitimate_sender():
    """Threat model: an attacker who can talk to Bob first registers
    a nonce N against him, intending to block any future legitimate
    sender that happens to roll the same N. With (source, nonce)
    keying, the legitimate sender uses a DIFFERENT key in the cache,
    so this attack does not work.
    """
    priv_attacker, pub_attacker = Ed25519SigningService.generate_keypair()
    priv_legit, pub_legit = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()

    target_nonce = b"\xff" * 8

    # Step 1: attacker registers the nonce against Bob.
    attack_pkt = _make_packet("attacker", nonce=target_nonce, payload=b"poison")
    svc.sign_packet(attack_pkt, priv_attacker)
    assert svc.verify_packet(attack_pkt, pub_attacker) is True

    # Step 2: legitimate sender's first packet happens to roll the same
    # nonce. With nonce-only keying it would be dropped; with (source,
    # nonce) keying it must succeed.
    legit_pkt = _make_packet("legitimate", nonce=target_nonce, payload=b"first-msg")
    svc.sign_packet(legit_pkt, priv_legit)
    assert svc.verify_packet(legit_pkt, pub_legit) is True


def test_dedup_cache_key_shape_is_tuple_of_str_and_bytes():
    """White-box check: the cache MUST be keyed by ``(sender_uhid, nonce)``
    tuples — not by nonce alone, not by hex-encoded composite, not by
    anything else. Pin the storage format.
    """
    priv, pub = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()

    nonce = b"\x01\x02\x03\x04\x05\x06\x07\x08"
    pkt = _make_packet("alice", nonce=nonce)
    svc.sign_packet(pkt, priv)
    svc.verify_packet(pkt, pub)

    keys = list(svc._nonce_cache.keys())
    assert len(keys) == 1
    key = keys[0]
    assert isinstance(key, tuple)
    assert len(key) == 2
    assert key[0] == "alice"
    assert key[1] == nonce


def test_dedup_cache_grows_per_distinct_source_nonce_pair():
    priv, _ = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()
    pub = Ed25519SigningService.derive_public(priv) if hasattr(
        Ed25519SigningService, "derive_public"
    ) else None
    if pub is None:
        # Older signing service variants — recover pub from a known keypair.
        priv, pub = Ed25519SigningService.generate_keypair()

    # 3 distinct (source, nonce) pairs → 3 cache entries.
    for source in ("alice", "bob", "carol"):
        pkt = _make_packet(source, nonce=b"\xcc" * 8)
        svc.sign_packet(pkt, priv)
        # Each verification will fail because the priv is alice's only —
        # but the dedup record is still made BEFORE verify? No: looking
        # at the code, the order is verify-signature first, then dedup
        # check, then record. So we use a single keypair for all sources
        # and accept that the signature won't match. That means the
        # dedup record isn't made unless the signature is valid. To
        # exercise the cache directly, use the SAME priv for all by
        # giving each source the SAME public key. We can't do that
        # without redirecting — instead, just record manually.
        svc._record_nonce(source, b"\xcc" * 8)

    assert len(svc._nonce_cache) == 3


# ─── TTL / cleanup ─────────────────────────────────────────────────────


def test_expired_entries_are_removed_by_cleanup():
    svc = PacketSigningService()
    # Manually plant an expired entry.
    svc._nonce_cache[("alice", b"\x00" * 8)] = datetime.utcnow() - timedelta(seconds=1)
    svc._cleanup_expired_entries()
    assert ("alice", b"\x00" * 8) not in svc._nonce_cache


def test_max_cache_size_triggers_cleanup():
    svc = PacketSigningService(max_cache_size=3)

    # Plant 4 entries: 2 expired, 2 fresh. The 4th _record_nonce should
    # trigger _cleanup_expired_entries because cache size > max_cache_size.
    svc._nonce_cache[("a", b"1")] = datetime.utcnow() - timedelta(seconds=1)
    svc._nonce_cache[("b", b"2")] = datetime.utcnow() - timedelta(seconds=1)
    svc._nonce_cache[("c", b"3")] = datetime.utcnow() + timedelta(seconds=300)

    svc._record_nonce("d", b"4")  # triggers cleanup since len becomes 4

    # Expired entries gone; the two fresh ones remain.
    assert ("a", b"1") not in svc._nonce_cache
    assert ("b", b"2") not in svc._nonce_cache
    assert ("c", b"3") in svc._nonce_cache
    assert ("d", b"4") in svc._nonce_cache


# ─── argument validation ───────────────────────────────────────────────


def test_sign_packet_rejects_none_packet():
    svc = PacketSigningService()
    priv, _ = Ed25519SigningService.generate_keypair()
    with pytest.raises(ValueError):
        svc.sign_packet(None, priv)


def test_sign_packet_rejects_none_private_key():
    svc = PacketSigningService()
    pkt = _make_packet("alice")
    with pytest.raises(ValueError):
        svc.sign_packet(pkt, None)


def test_verify_packet_rejects_none_packet():
    svc = PacketSigningService()
    _, pub = Ed25519SigningService.generate_keypair()
    with pytest.raises(ValueError):
        svc.verify_packet(None, pub)


def test_verify_packet_rejects_none_public_key():
    svc = PacketSigningService()
    pkt = _make_packet("alice")
    with pytest.raises(ValueError):
        svc.verify_packet(pkt, None)


# ─── Item 21: PacketSigning reputation hooks ───────────────────────────


class FakeReputation:
    """Minimal stand-in for NodeReputationService to verify hook calls."""

    def __init__(self):
        self.replay_calls: list[str] = []
        self.sig_failure_calls: list[str] = []

    def record_replay_attempt(self, uhid: str) -> None:
        self.replay_calls.append(uhid)

    def record_signature_failure(self, uhid: str) -> None:
        self.sig_failure_calls.append(uhid)


def test_replay_attempt_fires_record_replay_attempt():
    """Duplicate (source, nonce) after a valid first packet calls record_replay_attempt."""
    priv, pub = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()
    rep = FakeReputation()
    svc.set_reputation(rep)

    nonce = b"\xab" * 8

    # First packet: valid, accepted — no reputation calls expected.
    pkt1 = _make_packet("alice", nonce=nonce)
    svc.sign_packet(pkt1, priv)
    assert svc.verify_packet(pkt1, pub) is True
    assert rep.replay_calls == []
    assert rep.sig_failure_calls == []

    # Second packet: same (source, nonce) — replay, must call the hook.
    pkt2 = _make_packet("alice", nonce=nonce, payload=b"replayed")
    svc.sign_packet(pkt2, priv)
    assert svc.verify_packet(pkt2, pub) is False
    assert rep.replay_calls == ["alice"]
    assert rep.sig_failure_calls == []


def test_new_nonce_does_not_fire_replay_hook():
    """A fresh (source, nonce) pair must not trigger record_replay_attempt."""
    priv, pub = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()
    rep = FakeReputation()
    svc.set_reputation(rep)

    for i in range(3):
        nonce = bytes([i]) * 8
        pkt = _make_packet("alice", nonce=nonce, payload=bytes([i]))
        svc.sign_packet(pkt, priv)
        assert svc.verify_packet(pkt, pub) is True

    assert rep.replay_calls == []
    assert rep.sig_failure_calls == []


def test_signature_failure_fires_record_signature_failure():
    """An invalid Ed25519 signature calls record_signature_failure."""
    priv, pub = Ed25519SigningService.generate_keypair()
    _, wrong_pub = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()
    rep = FakeReputation()
    svc.set_reputation(rep)

    pkt = _make_packet("mallory")
    svc.sign_packet(pkt, priv)
    # Verify with the wrong public key — signature check must fail.
    assert svc.verify_packet(pkt, wrong_pub) is False
    assert rep.sig_failure_calls == ["mallory"]
    assert rep.replay_calls == []


def test_no_reputation_service_no_error_on_replay():
    """Replay detection without a reputation service attached does not raise."""
    priv, pub = Ed25519SigningService.generate_keypair()
    svc = PacketSigningService()
    # No set_reputation call — _reputation stays None.

    nonce = b"\xcc" * 8
    pkt1 = _make_packet("alice", nonce=nonce)
    svc.sign_packet(pkt1, priv)
    assert svc.verify_packet(pkt1, pub) is True

    pkt2 = _make_packet("alice", nonce=nonce, payload=b"replay")
    svc.sign_packet(pkt2, priv)
    # Must return False (replay detected) without raising.
    assert svc.verify_packet(pkt2, pub) is False
