# SPDX-License-Identifier: MIT

"""Unit tests for aether.transport.rank and aether.transport.per_transport_metrics."""

from __future__ import annotations

import unittest

from aether.transport.per_transport_metrics import PerTransportMetrics
from aether.transport.rank import RankedTransport, rank_transports


# ── FakeTransport — minimal duck-typed stub ───────────────────────────────────

class FakeTransport:
    """Minimal duck-typed stub that satisfies rank_transports' attribute access."""

    def __init__(
        self,
        name: str,
        is_available: bool = True,
        max_bandwidth_bps: int = 100_000,
        power_cost_relative: int = 1,
        metrics: PerTransportMetrics | None = None,
    ) -> None:
        self.name                = name
        self.is_available        = is_available
        self.max_bandwidth_bps   = max_bandwidth_bps
        self.power_cost_relative = power_cost_relative
        self.metrics             = metrics
        self.max_range_meters    = 100
        self.max_concurrent_peers = 10


# ── PerTransportMetrics ───────────────────────────────────────────────────────

class TestPerTransportMetrics(unittest.TestCase):
    """Direct tests for the EWMA metrics class."""

    # ── initial state ─────────────────────────────────────────────────────────

    def test_initial_sample_count_is_zero(self):
        m = PerTransportMetrics()
        self.assertEqual(0, m.sample_count)

    def test_initial_ewma_rtt_ms_is_200(self):
        m = PerTransportMetrics()
        self.assertAlmostEqual(200.0, m.ewma_rtt_ms)

    def test_initial_ewma_loss_rate_is_5_percent(self):
        m = PerTransportMetrics()
        self.assertAlmostEqual(0.05, m.ewma_loss_rate)

    def test_initial_ewma_throughput_bps_is_zero(self):
        m = PerTransportMetrics()
        self.assertAlmostEqual(0.0, m.ewma_throughput_bps)

    # ── record_sample — sample count ──────────────────────────────────────────

    def test_record_sample_increments_count_once(self):
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=1000)
        self.assertEqual(1, m.sample_count)

    def test_record_sample_increments_count_multiple_times(self):
        m = PerTransportMetrics()
        for _ in range(5):
            m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=1000)
        self.assertEqual(5, m.sample_count)

    # ── record_sample — RTT EWMA ──────────────────────────────────────────────

    def test_record_sample_updates_rtt_ewma_correctly(self):
        # After 1 sample of 100 ms: α×100 + (1−α)×200 = 0.2×100 + 0.8×200 = 180
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=1000)
        self.assertAlmostEqual(180.0, m.ewma_rtt_ms, places=6)

    def test_record_sample_zero_rtt_skips_rtt_update(self):
        # rtt_ms=0 → branch not entered → RTT stays at 200
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=0.0, success=True, bytes_transferred=0)
        self.assertAlmostEqual(200.0, m.ewma_rtt_ms)

    def test_record_sample_rtt_converges_toward_input(self):
        m = PerTransportMetrics()
        for _ in range(50):
            m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=1000)
        # After many identical samples the EWMA should be very close to 100
        self.assertAlmostEqual(100.0, m.ewma_rtt_ms, delta=2.0)

    # ── record_sample — loss rate EWMA ────────────────────────────────────────

    def test_record_sample_failure_raises_loss_rate(self):
        # α×1.0 + (1−α)×0.05 = 0.2 + 0.04 = 0.24
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=100.0, success=False, bytes_transferred=0)
        self.assertAlmostEqual(0.24, m.ewma_loss_rate, places=6)

    def test_record_sample_success_lowers_loss_rate(self):
        # α×0.0 + (1−α)×0.05 = 0 + 0.04 = 0.04
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=1000)
        self.assertAlmostEqual(0.04, m.ewma_loss_rate, places=6)

    # ── record_sample — throughput EWMA ──────────────────────────────────────

    def test_record_sample_bootstraps_throughput_on_first_success(self):
        # bytes=1000, rtt=100 ms → tput = 1000×8×1000/100 = 80_000 bps
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=1000)
        self.assertAlmostEqual(80_000.0, m.ewma_throughput_bps, places=0)

    def test_record_sample_ewma_throughput_blends_on_second_success(self):
        # First bootstrap: 80_000 bps
        # Second: bytes=2000, rtt=100 → tput=160_000; 0.2×160_000 + 0.8×80_000 = 96_000
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=1000)
        m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=2000)
        self.assertAlmostEqual(96_000.0, m.ewma_throughput_bps, places=0)

    def test_record_sample_failure_does_not_change_throughput(self):
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=100.0, success=True, bytes_transferred=1000)  # 80_000
        m.record_sample(rtt_ms=100.0, success=False, bytes_transferred=0)
        self.assertAlmostEqual(80_000.0, m.ewma_throughput_bps, places=0)

    def test_record_sample_zero_rtt_does_not_update_throughput(self):
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=0.0, success=True, bytes_transferred=1000)
        self.assertAlmostEqual(0.0, m.ewma_throughput_bps)

    # ── composite_score ───────────────────────────────────────────────────────

    def test_composite_score_is_positive_with_defaults(self):
        m = PerTransportMetrics()
        self.assertGreater(m.composite_score(500_000, 1), 0.0)

    def test_composite_score_zero_power_clamped_to_one(self):
        m = PerTransportMetrics()
        self.assertAlmostEqual(
            m.composite_score(500_000, 0),
            m.composite_score(500_000, 1),
            places=6,
        )

    def test_composite_score_formula_with_no_throughput(self):
        # effective_bps = max(0, 500_000 × 0.1) = 50_000
        # score = (50_000 / 1) × (1 − 0.05) / max(200, 1) = 50_000 × 0.95 / 200 = 237.5
        m = PerTransportMetrics()
        expected = (500_000 * 0.1 / 1) * (1.0 - 0.05) / 200.0
        self.assertAlmostEqual(expected, m.composite_score(500_000, 1), places=6)

    def test_composite_score_higher_bandwidth_yields_higher_score(self):
        m = PerTransportMetrics()
        self.assertGreater(
            m.composite_score(1_000_000, 1),
            m.composite_score(100_000, 1),
        )

    def test_composite_score_higher_power_cost_yields_lower_score(self):
        m = PerTransportMetrics()
        self.assertGreater(
            m.composite_score(500_000, 1),
            m.composite_score(500_000, 10),
        )

    def test_composite_score_improves_after_good_observations(self):
        m = PerTransportMetrics()
        before = m.composite_score(500_000, 1)
        for _ in range(20):
            m.record_sample(rtt_ms=10.0, success=True, bytes_transferred=5000)
        after = m.composite_score(500_000, 1)
        self.assertGreater(after, before,
                           "score should improve after many fast, lossless samples")


# ── RankedTransport ───────────────────────────────────────────────────────────

class TestRankedTransport(unittest.TestCase):

    def test_holds_transport_and_score(self):
        t  = FakeTransport("ble")
        rt = RankedTransport(transport=t, score=42.0)
        self.assertIs(t, rt.transport)
        self.assertAlmostEqual(42.0, rt.score)

    def test_equality_is_structural(self):
        t  = FakeTransport("ble")
        r1 = RankedTransport(transport=t, score=5.0)
        r2 = RankedTransport(transport=t, score=5.0)
        self.assertEqual(r1, r2)

    def test_different_scores_not_equal(self):
        t = FakeTransport("ble")
        self.assertNotEqual(
            RankedTransport(transport=t, score=1.0),
            RankedTransport(transport=t, score=2.0),
        )


# ── rank_transports ───────────────────────────────────────────────────────────

class TestRankTransports(unittest.TestCase):

    def test_empty_input_returns_empty_list(self):
        self.assertEqual([], rank_transports([]))

    def test_unavailable_transport_is_excluded(self):
        t = FakeTransport("ble", is_available=False)
        self.assertEqual([], rank_transports([t]))

    def test_all_unavailable_returns_empty_list(self):
        transports = [
            FakeTransport("ble",  is_available=False),
            FakeTransport("wifi", is_available=False),
        ]
        self.assertEqual([], rank_transports(transports))

    def test_available_transport_is_included(self):
        t = FakeTransport("ble")
        result = rank_transports([t])
        self.assertEqual(1, len(result))
        self.assertIs(t, result[0].transport)

    def test_results_sorted_by_score_descending(self):
        low  = FakeTransport("low",  max_bandwidth_bps=10_000,    power_cost_relative=10)
        high = FakeTransport("high", max_bandwidth_bps=1_000_000, power_cost_relative=1)
        result = rank_transports([low, high])
        self.assertEqual(2, len(result))
        self.assertGreaterEqual(result[0].score, result[1].score)
        self.assertEqual("high", result[0].transport.name)

    def test_static_score_is_bandwidth_divided_by_power(self):
        # power=1, bandwidth=500_000 → score = 500_000.0
        t = FakeTransport("wifi", max_bandwidth_bps=500_000, power_cost_relative=1)
        result = rank_transports([t])
        self.assertEqual(1, len(result))
        self.assertAlmostEqual(500_000.0 / 1, result[0].score, places=3)

    def test_static_score_clamps_power_cost_to_at_least_1(self):
        # power=0 → treated as 1; score = 200_000 / 1 = 200_000
        t = FakeTransport("zero-cost", max_bandwidth_bps=200_000, power_cost_relative=0)
        result = rank_transports([t])
        self.assertEqual(1, len(result))
        self.assertAlmostEqual(200_000.0, result[0].score, places=3)

    def test_transport_with_live_metrics_uses_composite_score(self):
        m = PerTransportMetrics()
        m.record_sample(rtt_ms=50.0, success=True, bytes_transferred=1000)
        t = FakeTransport("ble-live", max_bandwidth_bps=100_000, power_cost_relative=2,
                          metrics=m)
        result = rank_transports([t])
        self.assertEqual(1, len(result))
        self.assertGreater(result[0].score, 0.0)

    def test_only_available_from_mixed_list(self):
        a = FakeTransport("avail",   is_available=True)
        u = FakeTransport("unavail", is_available=False)
        result = rank_transports([a, u])
        self.assertEqual(1, len(result))
        self.assertEqual("avail", result[0].transport.name)


if __name__ == "__main__":
    unittest.main()
