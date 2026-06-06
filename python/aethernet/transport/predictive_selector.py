# SPDX-License-Identifier: MIT
"""
Predictive transport selector — 2-state Kalman RTT filter over PerTransportMetrics.

Why Kalman over EWMA?
─────────────────────
EWMA is a 1-pole IIR: it smooths past measurements but cannot predict future RTT
when a link is actively degrading.  The Kalman filter models RTT as a constant-
velocity process [rtt, drift]:

    x_t = F * x_{t−1} + w   (F = [[1,1],[0,1]])
    z_t = H * x_t   + v    (H = [1,0])

Positive drift signals a rising RTT *before* it exceeds a threshold, enabling
proactive transport switching.  The posterior variance further penalises uncertain
links even when their point estimate looks good.

Score formula:
    (effective_bps / power_cost) × (1 − loss_rate) / max(kalman_rtt, 1)
        × (1 / (1 + σ_rtt / 100))

Thread-safe via threading.RLock.
"""

from __future__ import annotations

import math
import threading
from dataclasses import dataclass
from typing import TYPE_CHECKING, Dict, List, Optional, Tuple

if TYPE_CHECKING:
    from aethernet.transport.transport_service import TransportService


# ── _KalmanRttFilter ──────────────────────────────────────────────────────────

class _KalmanRttFilter:
    """
    Two-state Kalman filter estimating RTT and its drift for one transport link.

    State vector: x = [rtt; drift]
    Process model: F = [[1, 1], [0, 1]]   (constant-velocity assumption)
    Observation:   H = [1, 0]             (only RTT is measured directly)

    Not thread-safe — callers must hold the selector lock.
    """

    __slots__ = ("_q_rtt", "_q_drift", "_r", "_rtt", "_drift", "_p00", "_p01", "_p11")

    def __init__(
        self,
        initial_rtt_ms: float = 200.0,
        q_rtt:          float = 25.0,
        q_drift:        float = 5.0,
        r:              float = 100.0,
    ) -> None:
        self._q_rtt   = q_rtt
        self._q_drift = q_drift
        self._r       = r
        self._rtt     = initial_rtt_ms
        self._drift   = 0.0
        # Initial covariance: large uncertainty on both states.
        self._p00 = 400.0
        self._p01 = 0.0
        self._p11 = 100.0

    # ── Read-only properties ──────────────────────────────────────────────

    @property
    def rtt_estimate_ms(self) -> float:
        """Current best estimate of RTT in milliseconds."""
        return self._rtt

    @property
    def drift_ms(self) -> float:
        """Current RTT drift (ms per sample). Positive = rising RTT."""
        return self._drift

    @property
    def rtt_variance(self) -> float:
        """Posterior variance of the RTT estimate (ms²). Lower = more confident."""
        return self._p00

    # ── Update ────────────────────────────────────────────────────────────

    def update(self, measured_rtt_ms: float) -> float:
        """
        Incorporate a new RTT measurement and return the updated estimate.

        Full Kalman predict→update cycle:
          1. Predict:  x̂ = F * x,  P̂ = F * P * Fᵀ + Q
          2. Gain:     S = H * P̂ * Hᵀ + R,  K = P̂ * Hᵀ / S
          3. Update:   x = x̂ + K * (z − H * x̂),  P = (I − K * H) * P̂
        """
        # ── 1. Predict ────────────────────────────────────────────────────────
        rtt_pred   = self._rtt + self._drift
        drift_pred = self._drift

        # P_pred = F * P * F^T + Q  (F = [[1,1],[0,1]])
        pp00 = self._p00 + 2.0 * self._p01 + self._p11 + self._q_rtt
        pp01 = self._p01 + self._p11
        pp11 = self._p11 + self._q_drift

        # ── 2. Kalman gain (H = [1, 0]) ──────────────────────────────────────
        s  = pp00 + self._r
        k0 = pp00 / s
        k1 = pp01 / s

        # ── 3. Update ─────────────────────────────────────────────────────────
        innovation  = measured_rtt_ms - rtt_pred
        self._rtt   = rtt_pred   + k0 * innovation
        self._drift = drift_pred + k1 * innovation

        # P = (I − K*H) * P_pred
        self._p00 = (1.0 - k0) * pp00
        self._p01 = (1.0 - k0) * pp01
        self._p11 = -k1 * pp01 + pp11

        # Clamp to prevent numerical drift below zero.
        self._p00 = max(self._p00, 1e-6)
        self._p11 = max(self._p11, 1e-6)

        return self._rtt


# ── PredictiveTransportSelector ───────────────────────────────────────────────

@dataclass
class PredictedRankedTransport:
    """A transport paired with its Kalman-predictive score and uncertainty metadata."""

    transport:        "TransportService"
    score:            float
    predicted_rtt_ms: float
    rtt_variance:     float


class PredictiveTransportSelector:
    """
    Predictive transport selector using per-transport Kalman RTT filters.

    Extends :func:`rank_transports` by replacing the EWMA RTT term with a
    Kalman-estimated RTT and adding a reliability penalty proportional to the
    RTT variance.

    Thread-safe via :class:`threading.RLock`.

    Score formula::

        score = (effective_bps / power_cost) × (1 − loss_rate)
                / max(kalman_rtt, 1) × (1 / (1 + σ_rtt / 100))

    where σ_rtt = sqrt(kalman_variance).
    """

    def __init__(self) -> None:
        self._lock: threading.RLock = threading.RLock()
        self._filters: Dict["TransportService", _KalmanRttFilter] = {}

    # ── Registration ──────────────────────────────────────────────────────

    def register(
        self,
        transport:       "TransportService",
        initial_rtt_ms:  float = 200.0,
    ) -> None:
        """Register *transport* for Kalman tracking with an initial RTT prior."""
        with self._lock:
            if transport not in self._filters:
                self._filters[transport] = _KalmanRttFilter(initial_rtt_ms)

    def unregister(self, transport: "TransportService") -> None:
        """Remove *transport* and discard its Kalman state."""
        with self._lock:
            self._filters.pop(transport, None)

    # ── Observation ───────────────────────────────────────────────────────

    def observe_metrics(
        self,
        transport:         "TransportService",
        rtt_ms:            int,
        success:           bool,
        bytes_transferred: int,
    ) -> None:
        """
        Feed a new sample to both the transport's PerTransportMetrics EWMA
        and our Kalman filter.  Call after every completed send attempt.

        Only successful sends with rtt_ms > 0 update the Kalman state
        (failures carry no useful propagation-delay signal).
        """
        # Forward to transport's own EWMA store.
        m = getattr(transport, "metrics", None)
        if m is not None:
            m.record_sample(rtt_ms, success, bytes_transferred)

        if rtt_ms <= 0 or not success:
            return

        with self._lock:
            f = self._filters.get(transport)
            if f is not None:
                f.update(float(rtt_ms))

    # ── Ranking ───────────────────────────────────────────────────────────

    def rank(self, payload_bytes: int = 512) -> List[PredictedRankedTransport]:
        """
        Return transports sorted by predictive score (highest first).

        Only available transports are included.  *payload_bytes* excludes
        transports whose max bandwidth would require > 30 s to serialise.
        """
        with self._lock:
            result: List[PredictedRankedTransport] = []

            for transport, filt in self._filters.items():
                if not getattr(transport, "is_available", False):
                    continue

                bw: int = getattr(transport, "max_bandwidth_bps", 0)
                if bw > 0:
                    serial_sec = (payload_bytes * 8.0) / bw
                    if serial_sec > 30.0:
                        continue

                kalman_rtt = max(filt.rtt_estimate_ms, 1.0)
                variance   = filt.rtt_variance
                stddev     = math.sqrt(variance)
                power_cost = max(getattr(transport, "power_cost_relative", 1), 1)

                m = getattr(transport, "metrics", None)
                if m is not None:
                    loss_rate     = m.ewma_loss_rate
                    effective_bps = max(m.ewma_throughput_bps, bw * 0.1)
                else:
                    loss_rate     = 0.05
                    effective_bps = bw * 0.1

                # Reliability factor: 1.0 at σ=0, ~0.5 at σ=100 ms.
                reliability_factor = 1.0 / (1.0 + stddev / 100.0)
                score = (
                    (effective_bps / power_cost)
                    * (1.0 - loss_rate)
                    / kalman_rtt
                    * reliability_factor
                )

                result.append(PredictedRankedTransport(
                    transport=transport,
                    score=score,
                    predicted_rtt_ms=kalman_rtt,
                    rtt_variance=variance,
                ))

            result.sort(key=lambda rt: rt.score, reverse=True)
            return result

    def select_best(
        self, payload_bytes: int = 512
    ) -> Optional["TransportService"]:
        """Return the highest-scoring available transport, or ``None``."""
        ranked = self.rank(payload_bytes)
        return ranked[0].transport if ranked else None

    def get_kalman_state(
        self, transport: "TransportService"
    ) -> Optional[Tuple[float, float, float]]:
        """
        Return ``(rtt_ms, drift_ms, variance)`` for a registered transport,
        or ``None`` if not registered.
        """
        with self._lock:
            f = self._filters.get(transport)
            if f is None:
                return None
            return (f.rtt_estimate_ms, f.drift_ms, f.rtt_variance)
