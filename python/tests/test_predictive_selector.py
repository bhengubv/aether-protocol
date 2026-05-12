# SPDX-License-Identifier: MIT
"""Unit tests for PredictiveTransportSelector — Kalman RTT filter and scoring."""

from __future__ import annotations

import math
import unittest

from aether.transport.predictive_selector import PredictiveTransportSelector
from aether.transport.per_transport_metrics import PerTransportMetrics


# ── FakeTransport — minimal duck-typed stub ────────────────────────────────────

class FakeTransport:
    """Minimal duck-typed stub satisfying PredictiveTransportSelector's getattr API."""

    def __init__(
        self,
        name: str,
        bandwidth_bps: int = 500_000,
        power_cost: int = 1,
        available: bool = True,
    ) -> None:
        self.name              = name
        self.max_bandwidth_bps = bandwidth_bps
        self.power_cost_relative = power_cost
        self.is_available      = available
        self.metrics           = PerTransportMetrics()


# ── Kalman filter (indirect) ───────────────────────────────────────────────────

class TestKalmanFilterBehavior(unittest.TestCase):

    def test_kalman_converges_on_steady_state(self):
        """Feed 50 identical RTT samples — estimate should converge close to that value."""
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        sel.register(t, initial_rtt_ms=200.0)
        for _ in range(50):
            sel.observe_metrics(t, rtt_ms=100, success=True, bytes_transferred=1000)
        state = sel.get_kalman_state(t)
        self.assertIsNotNone(state)
        rtt_ms, _, _ = state
        self.assertAlmostEqual(rtt_ms, 100.0, delta=5.0,
                               msg=f"Kalman did not converge: rtt={rtt_ms:.2f}")

    def test_kalman_variance_decreases_with_observations(self):
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        sel.register(t, initial_rtt_ms=200.0)
        initial_state = sel.get_kalman_state(t)
        initial_var = initial_state[2]
        for _ in range(10):
            sel.observe_metrics(t, rtt_ms=200, success=True, bytes_transferred=1000)
        state = sel.get_kalman_state(t)
        self.assertLess(state[2], initial_var,
                        "posterior variance should decrease with observations")

    def test_kalman_detects_positive_drift(self):
        """Rising RTT should produce a positive drift estimate."""
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        sel.register(t, initial_rtt_ms=100.0)
        for i in range(10):
            sel.observe_metrics(t, rtt_ms=100 + (i + 1) * 15,
                                success=True, bytes_transferred=1000)
        state = sel.get_kalman_state(t)
        self.assertIsNotNone(state)
        _, drift, _ = state
        self.assertGreater(drift, 0.0,
                           f"drift {drift:.4f} should be positive for rising RTT")


# ── PredictiveTransportSelector lifecycle ─────────────────────────────────────

class TestPredictiveTransportSelector(unittest.TestCase):

    def test_register_and_rank_two_transports(self):
        sel  = PredictiveTransportSelector()
        fast = FakeTransport("fast", bandwidth_bps=1_000_000, power_cost=1,  available=True)
        slow = FakeTransport("slow", bandwidth_bps=10_000,    power_cost=10, available=True)
        sel.register(fast, initial_rtt_ms=50.0)
        sel.register(slow, initial_rtt_ms=150.0)
        # Feed good observations to fast so it has a real EWMA score.
        for _ in range(5):
            sel.observe_metrics(fast, rtt_ms=50, success=True, bytes_transferred=1000)
        ranked = sel.rank(payload_bytes=100)
        self.assertEqual(len(ranked), 2)
        self.assertEqual(ranked[0].transport.name, "fast",
                         f"expected 'fast' first, got '{ranked[0].transport.name}'")

    def test_unavailable_transport_excluded_from_ranking(self):
        sel  = PredictiveTransportSelector()
        avail  = FakeTransport("avail",  available=True)
        unavail = FakeTransport("unavail", available=False)
        sel.register(avail,   initial_rtt_ms=100.0)
        sel.register(unavail, initial_rtt_ms=100.0)
        ranked = sel.rank()
        self.assertEqual(len(ranked), 1)
        self.assertEqual(ranked[0].transport.name, "avail")

    def test_unregister_removes_transport(self):
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        sel.register(t, initial_rtt_ms=100.0)
        sel.unregister(t)
        self.assertEqual(len(sel.rank()), 0)

    def test_select_best_returns_none_when_empty(self):
        sel = PredictiveTransportSelector()
        self.assertIsNone(sel.select_best())

    def test_duplicate_register_ignored(self):
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        sel.register(t, initial_rtt_ms=100.0)
        sel.register(t, initial_rtt_ms=200.0)  # duplicate — should be no-op
        self.assertEqual(len(sel.rank()), 1)

    def test_get_kalman_state_initial_values(self):
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        sel.register(t, initial_rtt_ms=123.0)
        state = sel.get_kalman_state(t)
        self.assertIsNotNone(state)
        rtt, drift, variance = state
        self.assertAlmostEqual(rtt, 123.0)
        self.assertAlmostEqual(drift, 0.0)
        self.assertGreater(variance, 0.0)

    def test_get_kalman_state_unregistered_returns_none(self):
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        self.assertIsNone(sel.get_kalman_state(t))

    def test_scores_contain_positive_values(self):
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        sel.register(t, initial_rtt_ms=100.0)
        ranked = sel.rank()
        self.assertEqual(len(ranked), 1)
        self.assertGreater(ranked[0].score, 0.0)

    def test_score_increases_after_good_observations(self):
        sel = PredictiveTransportSelector()
        t   = FakeTransport("t", available=True)
        sel.register(t, initial_rtt_ms=200.0)
        score_before = sel.rank()[0].score

        # Feed 10 fast, lossless samples.
        for _ in range(10):
            sel.observe_metrics(t, rtt_ms=20, success=True, bytes_transferred=5000)

        score_after = sel.rank()[0].score
        self.assertGreater(score_after, score_before,
                           "score should improve after good observations")


if __name__ == "__main__":
    unittest.main()
