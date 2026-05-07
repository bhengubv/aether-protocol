# SPDX-License-Identifier: MIT

"""Tests for the one-time pre-key (OPK) pool in SignalProtocolService.

The pool replaces the pre-2026-05-05 single-OPK behaviour, which dropped
legitimate concurrent initiators after the first consumed it. The pool's
default size is 100 (mirrors the C# DefaultOpkPoolSize); each
``generate_pre_key_bundle()`` call dequeues a fresh un-issued OPK and
tops the pool back up to ``opk_pool_size``.

Tests cover:
    * Pool size after construction (lazy — no OPKs generated yet).
    * 100 sequential bundles return distinct OPK ids.
    * Consumption removes an OPK from ``one_time_pre_keys``.
    * Top-up replenishes the pool on the next bundle call.
    * Concurrent (asyncio) initiators using DIFFERENT bundles do not
      collide.
    * Concurrent initiators using the SAME bundle: exactly one wins,
      the loser raises a clean error.
"""

from __future__ import annotations

import asyncio
from typing import List

import pytest

from aether.security.signal_protocol import (
    DEFAULT_OPK_POOL_SIZE,
    SignalProtocolService,
)


# ─── construction & invariants ─────────────────────────────────────────


def test_default_pool_size_is_100():
    svc = SignalProtocolService()
    assert svc.opk_pool_size == DEFAULT_OPK_POOL_SIZE
    assert DEFAULT_OPK_POOL_SIZE == 100


def test_pool_size_zero_rejected():
    with pytest.raises(ValueError):
        SignalProtocolService(opk_pool_size=0)


def test_pool_size_negative_rejected():
    with pytest.raises(ValueError):
        SignalProtocolService(opk_pool_size=-5)


def test_pool_empty_before_first_bundle():
    """No OPKs are generated until the first ``generate_pre_key_bundle()``
    call — the pool is lazy.
    """
    svc = SignalProtocolService(opk_pool_size=10)
    held, available = svc.get_opk_pool_status()
    assert held == 0
    assert available == 0


# ─── pool sizing & replenishment ───────────────────────────────────────


@pytest.mark.asyncio
async def test_first_bundle_tops_pool_to_size():
    """After the first bundle the pool holds ``opk_pool_size`` OPKs in
    total — one issued, ``opk_pool_size - 1`` available.
    """
    svc = SignalProtocolService(opk_pool_size=10)
    bundle = await svc.generate_pre_key_bundle("alice")
    assert bundle.pre_key_id > 0

    held, available = svc.get_opk_pool_status()
    # The pool was topped up to opk_pool_size, then 1 was dequeued.
    assert available == 10 - 1
    # The dequeued OPK stays in held until consumed (or never consumed).
    assert held == 10


@pytest.mark.asyncio
async def test_default_pool_size_top_up_to_100():
    svc = SignalProtocolService()  # default size = 100
    bundle = await svc.generate_pre_key_bundle("alice")
    assert bundle.pre_key_id > 0

    held, available = svc.get_opk_pool_status()
    assert available == 100 - 1
    assert held == 100


@pytest.mark.asyncio
async def test_subsequent_bundle_replenishes_to_pool_size():
    svc = SignalProtocolService(opk_pool_size=10)

    # Issue a few bundles. After each, available should bounce back to
    # opk_pool_size - issued_count_for_this_call.
    for i in range(3):
        await svc.generate_pre_key_bundle("alice")
        held, available = svc.get_opk_pool_status()
        # available is always topped back up to (opk_pool_size - 1) per
        # call: the top-up brings it to opk_pool_size, then we dequeue 1.
        assert available == 10 - 1
        # held grows by 1 on each call until any OPK is consumed.
        assert held == 10 + i


@pytest.mark.asyncio
async def test_100_sequential_bundles_return_distinct_opk_ids():
    """The whole point of the pool is collision-free OPK issuance under
    realistic concurrent-initiator load. 100 sequential bundles should
    therefore return 100 DISTINCT OPK ids.
    """
    svc = SignalProtocolService(opk_pool_size=100)
    seen: List[int] = []
    for _ in range(100):
        bundle = await svc.generate_pre_key_bundle("alice")
        seen.append(bundle.pre_key_id)

    assert len(set(seen)) == 100, "OPK ids collided across sequential bundles"


@pytest.mark.asyncio
async def test_signed_pre_key_id_stable_across_bundles():
    """The SPK should NOT rotate per bundle — it's only the OPK that's
    one-shot. (Re-using SPK is the C#-pre-2026 behaviour we're matching.)
    """
    svc = SignalProtocolService(opk_pool_size=10)
    b1 = await svc.generate_pre_key_bundle("alice")
    b2 = await svc.generate_pre_key_bundle("alice")
    b3 = await svc.generate_pre_key_bundle("alice")

    assert b1.signed_pre_key_id == b2.signed_pre_key_id == b3.signed_pre_key_id
    assert b1.signed_pre_key == b2.signed_pre_key == b3.signed_pre_key
    assert b1.signed_pre_key_signature == b2.signed_pre_key_signature == b3.signed_pre_key_signature


# ─── consumption ───────────────────────────────────────────────────────


@pytest.mark.asyncio
async def test_consumed_opk_is_removed_from_one_time_pre_keys():
    """When a responder consumes an OPK during X3DH, it MUST be removed
    from ``one_time_pre_keys`` so a second initiator using the same id
    cannot reuse it.
    """
    bob = SignalProtocolService(opk_pool_size=10)
    bob_bundle = await bob.generate_pre_key_bundle("bob")
    consumed_id = bob_bundle.pre_key_id

    # Sanity — bob holds the OPK.
    assert consumed_id in bob._pre_keys.one_time_pre_keys

    # Run the responder side of X3DH.
    alice = SignalProtocolService()
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bob_bundle)
    enc = await alice.encrypt("bob", b"hi")
    plaintext = await bob.decrypt("alice", enc)
    assert plaintext == b"hi"

    # OPK is consumed.
    assert consumed_id not in bob._pre_keys.one_time_pre_keys


@pytest.mark.asyncio
async def test_consumption_does_not_affect_other_pool_entries():
    """Consuming OPK X must not touch OPKs Y, Z, ... — each is one-shot
    independently.
    """
    bob = SignalProtocolService(opk_pool_size=10)
    bundle_for_alice = await bob.generate_pre_key_bundle("bob")
    bundle_for_carol = await bob.generate_pre_key_bundle("bob")

    # Different OPKs.
    assert bundle_for_alice.pre_key_id != bundle_for_carol.pre_key_id

    # Alice consumes hers.
    alice = SignalProtocolService()
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bundle_for_alice)
    enc = await alice.encrypt("bob", b"hi from alice")
    await bob.decrypt("alice", enc)

    # Carol's OPK is still held — she can still complete X3DH.
    carol = SignalProtocolService()
    await carol.generate_pre_key_bundle("carol")
    await carol.process_pre_key_bundle(bundle_for_carol)
    enc = await carol.encrypt("bob", b"hi from carol")
    out = await bob.decrypt("carol", enc)
    assert out == b"hi from carol"


# ─── concurrent initiators ─────────────────────────────────────────────


@pytest.mark.asyncio
async def test_concurrent_initiators_with_distinct_bundles_all_succeed():
    """The pool's whole reason for being: N concurrent initiators each
    holding a DIFFERENT bundle should ALL succeed. With the pre-pool
    single-OPK code this would silently drop N-1 of them.
    """
    bob = SignalProtocolService(opk_pool_size=10)

    n_concurrent = 8
    bundles = [await bob.generate_pre_key_bundle("bob") for _ in range(n_concurrent)]
    # All bundle OPK ids must be distinct.
    assert len(set(b.pre_key_id for b in bundles)) == n_concurrent

    async def run_initiator(idx: int) -> bytes:
        alice = SignalProtocolService()
        await alice.generate_pre_key_bundle(f"alice{idx}")
        await alice.process_pre_key_bundle(bundles[idx])
        enc = await alice.encrypt("bob", f"msg{idx}".encode())
        return await bob.decrypt(f"alice{idx}", enc)

    results = await asyncio.gather(*[run_initiator(i) for i in range(n_concurrent)])
    assert results == [f"msg{i}".encode() for i in range(n_concurrent)]


@pytest.mark.asyncio
async def test_concurrent_initiators_with_same_bundle_one_wins_others_fail_cleanly():
    """If multiple initiators happen to use the SAME bundle (shouldn't
    happen in practice, but the threat model includes a rogue redistributor)
    then exactly one wins and the others fail with a clean 'already
    consumed' ValueError — never with a corrupted session, never with a
    silent drop.
    """
    bob = SignalProtocolService(opk_pool_size=10)
    shared_bundle = await bob.generate_pre_key_bundle("bob")

    n_concurrent = 5

    async def run_initiator(idx: int):
        alice = SignalProtocolService()
        await alice.generate_pre_key_bundle(f"alice{idx}")
        await alice.process_pre_key_bundle(shared_bundle)
        enc = await alice.encrypt("bob", f"replay{idx}".encode())
        try:
            await bob.decrypt(f"alice{idx}", enc)
            return ("ok", idx)
        except ValueError as ex:
            return ("err", idx, str(ex))

    results = await asyncio.gather(*[run_initiator(i) for i in range(n_concurrent)])

    oks = [r for r in results if r[0] == "ok"]
    errs = [r for r in results if r[0] == "err"]

    # Exactly one must win.
    assert len(oks) == 1
    assert len(errs) == n_concurrent - 1
    # The error message names the OPK as already consumed / not held.
    for r in errs:
        assert "one-time pre-key" in r[2].lower()


# ─── observability ─────────────────────────────────────────────────────


@pytest.mark.asyncio
async def test_get_opk_pool_status_returns_held_and_available():
    svc = SignalProtocolService(opk_pool_size=5)
    held0, avail0 = svc.get_opk_pool_status()
    assert (held0, avail0) == (0, 0)

    await svc.generate_pre_key_bundle("alice")
    held1, avail1 = svc.get_opk_pool_status()
    assert held1 == 5
    assert avail1 == 4  # one issued

    # Multiple issuances drain available; held grows.
    for _ in range(3):
        await svc.generate_pre_key_bundle("alice")
    held4, avail4 = svc.get_opk_pool_status()
    assert avail4 == 4  # always topped back up to size - 1
    assert held4 == 8


@pytest.mark.asyncio
async def test_consumption_decreases_held_but_not_available():
    """Consuming an OPK reduces ``held`` (it leaves the pool altogether)
    but does NOT decrease ``available`` — that one wasn't in the
    available deque any more. This decouples the two counters cleanly."""
    bob = SignalProtocolService(opk_pool_size=5)
    bob_bundle = await bob.generate_pre_key_bundle("bob")

    held_before, avail_before = bob.get_opk_pool_status()
    assert held_before == 5
    assert avail_before == 4

    alice = SignalProtocolService()
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bob_bundle)
    enc = await alice.encrypt("bob", b"hi")
    await bob.decrypt("alice", enc)

    held_after, avail_after = bob.get_opk_pool_status()
    # Held drops by exactly 1.
    assert held_after == held_before - 1
    # Available is unchanged (the consumed OPK was issued, not in the deque).
    assert avail_after == avail_before


# ─── pool size = 1 (edge case, equivalent to old behaviour) ────────────


@pytest.mark.asyncio
async def test_pool_size_one_still_works_for_single_initiator():
    """Pool size = 1 mirrors the pre-pool behaviour — a single OPK,
    consumed once. Still valid for tests / single-peer hosts.
    """
    bob = SignalProtocolService(opk_pool_size=1)
    bob_bundle = await bob.generate_pre_key_bundle("bob")

    alice = SignalProtocolService()
    await alice.generate_pre_key_bundle("alice")
    await alice.process_pre_key_bundle(bob_bundle)
    enc = await alice.encrypt("bob", b"single")
    out = await bob.decrypt("alice", enc)
    assert out == b"single"


@pytest.mark.asyncio
async def test_pool_size_one_second_bundle_gets_fresh_opk():
    """Even with pool size = 1, sequential bundle calls should return
    distinct OPK ids (the consumed one is replenished, but a non-consumed
    issued one is NOT re-handed-out — the available deque is empty).
    """
    bob = SignalProtocolService(opk_pool_size=1)
    b1 = await bob.generate_pre_key_bundle("bob")
    b2 = await bob.generate_pre_key_bundle("bob")
    assert b1.pre_key_id != b2.pre_key_id
