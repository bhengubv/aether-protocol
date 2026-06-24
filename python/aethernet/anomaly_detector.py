# SPDX-License-Identifier: MIT

"""Behavioural anomaly detector — translates mesh traffic patterns into reputation signals.

Detectable patterns:
  1. Volume spike       : packet rate > spike_multiplier × EWMA baseline in a 30 s window
                          → record_rreq_flood_attempt
  2. Destination scatter: single source contacts > scatter_threshold unique destinations
                          in a 60 s sliding window
                          → record_rreq_flood_attempt
  3. Geohash mismatch   : claimed geohash prefix ≠ observed routing prefix
                          → record_signature_failure (rate-limited to 1 signal per 60 s per node)
  4. SPK-sig failure    : direct passthrough
                          → record_signature_failure (no rate limiting)
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from aethernet.reputation import NodeReputationService


@dataclass
class AnomalyDetectorOptions:
    """Configurable thresholds for :class:`BehavioralAnomalyDetector`."""

    volume_window_ms: int = 30_000
    """Width of the EWMA volume window in milliseconds."""

    volume_spike_multiplier: float = 5.0
    """Ratio of current window count to EWMA baseline that constitutes a spike."""

    ewma_alpha: float = 0.20
    """Smoothing factor for the exponentially-weighted moving average."""

    scatter_window_ms: int = 60_000
    """Sliding window width (ms) for destination-scatter detection."""

    scatter_threshold: int = 50
    """Number of unique destinations in the window that triggers a flood signal."""

    geohash_prefix_length: int = 4
    """Number of leading characters compared for geohash mismatch detection."""

    geohash_rate_limit_ms: int = 60_000
    """Minimum gap (ms) between geohash mismatch signals for the same node."""


# ---------------------------------------------------------------------------
# Per-node state containers (private)
# ---------------------------------------------------------------------------

@dataclass
class _VolumeState:
    window_start_ms: int | None = None   # None = "no window open yet"
    window_count: int = 0
    ewma_baseline: float = 0.0           # 0.0 means "no completed window yet"


@dataclass
class _ScatterState:
    # List of (destination_uhid, timestamp_ms) — pruned as entries age out
    entries: list[tuple[str, int]] = field(default_factory=list)


# ---------------------------------------------------------------------------
# Main class
# ---------------------------------------------------------------------------

class BehavioralAnomalyDetector:
    """Observes mesh traffic and converts anomalous patterns into reputation signals.

    All public methods are thread-safe.  Per-node state is guarded by individual
    :class:`threading.Lock` objects to minimise contention.

    Args:
        reputation: The :class:`~aethernet.reputation.NodeReputationService` that
            receives the emitted signals.
        options: Optional threshold configuration; uses defaults if *None*.
    """

    def __init__(
        self,
        reputation: "NodeReputationService",
        options: AnomalyDetectorOptions | None = None,
    ) -> None:
        self._reputation = reputation
        self._opts = options or AnomalyDetectorOptions()

        # Per-UHID state — created on first access under _meta_lock
        self._meta_lock = threading.Lock()

        self._volume_states: dict[str, _VolumeState] = {}
        self._volume_locks: dict[str, threading.Lock] = {}

        self._scatter_states: dict[str, _ScatterState] = {}
        self._scatter_locks: dict[str, threading.Lock] = {}

        # Last timestamp at which a geohash mismatch signal was emitted, per UHID
        self._geo_last_signal_ms: dict[str, int] = {}
        self._geo_lock = threading.Lock()

    # ------------------------------------------------------------------ public

    def observe_packet(
        self,
        source_uhid: str,
        destination_uhid: str,
        timestamp_ms: int,
    ) -> None:
        """Record a forwarded/sent packet and check for volume-spike and destination-scatter.

        Args:
            source_uhid:      UHID that originated the packet.
            destination_uhid: UHID that is the packet's next-hop / final destination.
            timestamp_ms:     Wall-clock timestamp when the packet was observed (ms).
        """
        self._check_volume_spike(source_uhid, timestamp_ms)
        self._check_destination_scatter(source_uhid, destination_uhid, timestamp_ms)

    def observe_geohash_claim(
        self,
        uhid: str,
        claimed_geohash: str,
        observed_routing_geohash: str,
    ) -> None:
        """Check whether a node's claimed geohash matches the routing-layer observation.

        Emits :meth:`~aethernet.reputation.NodeReputationService.record_signature_failure`
        when the first ``geohash_prefix_length`` characters differ, subject to a
        per-node rate limit of one signal per ``geohash_rate_limit_ms``.

        Args:
            uhid:                     The node making the claim.
            claimed_geohash:          Geohash string the node advertised.
            observed_routing_geohash: Geohash derived from actual routing behaviour.
        """
        # No caller-supplied timestamp here, so capture the system wall-clock and delegate
        # to the timestamp-aware path. This gives a REAL per-node time window — fire on the
        # first prefix mismatch, suppress repeats within geohash_rate_limit_ms, then reset
        # once the window elapses — instead of the previous fire-once-per-node sentinel that
        # set last_ms=0 and never reset, silently dropping every later mismatch (so a
        # persistent geohash spoofer was penalised exactly once, then ignored forever).
        now_ms = int(time.time() * 1000)
        self.observe_geohash_claim_ts(uhid, claimed_geohash, observed_routing_geohash, now_ms)

    def observe_geohash_claim_ts(
        self,
        uhid: str,
        claimed_geohash: str,
        observed_routing_geohash: str,
        timestamp_ms: int,
    ) -> None:
        """Timestamp-aware variant of :meth:`observe_geohash_claim`.

        Preferred when the caller has a reliable wall-clock value; allows the
        rate-limit window to reset after ``geohash_rate_limit_ms`` has elapsed.

        Args:
            uhid:                     The node making the claim.
            claimed_geohash:          Geohash string the node advertised.
            observed_routing_geohash: Geohash derived from actual routing behaviour.
            timestamp_ms:             Observation time in milliseconds.
        """
        pl = self._opts.geohash_prefix_length
        if claimed_geohash[:pl] == observed_routing_geohash[:pl]:
            return

        with self._geo_lock:
            last_ms = self._geo_last_signal_ms.get(uhid, -1)
            if last_ms == -1 or (timestamp_ms - last_ms) >= self._opts.geohash_rate_limit_ms:
                self._geo_last_signal_ms[uhid] = timestamp_ms
                should_signal = True
            else:
                should_signal = False

        if should_signal:
            self._reputation.record_signature_failure(uhid)

    def observe_spk_sig_failure(self, uhid: str) -> None:
        """Pass a signed-pre-key signature failure directly to the reputation service.

        No rate-limiting is applied; every failure is forwarded immediately.

        Args:
            uhid: The node whose SPK signature failed verification.
        """
        self._reputation.record_signature_failure(uhid)

    # ----------------------------------------------------------------- private

    def _get_or_create_volume_state(self, uhid: str) -> tuple[_VolumeState, threading.Lock]:
        with self._meta_lock:
            if uhid not in self._volume_states:
                self._volume_states[uhid] = _VolumeState()
                self._volume_locks[uhid] = threading.Lock()
            return self._volume_states[uhid], self._volume_locks[uhid]

    def _get_or_create_scatter_state(self, uhid: str) -> tuple[_ScatterState, threading.Lock]:
        with self._meta_lock:
            if uhid not in self._scatter_states:
                self._scatter_states[uhid] = _ScatterState()
                self._scatter_locks[uhid] = threading.Lock()
            return self._scatter_states[uhid], self._scatter_locks[uhid]

    def _check_volume_spike(self, uhid: str, timestamp_ms: int) -> None:
        """EWMA volume-spike detection for *uhid* at *timestamp_ms*."""
        state, lock = self._get_or_create_volume_state(uhid)
        spike_detected = False

        with lock:
            window_ms = self._opts.volume_window_ms
            alpha = self._opts.ewma_alpha
            multiplier = self._opts.volume_spike_multiplier

            if state.window_start_ms is None:
                # First packet ever seen — open the first window
                state.window_start_ms = timestamp_ms
                state.window_count = 1
                return

            if timestamp_ms - state.window_start_ms < window_ms:
                # Still within the current window — increment and check for spike
                state.window_count += 1
                if state.ewma_baseline > 0.0:
                    if state.window_count > multiplier * state.ewma_baseline:
                        spike_detected = True
            else:
                # Window has elapsed — commit it to the EWMA, open a new one
                completed_count = state.window_count
                if state.ewma_baseline == 0.0:
                    # First completed window seeds the EWMA baseline
                    state.ewma_baseline = float(completed_count)
                else:
                    state.ewma_baseline = (
                        alpha * completed_count + (1.0 - alpha) * state.ewma_baseline
                    )

                # Open a new window for this packet
                state.window_start_ms = timestamp_ms
                state.window_count = 1

        if spike_detected:
            self._reputation.record_rreq_flood_attempt(uhid)

    def _check_destination_scatter(
        self,
        source_uhid: str,
        destination_uhid: str,
        timestamp_ms: int,
    ) -> None:
        """Destination-scatter detection for *source_uhid* at *timestamp_ms*."""
        state, lock = self._get_or_create_scatter_state(source_uhid)
        scatter_detected = False

        with lock:
            window_ms = self._opts.scatter_window_ms
            threshold = self._opts.scatter_threshold
            cutoff = timestamp_ms - window_ms

            # Prune entries that have aged out of the sliding window
            state.entries = [
                (dest, ts) for (dest, ts) in state.entries if ts > cutoff
            ]

            # Record this observation
            state.entries.append((destination_uhid, timestamp_ms))

            # Count unique destinations in the window
            unique_dests = {dest for dest, _ in state.entries}
            if len(unique_dests) > threshold:
                scatter_detected = True

        if scatter_detected:
            self._reputation.record_rreq_flood_attempt(source_uhid)
