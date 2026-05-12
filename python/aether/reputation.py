# SPDX-License-Identifier: MIT

"""Node reputation service — aggregates per-UHID behavioural signals into [0.0, 1.0].

Score semantics:
  1.0 = pristine — no negative signals observed.
  0.5 = degraded — recurring minor violations or unreliable delivery.
  0.0 = untrusted — active attacker or catastrophic failure rate.

Signal deltas (matches InMemoryNodeReputationService.cs exactly):
  RREQ flood attempt   : −0.05
  Replay attempt       : −0.15
  Signature failure    : −0.20
  Custody refusal      : −0.05
  Delivery failure     : −0.02
  Delivery success     : +0.01
"""

from __future__ import annotations

import threading


_DELTA_RREQ_FLOOD: float = -0.05
_DELTA_REPLAY: float = -0.15
_DELTA_SIG_FAILURE: float = -0.20
_DELTA_CUSTODY_REFUSAL: float = -0.05
_DELTA_DELIVERY_FAIL: float = -0.02
_DELTA_DELIVERY_OK: float = +0.01

_EPSILON: float = 1e-12


class NodeReputationService:
    """Thread-safe, in-memory node reputation tracker.

    Unknown peers default to 1.0 (benefit of the doubt) until signals arrive.
    Scores are clamped to [0.0, 1.0] with epsilon-snap to avoid sub-ULP
    values like 5.5e-17 accumulating.
    """

    def __init__(self) -> None:
        self._scores: dict[str, float] = {}
        self._lock = threading.Lock()

    # ── Public recording API ─────────────────────────────────────────────────

    def record_rreq_flood_attempt(self, uhid: str) -> None:
        """Record that a RREQ was rate-limited from *uhid*."""
        self._apply_delta(uhid, _DELTA_RREQ_FLOOD)

    def record_replay_attempt(self, uhid: str) -> None:
        """Record a duplicate-nonce replay from *uhid*."""
        self._apply_delta(uhid, _DELTA_REPLAY)

    def record_signature_failure(self, uhid: str) -> None:
        """Record an Ed25519 signature verification failure from *uhid*."""
        self._apply_delta(uhid, _DELTA_SIG_FAILURE)

    def record_custody_refusal(self, uhid: str) -> None:
        """Record a DTN custody refusal by *uhid*."""
        self._apply_delta(uhid, _DELTA_CUSTODY_REFUSAL)

    def record_delivery_success(self, uhid: str, round_trip_ms: int) -> None:
        """Record a confirmed successful delivery via *uhid*.

        Args:
            uhid: The peer UHID that relayed/delivered the packet.
            round_trip_ms: Observed round-trip latency in milliseconds.
        """
        self._apply_delta(uhid, _DELTA_DELIVERY_OK)

    def record_delivery_failure(self, uhid: str) -> None:
        """Record a delivery failure (lost bundle / unacknowledged hop) through *uhid*."""
        self._apply_delta(uhid, _DELTA_DELIVERY_FAIL)

    # ── Query API ────────────────────────────────────────────────────────────

    def get_reputation_score(self, uhid: str) -> float:
        """Return the current reputation score for *uhid* in [0.0, 1.0].

        Returns 1.0 for unknown peers (benefit of the doubt until signals arrive).
        """
        with self._lock:
            return self._scores.get(uhid, 1.0)

    def get_all_scores(self) -> dict[str, float]:
        """Return a snapshot copy of all known reputation scores."""
        with self._lock:
            return dict(self._scores)

    def apply_weighted_delta(self, uhid: str, weighted_delta: float) -> None:
        """Apply a pre-weighted score delta (for gossip propagation). Clamped to [-1, 1]."""
        clamped = max(-1.0, min(1.0, weighted_delta))
        self._apply_delta(uhid, clamped)

    # ── Private helpers ──────────────────────────────────────────────────────

    @staticmethod
    def _clamp_score(v: float) -> float:
        """Clamp *v* to [0.0, 1.0] with epsilon-snap.

        Values within 1e-12 of 0 are snapped to exactly 0.0; values within
        1e-12 of 1 are snapped to exactly 1.0.  This prevents sub-ULP
        residues (e.g. 5.5e-17) from accumulating over many deltas.
        """
        clamped = max(0.0, min(1.0, v))
        if clamped < _EPSILON:
            return 0.0
        if clamped > 1.0 - _EPSILON:
            return 1.0
        return clamped

    def _apply_delta(self, uhid: str, delta: float) -> None:
        """Add *delta* to *uhid*'s score and clamp the result."""
        with self._lock:
            current = self._scores.get(uhid, 1.0)
            self._scores[uhid] = self._clamp_score(current + delta)
