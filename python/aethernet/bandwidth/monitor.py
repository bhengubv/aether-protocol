# SPDX-License-Identifier: MIT

"""Observable node activity monitor — the UI-facing layer of the ABMF.

Pythonic port of ``AetherNet.Transport/Bandwidth/NodeActivityMonitor.cs`` (W18-5).

Runs a background :class:`threading.Timer` loop at ``sample_interval_ms``
milliseconds.  Each tick computes ingress/egress rates from atomic byte counters,
reads per-transport estimates from registered estimators, and notifies subscribers.

Rate computation: byte deltas are divided by the elapsed wall-clock interval.
The sample interval itself acts as the averaging window (same as C# reference).

Subscriber protocol
-------------------
:meth:`subscribe` accepts a ``Callable[[NodeActivitySnapshot], None]`` and
returns a zero-argument *unsubscribe* callable.  This avoids the
``IObservable<T>`` pattern which has no direct Python analogue.
"""

from __future__ import annotations

import threading
import time
from typing import Callable, Optional

from aethernet.bandwidth.estimator import BandwidthEstimator
from aethernet.bandwidth.models import (
    NodeActivitySnapshot,
    NodeActivityState,
    TransportActivitySnapshot,
)


# ── _TransportTraffic ─────────────────────────────────────────────────────────


class _TransportTraffic:
    """Mutable traffic accumulators for one transport (reset each tick)."""

    __slots__ = ("ingress_bytes", "egress_bytes", "last_egress_ms", "_lock")

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.ingress_bytes: int = 0
        self.egress_bytes: int = 0
        self.last_egress_ms: float = time.time() * 1000.0

    def add_ingress(self, n: int) -> None:
        with self._lock:
            self.ingress_bytes += n

    def add_egress(self, n: int) -> None:
        with self._lock:
            self.egress_bytes += n
            self.last_egress_ms = time.time() * 1000.0

    def take_ingress(self) -> int:
        with self._lock:
            v = self.ingress_bytes
            self.ingress_bytes = 0
            return v

    def take_egress(self) -> int:
        with self._lock:
            v = self.egress_bytes
            self.egress_bytes = 0
            return v

    def last_egress_age_ms(self) -> float:
        return time.time() * 1000.0 - self.last_egress_ms


# ── NodeActivityMonitor ───────────────────────────────────────────────────────


class NodeActivityMonitor:
    """Observable node activity monitor.

    Produces :class:`~aethernet.bandwidth.models.NodeActivitySnapshot` objects
    at a configurable cadence (default 500 ms).  Each snapshot aggregates
    per-transport ingress/egress rates, active peer counts, and a unified
    :class:`~aethernet.bandwidth.models.NodeActivityState` for status indicators.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()

        # transport_name_lower → (BandwidthEstimator, _TransportTraffic)
        self._transports: dict[str, tuple[BandwidthEstimator, _TransportTraffic]] = {}

        # peer_uhid → last-seen Unix ms. A peer is "active" if it had ingress or
        # egress within idle_threshold_seconds. Populated only by the peer-aware
        # record_*_*_peer methods; the transport-only record_ingress/record_egress
        # do not contribute (the caller did not supply a peer). Stale entries are
        # pruned each tick so the dict stays bounded by recently-active peers.
        # Guarded by ``self._lock``.
        self._last_seen_peer_ms: dict[str, float] = {}

        self._sample_interval_ms: int = 500
        self._idle_threshold_seconds: int = 5

        self._current: NodeActivitySnapshot = _offline_snapshot()
        self._subscribers: list[Callable[[NodeActivitySnapshot], None]] = []

        self._timer: Optional[threading.Timer] = None
        self._running: bool = False
        self._last_tick_ms: float = time.time() * 1000.0

    # ── Configuration ──────────────────────────────────────────────────────────

    @property
    def sample_interval_ms(self) -> int:
        """How often the monitor re-samples (milliseconds). Default: 500."""
        return self._sample_interval_ms

    @sample_interval_ms.setter
    def sample_interval_ms(self, value: int) -> None:
        self._sample_interval_ms = max(100, min(60_000, value))
        # Restart timer with new interval if running.
        if self._running:
            self.stop()
            self.start()

    @property
    def idle_threshold_seconds(self) -> int:
        """Seconds without traffic before a transport is considered idle. Default: 5."""
        return self._idle_threshold_seconds

    @idle_threshold_seconds.setter
    def idle_threshold_seconds(self, value: int) -> None:
        self._idle_threshold_seconds = max(1, min(300, value))

    # ── Registration ──────────────────────────────────────────────────────────

    def register(self, name: str, estimator: BandwidthEstimator) -> None:
        """Register a transport estimator so its activity appears in snapshots."""
        with self._lock:
            self._transports[name.lower()] = (estimator, _TransportTraffic())

    # ── Traffic recording ──────────────────────────────────────────────────────

    def record_ingress(self, transport: str, bytes_: int) -> None:
        """Record inbound bytes on *transport*. Call from the transport receive path."""
        with self._lock:
            entry = self._transports.get(transport.lower())
        if entry is not None:
            entry[1].add_ingress(bytes_)

    def record_egress(self, transport: str, bytes_: int) -> None:
        """Record outbound bytes on *transport*. Call from the transport send path."""
        with self._lock:
            entry = self._transports.get(transport.lower())
        if entry is not None:
            entry[1].add_egress(bytes_)

    def record_ingress_from_peer(
        self, transport: str, peer_uhid: str, bytes_: int
    ) -> None:
        """Record inbound bytes on *transport* from a specific peer.

        Tracks the peer for the :attr:`NodeActivitySnapshot.active_peers` count.
        """
        self.record_ingress(transport, bytes_)
        if peer_uhid:
            with self._lock:
                self._last_seen_peer_ms[peer_uhid] = time.time() * 1000.0

    def record_egress_to_peer(
        self, transport: str, peer_uhid: str, bytes_: int
    ) -> None:
        """Record outbound bytes on *transport* to a specific peer.

        Tracks the peer for the :attr:`NodeActivitySnapshot.active_peers` count.
        """
        self.record_egress(transport, bytes_)
        if peer_uhid:
            with self._lock:
                self._last_seen_peer_ms[peer_uhid] = time.time() * 1000.0

    # ── Lifecycle ──────────────────────────────────────────────────────────────

    def start(self) -> None:
        """Start the background sampling loop."""
        with self._lock:
            if self._running:
                return
            self._running = True
            self._last_tick_ms = time.time() * 1000.0
        self._schedule_next()

    def stop(self) -> None:
        """Stop the background sampling loop."""
        with self._lock:
            self._running = False
            if self._timer is not None:
                self._timer.cancel()
                self._timer = None

    # ── Snapshot access ────────────────────────────────────────────────────────

    def current(self) -> NodeActivitySnapshot:
        """Return the most recent snapshot. Thread-safe (reference is immutable)."""
        return self._current

    # ── Subscriptions ──────────────────────────────────────────────────────────

    def subscribe(
        self, callback: Callable[[NodeActivitySnapshot], None]
    ) -> Callable[[], None]:
        """Subscribe to snapshot updates.

        Returns an *unsubscribe* callable that, when called, removes this
        callback from the notification list.
        """
        with self._lock:
            self._subscribers.append(callback)

        def unsubscribe() -> None:
            with self._lock:
                try:
                    self._subscribers.remove(callback)
                except ValueError:
                    pass

        return unsubscribe

    # ── Timer callback ─────────────────────────────────────────────────────────

    def _schedule_next(self) -> None:
        with self._lock:
            if not self._running:
                return
            interval_s = self._sample_interval_ms / 1000.0
            t = threading.Timer(interval_s, self._on_tick)
            t.daemon = True
            self._timer = t
        t.start()

    def _on_tick(self) -> None:
        now_ms = time.time() * 1000.0
        with self._lock:
            elapsed_s = max(0.001, (now_ms - self._last_tick_ms) / 1000.0)
            self._last_tick_ms = now_ms
            transports_snapshot = list(self._transports.items())
            idle_threshold_ms = self._idle_threshold_seconds * 1000.0
            subscribers = list(self._subscribers)

            # Count distinct peers active within the idle window; prune stale
            # entries so the dict stays bounded by recently-active peers.
            active_peers = 0
            for peer_uhid, last_seen in list(self._last_seen_peer_ms.items()):
                if now_ms - last_seen < idle_threshold_ms:
                    active_peers += 1
                else:
                    del self._last_seen_peer_ms[peer_uhid]

        transport_snapshots: list[TransportActivitySnapshot] = []
        total_ingress = 0
        total_egress = 0
        active_transports = 0

        for name, (estimator, traffic) in transports_snapshot:
            ingress_delta = traffic.take_ingress()
            egress_delta = traffic.take_egress()

            ingress_bps = int(ingress_delta * 8.0 / elapsed_s)
            egress_bps = int(egress_delta * 8.0 / elapsed_s)

            sample = estimator.current_sample()
            util_fraction = (
                max(0.0, min(1.0, egress_bps / sample.btlbw_bps))
                if sample.btlbw_bps > 0
                else 0.0
            )

            is_recent = traffic.last_egress_age_ms() < idle_threshold_ms
            state = _compute_transport_state(
                egress_bps, ingress_bps, sample.loss_rate, sample.btlbw_bps, is_recent
            )

            if state not in (NodeActivityState.Offline, NodeActivityState.Idle):
                active_transports += 1

            total_ingress += ingress_bps
            total_egress += egress_bps

            transport_snapshots.append(
                TransportActivitySnapshot(
                    transport_name=name,
                    is_available=True,
                    ingress_bps=ingress_bps,
                    egress_bps=egress_bps,
                    srtt=sample.srtt,
                    btlbw_bps=sample.btlbw_bps,
                    utilization_fraction=util_fraction,
                    state=state,
                    confidence=sample.confidence,
                )
            )

        node_state = _compute_node_state(transport_snapshots)
        primary = (
            max(transport_snapshots, key=lambda t: t.egress_bps).transport_name
            if transport_snapshots
            else None
        )

        snapshot = NodeActivitySnapshot(
            state=node_state,
            ingress_bps=total_ingress,
            egress_bps=total_egress,
            active_peers=active_peers,
            active_transports=active_transports,
            transports=tuple(transport_snapshots),
            primary_transport_name=primary,
            timestamp=time.time(),
        )

        self._current = snapshot

        # Notify subscribers unconditionally (heartbeat semantic).
        for cb in subscribers:
            try:
                cb(snapshot)
            except Exception:
                pass

        self._schedule_next()


# ── State computation helpers ─────────────────────────────────────────────────


def _compute_transport_state(
    egress_bps: int,
    ingress_bps: int,
    loss_rate: float,
    btlbw_bps: int,
    is_recent: bool,
) -> NodeActivityState:
    # Mirrors the C# reference NodeActivityMonitor.ComputeTransportState exactly:
    # a transport with no recent egress AND zero current rates is Idle; a transport
    # with zero current rates (regardless of recency) is also Idle. Otherwise the
    # loss/utilization logic applies. ``is_recent`` = (now - last_egress) < idle
    # threshold, tracked per-transport on the egress path.
    if not is_recent and egress_bps == 0 and ingress_bps == 0:
        return NodeActivityState.Idle
    if egress_bps == 0 and ingress_bps == 0:
        return NodeActivityState.Idle

    if loss_rate > 0.05:
        return NodeActivityState.Degraded

    util = egress_bps / btlbw_bps if btlbw_bps > 0 else 0.0
    return NodeActivityState.Busy if util >= 0.5 else NodeActivityState.Active


def _compute_node_state(
    transports: list[TransportActivitySnapshot],
) -> NodeActivityState:
    if not transports:
        return NodeActivityState.Offline

    states = {t.state for t in transports}

    if NodeActivityState.Degraded in states:
        return NodeActivityState.Degraded
    if NodeActivityState.Busy in states:
        return NodeActivityState.Busy
    if NodeActivityState.Active in states:
        return NodeActivityState.Active
    if states == {NodeActivityState.Offline}:
        return NodeActivityState.Offline
    return NodeActivityState.Idle


def _offline_snapshot() -> NodeActivitySnapshot:
    return NodeActivitySnapshot(
        state=NodeActivityState.Offline,
        ingress_bps=0,
        egress_bps=0,
        active_peers=0,
        active_transports=0,
        transports=(),
        primary_transport_name=None,
        timestamp=time.time(),
    )
