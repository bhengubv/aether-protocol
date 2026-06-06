# SPDX-License-Identifier: MIT

"""Unit tests for BehavioralAnomalyDetector.

Run with: python -m pytest tests/test_anomaly_detector.py -v
"""

from __future__ import annotations

import sys
from typing import Any

import pytest

from aethermesh.anomaly_detector import AnomalyDetectorOptions, BehavioralAnomalyDetector


# ---------------------------------------------------------------------------
# Spy reputation service
# ---------------------------------------------------------------------------

class _SpyReputation:
    """Records every call made to the reputation API."""

    def __init__(self) -> None:
        self.rreq_flood_calls: list[str] = []
        self.sig_failure_calls: list[str] = []
        self.replay_calls: list[str] = []
        self.custody_refusal_calls: list[str] = []
        self.delivery_success_calls: list[tuple[str, int]] = []
        self.delivery_failure_calls: list[str] = []

    def record_rreq_flood_attempt(self, uhid: str) -> None:
        self.rreq_flood_calls.append(uhid)

    def record_signature_failure(self, uhid: str) -> None:
        self.sig_failure_calls.append(uhid)

    def record_replay_attempt(self, uhid: str) -> None:
        self.replay_calls.append(uhid)

    def record_custody_refusal(self, uhid: str) -> None:
        self.custody_refusal_calls.append(uhid)

    def record_delivery_success(self, uhid: str, round_trip_ms: int) -> None:
        self.delivery_success_calls.append((uhid, round_trip_ms))

    def record_delivery_failure(self, uhid: str) -> None:
        self.delivery_failure_calls.append(uhid)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

ALICE = "alice-uhid"
BOB   = "bob-uhid"

def _make(opts: AnomalyDetectorOptions | None = None) -> tuple[_SpyReputation, BehavioralAnomalyDetector]:
    spy = _SpyReputation()
    det = BehavioralAnomalyDetector(spy, opts)
    return spy, det


# ---------------------------------------------------------------------------
# Volume-spike tests
# ---------------------------------------------------------------------------

class TestVolumeSpike:

    def test_volume_spike_detected(self) -> None:
        """Window 1 = 5 packets → EWMA = 5.  Window 2 = 20 packets > 3×5=15 → spike."""
        opts = AnomalyDetectorOptions(
            volume_window_ms=30_000,
            volume_spike_multiplier=3.0,
            ewma_alpha=1.0,       # α=1 means EWMA = last completed window exactly
            scatter_threshold=9999,
        )
        spy, det = _make(opts)

        # --- Window 1 (t = 0..29_999) ---
        for i in range(5):
            det.observe_packet(ALICE, f"dest-{i}", timestamp_ms=i * 1_000)

        # --- Window 2 starts at t = 30_000 ---
        # First packet in window 2 rolls the window and sets EWMA = 5.
        # Then 19 more packets → window_count = 20 > 3×5 = 15 → spike.
        for i in range(20):
            det.observe_packet(ALICE, f"dest-w2-{i}", timestamp_ms=30_000 + i * 100)

        assert len(spy.rreq_flood_calls) >= 1
        assert spy.rreq_flood_calls[0] == ALICE

    def test_no_volume_spike_normal_traffic(self) -> None:
        """Window 1 = 10, window 2 = 12; multiplier=5 → 12 < 50 → no spike."""
        opts = AnomalyDetectorOptions(
            volume_window_ms=30_000,
            volume_spike_multiplier=5.0,
            ewma_alpha=1.0,
            scatter_threshold=9999,
        )
        spy, det = _make(opts)

        for i in range(10):
            det.observe_packet(ALICE, f"d{i}", timestamp_ms=i * 1_000)

        for i in range(12):
            det.observe_packet(ALICE, f"d2-{i}", timestamp_ms=30_000 + i * 100)

        assert spy.rreq_flood_calls == []


# ---------------------------------------------------------------------------
# Destination-scatter tests
# ---------------------------------------------------------------------------

class TestDestinationScatter:

    def test_destination_scatter_detected(self) -> None:
        """threshold=5, send to 6 unique destinations → flood signal fired."""
        opts = AnomalyDetectorOptions(
            scatter_threshold=5,
            scatter_window_ms=60_000,
            volume_spike_multiplier=9999.0,   # disable volume spike
        )
        spy, det = _make(opts)

        for i in range(6):
            det.observe_packet(ALICE, f"unique-dest-{i}", timestamp_ms=1_000)

        assert len(spy.rreq_flood_calls) >= 1
        assert spy.rreq_flood_calls[0] == ALICE

    def test_destination_scatter_not_triggered_by_repeats(self) -> None:
        """100 packets to only 3 unique destinations → no scatter signal."""
        opts = AnomalyDetectorOptions(
            scatter_threshold=5,
            scatter_window_ms=60_000,
            volume_spike_multiplier=9999.0,
        )
        spy, det = _make(opts)

        dests = ["dest-a", "dest-b", "dest-c"]
        for i in range(100):
            det.observe_packet(ALICE, dests[i % 3], timestamp_ms=i * 10)

        assert spy.rreq_flood_calls == []


# ---------------------------------------------------------------------------
# Geohash mismatch tests
# ---------------------------------------------------------------------------

class TestGeohashMismatch:

    def test_geohash_mismatch_emits_sig_failure(self) -> None:
        """Different 4-char prefixes → sig failure recorded."""
        spy, det = _make()
        det.observe_geohash_claim(
            ALICE,
            claimed_geohash="ezjm99",
            observed_routing_geohash="u4pruv",
        )
        assert spy.sig_failure_calls == [ALICE]

    def test_geohash_match_no_signal(self) -> None:
        """Same 4-char prefix → no signal."""
        spy, det = _make()
        det.observe_geohash_claim(
            ALICE,
            claimed_geohash="ezjm11",
            observed_routing_geohash="ezjm99",
        )
        assert spy.sig_failure_calls == []

    def test_geohash_rate_limited(self) -> None:
        """rate_limit_ms=sys.maxsize → only the first mismatch fires a signal."""
        opts = AnomalyDetectorOptions(geohash_rate_limit_ms=sys.maxsize)
        spy, det = _make(opts)

        det.observe_geohash_claim(ALICE, "aaaa11", "bbbb99")   # fires
        det.observe_geohash_claim(ALICE, "aaaa11", "bbbb99")   # suppressed

        assert spy.sig_failure_calls == [ALICE]   # exactly one signal

    def test_geohash_rate_limit_resets_with_timestamp(self) -> None:
        """Using observe_geohash_claim_ts the window resets after rate_limit_ms."""
        opts = AnomalyDetectorOptions(geohash_rate_limit_ms=60_000)
        spy, det = _make(opts)

        det.observe_geohash_claim_ts(ALICE, "aaaa11", "bbbb99", timestamp_ms=0)
        det.observe_geohash_claim_ts(ALICE, "aaaa11", "bbbb99", timestamp_ms=30_000)   # suppressed
        det.observe_geohash_claim_ts(ALICE, "aaaa11", "bbbb99", timestamp_ms=60_000)   # fires again

        assert len(spy.sig_failure_calls) == 2
        assert spy.sig_failure_calls == [ALICE, ALICE]


# ---------------------------------------------------------------------------
# SPK-sig failure tests
# ---------------------------------------------------------------------------

class TestSpkSigFailure:

    def test_spk_sig_failure_passthrough(self) -> None:
        """observe_spk_sig_failure → exactly one record_signature_failure call."""
        spy, det = _make()
        det.observe_spk_sig_failure(ALICE)
        assert spy.sig_failure_calls == [ALICE]

    def test_spk_sig_failure_multiple(self) -> None:
        """3 consecutive failures → 3 signal emissions (no rate limiting)."""
        spy, det = _make()
        det.observe_spk_sig_failure(ALICE)
        det.observe_spk_sig_failure(ALICE)
        det.observe_spk_sig_failure(ALICE)
        assert spy.sig_failure_calls == [ALICE, ALICE, ALICE]


# ---------------------------------------------------------------------------
# Cross-contamination / isolation test
# ---------------------------------------------------------------------------

class TestCrossContamination:

    def test_no_cross_contamination(self) -> None:
        """Scatter threshold=2: Alice triggers (3 unique), Bob does not (2 unique)."""
        opts = AnomalyDetectorOptions(
            scatter_threshold=2,
            scatter_window_ms=60_000,
            volume_spike_multiplier=9999.0,
        )
        spy, det = _make(opts)

        # Alice contacts 3 unique destinations → over threshold
        det.observe_packet(ALICE, "d1", timestamp_ms=1_000)
        det.observe_packet(ALICE, "d2", timestamp_ms=1_000)
        det.observe_packet(ALICE, "d3", timestamp_ms=1_000)

        # Bob contacts only 2 unique destinations → exactly at threshold (not over)
        det.observe_packet(BOB, "d1", timestamp_ms=1_000)
        det.observe_packet(BOB, "d2", timestamp_ms=1_000)

        alice_floods = [c for c in spy.rreq_flood_calls if c == ALICE]
        bob_floods   = [c for c in spy.rreq_flood_calls if c == BOB]

        assert len(alice_floods) >= 1, "Alice should have triggered a flood signal"
        assert len(bob_floods) == 0,   "Bob should NOT have triggered a flood signal"
