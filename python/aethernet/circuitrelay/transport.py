# SPDX-License-Identifier: MIT

"""Native circuit-relay-v2 engine — the decentralised, no-libp2p equivalent of
libp2p's circuit-relay-v2, built on top of the fixture-locked :mod:`frame` codec.

Any AetherNet node can act as a relay: a node that cannot reach a peer directly
routes through a third node that can reach both. Three roles live in this one
engine (a node can be any/all at once):

* **Target** — :meth:`Transport.reserve` reserves capacity on a relay so peers
  behind NAT can be reached via that relay.
* **Client** — :meth:`Transport.send` to a peer for which a relay route is known
  (:meth:`Transport.set_route`) performs the CONNECT handshake then tunnels DATA.
* **Relay** — grants reservations, bridges CONNECT->STOP, and forwards DATA
  between the two legs under a data/duration budget.

Faithful port of the C# ``CircuitRelayTransportService`` and the Go ``Transport``
engine. One hop of a frame is carried by the injected :class:`RelayLink`; frames
are the native :class:`RelayFrame` wire format (byte-identical across all eight
language SDKs).

The clock is injectable (``now() -> float`` seconds since some epoch) so
reservation-expiry and bridge-duration behaviour is deterministic in tests; a
deadline of ``None`` means "no limit" (mirrors Go's zero ``time.Time`` and C#'s
``DateTimeOffset.MaxValue``).
"""

from __future__ import annotations

import queue
import threading
import time
import uuid
from dataclasses import dataclass, field
from typing import Callable, Optional
from uuid import UUID

from aethernet.circuitrelay.frame import (
    MessageType,
    RelayFrame,
    Status,
    deserialize,
    serialize,
)

# Callback / handler signatures (kept explicit for the eight-language parity docs).
FrameHandler = Callable[[str, bytes], None]
OnDataCallback = Callable[[str, bytes], None]
NowFn = Callable[[], float]

_NIL_UUID = UUID(int=0)


# ── Options (mirror C# CircuitRelayOptions / Go Options) ──────────────────────


@dataclass
class CircuitRelayOptions:
    """Tuning + policy for a :class:`Transport` (mirrors C# ``CircuitRelayOptions``
    and Go ``Options``). Durations are in seconds."""

    reservation_ttl_seconds: float = 30 * 60  # 30 minutes
    max_reservations: int = 128
    max_bridges: int = 128
    bridge_data_limit_bytes: int = 0  # 0 = unlimited
    bridge_duration_limit_seconds: int = 0  # 0 = unlimited
    connect_timeout_seconds: float = 10.0
    reserve_timeout_seconds: float = 10.0
    act_as_relay: bool = True


# ── RelayLink abstraction (mirror C# IRelayLink / Go RelayLink) ───────────────


class RelayLink:
    """The one-hop link a :class:`Transport` uses to exchange raw relay frames with
    *directly reachable* nodes — the seam between circuit-relay-v2 (transport-
    agnostic) and whatever real transport carries a frame one hop (BLE, Wi-Fi
    Direct, WebRTC, the HTTP relay, or an in-process link in tests). Mirrors the C#
    ``IRelayLink`` and Go ``RelayLink``.
    """

    def send_frame(self, node: str, frame: bytes) -> bool:
        """Send a raw relay frame to a node reachable in one hop. Returns ``True``
        if the frame was handed to that node's link."""
        raise NotImplementedError

    def can_reach(self, node: str) -> bool:
        """Whether this node currently has a direct one-hop link to ``node``."""
        raise NotImplementedError

    def on_frame(self, handler: FrameHandler) -> None:
        """Register the handler invoked when a raw frame arrives from a directly-
        reachable node (sender node UHID, frame bytes)."""
        raise NotImplementedError


# ── Internal bridge/route state ───────────────────────────────────────────────


@dataclass
class _RelayBridge:
    """A bridge this node is relaying (relay role)."""

    a: str
    b: str
    data_budget: int
    deadline: Optional[float]  # None => no duration limit
    data_used: int = 0
    open: bool = False


@dataclass
class _ActiveBridge:
    """An established bridge from this node's endpoint view: which connection,
    via which relay."""

    conn_id: UUID
    relay: str


# ── The engine (mirror C# CircuitRelayTransportService / Go Transport) ────────


class Transport:
    """Native circuit-relay-v2 engine: any node can act as target (reserve),
    client (send over a known relay route), and relay (grant reservations, bridge
    CONNECT->STOP, forward DATA under a budget). Faithful port of the C#
    ``CircuitRelayTransportService`` and the Go ``Transport``.
    """

    def __init__(
        self,
        local_uhid: str,
        link: RelayLink,
        options: Optional[CircuitRelayOptions] = None,
        now: Optional[NowFn] = None,
    ) -> None:
        if local_uhid is None:
            raise ValueError("local_uhid is required")
        if link is None:
            raise ValueError("link is required")
        self._local_uhid = local_uhid
        self._link = link
        self._options = options or CircuitRelayOptions()
        self._now: NowFn = now if now is not None else time.time

        self._lock = threading.Lock()
        # Relay role.
        self._reservations: dict[str, float] = {}          # client UHID -> expiry (seconds)
        self._bridges: dict[UUID, _RelayBridge] = {}
        # Client / target role.
        self._routes: dict[str, str] = {}                  # dest -> relay
        self._peer_bridges: dict[str, _ActiveBridge] = {}  # peer -> bridge
        self._pending_connects: dict[UUID, "queue.Queue[Status]"] = {}
        self._pending_reservations: dict[str, "queue.Queue[Status]"] = {}

        self._on_data: Optional[OnDataCallback] = None
        self._disposed = False

        link.on_frame(self._on_frame)

    # ── Configuration / diagnostics ──────────────────────────────────────────

    def set_on_data(self, cb: Optional[OnDataCallback]) -> None:
        """Register the callback invoked when tunnelled data is delivered to this
        node as an endpoint (sender UHID, payload)."""
        self._on_data = cb

    def set_route(self, dest: str, relay: str) -> None:
        """Record that ``dest`` is reachable via ``relay`` (in production from the
        directory / reservation gossip; tests set it directly)."""
        with self._lock:
            self._routes[dest] = relay

    @property
    def active_bridge_count(self) -> int:
        """Number of bridges this node is currently servicing as a relay."""
        with self._lock:
            return len(self._bridges)

    @property
    def active_reservation_count(self) -> int:
        """Number of reservations this node is currently holding as a relay."""
        with self._lock:
            return len(self._reservations)

    def is_connected(self, peer: str) -> bool:
        """True once a relay bridge to ``peer`` has been established."""
        with self._lock:
            return peer in self._peer_bridges

    # ── Public target / client API ───────────────────────────────────────────

    def reserve(self, relay: str) -> bool:
        """Reserve capacity on ``relay`` so peers can reach this node through it.
        Returns ``True`` once the relay confirms the reservation."""
        if self._disposed:
            raise RuntimeError("Transport is disposed")
        if not self._link.can_reach(relay):
            return False

        ch: "queue.Queue[Status]" = queue.Queue(maxsize=1)
        with self._lock:
            self._pending_reservations[relay] = ch
        try:
            f = RelayFrame(
                type=MessageType.Reserve,
                source_uhid=self._local_uhid,
                relay_uhid=relay,
            )
            self._link.send_frame(relay, serialize(f))
            return self._await(ch, self._options.reserve_timeout_seconds) == Status.Ok
        finally:
            with self._lock:
                self._pending_reservations.pop(relay, None)

    def send(self, peer: str, data: bytes) -> bool:
        """Deliver ``data`` to ``peer``, establishing a relay bridge first if
        needed. Returns ``True`` if the frame was tunnelled."""
        if self._disposed:
            raise RuntimeError("Transport is disposed")

        with self._lock:
            ab = self._peer_bridges.get(peer)
        if ab is not None:
            return self._send_data(ab, peer, data)

        # No bridge yet — establish one through the known relay for this peer.
        with self._lock:
            relay = self._routes.get(peer)
        if relay is None or not self._link.can_reach(relay):
            return False

        if self._connect(peer, relay) != Status.Ok:
            return False

        with self._lock:
            ab = self._peer_bridges.get(peer)
        return ab is not None and self._send_data(ab, peer, data)

    # ── Client handshake ─────────────────────────────────────────────────────

    def _connect(self, dest: str, relay: str) -> Status:
        conn_id = uuid.uuid4()
        ch: "queue.Queue[Status]" = queue.Queue(maxsize=1)
        with self._lock:
            self._pending_connects[conn_id] = ch
        try:
            f = RelayFrame(
                type=MessageType.Connect,
                source_uhid=self._local_uhid,
                destination_uhid=dest,
                relay_uhid=relay,
                connection_id=conn_id,
            )
            if not self._link.send_frame(relay, serialize(f)):
                return Status.ConnectionFailed
            return self._await(ch, self._options.connect_timeout_seconds)
        finally:
            with self._lock:
                self._pending_connects.pop(conn_id, None)

    def _send_data(self, bridge: _ActiveBridge, peer: str, data: bytes) -> bool:
        f = RelayFrame(
            type=MessageType.Data,
            source_uhid=self._local_uhid,
            destination_uhid=peer,
            relay_uhid=bridge.relay,
            connection_id=bridge.conn_id,
            payload=data,
        )
        return self._link.send_frame(bridge.relay, serialize(f))

    def _await(self, ch: "queue.Queue[Status]", timeout: float) -> Status:
        try:
            return ch.get(timeout=timeout)
        except queue.Empty:
            return Status.ConnectionFailed

    # ── Inbound frame dispatch ───────────────────────────────────────────────

    def _on_frame(self, from_node: str, frame: bytes) -> None:
        if self._disposed:
            return
        try:
            f = deserialize(frame)
        except Exception:
            return  # drop malformed

        try:
            t = f.type
            if t == MessageType.Reserve:
                self._handle_reserve(from_node, f)
            elif t == MessageType.ReserveResponse:
                self._handle_reserve_response(from_node, f)
            elif t == MessageType.Connect:
                self._handle_connect(from_node, f)
            elif t == MessageType.Stop:
                self._handle_stop(from_node, f)
            elif t == MessageType.StopResponse:
                self._handle_stop_response(from_node, f)
            elif t == MessageType.ConnectResponse:
                self._handle_connect_response(from_node, f)
            elif t == MessageType.Data:
                self._handle_data(from_node, f)
        except Exception:
            # Handler errors must never crash the delivery thread.
            return

    # Relay: grant/refuse a reservation.
    def _handle_reserve(self, from_node: str, f: RelayFrame) -> None:
        with self._lock:
            refuse = (
                not self._options.act_as_relay
                or len(self._reservations) >= self._options.max_reservations
            )
            expiry = 0.0
            if not refuse:
                expiry = self._now() + self._options.reservation_ttl_seconds
                self._reservations[f.source_uhid] = expiry

        if refuse:
            self._send(
                from_node,
                RelayFrame(
                    type=MessageType.ReserveResponse,
                    source_uhid=f.source_uhid,
                    relay_uhid=self._local_uhid,
                    status=Status.ReservationRefused,
                ),
            )
            return
        self._send(
            from_node,
            RelayFrame(
                type=MessageType.ReserveResponse,
                source_uhid=f.source_uhid,
                relay_uhid=self._local_uhid,
                status=Status.Ok,
                reservation_expires_at_ms=int(expiry * 1000),
            ),
        )

    # Client: reservation confirmed/denied.
    def _handle_reserve_response(self, from_node: str, f: RelayFrame) -> None:
        with self._lock:
            ch = self._pending_reservations.get(from_node)
        if ch is not None:
            _try_put(ch, f.status)

    # Relay: A wants to reach B. Validate B's reservation + reachability, then open
    # a STOP to B.
    def _handle_connect(self, from_node: str, f: RelayFrame) -> None:
        a = f.source_uhid
        b = f.destination_uhid
        conn_id = f.connection_id
        if conn_id is None or conn_id == _NIL_UUID:
            return

        if not self._options.act_as_relay:
            self._reply_connect(a, f, Status.ConnectionFailed)
            return

        with self._lock:
            exp = self._reservations.get(b)
            if exp is None or not (self._now() < exp):
                self._reservations.pop(b, None)
                stop_status: Optional[Status] = Status.NoReservation
            elif not self._link.can_reach(b):
                stop_status = Status.ConnectionFailed
            elif len(self._bridges) >= self._options.max_bridges:
                stop_status = Status.ResourceLimitExceeded
            else:
                stop_status = None
                deadline: Optional[float] = None
                if self._options.bridge_duration_limit_seconds > 0:
                    deadline = self._now() + self._options.bridge_duration_limit_seconds
                self._bridges[conn_id] = _RelayBridge(
                    a=a,
                    b=b,
                    data_budget=self._options.bridge_data_limit_bytes,
                    deadline=deadline,
                )

        if stop_status is not None:
            self._reply_connect(a, f, stop_status)
            return

        self._send(
            b,
            RelayFrame(
                type=MessageType.Stop,
                source_uhid=a,
                destination_uhid=b,
                relay_uhid=self._local_uhid,
                connection_id=conn_id,
                limit_data_bytes=self._options.bridge_data_limit_bytes,
                limit_duration_seconds=self._options.bridge_duration_limit_seconds,
            ),
        )

    # Target: relay says A wants to reach us. Accept and record a return route to A.
    def _handle_stop(self, from_node: str, f: RelayFrame) -> None:
        conn_id = f.connection_id
        if conn_id is None or conn_id == _NIL_UUID:
            return
        with self._lock:
            self._peer_bridges[f.source_uhid] = _ActiveBridge(conn_id=conn_id, relay=from_node)
        self._send(
            from_node,
            RelayFrame(
                type=MessageType.StopResponse,
                source_uhid=f.source_uhid,
                destination_uhid=self._local_uhid,
                relay_uhid=from_node,
                connection_id=conn_id,
                status=Status.Ok,
            ),
        )

    # Relay: target accepted/refused. Finalise the bridge and answer the client.
    def _handle_stop_response(self, from_node: str, f: RelayFrame) -> None:
        conn_id = f.connection_id
        if conn_id is None or conn_id == _NIL_UUID:
            return
        with self._lock:
            bridge = self._bridges.get(conn_id)
            if bridge is None:
                return
            refused = f.status != Status.Ok
            if refused:
                a_uhid = bridge.a
                self._bridges.pop(conn_id, None)
            else:
                bridge.open = True
                a_uhid, b_uhid, budget = bridge.a, bridge.b, bridge.data_budget

        if refused:
            self._reply_connect(a_uhid, f, Status.ConnectionFailed)
            return

        self._send(
            a_uhid,
            RelayFrame(
                type=MessageType.ConnectResponse,
                source_uhid=a_uhid,
                destination_uhid=b_uhid,
                relay_uhid=self._local_uhid,
                connection_id=conn_id,
                status=Status.Ok,
                limit_data_bytes=budget,
            ),
        )

    # Client: bridge established/refused.
    def _handle_connect_response(self, from_node: str, f: RelayFrame) -> None:
        conn_id = f.connection_id
        if conn_id is None or conn_id == _NIL_UUID:
            return
        with self._lock:
            if f.status == Status.Ok:
                self._peer_bridges[f.destination_uhid] = _ActiveBridge(conn_id=conn_id, relay=from_node)
            ch = self._pending_connects.get(conn_id)
        if ch is not None:
            _try_put(ch, f.status)

    # Data: endpoint delivery, or relay forward (under budget).
    def _handle_data(self, from_node: str, f: RelayFrame) -> None:
        if f.destination_uhid == self._local_uhid:
            cb = self._on_data
            if cb is not None:
                cb(f.source_uhid, f.payload)
            return

        conn_id = f.connection_id
        if conn_id is None or conn_id == _NIL_UUID:
            return

        with self._lock:
            bridge = self._bridges.get(conn_id)
            if bridge is None or not bridge.open or (from_node != bridge.a and from_node != bridge.b):
                return  # unknown / not-yet-open / not-a-party — drop
            if bridge.deadline is not None and not (self._now() < bridge.deadline):
                self._bridges.pop(conn_id, None)
                return
            bridge.data_used += len(f.payload)
            over = bridge.data_budget > 0 and bridge.data_used > bridge.data_budget
            if over:
                self._bridges.pop(conn_id, None)
                return

        # Forward the frame unchanged to the other endpoint (= its dst).
        self._link.send_frame(f.destination_uhid, serialize(f))

    # ── Reply helpers ────────────────────────────────────────────────────────

    def _send(self, to: str, f: RelayFrame) -> None:
        try:
            b = serialize(f)
        except Exception:
            return
        self._link.send_frame(to, b)

    def _reply_connect(self, client: str, connect: RelayFrame, status: Status) -> None:
        self._send(
            client,
            RelayFrame(
                type=MessageType.ConnectResponse,
                source_uhid=connect.source_uhid,
                destination_uhid=connect.destination_uhid,
                relay_uhid=self._local_uhid,
                connection_id=connect.connection_id,
                status=status,
            ),
        )

    # ── Teardown ─────────────────────────────────────────────────────────────

    def dispose(self) -> None:
        """Release pending waiters so no caller blocks forever."""
        if self._disposed:
            return
        self._disposed = True
        with self._lock:
            pending_c = list(self._pending_connects.values())
            pending_r = list(self._pending_reservations.values())
        for ch in pending_c:
            _try_put(ch, Status.ConnectionFailed)
        for ch in pending_r:
            _try_put(ch, Status.ConnectionFailed)


def _try_put(ch: "queue.Queue[Status]", s: Status) -> None:
    """Non-blocking put that mirrors Go's ``trySend`` (buffered send with a default
    case): if the slot is already full, drop the extra value."""
    try:
        ch.put_nowait(s)
    except queue.Full:
        pass
