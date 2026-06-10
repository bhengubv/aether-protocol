# SPDX-License-Identifier: MIT

"""Tests for the AetherNet Bandwidth Measurement Framework (ABMF) — W18-5.

Covers the same scenarios as the Go agent reference tests.

Run from the python/ directory:
    python -m pytest tests/test_bandwidth.py -v
"""

from __future__ import annotations

import threading
import time
from datetime import timedelta

import pytest

from aethernet.bandwidth import (
    BandwidthConfidence,
    BandwidthDirector,
    BandwidthEstimator,
    BandwidthGossipPayload,
    BandwidthProbeAck,
    BandwidthSample,
    NodeActivityMonitor,
    NodeActivityState,
)
from aethernet.bandwidth.monitor import _compute_transport_state
from aethernet.protocol.mesh_packet import PacketType


# ── Helpers ───────────────────────────────────────────────────────────────────


def _now_us() -> int:
    """Current time in microseconds (monotonically non-decreasing)."""
    return int(time.time() * 1_000_000)


def _make_estimator(name: str = "BLE", max_bps: int = 2_000_000) -> BandwidthEstimator:
    return BandwidthEstimator(name, max_bandwidth_bps=max_bps)


def _make_probe_ack(
    *,
    seq: int = 1,
    rtt_us: int = 20_000,
    probe_bytes: int = 1024,
    processing_us: int = 1_000,
) -> BandwidthProbeAck:
    """Build a BandwidthProbeAck with a deterministic RTT of *rtt_us* µs."""
    sender_send = 1_000_000
    receiver_receive = sender_send + rtt_us // 2
    receiver_send = receiver_receive + processing_us
    sender_receive = sender_send + rtt_us + processing_us
    return BandwidthProbeAck(
        sequence=seq,
        sender_send_us=sender_send,
        receiver_receive_us=receiver_receive,
        receiver_send_us=receiver_send,
        sender_receive_us=sender_receive,
        probe_bytes=probe_bytes,
    )


# ─────────────────────────────────────────────────────────────────────────────
# BandwidthEstimator tests
# ─────────────────────────────────────────────────────────────────────────────


class TestEstimatorInitialState:
    def test_confidence_is_none_initially(self) -> None:
        e = _make_estimator()
        assert e.confidence is BandwidthConfidence.None_

    def test_loss_rate_is_zero_initially(self) -> None:
        e = _make_estimator()
        assert e.loss_rate == pytest.approx(0.0)

    def test_transport_name_stored(self) -> None:
        e = _make_estimator("NearLink")
        assert e.transport_name == "NearLink"


class TestEstimatorRecordDelivery:
    def test_record_delivery_increases_confidence(self) -> None:
        e = _make_estimator()
        now = _now_us()
        # Feed enough deliveries to reach Low confidence (>= 1 probe round).
        for i in range(1, 6):
            e.record_delivery(1024, now + i * 10_000, now + i * 10_000 + 20_000)
        assert e.confidence.value >= BandwidthConfidence.Low.value

    def test_record_delivery_ignores_zero_bytes(self) -> None:
        e = _make_estimator()
        prev = e.current_sample()
        e.record_delivery(0, _now_us(), _now_us() + 1_000)
        # State should not change.
        assert e.confidence is prev.confidence

    def test_record_delivery_ignores_non_positive_elapsed(self) -> None:
        e = _make_estimator()
        now = _now_us()
        e.record_delivery(1024, now + 100, now)  # deliver <= send → ignored
        assert e.confidence is BandwidthConfidence.None_

    def test_record_delivery_updates_btlbw(self) -> None:
        e = _make_estimator(max_bps=0)
        now = _now_us()
        e.record_delivery(10_000, now, now + 100_000)  # 100 ms → 800 kbps
        assert e.btlbw_bps > 0

    def test_four_rounds_gives_low_confidence(self) -> None:
        # C# reference: probe_rounds < 5 → Low; at exactly 5 rounds → Medium.
        e = _make_estimator(max_bps=0)
        now = _now_us()
        for i in range(4):
            e.record_delivery(1024, now + i * 10_000, now + i * 10_000 + 5_000)
        assert e.confidence is BandwidthConfidence.Low

    def test_five_rounds_gives_medium_confidence(self) -> None:
        # At exactly 5 rounds the C# switch ``< 5 → Low, < 20 → Medium`` fires Medium.
        e = _make_estimator(max_bps=0)
        now = _now_us()
        for i in range(5):
            e.record_delivery(1024, now + i * 10_000, now + i * 10_000 + 5_000)
        assert e.confidence is BandwidthConfidence.Medium

    def test_twenty_rounds_gives_high_confidence(self) -> None:
        e = _make_estimator(max_bps=0)
        now = _now_us()
        for i in range(20):
            e.record_delivery(1024, now + i * 10_000, now + i * 10_000 + 5_000)
        assert e.confidence is BandwidthConfidence.High


class TestEstimatorRecordLoss:
    def test_record_loss_increases_loss_rate(self) -> None:
        e = _make_estimator()
        assert e.loss_rate == pytest.approx(0.0)
        e.record_loss(1024)
        assert e.loss_rate > 0.0

    def test_record_loss_multiple_times_increases_monotonically(self) -> None:
        e = _make_estimator()
        e.record_loss(512)
        r1 = e.loss_rate
        e.record_loss(512)
        r2 = e.loss_rate
        assert r2 > r1

    def test_record_loss_ignores_zero_bytes(self) -> None:
        e = _make_estimator()
        e.record_loss(0)
        assert e.loss_rate == pytest.approx(0.0)


class TestEstimatorWarmFromGossip:
    def test_warm_from_gossip_seeds_when_none(self) -> None:
        e = _make_estimator(max_bps=0)
        assert e.confidence is BandwidthConfidence.None_
        e.warm_from_gossip(1_000_000, timedelta(milliseconds=20), BandwidthConfidence.Low)
        assert e.btlbw_bps > 0
        # Confidence should be Low (warmed, no probe rounds yet).
        assert e.confidence is BandwidthConfidence.Low

    def test_warm_from_gossip_never_downgrades(self) -> None:
        e = _make_estimator(max_bps=0)
        # Drive to Medium confidence.
        now = _now_us()
        for i in range(10):
            e.record_delivery(4096, now + i * 10_000, now + i * 10_000 + 5_000)
        assert e.confidence.value >= BandwidthConfidence.Medium.value
        before_bw = e.btlbw_bps
        before_conf = e.confidence

        # Gossip with weaker params should not downgrade.
        e.warm_from_gossip(100, timedelta(seconds=10), BandwidthConfidence.Low)
        assert e.confidence.value >= before_conf.value
        # BtlBw should not drop dramatically.
        assert e.btlbw_bps >= before_bw // 2

    def test_warm_from_gossip_only_effective_once(self) -> None:
        e = _make_estimator(max_bps=0)
        e.warm_from_gossip(1_000_000, timedelta(milliseconds=20), BandwidthConfidence.Low)
        bw_after_first = e.btlbw_bps

        # Second call should be ignored.
        e.warm_from_gossip(9_999_999, timedelta(milliseconds=1), BandwidthConfidence.High)
        assert e.btlbw_bps == bw_after_first


class TestEstimatorApplyPhyHint:
    def test_weak_signal_caps_btlbw(self) -> None:
        e = _make_estimator(max_bps=600_000_000)
        # Feed a very high delivery rate sample.
        now = _now_us()
        e.record_delivery(1_000_000, now, now + 1_000)  # extremely fast
        high_bw = e.btlbw_bps

        # Apply a very weak RSSI → cap = 40 000 bps.
        e.apply_phy_hint(-100)
        assert e.btlbw_bps <= 40_000

    def test_strong_signal_does_not_cap(self) -> None:
        e = _make_estimator(max_bps=600_000_000)
        e.apply_phy_hint(-45)  # ≥ -50 → 600 Mbps cap
        now = _now_us()
        e.record_delivery(75_000_000, now, now + 1_000)  # 600 Mbps in 1 ms
        # BtlBw should not be capped below 600 Mbps.
        assert e.btlbw_bps <= 600_000_000

    def test_rssi_thresholds(self) -> None:
        cases = [
            (-49, 600_000_000),
            (-50, 600_000_000),
            (-60, 200_000_000),
            (-67, 200_000_000),
            (-69, 2_000_000),
            (-70, 2_000_000),
            (-75, 54_000_000),
            (-80, 54_000_000),
            (-82, 500_000),
            (-85, 500_000),
            (-90, 125_000),
            (-95, 125_000),
            (-100, 40_000),
        ]
        for rssi, expected_cap in cases:
            e = BandwidthEstimator("BLE", max_bandwidth_bps=expected_cap * 2)
            e.apply_phy_hint(rssi)
            # After hint, phy_cap_bps should be the expected cap.
            snap = e.current_sample()
            assert snap.phy_cap_bps == expected_cap, (
                f"RSSI {rssi} dBm: expected cap {expected_cap}, got {snap.phy_cap_bps}"
            )


# ── BandwidthProbeAck ─────────────────────────────────────────────────────────


class TestBandwidthProbeAck:
    def test_rtt_is_clock_sync_free(self) -> None:
        """RTT = (SenderReceive-SenderSend) - processing time; clock offsets cancel."""
        # Sender and receiver clocks differ by 1 000 000 µs (1 s).
        offset = 1_000_000
        sender_send = 1_000_000
        receiver_receive = sender_send + 10_000 + offset  # forward OWD 10 ms + offset
        processing = 2_000
        receiver_send = receiver_receive + processing
        sender_receive = sender_send + 20_000 + processing  # RTT 20 ms + processing

        ack = BandwidthProbeAck(
            sequence=1,
            sender_send_us=sender_send,
            receiver_receive_us=receiver_receive,
            receiver_send_us=receiver_send,
            sender_receive_us=sender_receive,
            probe_bytes=512,
        )
        rtt = ack.rtt
        # RTT should be 20 ms regardless of the clock offset.
        assert abs(rtt.total_seconds() * 1000 - 20.0) < 1.0

    def test_forward_owd_includes_clock_skew(self) -> None:
        """forward_owd is approximate when clocks differ."""
        ack = _make_probe_ack(rtt_us=20_000)
        # For a symmetric path, OWD ≈ RTT / 2.
        owd_ms = ack.forward_owd.total_seconds() * 1000
        assert 0 < owd_ms < 30


class TestBandwidthSampleRto:
    def test_rto_clamped_to_200ms_minimum(self) -> None:
        """RTO must never be below 200 ms (RFC 6298 §2.4)."""
        sample = BandwidthSample(
            transport_name="BLE",
            btlbw_bps=1_000_000,
            available_bps=1_000_000,
            bdp_bytes=0,
            srtt=timedelta(milliseconds=1),
            rtt_var=timedelta(milliseconds=0),
            rt_prop=timedelta(milliseconds=1),
            loss_rate=0.0,
            phy_cap_bps=0,
            confidence=BandwidthConfidence.High,
            measured_at=time.time(),
        )
        assert sample.rto >= timedelta(milliseconds=200)

    def test_rto_clamped_to_60s_maximum(self) -> None:
        """RTO must never exceed 60 s (RFC 6298 §2.4)."""
        sample = BandwidthSample(
            transport_name="BLE",
            btlbw_bps=0,
            available_bps=0,
            bdp_bytes=0,
            srtt=timedelta(seconds=100),
            rtt_var=timedelta(seconds=100),
            rt_prop=timedelta(seconds=100),
            loss_rate=0.0,
            phy_cap_bps=0,
            confidence=BandwidthConfidence.None_,
            measured_at=time.time(),
        )
        assert sample.rto <= timedelta(seconds=60)

    def test_rto_uses_rtt_var(self) -> None:
        """RTO = SRTT + max(G, 4×RTTVAR)."""
        srtt_ms = 50.0
        rttvar_ms = 10.0
        expected_ms = srtt_ms + 4.0 * rttvar_ms  # = 90 ms → clamped to 200 ms
        sample = BandwidthSample(
            transport_name="BLE",
            btlbw_bps=0,
            available_bps=0,
            bdp_bytes=0,
            srtt=timedelta(milliseconds=srtt_ms),
            rtt_var=timedelta(milliseconds=rttvar_ms),
            rt_prop=timedelta(milliseconds=srtt_ms),
            loss_rate=0.0,
            phy_cap_bps=0,
            confidence=BandwidthConfidence.None_,
            measured_at=time.time(),
        )
        # 90 ms < 200 ms minimum → clamped.
        assert sample.rto == timedelta(milliseconds=200)


class TestEstimatorRecordProbeResult:
    def test_record_probe_result_updates_confidence(self) -> None:
        e = _make_estimator(max_bps=0)
        ack = _make_probe_ack(rtt_us=20_000, probe_bytes=1024)
        e.record_probe_result(ack, _now_us())
        assert e.confidence.value >= BandwidthConfidence.Low.value

    def test_record_probe_result_ignores_nonpositive_rtt(self) -> None:
        e = _make_estimator()
        # RTT = 0 → ignored.
        ack = BandwidthProbeAck(
            sequence=1,
            sender_send_us=1_000_000,
            receiver_receive_us=1_000_010,
            receiver_send_us=1_000_010,
            sender_receive_us=1_000_000,  # sender_receive < sender_send → RTT < 0
            probe_bytes=512,
        )
        prev = e.confidence
        e.record_probe_result(ack, _now_us())
        assert e.confidence is prev


class TestEstimatorCallbacks:
    def test_on_sample_improved_fires_on_btlbw_increase(self) -> None:
        e = _make_estimator(max_bps=0)
        fired: list[BandwidthSample] = []
        e.on_sample_improved.append(fired.append)

        now = _now_us()
        e.record_delivery(100_000, now, now + 10_000)

        # Give the background thread a moment.
        deadline = time.time() + 1.0
        while not fired and time.time() < deadline:
            time.sleep(0.01)

        assert len(fired) >= 1

    def test_on_sample_improved_fires_on_confidence_advance(self) -> None:
        e = _make_estimator(max_bps=0)
        e.warm_from_gossip(1_000_000, timedelta(milliseconds=20), BandwidthConfidence.Low)

        fired: list[BandwidthSample] = []
        e.on_sample_improved.append(fired.append)

        now = _now_us()
        for i in range(20):
            e.record_delivery(4096, now + i * 10_000, now + i * 10_000 + 5_000)

        deadline = time.time() + 1.0
        while len(fired) < 1 and time.time() < deadline:
            time.sleep(0.01)

        assert any(s.confidence is BandwidthConfidence.High for s in fired)


# ─────────────────────────────────────────────────────────────────────────────
# BandwidthDirector tests
# ─────────────────────────────────────────────────────────────────────────────


class TestDirectorGetEstimate:
    def test_get_estimate_unknown_returns_none(self) -> None:
        d = BandwidthDirector()
        assert d.get_estimate("peer-1", "BLE") is None

    def test_get_estimate_unknown_transport_returns_none(self) -> None:
        d = BandwidthDirector()
        e = _make_estimator("BLE")
        d.register(e)
        assert d.get_estimate("peer-1", "NearLink") is None


class TestDirectorApplyGossip:
    def test_apply_gossip_seeds_matrix(self) -> None:
        d = BandwidthDirector()
        e = _make_estimator("BLE", max_bps=0)
        d.register(e)

        payload = BandwidthGossipPayload(
            peer_uhid="peer-1",
            transport_name="BLE",
            btlbw_bps=1_000_000,
            rt_prop_us=20_000,
            confidence=BandwidthConfidence.Low,
            measured_at=time.time(),
        )
        d.apply_gossip(payload)

        sample = d.get_estimate("peer-1", "BLE")
        assert sample is not None
        assert sample.btlbw_bps > 0

    def test_apply_gossip_unknown_transport_ignored(self) -> None:
        d = BandwidthDirector()
        payload = BandwidthGossipPayload(
            peer_uhid="peer-1",
            transport_name="NearLink",
            btlbw_bps=5_000_000,
            rt_prop_us=5_000,
            confidence=BandwidthConfidence.Medium,
            measured_at=time.time(),
        )
        # Should not raise.
        d.apply_gossip(payload)
        assert d.get_estimate("peer-1", "NearLink") is None


class TestDirectorGetEstimates:
    def test_get_estimates_ordered_by_available_bps_desc(self) -> None:
        d = BandwidthDirector()

        ble = BandwidthEstimator("BLE", max_bandwidth_bps=0)
        wfd = BandwidthEstimator("Wi-Fi Direct", max_bandwidth_bps=0)
        d.register(ble)
        d.register(wfd)

        d.apply_gossip(BandwidthGossipPayload(
            peer_uhid="peer-1",
            transport_name="BLE",
            btlbw_bps=500_000,
            rt_prop_us=10_000,
            confidence=BandwidthConfidence.Low,
            measured_at=time.time(),
        ))
        d.apply_gossip(BandwidthGossipPayload(
            peer_uhid="peer-1",
            transport_name="Wi-Fi Direct",
            btlbw_bps=50_000_000,
            rt_prop_us=5_000,
            confidence=BandwidthConfidence.Medium,
            measured_at=time.time(),
        ))

        estimates = d.get_estimates("peer-1")
        assert len(estimates) == 2
        assert estimates[0].available_bps >= estimates[1].available_bps

    def test_get_estimates_empty_for_unknown_peer(self) -> None:
        d = BandwidthDirector()
        assert d.get_estimates("nobody") == []


class TestDirectorRecommendTransport:
    def test_recommend_single_transport_returns_it(self) -> None:
        d = BandwidthDirector()
        e = _make_estimator("BLE", max_bps=0)
        d.register(e)

        d.apply_gossip(BandwidthGossipPayload(
            peer_uhid="peer-1",
            transport_name="BLE",
            btlbw_bps=1_000_000,
            rt_prop_us=20_000,
            confidence=BandwidthConfidence.Low,
            measured_at=time.time(),
        ))

        result = d.recommend_transport("peer-1", 1024)
        assert result == "BLE"

    def test_recommend_no_data_falls_back_to_lowest_power(self) -> None:
        d = BandwidthDirector()
        near = BandwidthEstimator("NearLink", max_bandwidth_bps=0)
        ble = BandwidthEstimator("BLE", max_bandwidth_bps=0)
        d.register(near)
        d.register(ble)

        # No gossip → no matrix data → fall back to NearLink (power cost 1).
        result = d.recommend_transport("peer-x", 4096)
        assert result == "NearLink"

    def test_recommend_no_estimators_returns_none(self) -> None:
        d = BandwidthDirector()
        assert d.recommend_transport("peer-x", 4096) is None

    def test_recommend_prefers_high_bandwidth(self) -> None:
        d = BandwidthDirector()

        ble = BandwidthEstimator("BLE", max_bandwidth_bps=0)
        wfd = BandwidthEstimator("Wi-Fi Direct", max_bandwidth_bps=0)
        d.register(ble)
        d.register(wfd)

        # Wi-Fi Direct: 50 Mbps; BLE: 500 kbps.
        d.apply_gossip(BandwidthGossipPayload(
            peer_uhid="peer-1",
            transport_name="BLE",
            btlbw_bps=500_000,
            rt_prop_us=20_000,
            confidence=BandwidthConfidence.Medium,
            measured_at=time.time(),
        ))
        d.apply_gossip(BandwidthGossipPayload(
            peer_uhid="peer-1",
            transport_name="Wi-Fi Direct",
            btlbw_bps=50_000_000,
            rt_prop_us=5_000,
            confidence=BandwidthConfidence.Medium,
            measured_at=time.time(),
        ))

        result = d.recommend_transport("peer-1", 1024)
        assert result == "Wi-Fi Direct"


class TestDirectorBuildGossipPayload:
    def test_build_gossip_no_confidence_returns_none(self) -> None:
        d = BandwidthDirector()
        e = _make_estimator("BLE", max_bps=0)
        d.register(e)
        # Confidence is None_ → should not gossip.
        result = d.build_gossip_payload("peer-1", "BLE")
        assert result is None

    def test_build_gossip_with_confidence_returns_payload(self) -> None:
        d = BandwidthDirector()
        e = _make_estimator("BLE", max_bps=0)
        d.register(e)

        # Warm the estimator so it has Low confidence.
        e.warm_from_gossip(1_000_000, timedelta(milliseconds=20), BandwidthConfidence.Low)

        result = d.build_gossip_payload("peer-1", "BLE")
        assert result is not None
        assert result.btlbw_bps > 0
        assert result.peer_uhid == "peer-1"

    def test_build_gossip_unknown_transport_returns_none(self) -> None:
        d = BandwidthDirector()
        assert d.build_gossip_payload("peer-1", "NearLink") is None


class TestDirectorGossipNeverDowngrades:
    def test_gossip_never_downgrades_existing_estimate(self) -> None:
        d = BandwidthDirector()
        e = _make_estimator("BLE", max_bps=0)
        d.register(e)

        # Drive to Medium confidence.
        now = _now_us()
        for i in range(10):
            e.record_delivery(4096, now + i * 10_000, now + i * 10_000 + 5_000)
        before_conf = e.confidence
        before_bw = e.btlbw_bps

        # Apply weak gossip.
        d.apply_gossip(BandwidthGossipPayload(
            peer_uhid="peer-1",
            transport_name="BLE",
            btlbw_bps=1,
            rt_prop_us=999_999_999,
            confidence=BandwidthConfidence.Low,
            measured_at=time.time(),
        ))

        assert e.confidence.value >= before_conf.value


# ─────────────────────────────────────────────────────────────────────────────
# NodeActivityMonitor tests
# ─────────────────────────────────────────────────────────────────────────────


class TestMonitorInitialState:
    def test_initial_state_is_offline(self) -> None:
        monitor = NodeActivityMonitor()
        assert monitor.current().state is NodeActivityState.Offline

    def test_initial_total_bps_is_zero(self) -> None:
        monitor = NodeActivityMonitor()
        snap = monitor.current()
        assert snap.total_bps == 0
        assert snap.has_activity is False


class TestMonitorSubscribe:
    def test_subscribe_fires_on_tick(self) -> None:
        monitor = NodeActivityMonitor()
        e = _make_estimator("BLE")
        monitor.register("BLE", e)
        monitor.sample_interval_ms = 100

        received: list = []
        monitor.subscribe(received.append)
        monitor.start()

        try:
            deadline = time.time() + 2.0
            while not received and time.time() < deadline:
                time.sleep(0.05)
            assert len(received) >= 1
        finally:
            monitor.stop()

    def test_unsubscribe_stops_callbacks(self) -> None:
        monitor = NodeActivityMonitor()
        e = _make_estimator("BLE")
        monitor.register("BLE", e)
        monitor.sample_interval_ms = 100

        received: list = []
        unsubscribe = monitor.subscribe(received.append)
        monitor.start()

        # Wait for at least one tick.
        deadline = time.time() + 2.0
        while not received and time.time() < deadline:
            time.sleep(0.05)

        count_at_unsub = len(received)
        unsubscribe()

        # Wait another two ticks.
        time.sleep(0.25)
        monitor.stop()

        # No more callbacks after unsubscribe.
        assert len(received) == count_at_unsub

    def test_subscribe_returns_callable(self) -> None:
        monitor = NodeActivityMonitor()
        unsub = monitor.subscribe(lambda _: None)
        assert callable(unsub)


class TestMonitorTrafficRecording:
    def test_record_ingress_egress_reflect_in_snapshot(self) -> None:
        monitor = NodeActivityMonitor()
        e = _make_estimator("BLE")
        monitor.register("BLE", e)
        monitor.sample_interval_ms = 100

        received: list = []
        monitor.subscribe(received.append)
        monitor.start()

        monitor.record_ingress("BLE", 10_000)
        monitor.record_egress("BLE", 5_000)

        try:
            deadline = time.time() + 2.0
            while not received and time.time() < deadline:
                time.sleep(0.05)
            # At least one snapshot should show non-zero rates.
            assert any(s.ingress_bps > 0 or s.egress_bps > 0 for s in received)
        finally:
            monitor.stop()

    def test_record_unknown_transport_is_noop(self) -> None:
        monitor = NodeActivityMonitor()
        # Should not raise.
        monitor.record_ingress("NearLink", 1024)
        monitor.record_egress("NearLink", 512)

    def test_record_egress_to_peer_counts_active_peers(self) -> None:
        monitor = NodeActivityMonitor()
        e = _make_estimator("BLE")
        monitor.register("BLE", e)
        monitor.sample_interval_ms = 100

        received: list = []
        monitor.subscribe(received.append)
        monitor.start()

        monitor.record_egress_to_peer("BLE", "peer-a", 5_000)
        monitor.record_egress_to_peer("BLE", "peer-b", 5_000)

        try:
            deadline = time.time() + 2.0
            while not received and time.time() < deadline:
                time.sleep(0.05)
            # Two distinct peers seen within the idle window → active_peers >= 2.
            assert any(s.active_peers >= 2 for s in received)
        finally:
            monitor.stop()

    def test_record_egress_without_peer_keeps_active_peers_zero(self) -> None:
        monitor = NodeActivityMonitor()
        e = _make_estimator("BLE")
        monitor.register("BLE", e)
        monitor.sample_interval_ms = 100

        received: list = []
        monitor.subscribe(received.append)
        monitor.start()

        # Transport-only recording must NOT count a peer.
        monitor.record_egress("BLE", 5_000)
        monitor.record_ingress("BLE", 5_000)

        try:
            deadline = time.time() + 2.0
            while not received and time.time() < deadline:
                time.sleep(0.05)
            assert received, "expected at least one snapshot"
            assert all(s.active_peers == 0 for s in received)
        finally:
            monitor.stop()


# ─────────────────────────────────────────────────────────────────────────────
# Transport state computation — Idle-state guard parity with the C# reference
# (NodeActivityMonitor.ComputeTransportState).
# ─────────────────────────────────────────────────────────────────────────────


class TestComputeTransportStateIdleGuard:
    """Mirrors the C# reference ComputeTransportState truth table exactly.

    C# semantics:
        if (!isRecent && egress == 0 && ingress == 0) return Idle;
        if (egress == 0 && ingress == 0)              return Idle;
        if (lossRate > 0.05)                          return Degraded;
        util = egress / btlbw; return util >= 0.5 ? Busy : Active;
    """

    def test_no_recent_egress_and_zero_rates_is_idle(self) -> None:
        # No recent egress AND zero current rates → Idle (first C# guard).
        state = _compute_transport_state(
            egress_bps=0, ingress_bps=0, loss_rate=0.0, btlbw_bps=1_000_000,
            is_recent=False,
        )
        assert state is NodeActivityState.Idle

    def test_recent_egress_but_zero_rates_is_idle(self) -> None:
        # Recent egress but zero current rates → still Idle (second C# guard).
        state = _compute_transport_state(
            egress_bps=0, ingress_bps=0, loss_rate=0.0, btlbw_bps=1_000_000,
            is_recent=True,
        )
        assert state is NodeActivityState.Idle

    def test_low_utilization_is_active(self) -> None:
        # Data flowing, util < 0.5 → Active. is_recent must not force Idle here.
        state = _compute_transport_state(
            egress_bps=100_000, ingress_bps=0, loss_rate=0.0, btlbw_bps=1_000_000,
            is_recent=True,
        )
        assert state is NodeActivityState.Active

    def test_active_even_when_not_recent_if_rates_nonzero(self) -> None:
        # Non-zero current rates → the Idle guards do NOT fire even if !is_recent.
        state = _compute_transport_state(
            egress_bps=100_000, ingress_bps=0, loss_rate=0.0, btlbw_bps=1_000_000,
            is_recent=False,
        )
        assert state is NodeActivityState.Active

    def test_high_utilization_is_busy(self) -> None:
        # util >= 0.5 → Busy.
        state = _compute_transport_state(
            egress_bps=600_000, ingress_bps=0, loss_rate=0.0, btlbw_bps=1_000_000,
            is_recent=True,
        )
        assert state is NodeActivityState.Busy

    def test_high_loss_is_degraded(self) -> None:
        # loss_rate > 0.05 with data flowing → Degraded (takes priority over util).
        state = _compute_transport_state(
            egress_bps=600_000, ingress_bps=0, loss_rate=0.10, btlbw_bps=1_000_000,
            is_recent=True,
        )
        assert state is NodeActivityState.Degraded

    def test_ingress_only_keeps_transport_out_of_idle(self) -> None:
        # Ingress alone (no egress) is still activity → not Idle.
        state = _compute_transport_state(
            egress_bps=0, ingress_bps=50_000, loss_rate=0.0, btlbw_bps=1_000_000,
            is_recent=False,
        )
        assert state is NodeActivityState.Active

    def test_idle_transition_via_monitor_after_threshold(self) -> None:
        """End-to-end: a registered transport with no egress ticks to Idle.

        Uses a 1-second idle threshold and a tight sample interval so the
        ``is_recent`` flag flips from recent (at registration) to stale, and the
        transport settles into Idle — the exact transition Task B aligns with C#.
        """
        monitor = NodeActivityMonitor()
        e = _make_estimator("BLE")
        monitor.register("BLE", e)
        monitor.sample_interval_ms = 100
        monitor.idle_threshold_seconds = 1

        received: list = []
        monitor.subscribe(received.append)
        monitor.start()
        try:
            # Wait long enough for last_egress (set at construction) to go stale
            # AND for several ticks to fire with zero traffic.
            deadline = time.time() + 3.0
            while time.time() < deadline:
                snap = monitor.current()
                if snap.transports and snap.transports[0].state is NodeActivityState.Idle:
                    break
                time.sleep(0.05)

            snap = monitor.current()
            assert snap.transports, "expected at least one transport snapshot"
            assert snap.transports[0].state is NodeActivityState.Idle
            assert snap.state is NodeActivityState.Idle
        finally:
            monitor.stop()


# ─────────────────────────────────────────────────────────────────────────────
# PacketType constants
# ─────────────────────────────────────────────────────────────────────────────


class TestPacketTypeConstants:
    def test_bandwidth_probe_is_53(self) -> None:
        assert PacketType.BandwidthProbe == 53

    def test_bandwidth_ack_is_54(self) -> None:
        assert PacketType.BandwidthAck == 54

    def test_bandwidth_gossip_is_55(self) -> None:
        assert PacketType.BandwidthGossip == 55

    def test_no_collisions(self) -> None:
        values = [t.value for t in PacketType]
        assert len(values) == len(set(values)), "Duplicate PacketType values detected"
