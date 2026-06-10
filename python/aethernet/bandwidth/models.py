# SPDX-License-Identifier: MIT

"""Data models for the AetherNet Bandwidth Measurement Framework (ABMF).

Pythonic port of ``AetherNet.Core/Bandwidth/BandwidthModels.cs`` (W18-5).

Design notes
------------
* All dataclasses are ``frozen=True`` — safe to share across threads.
* ``BandwidthConfidence.None_`` avoids shadowing the Python builtin ``None``.
* Timestamps are ``float`` seconds since Unix epoch (``time.time()``-compatible)
  where the C# reference uses ``DateTimeOffset``; callers should use
  ``time.time()`` or ``time.monotonic()`` as appropriate.
* ``timedelta`` is used wherever the C# reference uses ``TimeSpan``.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import timedelta
from enum import Enum


# ── BandwidthConfidence ───────────────────────────────────────────────────────


class BandwidthConfidence(Enum):
    """How confident we are in the current bandwidth estimate.

    Rises with probe rounds; resets on topology change or extended idle.
    ``None_`` is used instead of ``None`` to avoid shadowing the Python builtin.
    """

    None_ = 0
    Low = 1
    Medium = 2
    High = 3


# ── BandwidthSample ───────────────────────────────────────────────────────────


@dataclass(frozen=True)
class BandwidthSample:
    """Point-in-time bandwidth measurement for a single transport link.

    Derivation follows BBRv3
    (draft-cardwell-iccrg-bbr-congestion-control-02):

    * ``btlbw_bps`` — max delivery rate over 10×RTprop window.
    * ``rt_prop`` — minimum RTT observed in the last 10 s (ProbeRTT window).
    * ``srtt`` — RFC 6298 smoothed RTT (α = 1/8).
    * ``rtt_var`` — RFC 6298 mean deviation (β = 1/4).

    AetherNet extensions:

    * ``bdp_bytes`` — pre-computed BDP so callers never re-derive it.
    * ``phy_cap_bps`` — PHY-layer cap from RSSI mapping; 0 if unknown.
    * ``confidence`` — explicit quality tier for ABR decisions.
    """

    transport_name: str

    #: BBRv3 BtlBw: maximum sustained delivery rate the network can carry (bps).
    btlbw_bps: int

    #: Available bandwidth ceiling: btlbw_bps × (1 − loss_rate).
    available_bps: int

    #: Bandwidth-Delay Product: btlbw_bps × rt_prop / 8 (bytes).
    bdp_bytes: int

    #: RFC 6298 smoothed RTT.
    srtt: timedelta

    #: RFC 6298 RTT mean deviation (RTTVAR).
    rtt_var: timedelta

    #: BBRv3 RTprop: minimum observed RTT over the last 10 s.
    rt_prop: timedelta

    #: EWMA fractional loss rate [0, 1]; α = 0.10.
    loss_rate: float

    #: PHY-layer bandwidth cap from RSSI hints (bps). 0 = unknown.
    phy_cap_bps: int

    confidence: BandwidthConfidence

    #: Wall-clock timestamp of this sample (seconds since Unix epoch).
    measured_at: float

    # ── Derived properties ────────────────────────────────────────────────────

    @property
    def rto(self) -> timedelta:
        """RFC 6298 §2.4 RTO: SRTT + max(G, 4×RTTVAR), G = 1 ms clock granularity.

        Clamped to [200 ms, 60 s] per §2.4.
        """
        g_ms = 1.0  # clock granularity
        raw_ms = (
            self.srtt.total_seconds() * 1000.0
            + max(g_ms, 4.0 * self.rtt_var.total_seconds() * 1000.0)
        )
        clamped_ms = max(200.0, min(60_000.0, raw_ms))
        return timedelta(milliseconds=clamped_ms)

    @property
    def effective_bps(self) -> int:
        """Effective bandwidth: min of btlbw_bps and phy_cap_bps (if known)."""
        if self.phy_cap_bps > 0:
            return min(self.btlbw_bps, self.phy_cap_bps)
        return self.btlbw_bps


# ── Probe wire models ─────────────────────────────────────────────────────────


@dataclass(frozen=True)
class BandwidthProbeAck:
    """Four-timestamp probe ACK for two-way delay / RTT measurement (RFC 5136 §3).

    All timestamps are microseconds since Unix epoch on each peer's local clock.
    Clock synchronisation is *not* required — RTT is computed from sender-side
    timestamps only.
    """

    sequence: int
    sender_send_us: int
    receiver_receive_us: int
    receiver_send_us: int
    sender_receive_us: int
    probe_bytes: int

    @property
    def rtt(self) -> timedelta:
        """Round-trip time (clock-sync-free).

        RTT = (SenderReceive − SenderSend) − receiver processing time.
        """
        raw_us = (
            (self.sender_receive_us - self.sender_send_us)
            - (self.receiver_send_us - self.receiver_receive_us)
        )
        return timedelta(microseconds=raw_us)

    @property
    def forward_owd(self) -> timedelta:
        """Forward one-way delay (sender → receiver).

        Requires loose clock sync; treat as approximate unless NTP/PTP is available.
        """
        return timedelta(microseconds=self.receiver_receive_us - self.sender_send_us)


# ── Gossip warm-start ─────────────────────────────────────────────────────────


@dataclass(frozen=True)
class BandwidthGossipPayload:
    """Gossip payload broadcast to new peers during handshake.

    Allows the new session to start with a warm BtlBw estimate instead of
    probing from zero — unique to AetherNet's mesh topology awareness.
    QUIC and TCP always cold-start; gossip warming is an AetherNet invention.
    """

    peer_uhid: str
    transport_name: str
    btlbw_bps: int
    rt_prop_us: int
    confidence: BandwidthConfidence

    #: Wall-clock timestamp when this measurement was taken (seconds since Unix epoch).
    measured_at: float


# ── Node activity (UI layer) ──────────────────────────────────────────────────


class NodeActivityState(Enum):
    """High-level activity state of a node.

    Suitable for status-bar indicators, dashboard health badges, and
    connection-quality icons.
    """

    #: No transports available. Node is isolated.
    Offline = 0

    #: Transports available but no data in the last 5 s.
    Idle = 1

    #: Data flowing; link utilization < 50 % of estimated capacity.
    Active = 2

    #: Link utilization ≥ 50 %; performance good but approaching limits.
    Busy = 3

    #: Loss rate > 5 % or delivery rate declining — likely interference.
    Degraded = 4


@dataclass(frozen=True)
class TransportActivitySnapshot:
    """Activity snapshot for a single transport within the node."""

    transport_name: str
    is_available: bool

    #: Bytes per second being received on this transport.
    ingress_bps: int

    #: Bytes per second being sent on this transport.
    egress_bps: int

    #: Smoothed RTT from BandwidthEstimator.
    srtt: timedelta

    #: Bottleneck bandwidth from BandwidthEstimator (bps).
    btlbw_bps: int

    #: Egress utilization fraction: egress_bps / btlbw_bps. 0.0 if btlbw_bps = 0.
    utilization_fraction: float

    state: NodeActivityState
    confidence: BandwidthConfidence

    @property
    def utilization_percent(self) -> str:
        """Human-readable utilization percentage string (e.g. ``'34 %'``)."""
        return f"{self.utilization_fraction * 100.0:.0f} %"


@dataclass(frozen=True)
class NodeActivitySnapshot:
    """Full node activity snapshot — the top-level model surfaced to UI.

    Consumption patterns:

    * **Status bar / widget:** poll ``NodeActivityMonitor.current()`` every 1 s.
    * **Dashboard:** subscribe via ``NodeActivityMonitor.subscribe()``.
    * **ABR controller:** subscribe to watch for ``NodeActivityState.Degraded``
      and step down the bitrate ladder.
    """

    state: NodeActivityState

    #: Aggregate bytes per second flowing INTO this node (all transports).
    ingress_bps: int

    #: Aggregate bytes per second flowing OUT of this node (all transports).
    egress_bps: int

    #: Number of remote peers that had traffic in the last 5 s.
    active_peers: int

    #: Number of transports currently carrying data.
    active_transports: int

    #: Per-transport breakdown.
    transports: tuple[TransportActivitySnapshot, ...]

    #: Dominant transport: the one carrying the most egress bytes. ``None`` if offline/idle.
    primary_transport_name: str | None

    #: Wall-clock timestamp of this snapshot (seconds since Unix epoch).
    timestamp: float

    @property
    def total_bps(self) -> int:
        """Combined throughput (ingress + egress)."""
        return self.ingress_bps + self.egress_bps

    @property
    def has_activity(self) -> bool:
        """True if any transport has data flowing."""
        return self.state in (
            NodeActivityState.Active,
            NodeActivityState.Busy,
            NodeActivityState.Degraded,
        )
