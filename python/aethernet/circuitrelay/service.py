# SPDX-License-Identifier: MIT

"""Circuit-relay-v2 as an auto-selectable :class:`TransportService`.

The byte-locked :class:`~aethernet.circuitrelay.transport.Transport` engine is
synchronous (threads + queues); the mesh transport contract
(:class:`~aethernet.transport.transport_service.TransportService`) is ``async``.
This module is the thin, wire-neutral adapter between the two — it changes NO
serialization and touches NO frame bytes. It exists so a
:class:`~aethernet.transport.manager.TransportManager` can pick relaying as the
last-resort, serverless fallback exactly like every other transport (BLE / Wi-Fi
Direct / WebRTC / the HTTP relay), rather than the host hand-wiring the engine.

Faithful mirror of the C# ``CircuitRelayTransportService`` and the
``MeshCircuitRelay`` factory (``src/AetherNet.Transport/CircuitRelay/``):

* :class:`CircuitRelayTransportService` — an :class:`TransportService` whose
  :meth:`~CircuitRelayTransportService.send_async` establishes a relay bridge (if
  needed) then tunnels DATA, at ``power_cost_relative`` **90** so the manager
  ranks it below the HTTP relay's last-resort cost of 100.
* :func:`MeshCircuitRelay.create` — wires the adapter onto a
  :class:`~aethernet.circuitrelay.mesh_link.MeshRelayLink`, returning
  ``(transport, link)`` for the host to register with the manager and to feed
  inbound ``PacketType.CircuitRelayControl`` packets into.
"""

from __future__ import annotations

import asyncio
from typing import Callable, Optional, Tuple

from aethernet.circuitrelay.mesh_link import CanReachFn, MeshRelayLink, SendOneHop
from aethernet.circuitrelay.transport import (
    CircuitRelayOptions,
    NowFn,
    RelayLink,
    Transport,
)
from aethernet.transport.per_transport_metrics import PerTransportMetrics
from aethernet.transport.transport_service import TransportService

#: Relayed traffic is costly (an extra hop through a third node), so it sits just
#: below the HTTP relay's last-resort cost of 100 — mirrors the C#
#: ``CircuitRelayTransportService.PowerCostRelative``.
CIRCUIT_RELAY_POWER_COST = 90

#: The transport name surfaced through the manager's ``data_received`` tag —
#: byte-for-byte identical to the C# ``Name`` so cross-language selection tests agree.
CIRCUIT_RELAY_NAME = "Circuit Relay (v2)"


class CircuitRelayTransportService(TransportService):
    """Native circuit-relay-v2 transport adapter.

    Wraps the synchronous :class:`~aethernet.circuitrelay.transport.Transport`
    engine so it satisfies the async :class:`TransportService` contract and can be
    auto-selected by a :class:`~aethernet.transport.manager.TransportManager`. Any
    AetherNet node can act as a relay: a node that cannot reach a peer directly
    routes through a third node reachable to both — the decentralised, no-libp2p
    equivalent of libp2p's circuit-relay-v2.

    The engine is authoritative for all wire behaviour; this class only bridges
    calling conventions (blocking engine call -> awaitable) and exposes the
    :class:`TransportService` surface. It adds nothing to the wire.
    """

    def __init__(
        self,
        local_uhid: str,
        link: RelayLink,
        options: Optional[CircuitRelayOptions] = None,
        now: Optional[NowFn] = None,
    ) -> None:
        """Build the adapter around a relay :class:`~aethernet.circuitrelay.transport.RelayLink`.

        Args:
            local_uhid: This node's UHID.
            link: One-hop link to directly-reachable nodes (e.g. a
                :class:`~aethernet.circuitrelay.mesh_link.MeshRelayLink`).
            options: Policy/tuning (optional).
            now: Clock (optional; injectable for deterministic reservation-expiry tests).
        """
        if not local_uhid:
            raise ValueError("local_uhid is required")
        if link is None:
            raise ValueError("link is required")
        self._local_uhid = local_uhid
        self._engine = Transport(local_uhid, link, options, now)
        self._metrics = PerTransportMetrics()
        self._on_data: Optional[Callable[[str, bytes], None]] = None
        # Bridge the engine's synchronous on-data callback to the transport callback.
        self._engine.set_on_data(self._deliver)

    # ── TransportService ───────────────────────────────────────────────────────

    @property
    def name(self) -> str:
        return CIRCUIT_RELAY_NAME

    @property
    def is_available(self) -> bool:
        return not self._engine._disposed  # noqa: SLF001 — mirrors C# IsAvailable => !_disposed

    @property
    def max_bandwidth_bps(self) -> int:
        return 5_000_000  # relayed path; conservatively below a direct link

    @property
    def max_range_meters(self) -> int:
        return 0  # internet-scope

    @property
    def power_cost_relative(self) -> int:
        """Relayed traffic is costly (an extra hop), so it sits just below the HTTP
        relay's last-resort cost of 100."""
        return CIRCUIT_RELAY_POWER_COST

    @property
    def max_concurrent_peers(self) -> int:
        return 256

    @property
    def metrics(self) -> PerTransportMetrics:
        """Per-transport EWMA metrics (sample count, RTT, loss, throughput) for ranking."""
        return self._metrics

    async def send_async(self, peer_uhid: str, data: bytes) -> bool:
        """Deliver ``data`` to ``peer_uhid`` via the relay, establishing a bridge first
        if needed. Returns ``True`` if the frame was tunnelled.

        The engine call blocks on the CONNECT handshake, so it runs in a worker
        thread to keep the event loop free (mirrors the C# ``async`` handshake).
        """
        if self._engine._disposed:  # noqa: SLF001
            return False
        ok = await asyncio.to_thread(self._engine.send, peer_uhid, data)
        self._metrics.record_sample(0, ok, len(data) if ok else 0)
        return ok

    async def send_stream_async(self, peer_uhid: str, data_stream: asyncio.StreamReader) -> bool:
        try:
            data = await data_stream.read()
        except Exception:
            return False
        return await self.send_async(peer_uhid, data)

    def is_connected(self, peer_uhid: str) -> bool:
        """True once a relay bridge to ``peer_uhid`` has been established."""
        return self._engine.is_connected(peer_uhid)

    def on_data_received(self, callback: Callable[[str, bytes], None]) -> None:
        self._on_data = callback

    # ── Relay / target API (delegates to the engine) ────────────────────────────

    async def reserve_async(self, relay_uhid: str) -> bool:
        """Reserve capacity on ``relay_uhid`` so peers can reach this node through it.
        Returns ``True`` once the relay confirms. Runs the blocking engine call in a
        worker thread."""
        if self._engine._disposed:  # noqa: SLF001
            return False
        return await asyncio.to_thread(self._engine.reserve, relay_uhid)

    def set_route(self, dest_uhid: str, relay_uhid: str) -> None:
        """Record that ``dest_uhid`` is reachable via ``relay_uhid`` (directory /
        reservation gossip in production; tests set it directly)."""
        self._engine.set_route(dest_uhid, relay_uhid)

    @property
    def active_bridge_count(self) -> int:
        """Number of bridges this node is currently servicing as a relay."""
        return self._engine.active_bridge_count

    @property
    def active_reservation_count(self) -> int:
        """Number of reservations this node is currently holding as a relay."""
        return self._engine.active_reservation_count

    @property
    def engine(self) -> Transport:
        """The underlying byte-locked relay engine (diagnostics / advanced hosts)."""
        return self._engine

    def dispose(self) -> None:
        """Release the engine's pending waiters so no caller blocks forever."""
        self._engine.dispose()

    # ── Internal ────────────────────────────────────────────────────────────────

    def _deliver(self, sender_uhid: str, payload: bytes) -> None:
        cb = self._on_data
        if cb is not None:
            cb(sender_uhid, payload)


class MeshCircuitRelay:
    """Wires a :class:`CircuitRelayTransportService` onto a
    :class:`~aethernet.circuitrelay.mesh_link.MeshRelayLink`. Mirrors the C#
    ``MeshCircuitRelay`` static factory.

    The host: (1) registers the returned transport with the mesh — a
    :class:`~aethernet.transport.manager.TransportManager` includes it automatically
    via its ``additional_transports`` argument, at ``power_cost_relative`` 90 (just
    below the HTTP relay); and (2) routes every received
    :attr:`~aethernet.protocol.mesh_packet.PacketType.CircuitRelayControl` packet to
    the returned link's
    :meth:`~aethernet.circuitrelay.mesh_link.MeshRelayLink.handle_incoming_packet`.
    """

    @staticmethod
    def create(
        local_uhid: str,
        send_one_hop: SendOneHop,
        can_reach: CanReachFn,
        options: Optional[CircuitRelayOptions] = None,
    ) -> Tuple[CircuitRelayTransportService, MeshRelayLink]:
        """Create the relay transport + its mesh link.

        Args:
            local_uhid: This node's UHID (stamped as the packet source).
            send_one_hop: Sends a ``MeshPacket`` one hop to a directly-connected peer;
                ``True`` if handed off. Must exclude the circuit-relay transport to
                avoid recursion.
            can_reach: Reports whether this node has a direct one-hop link to a peer.
            options: Policy/tuning (optional).

        Returns:
            ``(transport, link)`` — register ``transport`` with the manager and feed
            inbound ``CircuitRelayControl`` packets to ``link``.
        """
        link = MeshRelayLink(local_uhid, send_one_hop, can_reach)
        transport = CircuitRelayTransportService(local_uhid, link, options)
        return transport, link
