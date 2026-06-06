# SPDX-License-Identifier: MIT

"""Persistence tests for SignalProtocolService identity + pre-key state.

Mirrors the C# ``PreKeyPersistenceTests``: long-term identity keys, the
active signed-pre-key, and the OPK pool all survive a process restart
when wired up to a :class:`PreKeyStore`. Without persistence, every
restart would regenerate identity keys and invalidate every outstanding
bundle ever published for this node.
"""

from __future__ import annotations

import asyncio

import pytest

from aethernet.security import SignalProtocolService
from aethernet.security.session_store import KeyValueSignalSessionStore
from aethernet.security.pre_key_store import KeyValuePreKeyStore
from aethernet.storage import InMemoryKeyValueStore, KeyValueStore


_LOCAL_UHID = "uhid-prekey-persist"


def _build_service(pre_key_kv: KeyValueStore) -> SignalProtocolService:
    return SignalProtocolService(
        opk_pool_size=16,
        pre_key_store=KeyValuePreKeyStore(pre_key_kv),
    )


@pytest.mark.asyncio
async def test_identity_survives_restart():
    kv = InMemoryKeyValueStore()

    first = _build_service(kv)
    ed25519_pub_before = first.get_public_key()
    x25519_pub_before = first.get_x25519_public_key()
    await asyncio.sleep(0)

    second = _build_service(kv)
    ed25519_pub_after = second.get_public_key()
    x25519_pub_after = second.get_x25519_public_key()

    assert ed25519_pub_before == ed25519_pub_after
    assert x25519_pub_before == x25519_pub_after

    # Signature round-trip through the restored Ed25519 key works.
    sig = await second.sign_data(b"identity-persistence-check")
    assert first.verify_signature(ed25519_pub_before, b"identity-persistence-check", sig)


@pytest.mark.asyncio
async def test_active_signed_pre_key_survives_restart():
    kv = InMemoryKeyValueStore()

    first = _build_service(kv)
    bundle_before = await first.generate_pre_key_bundle(_LOCAL_UHID)
    await asyncio.sleep(0)

    second = _build_service(kv)
    # Re-issuing a bundle should reuse the persisted active SPK
    # (no rotation: default rotation_interval is 7 days).
    bundle_after = await second.generate_pre_key_bundle(_LOCAL_UHID)

    assert bundle_before.signed_pre_key_id == bundle_after.signed_pre_key_id
    assert bundle_before.signed_pre_key == bundle_after.signed_pre_key
    assert bundle_before.signed_pre_key_signature == bundle_after.signed_pre_key_signature


@pytest.mark.asyncio
async def test_one_time_pre_key_pool_survives_restart():
    kv = InMemoryKeyValueStore()

    first = _build_service(kv)
    bundle1 = await first.generate_pre_key_bundle(_LOCAL_UHID)
    held_before, available_before = first.get_opk_pool_status()
    assert held_before == 16
    assert available_before == 15  # one issued in bundle1
    await asyncio.sleep(0)

    # Restart. The pool size should match — the issued OPK should still be
    # marked issued, and the un-issued ones should still be un-issued.
    # Issuing another bundle must NOT reuse bundle1's id.
    second = _build_service(kv)
    held_after, available_after = second.get_opk_pool_status()
    assert held_after == held_before
    assert available_after == available_before

    bundle2 = await second.generate_pre_key_bundle(_LOCAL_UHID)
    assert bundle1.pre_key_id != bundle2.pre_key_id


@pytest.mark.asyncio
async def test_responder_session_across_restart_consumes_opk():
    """Bob persists. Alice initiates against Bob's bundle, then Bob
    restarts BEFORE the PreKey message arrives. After restart, the OPK
    that was reserved for Alice is still in Bob's pool — so X3DH should
    still complete on Bob's side.

    A second restart sees the OPK gone — the persistent consume on
    successful X3DH means the slot is freed.
    """
    bob_pre_key_kv = InMemoryKeyValueStore()
    bob_session_kv = InMemoryKeyValueStore()

    bob = SignalProtocolService(
        opk_pool_size=16,
        session_store=KeyValueSignalSessionStore(bob_session_kv),
        pre_key_store=KeyValuePreKeyStore(bob_pre_key_kv),
    )
    bob_bundle = await bob.generate_pre_key_bundle("bob-uhid-x3dh-restart")
    await asyncio.sleep(0)

    alice = SignalProtocolService()
    await alice.generate_pre_key_bundle("alice-uhid-x3dh-restart")
    await alice.process_pre_key_bundle(bob_bundle)
    msg = await alice.encrypt("bob-uhid-x3dh-restart", b"hello-after-restart")

    # Bob "restarts" — fresh service over the SAME stores.
    bob_restarted = SignalProtocolService(
        opk_pool_size=16,
        session_store=KeyValueSignalSessionStore(bob_session_kv),
        pre_key_store=KeyValuePreKeyStore(bob_pre_key_kv),
    )
    plain = await bob_restarted.decrypt("alice-uhid-x3dh-restart", msg)
    assert plain == b"hello-after-restart"
    await asyncio.sleep(0)

    # Replay attempt: a second restart with the same stored OPK pool
    # should NOT have the consumed OPK any more — the OPK was deleted
    # from the persistent store on consumption.
    bob_restarted_again = SignalProtocolService(
        opk_pool_size=16,
        session_store=KeyValueSignalSessionStore(bob_session_kv),
        pre_key_store=KeyValuePreKeyStore(bob_pre_key_kv),
    )
    held, _ = bob_restarted_again.get_opk_pool_status()
    assert held == 15  # one consumed by Alice's X3DH


@pytest.mark.asyncio
async def test_local_uhid_survives_restart():
    """Mutation of local_uhid should be persisted so a restart does not
    require a fresh ``set_local_uhid`` call before encrypting.
    """
    kv = InMemoryKeyValueStore()

    first = _build_service(kv)
    first.set_local_uhid("uhid-persisted-on-set")
    await asyncio.sleep(0)

    second = _build_service(kv)
    # The internal _local_uhid should be hydrated from the store.
    assert second._local_uhid == "uhid-persisted-on-set"


@pytest.mark.asyncio
async def test_no_pre_key_store_behaviour_unchanged():
    """Without a pre_key_store, a fresh service must regenerate identity
    keys — no cross-service leakage from anywhere on disk.
    """
    a = SignalProtocolService()
    b = SignalProtocolService()
    assert a.get_public_key() != b.get_public_key()
    assert a.get_x25519_public_key() != b.get_x25519_public_key()


@pytest.mark.asyncio
async def test_filesystem_kv_persists_across_restart(tmp_path):
    """End-to-end with the durable :class:`FileSystemKeyValueStore`.

    Verifies that the identity round-trips through actual disk I/O, not
    just the in-memory KV. ``tmp_path`` is wiped by pytest at end-of-test.
    """
    from aethernet.storage import FileSystemKeyValueStore

    kv_path = tmp_path / "prekey"
    first = SignalProtocolService(
        opk_pool_size=8,
        pre_key_store=KeyValuePreKeyStore(FileSystemKeyValueStore(str(kv_path))),
    )
    bundle1 = await first.generate_pre_key_bundle(_LOCAL_UHID)
    await asyncio.sleep(0)

    second = SignalProtocolService(
        opk_pool_size=8,
        pre_key_store=KeyValuePreKeyStore(FileSystemKeyValueStore(str(kv_path))),
    )
    bundle2 = await second.generate_pre_key_bundle(_LOCAL_UHID)
    # Identity (and SPK) round-trip — only the OPK id should differ.
    assert bundle1.identity_key == bundle2.identity_key
    assert bundle1.identity_key_x25519 == bundle2.identity_key_x25519
    assert bundle1.signed_pre_key_id == bundle2.signed_pre_key_id
    assert bundle1.pre_key_id != bundle2.pre_key_id
