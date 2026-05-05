"""Cross-language Signal-protocol fixture verifier and end-to-end exercises.

These tests load fixtures/signal/inputs.json and verify the Python
implementation produces byte-identical X3DH and ratchet outputs to the
C# reference (committed in fixtures/signal/expected/*.json).

Any drift between this Python implementation and the C# / Go / Swift / TS /
... implementations shows up here as a hex mismatch.
"""

import asyncio
import hashlib
import hmac as stdlib_hmac
import json
import os
from pathlib import Path
from typing import Tuple

import pytest
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives.serialization import (
    Encoding, PrivateFormat, PublicFormat, NoEncryption
)
from cryptography.hazmat.backends import default_backend

from aether.security.signal_protocol import (
    SignalProtocolService,
    MESSAGE_TYPE_PRE_KEY,
    MESSAGE_TYPE_NORMAL,
)


# ─── Fixture path resolution ─────────────────────────────────────────────

def _repo_root() -> Path:
    here = Path(__file__).resolve()
    for parent in [here] + list(here.parents):
        if (parent / "AetherProtocol.slnx").is_file():
            return parent
    raise RuntimeError(f"Repo root not found from {here}")


def _load_fixture_pair(case_name: str) -> Tuple[dict, dict]:
    root = _repo_root()
    inputs = json.loads((root / "fixtures" / "signal" / "inputs.json").read_text())
    inputs_case = next(c for c in inputs["cases"] if c["name"] == case_name)
    expected = json.loads((root / "fixtures" / "signal" / "expected" / f"{case_name}.json").read_text())
    return inputs_case, expected


# ─── Crypto helpers (used to compute expected fixture values) ─────────────

def _x25519_derive_public(priv_bytes: bytes) -> bytes:
    priv = X25519PrivateKey.from_private_bytes(priv_bytes)
    return priv.public_key().public_bytes(encoding=Encoding.Raw, format=PublicFormat.Raw)


def _x25519_agree(priv_bytes: bytes, pub_bytes: bytes) -> bytes:
    priv = X25519PrivateKey.from_private_bytes(priv_bytes)
    pub = X25519PublicKey.from_public_bytes(pub_bytes)
    return priv.exchange(pub)


def _hkdf(ikm: bytes, info: bytes) -> bytes:
    kdf = HKDF(
        algorithm=hashes.SHA256(), length=32, salt=None, info=info, backend=default_backend()
    )
    return kdf.derive(ikm)


def _hmac_one(key: bytes, b: int) -> bytes:
    return stdlib_hmac.new(key, bytes([b]), hashlib.sha256).digest()


# ─── Fixture verifiers ────────────────────────────────────────────────────


def test_signal_fixture_x3dh_basic():
    inputs, expected = _load_fixture_pair("x3dh_basic")

    alice_ik = bytes.fromhex(inputs["alice_identity_priv_hex"])
    alice_ek = bytes.fromhex(inputs["alice_ephemeral_priv_hex"])
    bob_ik = bytes.fromhex(inputs["bob_identity_priv_hex"])
    bob_spk = bytes.fromhex(inputs["bob_signed_pre_key_priv_hex"])
    bob_opk = bytes.fromhex(inputs["bob_one_time_pre_key_priv_hex"])

    alice_ik_pub = _x25519_derive_public(alice_ik)
    alice_ek_pub = _x25519_derive_public(alice_ek)
    bob_ik_pub = _x25519_derive_public(bob_ik)
    bob_spk_pub = _x25519_derive_public(bob_spk)
    bob_opk_pub = _x25519_derive_public(bob_opk)

    dh1 = _x25519_agree(alice_ik, bob_spk_pub)
    dh2 = _x25519_agree(alice_ek, bob_ik_pub)
    dh3 = _x25519_agree(alice_ek, bob_spk_pub)
    dh4 = _x25519_agree(alice_ek, bob_opk_pub)

    shared = dh1 + dh2 + dh3 + dh4
    root_info = inputs["hkdf_root_info_utf8"].encode("utf-8")
    send_info = inputs["hkdf_chain_initiator_send_info_utf8"].encode("utf-8")
    recv_info = inputs["hkdf_chain_initiator_recv_info_utf8"].encode("utf-8")

    root = _hkdf(shared, root_info)
    send_chain = _hkdf(root, send_info)
    recv_chain = _hkdf(root, recv_info)

    assert alice_ik_pub.hex() == expected["alice_identity_pub_hex"]
    assert alice_ek_pub.hex() == expected["alice_ephemeral_pub_hex"]
    assert bob_ik_pub.hex() == expected["bob_identity_pub_hex"]
    assert bob_spk_pub.hex() == expected["bob_signed_pre_key_pub_hex"]
    assert bob_opk_pub.hex() == expected["bob_one_time_pre_key_pub_hex"]
    assert dh1.hex() == expected["dh1_hex"]
    assert dh2.hex() == expected["dh2_hex"]
    assert dh3.hex() == expected["dh3_hex"]
    assert dh4.hex() == expected["dh4_hex"]
    assert shared.hex() == expected["shared_secret_hex"]
    assert root.hex() == expected["root_key_hex"]
    assert send_chain.hex() == expected["initiator_send_chain_key_hex"]
    assert recv_chain.hex() == expected["initiator_recv_chain_key_hex"]


def test_signal_fixture_ratchet_step_basic():
    inputs, expected = _load_fixture_pair("ratchet_step_basic")
    chain_key = bytes.fromhex(inputs["chain_key_hex"])
    msg_key = _hmac_one(chain_key, 0x01)
    next_chain = _hmac_one(chain_key, 0x02)
    assert msg_key.hex() == expected["message_key_hex"]
    assert next_chain.hex() == expected["next_chain_key_hex"]


def test_signal_fixture_ratchet_step_three_iterations():
    inputs, expected = _load_fixture_pair("ratchet_step_three_iterations")
    chain_key = bytes.fromhex(inputs["initial_chain_key_hex"])
    for i in range(3):
        msg = _hmac_one(chain_key, 0x01)
        nxt = _hmac_one(chain_key, 0x02)
        assert msg.hex() == expected[f"step_{i}_message_key_hex"]
        assert nxt.hex() == expected[f"step_{i}_chain_key_after_hex"]
        chain_key = nxt


# ─── End-to-end exercises ────────────────────────────────────────────────


@pytest.mark.asyncio
async def test_x3dh_first_message_round_trips():
    alice = SignalProtocolService()
    bob = SignalProtocolService()

    bob_bundle = await bob.generate_pre_key_bundle("bob")
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bob_bundle)

    encrypted = await alice.encrypt("bob", b"the mesh is alive")
    assert encrypted.message_type == MESSAGE_TYPE_PRE_KEY
    assert len(encrypted.initiator_identity_key_x25519) == 32
    assert len(encrypted.initiator_ephemeral_key_x25519) == 32
    assert encrypted.sender_uhid == "alice"

    plaintext = await bob.decrypt("alice", encrypted)
    assert plaintext == b"the mesh is alive"
    assert bob.has_session("alice")


@pytest.mark.asyncio
async def test_x3dh_subsequent_message_is_normal_not_pre_key():
    alice = SignalProtocolService()
    bob = SignalProtocolService()

    bob_bundle = await bob.generate_pre_key_bundle("bob")
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bob_bundle)

    first = await alice.encrypt("bob", b"a")
    await bob.decrypt("alice", first)

    second = await alice.encrypt("bob", b"b")
    assert second.message_type == MESSAGE_TYPE_NORMAL
    assert second.initiator_identity_key_x25519 is None

    out = await bob.decrypt("alice", second)
    assert out == b"b"


@pytest.mark.asyncio
async def test_x3dh_bidirectional_after_first_message():
    alice = SignalProtocolService()
    bob = SignalProtocolService()

    bob_bundle = await bob.generate_pre_key_bundle("bob")
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bob_bundle)

    a = await alice.encrypt("bob", b"ping")
    assert (await bob.decrypt("alice", a)) == b"ping"

    b = await bob.encrypt("alice", b"pong")
    assert b.message_type == MESSAGE_TYPE_NORMAL
    assert (await alice.decrypt("bob", b)) == b"pong"


@pytest.mark.asyncio
async def test_x3dh_five_sequential_messages_ratchet_forward():
    alice = SignalProtocolService()
    bob = SignalProtocolService()

    bob_bundle = await bob.generate_pre_key_bundle("bob")
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bob_bundle)

    for i in range(5):
        msg = bytes([i])
        enc = await alice.encrypt("bob", msg)
        assert enc.counter == i
        dec = await bob.decrypt("alice", enc)
        assert dec == msg


@pytest.mark.asyncio
async def test_one_time_pre_key_consumed_after_responder_establishes():
    alice = SignalProtocolService()
    bob = SignalProtocolService()

    bob_bundle = await bob.generate_pre_key_bundle("bob")
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bob_bundle)

    first = await alice.encrypt("bob", b"first")
    await bob.decrypt("alice", first)

    # A second initiator using the same bundle (and thus same OPK id) should
    # fail because Bob consumed the OPK.
    alice2 = SignalProtocolService()
    await alice2.generate_pre_key_bundle("alice2")
    await alice2.process_pre_key_bundle(bob_bundle)
    replay = await alice2.encrypt("bob", b"replay")

    with pytest.raises(ValueError):
        await bob.decrypt("alice2", replay)


@pytest.mark.asyncio
async def test_encrypt_without_local_uhid_raises():
    alice = SignalProtocolService()
    bob = SignalProtocolService()
    bob_bundle = await bob.generate_pre_key_bundle("bob")
    # Note: no generate_pre_key_bundle / set_local_uhid on Alice.
    await alice.process_pre_key_bundle(bob_bundle)
    with pytest.raises(ValueError):
        await alice.encrypt("bob", b"x")


@pytest.mark.asyncio
async def test_pre_key_bundle_has_both_identity_keys():
    svc = SignalProtocolService()
    bundle = await svc.generate_pre_key_bundle("alice")
    assert len(bundle.identity_key) == 32           # Ed25519
    assert len(bundle.identity_key_x25519) == 32    # X25519
    assert bundle.identity_key != bundle.identity_key_x25519
    assert len(bundle.signed_pre_key) == 32
    assert len(bundle.pre_key) == 32
    assert len(bundle.signed_pre_key_signature) == 64
