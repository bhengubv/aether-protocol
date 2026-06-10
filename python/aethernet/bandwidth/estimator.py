# SPDX-License-Identifier: MIT

"""BBRv3-inspired per-transport bandwidth estimator.

Pythonic port of ``AetherNet.Transport/Bandwidth/BandwidthEstimator.cs`` (W18-5).

Algorithm summary
-----------------
* **BtlBw (bottleneck bandwidth):** rolling maximum of per-delivery delivery-rate
  samples over a ``BTLBW_WINDOW_SIZE`` = 10-RTprop window. The maximum (not the
  average) tracks pipe capacity, not current load — mirrors BBRv3 BtlBwFilter
  (draft-cardwell-iccrg-bbr-congestion-control-02 §4.3.2.1).
* **RTprop (path propagation delay):** rolling minimum RTT over a 10 000 ms window.
  The minimum filters out queueing delay.
* **SRTT / RTTVAR:** RFC 6298 §2.3 Jacobson/Karels algorithm. α = 1/8, β = 1/4.
* **Loss rate:** EWMA with α = 0.10.
* **PHY cap:** RSSI-to-BtlBw mapping constrains estimates on weak radio links.

Thread safety
-------------
All mutable state is protected by ``threading.Lock``. The current sample is a
``volatile`` reference updated atomically after each observation.
"""

from __future__ import annotations

import threading
import time
from collections import deque
from datetime import timedelta
from typing import Callable

from aethernet.bandwidth.models import (
    BandwidthConfidence,
    BandwidthProbeAck,
    BandwidthSample,
)


# ── Constants ─────────────────────────────────────────────────────────────────

#: Number of delivery-rate samples kept in the BtlBw max-filter window.
BTLBW_WINDOW_SIZE: int = 10

#: Minimum RTT window duration in milliseconds (BBRv3 ProbeRTT period).
RT_PROP_WINDOW_MS: float = 10_000.0

#: EWMA loss rate smoothing factor (α).
LOSS_ALPHA: float = 0.10

#: RFC 6298 SRTT smoothing factor (1/8).
_SRTT_ALPHA: float = 0.125

#: RFC 6298 RTTVAR smoothing factor (1/4).
_RTT_VAR_BETA: float = 0.25

#: 5 % improvement threshold for the ``on_sample_improved`` callbacks.
_IMPROVEMENT_THRESHOLD: float = 0.05


def _now_ms() -> float:
    """Milliseconds since Unix epoch (monotonically non-decreasing within a process)."""
    return time.time() * 1000.0


# ── BandwidthEstimator ────────────────────────────────────────────────────────


class BandwidthEstimator:
    """Per-transport link bandwidth estimator.

    Parameters
    ----------
    transport_name:
        Transport identifier (e.g. ``"BLE"``, ``"NearLink"``, ``"Wi-Fi Direct"``).
    max_bandwidth_bps:
        Optimistic initial BtlBw for the transport. PHY hints and probes will
        tighten this quickly. ``0`` is valid (starts fully unconstrained).
    """

    def __init__(self, transport_name: str, max_bandwidth_bps: int = 0) -> None:
        self._transport_name = transport_name
        self._lock = threading.Lock()

        # BtlBw max-filter: deque of (rate_bps, timestamp_ms) pairs.
        self._btlbw_window: deque[tuple[int, float]] = deque(
            maxlen=BTLBW_WINDOW_SIZE
        )

        # RTprop min-filter: deque of (rtt_ms, timestamp_ms) pairs.
        self._rt_prop_samples: deque[tuple[float, float]] = deque()

        # RFC 6298 SRTT / RTTVAR
        self._srtt_ms: float = 0.0
        self._rtt_var_ms: float = 0.0
        self._first_rtt: bool = True

        # Loss EWMA
        self._loss_rate: float = 0.0

        # PHY cap (bps, 0 = unknown)
        self._phy_cap_bps: int = 0

        # Probe-round counter drives confidence tier.
        self._probe_rounds: int = 0

        # True once warmed from a gossip payload.
        self._warmed_from_gossip: bool = False

        # Callbacks fired when the sample improves significantly.
        self.on_sample_improved: list[Callable[[BandwidthSample], None]] = []

        # Initialise snapshot.  Optimistic start: theoretical max, None confidence.
        self._current: BandwidthSample = self._build_snapshot(
            max_bandwidth_bps, timedelta(milliseconds=50)
        )

    # ── Public properties ─────────────────────────────────────────────────────

    @property
    def transport_name(self) -> str:
        return self._transport_name

    @property
    def btlbw_bps(self) -> int:
        return self._current.btlbw_bps

    @property
    def available_bps(self) -> int:
        return self._current.available_bps

    @property
    def bdp_bytes(self) -> int:
        return self._current.bdp_bytes

    @property
    def srtt(self) -> timedelta:
        return self._current.srtt

    @property
    def rtt_var(self) -> timedelta:
        return self._current.rtt_var

    @property
    def rt_prop(self) -> timedelta:
        return self._current.rt_prop

    @property
    def loss_rate(self) -> float:
        return self._current.loss_rate

    @property
    def confidence(self) -> BandwidthConfidence:
        return self._current.confidence

    def current_sample(self) -> BandwidthSample:
        """Return the current immutable snapshot (safe to share across threads)."""
        return self._current

    # ── Observation feed ──────────────────────────────────────────────────────

    def record_delivery(
        self, bytes_: int, send_us: int, deliver_us: int
    ) -> None:
        """Record a successful delivery.

        Both timestamps must be microseconds on the **same clock**.
        """
        if bytes_ <= 0 or deliver_us <= send_us:
            return

        elapsed_ms = (deliver_us - send_us) / 1000.0
        delivery_rate_bps = int(bytes_ * 8.0 / (elapsed_ms / 1000.0))
        rtt_ms = elapsed_ms  # one-way → treat as RTT (conservative)

        with self._lock:
            self._add_to_btlbw_window(delivery_rate_bps, _now_ms())
            self._update_rtt_estimates(rtt_ms)
            self._probe_rounds += 1
            self._commit()

    def record_loss(self, bytes_: int) -> None:
        """Record that *bytes_* were lost (timeout or explicit NAK)."""
        if bytes_ <= 0:
            return
        with self._lock:
            self._loss_rate = LOSS_ALPHA * 1.0 + (1.0 - LOSS_ALPHA) * self._loss_rate
            self._commit()

    def record_probe_result(
        self, ack: BandwidthProbeAck, local_receive_us: int
    ) -> None:
        """Feed an active probe ACK into the estimator.

        *local_receive_us* is the local clock µs at ACK receipt (unused in the
        current algorithm but kept for API parity with the C# reference).
        """
        rtt = ack.rtt
        if rtt <= timedelta(0) or rtt > timedelta(seconds=30):
            return

        delivery_rate_bps = (
            int(ack.probe_bytes * 8.0 / rtt.total_seconds())
            if ack.probe_bytes > 0
            else 0
        )

        with self._lock:
            self._update_rtt_estimates(rtt.total_seconds() * 1000.0)
            if delivery_rate_bps > 0:
                self._add_to_btlbw_window(delivery_rate_bps, _now_ms())
            self._probe_rounds += 1
            self._commit()

    def warm_from_gossip(
        self,
        btlbw_bps: int,
        rt_prop: timedelta,
        confidence: BandwidthConfidence,
    ) -> None:
        """Pre-warm from a gossip payload.

        Only effective when :attr:`confidence` is
        ``BandwidthConfidence.None_`` — never downgrades an existing estimate.
        The *confidence* parameter is accepted for API symmetry but the
        resulting confidence is re-derived from internal state (gossip seeds
        one round at ``Low``).
        """
        with self._lock:
            if self._probe_rounds > 0 or self._warmed_from_gossip:
                return  # never downgrade
            self._add_to_btlbw_window(btlbw_bps, _now_ms())
            rtt_ms = rt_prop.total_seconds() * 1000.0
            if rtt_ms > 0:
                self._srtt_ms = rtt_ms
                self._rtt_var_ms = rtt_ms / 2.0
                self._first_rtt = False
                self._add_to_rt_prop_window(rtt_ms, _now_ms())
            self._warmed_from_gossip = True
            self._commit()

    def apply_phy_hint(self, rssi_dbm: int) -> None:
        """Apply a physical-layer RSSI hint to cap the bandwidth estimate.

        Mapping follows BLE Bluetooth SIG Core Spec 5.4 / 802.11ax / 3GPP TS 36.213.
        This is a conservative BLE-based fallback; transport-specific callers
        should supply transport-specific RSSI tables.
        """
        if rssi_dbm >= -50:
            cap = 600_000_000
        elif rssi_dbm >= -67:
            cap = 200_000_000
        elif rssi_dbm >= -70:
            cap = 2_000_000
        elif rssi_dbm >= -80:
            cap = 54_000_000
        elif rssi_dbm >= -85:
            cap = 500_000
        elif rssi_dbm >= -95:
            cap = 125_000
        else:
            cap = 40_000

        with self._lock:
            self._phy_cap_bps = cap
            self._commit()

    # ── Internal helpers ──────────────────────────────────────────────────────

    def _update_rtt_estimates(self, rtt_ms: float) -> None:
        """RFC 6298 §2.3 RTT sample integration (Jacobson/Karels)."""
        if self._first_rtt:
            self._srtt_ms = rtt_ms
            self._rtt_var_ms = rtt_ms / 2.0
            self._first_rtt = False
        else:
            self._rtt_var_ms = (
                (1.0 - _RTT_VAR_BETA) * self._rtt_var_ms
                + _RTT_VAR_BETA * abs(self._srtt_ms - rtt_ms)
            )
            self._srtt_ms = (
                (1.0 - _SRTT_ALPHA) * self._srtt_ms + _SRTT_ALPHA * rtt_ms
            )

        # Successful delivery → EWMA loss rate contribution of 0.
        self._loss_rate = LOSS_ALPHA * 0.0 + (1.0 - LOSS_ALPHA) * self._loss_rate

        self._add_to_rt_prop_window(rtt_ms, _now_ms())

    def _add_to_btlbw_window(self, rate_bps: int, now_ms: float) -> None:
        """Insert a delivery-rate sample into the max-filter window.

        Evicts entries outside 10×RTprop.  The ``deque(maxlen=…)`` handles
        the hard cap; we additionally evict by time.
        """
        window_duration_ms = 10.0 * max(1.0, self._min_rt_prop_ms())
        expiry = now_ms - window_duration_ms

        # Trim expired entries from the left (oldest first).
        while self._btlbw_window and self._btlbw_window[0][1] < expiry:
            self._btlbw_window.popleft()

        self._btlbw_window.append((rate_bps, now_ms))

    def _add_to_rt_prop_window(self, rtt_ms: float, now_ms: float) -> None:
        """Insert an RTT sample into the min-filter window; evict stale entries."""
        self._rt_prop_samples.append((rtt_ms, now_ms))
        expiry = now_ms - RT_PROP_WINDOW_MS
        while self._rt_prop_samples and self._rt_prop_samples[0][1] < expiry:
            self._rt_prop_samples.popleft()

    def _max_btlbw_bps(self) -> int:
        if not self._btlbw_window:
            return 0
        return max(rate for rate, _ in self._btlbw_window)

    def _min_rt_prop_ms(self) -> float:
        if not self._rt_prop_samples:
            return self._srtt_ms if self._srtt_ms > 0 else 50.0
        m = min(rtt for rtt, _ in self._rt_prop_samples)
        return m if m > 0 else 1.0

    def _compute_confidence(self) -> BandwidthConfidence:
        if self._probe_rounds == 0 and not self._warmed_from_gossip:
            return BandwidthConfidence.None_
        if self._probe_rounds == 0:
            return BandwidthConfidence.Low
        if self._probe_rounds < 5:
            return BandwidthConfidence.Low
        if self._probe_rounds < 20:
            return BandwidthConfidence.Medium
        return BandwidthConfidence.High

    def _commit(self) -> None:
        """Rebuild the snapshot; fire ``on_sample_improved`` callbacks if significant."""
        prev = self._current
        new_btlbw = self._max_btlbw_bps()
        rt_prop = timedelta(milliseconds=self._min_rt_prop_ms())
        self._current = self._build_snapshot(new_btlbw, rt_prop)
        cur = self._current

        improved = (
            prev.btlbw_bps == 0
            or (cur.btlbw_bps - prev.btlbw_bps) > prev.btlbw_bps * _IMPROVEMENT_THRESHOLD
            or cur.confidence.value > prev.confidence.value
        )

        if improved:
            callbacks = list(self.on_sample_improved)
            if callbacks:
                # Fire outside the lock; use a local snapshot reference.
                sample_ref = cur

                def _fire(cbs: list, s: BandwidthSample) -> None:
                    for cb in cbs:
                        try:
                            cb(s)
                        except Exception:
                            pass

                t = threading.Thread(
                    target=_fire, args=(callbacks, sample_ref), daemon=True
                )
                t.start()

    def _build_snapshot(self, btlbw: int, rt_prop: timedelta) -> BandwidthSample:
        srtt_ms = max(1.0, self._srtt_ms)
        srtt = timedelta(milliseconds=srtt_ms)
        rtt_var = timedelta(milliseconds=max(0.0, self._rtt_var_ms))
        loss = max(0.0, min(1.0, self._loss_rate))

        # PHY cap applied to BtlBw before availability calculation.
        effective = (
            min(btlbw, self._phy_cap_bps) if self._phy_cap_bps > 0 else btlbw
        )
        available = int(effective * (1.0 - loss))
        bdp = (
            int(effective / 8.0 * rt_prop.total_seconds()) if effective > 0 else 0
        )

        return BandwidthSample(
            transport_name=self._transport_name,
            btlbw_bps=effective,
            available_bps=available,
            bdp_bytes=bdp,
            srtt=srtt,
            rtt_var=rtt_var,
            rt_prop=rt_prop,
            loss_rate=loss,
            phy_cap_bps=self._phy_cap_bps,
            confidence=self._compute_confidence(),
            measured_at=time.time(),
        )
