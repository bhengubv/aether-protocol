# SPDX-License-Identifier: MIT

"""Signed-pre-key rotation tests.

Mirrors the C# ``SignedPreKeyRotationTests``:

1. A SPK older than ``SignedPreKeyRotationOptions.rotation_interval`` is
   rotated on the next ``generate_pre_key_bundle`` or
   ``rotate_signed_pre_key`` call.
2. Recently-rotated SPKs (within the retention window) still complete
   X3DH on the responder side.
3. Pruned SPKs (rotated out of the retention window) fail X3DH on the
   responder side.

All time math is driven by a synthetic ``now_provider`` callable so the
tests run deterministically without sleeps.
"""

from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone
from typing import Tuple

import pytest

from aether.security import (
    SignalProtocolService,
    SignedPreKeyRotationOptions,
)
from aether.security.session_store import KeyValueSignalSessionStore
from aether.security.pre_key_store import KeyValuePreKeyStore
from aether.storage import InMemoryKeyValueStore, KeyValueStore


_RESPONDER_UHID = "responder-rotation"
_INITIATOR_UHID = "initiator-rotation"


class _MutableClock:
    """Synthetic clock — tests advance time without re-instantiating the service."""

    def __init__(self, now: datetime) -> None:
        self.now: datetime = now

    def __call__(self) -> datetime:
        return self.now


def _build_responder(
    rotation_interval: timedelta,
    retained_history: int,
    pre_key_kv: KeyValueStore | None = None,
    session_kv: KeyValueStore | None = None,
) -> Tuple[SignalProtocolService, _MutableClock]:
    """Build a service with a synthetic clock and configurable rotation."""
    # Anchor at a year far enough ahead to dwarf any timezone artefacts.
    clock = _MutableClock(datetime(2065, 1, 1, tzinfo=timezone.utc))
    opts = SignedPreKeyRotationOptions(
        rotation_interval=rotation_interval,
        retained_history_count=retained_history,
    )
    svc = SignalProtocolService(
        opk_pool_size=16,
        session_store=KeyValueSignalSessionStore(session_kv) if session_kv else None,
        pre_key_store=KeyValuePreKeyStore(pre_key_kv) if pre_key_kv else None,
        rotation_options=opts,
        now_provider=clock,
    )
    return svc, clock


@pytest.mark.asyncio
async def test_no_rotation_before_interval_elapses():
    svc, clock = _build_responder(timedelta(days=7), retained_history=3)

    b1 = await svc.generate_pre_key_bundle(_RESPONDER_UHID)
    assert svc.signed_pre_key_history_count == 1

    # Advance 6 days — under the 7-day rotation interval.
    clock.now = datetime(2065, 1, 7, tzinfo=timezone.utc)
    b2 = await svc.generate_pre_key_bundle(_RESPONDER_UHID)
    assert b1.signed_pre_key_id == b2.signed_pre_key_id
    assert svc.signed_pre_key_history_count == 1


@pytest.mark.asyncio
async def test_rotates_after_interval_elapses():
    svc, clock = _build_responder(timedelta(days=7), retained_history=3)

    b1 = await svc.generate_pre_key_bundle(_RESPONDER_UHID)

    # Advance past the rotation interval.
    clock.now = datetime(2065, 1, 9, tzinfo=timezone.utc)
    b2 = await svc.generate_pre_key_bundle(_RESPONDER_UHID)
    assert b1.signed_pre_key_id != b2.signed_pre_key_id
    assert svc.signed_pre_key_history_count == 2


@pytest.mark.asyncio
async def test_retains_prior_spks_up_to_history_count():
    svc, clock = _build_responder(timedelta(days=7), retained_history=3)

    t0 = datetime(2065, 1, 1, tzinfo=timezone.utc)
    await svc.generate_pre_key_bundle(_RESPONDER_UHID)

    # Rotate three more times — history should be at the cap (1+3=4).
    for i in range(1, 4):
        clock.now = t0 + timedelta(days=7 * i + 1)
        await svc.generate_pre_key_bundle(_RESPONDER_UHID)
    assert svc.signed_pre_key_history_count == 4

    # One more rotation — oldest entry should be pruned.
    clock.now = t0 + timedelta(days=7 * 4 + 1)
    await svc.generate_pre_key_bundle(_RESPONDER_UHID)
    assert svc.signed_pre_key_history_count == 4


@pytest.mark.asyncio
async def test_retained_spk_still_decrypts_inflight_x3dh():
    """Responder rotates SPK while an initiator's PreKey message is still
    in flight (signed under the OLD SPK). The responder must still be able
    to decrypt because the old SPK is in the retained history.
    """
    responder, clock = _build_responder(timedelta(days=7), retained_history=3)
    b0 = await responder.generate_pre_key_bundle(_RESPONDER_UHID)

    # Initiator processes the OLD bundle (b0) and prepares to send.
    initiator = SignalProtocolService()
    await initiator.generate_pre_key_bundle(_INITIATOR_UHID)
    await initiator.process_pre_key_bundle(b0)

    # Responder rotates SPK before the initiator's first message arrives.
    clock.now = datetime(2065, 1, 9, tzinfo=timezone.utc)
    b1 = await responder.generate_pre_key_bundle(_RESPONDER_UHID)
    assert b0.signed_pre_key_id != b1.signed_pre_key_id

    # Initiator now sends — under the OLD SPK. The responder must still
    # be able to decrypt because b0's SPK is in the retained history.
    msg = await initiator.encrypt(_RESPONDER_UHID, b"retained-spk-msg")
    plain = await responder.decrypt(_INITIATOR_UHID, msg)
    assert plain == b"retained-spk-msg"


@pytest.mark.asyncio
async def test_pruned_spk_fails_x3dh():
    """Retain 0 prior — every rotation prunes the previous SPK
    immediately. The initiator's PreKey message under the old SPK then
    fails on the responder side.
    """
    responder, clock = _build_responder(timedelta(days=7), retained_history=0)
    b0 = await responder.generate_pre_key_bundle(_RESPONDER_UHID)

    initiator = SignalProtocolService()
    await initiator.generate_pre_key_bundle(_INITIATOR_UHID)
    await initiator.process_pre_key_bundle(b0)

    # Rotate the responder's SPK — b0's SPK is pruned (no retention).
    clock.now = datetime(2065, 1, 9, tzinfo=timezone.utc)
    rotated = await responder.rotate_signed_pre_key()
    assert rotated
    assert responder.signed_pre_key_history_count == 1

    msg = await initiator.encrypt(_RESPONDER_UHID, b"pruned-spk-msg")

    # X3DH on the responder side must reject — the SPK referenced by the
    # PreKey message has been pruned.
    with pytest.raises(ValueError, match="signed pre-key"):
        await responder.decrypt(_INITIATOR_UHID, msg)


@pytest.mark.asyncio
async def test_explicit_rotate_returns_true_when_interval_elapsed():
    svc, clock = _build_responder(timedelta(days=7), retained_history=1)
    await svc.generate_pre_key_bundle(_RESPONDER_UHID)

    # Inside the interval — explicit rotate is a no-op.
    assert not await svc.rotate_signed_pre_key()
    assert svc.signed_pre_key_history_count == 1

    # Past the interval — explicit rotate succeeds.
    clock.now = datetime(2065, 1, 9, tzinfo=timezone.utc)
    assert await svc.rotate_signed_pre_key()
    assert svc.signed_pre_key_history_count == 2


@pytest.mark.asyncio
async def test_rotation_history_persists_across_restart():
    pre_key_kv = InMemoryKeyValueStore()
    svc1, clock1 = _build_responder(
        timedelta(days=7), retained_history=3, pre_key_kv=pre_key_kv)
    await svc1.generate_pre_key_bundle(_RESPONDER_UHID)
    clock1.now = datetime(2065, 1, 9, tzinfo=timezone.utc)
    await svc1.rotate_signed_pre_key()
    history_before = svc1.signed_pre_key_history_count
    await asyncio.sleep(0)

    # Restart against the same store. History should be hydrated.
    svc2, _ = _build_responder(
        timedelta(days=7), retained_history=3, pre_key_kv=pre_key_kv)
    assert svc2.signed_pre_key_history_count == history_before


def test_negative_retention_count_raises():
    with pytest.raises(ValueError, match="retained_history_count"):
        SignalProtocolService(
            rotation_options=SignedPreKeyRotationOptions(
                rotation_interval=timedelta(days=7),
                retained_history_count=-1,
            ),
        )


def test_zero_rotation_interval_raises():
    with pytest.raises(ValueError, match="rotation_interval"):
        SignalProtocolService(
            rotation_options=SignedPreKeyRotationOptions(
                rotation_interval=timedelta(0),
                retained_history_count=1,
            ),
        )
