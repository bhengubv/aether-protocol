# SPDX-License-Identifier: MIT

"""Cross-transport bandwidth synthesis and mesh gossip coordinator.

Pythonic port of ``AetherNet.Transport/Bandwidth/BandwidthDirector.cs`` (W18-5).

The director sits above individual :class:`~aethernet.bandwidth.estimator.BandwidthEstimator`
instances and provides:

1. **Multi-transport BDP matrix.** Maintains a per-peer × per-transport estimate
   matrix and answers "which transport should I use for a 1 MB transfer to peer X?"
   correctly, even when transports have wildly different bandwidth profiles.

2. **Mesh gossip pre-warming.** On handshake the director emits a
   :class:`~aethernet.bandwidth.models.BandwidthGossipPayload` carrying the
   local node's current BtlBw estimate so the new session starts warm (not cold).

Transport selection scoring (same algorithm as C# reference):

* ``score = available_bps / power_cost × bdp_bonus × confidence_factor``
* ``bdp_bonus = 1.5`` if ``payload_bytes ≤ bdp_bytes``, else ``1.0``
* ``confidence_factor = 0.5`` for ``None_`` confidence, ``1.0`` otherwise.

Thread safety
-------------
All state mutations are protected by ``threading.RLock``.
"""

from __future__ import annotations

import threading
from datetime import timedelta
from typing import Optional

from aethernet.bandwidth.estimator import BandwidthEstimator
from aethernet.bandwidth.models import (
    BandwidthConfidence,
    BandwidthGossipPayload,
    BandwidthSample,
)


# ── Power cost table ──────────────────────────────────────────────────────────

# Lower = preferred.  Mirrors ITransportService conventions in the C# reference.
_DEFAULT_POWER_COSTS: dict[str, int] = {
    "nearlink": 1,
    "ble": 2,
    "wi-fi direct": 3,
    "circlelink": 3,
    "quic relay": 10,
    "http relay": 10,
}

_DEFAULT_POWER_COST_FALLBACK: int = 5


def _power_cost(transport_name: str) -> int:
    return _DEFAULT_POWER_COSTS.get(transport_name.lower(), _DEFAULT_POWER_COST_FALLBACK)


# ── BandwidthDirector ─────────────────────────────────────────────────────────


class BandwidthDirector:
    """Cross-transport bandwidth synthesis and mesh gossip coordinator."""

    def __init__(self) -> None:
        self._lock = threading.RLock()

        # (peer_uhid_lower, transport_name_lower) → BandwidthSample
        self._matrix: dict[tuple[str, str], BandwidthSample] = {}

        # transport_name_lower → BandwidthEstimator
        self._estimators: dict[str, BandwidthEstimator] = {}

    # ── Registration ──────────────────────────────────────────────────────────

    def register(self, estimator: BandwidthEstimator) -> None:
        """Register a transport estimator.

        Call once per transport at startup.  When the estimator fires an
        ``on_sample_improved`` callback, all known peers' entries for that
        transport are refreshed in the matrix.
        """
        key = estimator.transport_name.lower()
        with self._lock:
            self._estimators[key] = estimator

        def _on_improved(sample: BandwidthSample) -> None:
            t_key = sample.transport_name.lower()
            with self._lock:
                for (p, t), _ in list(self._matrix.items()):
                    if t == t_key:
                        self._matrix[(p, t)] = sample

        estimator.on_sample_improved.append(_on_improved)

    # ── Query ──────────────────────────────────────────────────────────────────

    def get_estimate(
        self, peer_uhid: str, transport: str
    ) -> Optional[BandwidthSample]:
        """Return the current estimate for *peer_uhid* on *transport*, or ``None``."""
        key = (peer_uhid.lower(), transport.lower())
        with self._lock:
            return self._matrix.get(key)

    def get_estimates(self, peer_uhid: str) -> list[BandwidthSample]:
        """Return all estimates for *peer_uhid*, ranked by ``available_bps`` descending."""
        p = peer_uhid.lower()
        with self._lock:
            samples = [s for (peer, _), s in self._matrix.items() if peer == p]
        samples.sort(key=lambda s: s.available_bps, reverse=True)
        return samples

    def recommend_transport(
        self, peer_uhid: str, payload_bytes: int
    ) -> Optional[str]:
        """Recommend the best transport for a payload of *payload_bytes*.

        Returns ``None`` if the director has no registered estimators.
        Falls back to the lowest-power-cost registered transport when no
        measurement data exists yet.
        """
        candidates = self.get_estimates(peer_uhid)

        with self._lock:
            estimators = list(self._estimators.values())

        if not candidates:
            if not estimators:
                return None
            # No measurement data yet — fall back to lowest power cost.
            best = min(estimators, key=lambda e: _power_cost(e.transport_name))
            return best.transport_name

        best_sample: Optional[BandwidthSample] = None
        best_score = float("-inf")

        for s in candidates:
            power = float(_power_cost(s.transport_name))
            available = float(s.available_bps)
            bdp_bonus = 1.5 if payload_bytes <= s.bdp_bytes else 1.0
            conf_factor = 0.5 if s.confidence is BandwidthConfidence.None_ else 1.0
            score = (available / power) * bdp_bonus * conf_factor

            if score > best_score:
                best_score = score
                best_sample = s

        return best_sample.transport_name if best_sample is not None else None

    def build_gossip_payload(
        self, peer_uhid: str, transport: str
    ) -> Optional[BandwidthGossipPayload]:
        """Build a gossip payload for *peer_uhid* via *transport*.

        Returns ``None`` if no estimator is registered for *transport* or if
        the estimator's confidence is ``None_`` (not enough data to gossip).
        """
        t_key = transport.lower()
        with self._lock:
            estimator = self._estimators.get(t_key)
        if estimator is None:
            return None

        sample = estimator.current_sample()
        if sample.confidence is BandwidthConfidence.None_:
            return None

        return BandwidthGossipPayload(
            peer_uhid=peer_uhid,
            transport_name=transport,
            btlbw_bps=sample.btlbw_bps,
            rt_prop_us=int(sample.rt_prop.total_seconds() * 1_000_000),
            confidence=sample.confidence,
            measured_at=sample.measured_at,
        )

    def apply_gossip(self, payload: BandwidthGossipPayload) -> None:
        """Receive and apply a gossip payload from a remote peer.

        Forwards the warm-start data to the appropriate estimator and seeds
        the matrix so :meth:`get_estimate` returns something before probing.
        """
        t_key = payload.transport_name.lower()
        with self._lock:
            estimator = self._estimators.get(t_key)
        if estimator is None:
            return

        estimator.warm_from_gossip(
            payload.btlbw_bps,
            timedelta(microseconds=payload.rt_prop_us),
            payload.confidence,
        )

        matrix_key = (payload.peer_uhid.lower(), t_key)
        with self._lock:
            self._matrix[matrix_key] = estimator.current_sample()
