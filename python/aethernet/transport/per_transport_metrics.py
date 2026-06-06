# SPDX-License-Identifier: MIT

"""Real-time per-transport EWMA metrics for adaptive transport selection."""

import threading
from dataclasses import dataclass, field


_ALPHA = 0.2  # EWMA smoothing factor


class PerTransportMetrics:
    """
    Thread-safe, exponentially-weighted moving-average metrics for one transport.

    α = 0.2: the most-recent sample contributes 20 %; older history decays
    by a factor of 0.8 per observation.

    Initial priors:
      - ewma_rtt_ms   = 200 ms   (conservative assumption for unknown links)
      - ewma_loss_rate = 0.05    (5 % initial loss assumption)
      - ewma_throughput_bps = 0  (bootstrapped on first successful send)
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._sample_count: int = 0
        self._ewma_rtt_ms: float = 200.0
        self._ewma_loss_rate: float = 0.05
        self._ewma_throughput_bps: float = 0.0

    # ── Read-only accessors ───────────────────────────────────────────────────

    @property
    def sample_count(self) -> int:
        """Total samples recorded since this transport started."""
        with self._lock:
            return self._sample_count

    @property
    def ewma_rtt_ms(self) -> float:
        """EWMA round-trip time in milliseconds (lower = better)."""
        with self._lock:
            return self._ewma_rtt_ms

    @property
    def ewma_loss_rate(self) -> float:
        """EWMA packet-loss rate in [0, 1] (lower = better)."""
        with self._lock:
            return self._ewma_loss_rate

    @property
    def ewma_throughput_bps(self) -> float:
        """EWMA throughput in bits per second (higher = better)."""
        with self._lock:
            return self._ewma_throughput_bps

    # ── Mutation ──────────────────────────────────────────────────────────────

    def record_sample(
        self,
        rtt_ms: float,
        success: bool,
        bytes_transferred: int,
    ) -> None:
        """
        Update EWMA state from one send observation.

        Args:
            rtt_ms:            Measured round-trip time in ms (0 = one-way send).
            success:           Whether the peer acknowledged receipt.
            bytes_transferred: Payload bytes on wire; used for throughput.
        """
        with self._lock:
            self._sample_count += 1

            if rtt_ms > 0:
                self._ewma_rtt_ms = (
                    _ALPHA * rtt_ms + (1 - _ALPHA) * self._ewma_rtt_ms
                )

            loss_obs = 0.0 if success else 1.0
            self._ewma_loss_rate = (
                _ALPHA * loss_obs + (1 - _ALPHA) * self._ewma_loss_rate
            )

            if success and rtt_ms > 0 and bytes_transferred > 0:
                tput_bps = bytes_transferred * 8.0 * 1000.0 / rtt_ms
                if self._ewma_throughput_bps < 1.0:
                    self._ewma_throughput_bps = tput_bps  # bootstrap
                else:
                    self._ewma_throughput_bps = (
                        _ALPHA * tput_bps
                        + (1 - _ALPHA) * self._ewma_throughput_bps
                    )

    # ── Scoring ───────────────────────────────────────────────────────────────

    def composite_score(
        self,
        max_bandwidth_bps: int,
        power_cost_relative: int,
    ) -> float:
        """
        Composite score (higher = better transport to select right now).

        Formula:
            score = (effective_bps / power_cost) × (1 − loss_rate) / max(rtt_ms, 1)

        where effective_bps = max(ewma_throughput_bps, max_bandwidth_bps × 0.1)
        so zero-sample transports still rank by their declared capacity.
        """
        power = max(power_cost_relative, 1)
        with self._lock:
            rtt = max(self._ewma_rtt_ms, 1.0)
            loss = self._ewma_loss_rate
            tput = self._ewma_throughput_bps

        effective_bps = max(tput, max_bandwidth_bps * 0.1)
        return (effective_bps / power) * (1.0 - loss) / rtt
