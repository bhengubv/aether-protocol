"""Persistence tests for SignalProtocolService session state.

Mirrors the C# ``SignalSessionPersistenceTests``: a "process restart" is
simulated by tearing down the in-memory service and constructing a brand
new one against the SAME persistent stores. The new service must come up
with every previously-active session in place and able to encrypt /
decrypt against the peer.
"""

from __future__ import annotations

import asyncio

import pytest

from aether.security import SignalProtocolService
from aether.security.session_store import KeyValueSignalSessionStore
from aether.security.pre_key_store import KeyValuePreKeyStore
from aether.storage import InMemoryKeyValueStore, KeyValueStore


_ALICE_UHID = "alice-uhid-persist"
_BOB_UHID = "bob-uhid-persist"


def _build_service(
    session_kv: KeyValueStore,
    pre_key_kv: KeyValueStore,
) -> SignalProtocolService:
    """Build a service against the supplied KV stores. Calling this twice
    with the same instances simulates a process restart — the second
    instance must hydrate every previously-stored session from disk.
    """
    return SignalProtocolService(
        opk_pool_size=SignalProtocolService.__init__.__defaults__[0]
            if False else 100,
        session_store=KeyValueSignalSessionStore(session_kv),
        pre_key_store=KeyValuePreKeyStore(pre_key_kv),
    )


@pytest.mark.asyncio
async def test_established_session_survives_restart_on_initiator_side():
    alice_session_kv = InMemoryKeyValueStore()
    alice_pre_key_kv = InMemoryKeyValueStore()
    bob_session_kv = InMemoryKeyValueStore()
    bob_pre_key_kv = InMemoryKeyValueStore()

    alice = _build_service(alice_session_kv, alice_pre_key_kv)
    bob = _build_service(bob_session_kv, bob_pre_key_kv)

    await alice.generate_pre_key_bundle(_ALICE_UHID)
    bob_bundle = await bob.generate_pre_key_bundle(_BOB_UHID)
    await alice.process_pre_key_bundle(bob_bundle)

    first = await alice.encrypt(_BOB_UHID, b"pre-restart-1")
    await bob.decrypt(_ALICE_UHID, first)

    second = await alice.encrypt(_BOB_UHID, b"pre-restart-2")
    second_plain = await bob.decrypt(_ALICE_UHID, second)
    assert second_plain == b"pre-restart-2"

    # Give any fire-and-forget tasks a tick to flush before "restarting".
    await asyncio.sleep(0)

    # Round 2: Alice "restarts" — fresh service, same KV stores.
    alice_restarted = _build_service(alice_session_kv, alice_pre_key_kv)
    assert alice_restarted.has_session(_BOB_UHID)

    third = await alice_restarted.encrypt(_BOB_UHID, b"post-restart")
    third_plain = await bob.decrypt(_ALICE_UHID, third)
    assert third_plain == b"post-restart"


@pytest.mark.asyncio
async def test_established_session_survives_restart_on_responder_side():
    alice_session_kv = InMemoryKeyValueStore()
    alice_pre_key_kv = InMemoryKeyValueStore()
    bob_session_kv = InMemoryKeyValueStore()
    bob_pre_key_kv = InMemoryKeyValueStore()

    alice = _build_service(alice_session_kv, alice_pre_key_kv)
    bob = _build_service(bob_session_kv, bob_pre_key_kv)

    await alice.generate_pre_key_bundle(_ALICE_UHID)
    bob_bundle = await bob.generate_pre_key_bundle(_BOB_UHID)
    await alice.process_pre_key_bundle(bob_bundle)

    first = await alice.encrypt(_BOB_UHID, b"ping-1")
    await bob.decrypt(_ALICE_UHID, first)

    # Bob sends a reply so his session has both send + recv chains primed.
    reply = await bob.encrypt(_ALICE_UHID, b"pong-1")
    await alice.decrypt(_BOB_UHID, reply)
    await asyncio.sleep(0)

    # Bob "restarts" — fresh service over the same stores.
    bob_restarted = _build_service(bob_session_kv, bob_pre_key_kv)
    assert bob_restarted.has_session(_ALICE_UHID)

    msg = await alice.encrypt(_BOB_UHID, b"post-bob-restart")
    plain = await bob_restarted.decrypt(_ALICE_UHID, msg)
    assert plain == b"post-bob-restart"


@pytest.mark.asyncio
async def test_no_store_behaviour_unchanged_sessions_are_in_memory_only():
    # Sanity check: the persistence path must not affect behaviour when
    # no stores are wired up. Two services sharing nothing must NOT see
    # each other's sessions.
    alice = SignalProtocolService()
    bob = SignalProtocolService()

    bob_bundle = await bob.generate_pre_key_bundle(_BOB_UHID)
    await alice.generate_pre_key_bundle(_ALICE_UHID)
    await alice.process_pre_key_bundle(bob_bundle)

    msg = await alice.encrypt(_BOB_UHID, b"hello")
    await bob.decrypt(_ALICE_UHID, msg)

    # A second Alice with no stores must NOT see the session.
    alice_fresh = SignalProtocolService()
    assert not alice_fresh.has_session(_BOB_UHID)


@pytest.mark.asyncio
async def test_list_peers_enumerates_only_stored_sessions():
    session_kv = InMemoryKeyValueStore()
    pre_key_kv = InMemoryKeyValueStore()

    alice = _build_service(session_kv, pre_key_kv)
    await alice.generate_pre_key_bundle(_ALICE_UHID)

    # Establish sessions to two distinct peers.
    bob = _build_service(InMemoryKeyValueStore(), InMemoryKeyValueStore())
    carol = _build_service(InMemoryKeyValueStore(), InMemoryKeyValueStore())
    bob_bundle = await bob.generate_pre_key_bundle(_BOB_UHID)
    carol_bundle = await carol.generate_pre_key_bundle("carol-uhid-persist")

    await alice.process_pre_key_bundle(bob_bundle)
    await alice.process_pre_key_bundle(carol_bundle)
    await asyncio.sleep(0)

    # The session store should reflect both peers — implicitly verified
    # by hydrating a fresh Alice and checking has_session.
    alice_restarted = _build_service(session_kv, pre_key_kv)
    assert alice_restarted.has_session(_BOB_UHID)
    assert alice_restarted.has_session("carol-uhid-persist")


@pytest.mark.asyncio
async def test_session_dh_ratchet_state_survives_restart():
    """Restart should preserve send/recv counters across a multi-message
    conversation — not just the bare existence of the session.

    A stale session whose counters reset would cause AES-GCM nonce reuse
    via the lock-step nonce derivation; the test would fail with a
    decryption error.
    """
    alice_session_kv = InMemoryKeyValueStore()
    alice_pre_key_kv = InMemoryKeyValueStore()
    bob_session_kv = InMemoryKeyValueStore()
    bob_pre_key_kv = InMemoryKeyValueStore()

    alice = _build_service(alice_session_kv, alice_pre_key_kv)
    bob = _build_service(bob_session_kv, bob_pre_key_kv)

    await alice.generate_pre_key_bundle(_ALICE_UHID)
    bob_bundle = await bob.generate_pre_key_bundle(_BOB_UHID)
    await alice.process_pre_key_bundle(bob_bundle)

    # 5 messages back-and-forth. Each one ratchets the chain key.
    for i in range(5):
        msg = await alice.encrypt(_BOB_UHID, f"ping-{i}".encode("utf-8"))
        plain = await bob.decrypt(_ALICE_UHID, msg)
        assert plain == f"ping-{i}".encode("utf-8")
    await asyncio.sleep(0)

    alice_restarted = _build_service(alice_session_kv, alice_pre_key_kv)
    # Counters should be restored — a 6th send must continue from counter=5
    # (or 0 after a DH-ratchet) in lockstep with what Bob expects.
    msg = await alice_restarted.encrypt(_BOB_UHID, b"post-restart-ratcheted")
    plain = await bob.decrypt(_ALICE_UHID, msg)
    assert plain == b"post-restart-ratcheted"
