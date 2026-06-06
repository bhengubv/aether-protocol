# SPDX-License-Identifier: MIT

"""Unit tests for NodeReputationService.

Mirror of tests/AetherNet.Core.Tests/NodeReputationServiceTests.cs.
Run with: python -m pytest tests/test_reputation.py -v
"""

from __future__ import annotations

import math

import pytest

from aethernet.reputation import NodeReputationService


ALICE = "alice-uhid"
BOB = "bob-uhid"


def _svc() -> NodeReputationService:
    return NodeReputationService()


# ── Default score ────────────────────────────────────────────────────────────


def test_unknown_peer_returns_one():
    assert _svc().get_reputation_score("nobody") == 1.0


# ── Single negative signals ──────────────────────────────────────────────────


def test_rreq_flood_reduces_score():
    svc = _svc()
    svc.record_rreq_flood_attempt(ALICE)
    assert math.isclose(svc.get_reputation_score(ALICE), 0.95, rel_tol=0, abs_tol=1e-9)


def test_replay_attempt_reduces_score_by_fifteen():
    svc = _svc()
    svc.record_replay_attempt(ALICE)
    assert math.isclose(svc.get_reputation_score(ALICE), 0.85, rel_tol=0, abs_tol=1e-9)


def test_signature_failure_reduces_score_by_twenty():
    svc = _svc()
    svc.record_signature_failure(ALICE)
    assert math.isclose(svc.get_reputation_score(ALICE), 0.80, rel_tol=0, abs_tol=1e-9)


def test_custody_refusal_reduces_score_by_five():
    svc = _svc()
    svc.record_custody_refusal(ALICE)
    assert math.isclose(svc.get_reputation_score(ALICE), 0.95, rel_tol=0, abs_tol=1e-9)


def test_delivery_failure_reduces_score_by_two():
    svc = _svc()
    svc.record_delivery_failure(ALICE)
    assert math.isclose(svc.get_reputation_score(ALICE), 0.98, rel_tol=0, abs_tol=1e-9)


# ── Clamping ─────────────────────────────────────────────────────────────────


def test_repeated_sig_failures_clamp_to_zero():
    """5 × −0.20 = −1.0 → floor at 0.0."""
    svc = _svc()
    for _ in range(5):
        svc.record_signature_failure(ALICE)
    assert abs(svc.get_reputation_score(ALICE)) < 1e-9


def test_repeated_delivery_success_clamps_to_one():
    """10 × +0.01 starting from 1.0 is still capped at 1.0."""
    svc = _svc()
    for _ in range(10):
        svc.record_delivery_success(ALICE, round_trip_ms=50)
    assert svc.get_reputation_score(ALICE) == 1.0


# ── Multiple peers — no cross-contamination ──────────────────────────────────


def test_signals_do_not_cross_contaminate_peers():
    svc = _svc()
    svc.record_signature_failure(ALICE)
    svc.record_signature_failure(ALICE)

    alice = svc.get_reputation_score(ALICE)
    bob = svc.get_reputation_score(BOB)

    assert alice < 1.0
    assert bob == 1.0  # Bob is untouched


# ── get_all_scores ───────────────────────────────────────────────────────────


def test_get_all_scores_returns_snapshot():
    svc = _svc()
    svc.record_rreq_flood_attempt(ALICE)
    svc.record_replay_attempt(BOB)

    all_scores = svc.get_all_scores()
    assert len(all_scores) == 2
    assert ALICE in all_scores
    assert BOB in all_scores
    assert all_scores[ALICE] < 1.0
    assert all_scores[BOB] < 1.0


def test_get_all_scores_is_independent_copy():
    """Mutating the returned dict must not affect the service's internal state."""
    svc = _svc()
    svc.record_rreq_flood_attempt(ALICE)

    snapshot = svc.get_all_scores()
    snapshot[ALICE] = 0.0  # tamper with the copy

    assert svc.get_reputation_score(ALICE) != 0.0  # original unchanged


# ── Compound signals ─────────────────────────────────────────────────────────


def test_compound_signals_accumulate():
    """RREQ(−0.05) + Replay(−0.15) + SigFail(−0.20) = 0.60."""
    svc = _svc()
    svc.record_rreq_flood_attempt(ALICE)   # 1.00 − 0.05 = 0.95
    svc.record_replay_attempt(ALICE)       # 0.95 − 0.15 = 0.80
    svc.record_signature_failure(ALICE)   # 0.80 − 0.20 = 0.60

    assert math.isclose(svc.get_reputation_score(ALICE), 0.60, rel_tol=0, abs_tol=1e-9)
