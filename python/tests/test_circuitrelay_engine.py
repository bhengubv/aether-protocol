# SPDX-License-Identifier: MIT

"""Behavioural proof of the native circuit-relay-v2 engine: a three-node topology
where A and B can each reach relay R but NOT each other directly. A message from A
must traverse the relay bridge to reach B — server off, no libp2p. Mirrors the Go
``transport_test.go`` (and the C# ``CircuitRelayBridgeTests``).

Run from the python/ directory:
    python -m pytest tests/test_circuitrelay_engine.py -q
"""

from __future__ import annotations

import queue
import threading
from dataclasses import dataclass
from typing import Callable, Optional

import pytest

from aethernet.circuitrelay.transport import (
    CircuitRelayOptions,
    RelayLink,
    Transport,
)

FrameHandler = Callable[[str, bytes], None]


# ── in-process one-hop mesh ──────────────────────────────────────────────────


class _Mesh:
    """A tiny in-process mesh of one-hop links. An edge x<->y means x and y can
    reach each other directly; delivery of a frame happens asynchronously on a
    separate thread (like a real transport's ``go func``), which is what keeps the
    relay handshake from re-entrant-deadlocking on a single call stack."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._edges: set[str] = set()
        self._links: dict[str, "_ProcLink"] = {}

    def connect(self, x: str, y: str) -> None:
        with self._lock:
            self._edges.add(f"{x}|{y}")
            self._edges.add(f"{y}|{x}")

    def adjacent(self, x: str, y: str) -> bool:
        with self._lock:
            return f"{x}|{y}" in self._edges

    def link(self, node: str) -> "_ProcLink":
        with self._lock:
            l = self._links.get(node)
            if l is None:
                l = _ProcLink(self, node)
                self._links[node] = l
            return l

    def deliver(self, from_node: str, to: str, frame: bytes) -> None:
        if not self.adjacent(from_node, to):
            return
        l = self.link(to)

        def _run() -> None:  # async hop, like a real transport
            with l._lock:
                h = l._handler
            if h is not None:
                h(from_node, frame)

        threading.Thread(target=_run, daemon=True).start()


class _ProcLink(RelayLink):
    def __init__(self, mesh: _Mesh, node: str) -> None:
        self._mesh = mesh
        self._node = node
        self._lock = threading.Lock()
        self._handler: Optional[FrameHandler] = None

    def send_frame(self, node: str, frame: bytes) -> bool:
        if not self._mesh.adjacent(self._node, node):
            return False
        self._mesh.deliver(self._node, node, frame)
        return True

    def can_reach(self, node: str) -> bool:
        return self._mesh.adjacent(self._node, node)

    def on_frame(self, handler: FrameHandler) -> None:
        with self._lock:
            self._handler = handler


# ── controllable clock ───────────────────────────────────────────────────────


class _TestClock:
    """A monotonic-seconds clock the tests can advance by hand, so reservation
    expiry and bridge deadlines are deterministic."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        # Arbitrary fixed epoch (2026-01-01T00:00:00Z in seconds) — value is
        # irrelevant; only advancement matters.
        self._t = 1_767_225_600.0

    def now(self) -> float:
        with self._lock:
            return self._t

    def advance(self, seconds: float) -> None:
        with self._lock:
            self._t += seconds


@dataclass
class _Recv:
    sender: str
    data: str


def _default_options() -> CircuitRelayOptions:
    return CircuitRelayOptions()


def _build_line(
    relay_opts: CircuitRelayOptions,
    relay_now: Optional[Callable[[], float]] = None,
):
    """Wire A -- R -- B with NO A-B edge. ``relay_opts`` / ``relay_now`` configure R.

    Returns (a, r, b, b_recv, a_recv) where the *_recv queues receive an :class:`_Recv`
    for every payload delivered to that endpoint.
    """
    m = _Mesh()
    m.connect("A", "R")
    m.connect("R", "B")

    a = Transport("A", m.link("A"), _default_options())
    r = Transport("R", m.link("R"), relay_opts, relay_now)
    b = Transport("B", m.link("B"), _default_options())

    b_recv: "queue.Queue[_Recv]" = queue.Queue()
    a_recv: "queue.Queue[_Recv]" = queue.Queue()
    b.set_on_data(lambda s, d: b_recv.put(_Recv(s, d.decode("latin-1"))))
    a.set_on_data(lambda s, d: a_recv.put(_Recv(s, d.decode("latin-1"))))
    return a, r, b, b_recv, a_recv


def _wait_recv(ch: "queue.Queue[_Recv]", what: str) -> _Recv:
    try:
        return ch.get(timeout=3.0)
    except queue.Empty:
        pytest.fail(f"timeout waiting for {what}")


def _expect_nothing(ch: "queue.Queue[_Recv]", timeout: float = 0.3) -> None:
    try:
        got = ch.get(timeout=timeout)
        pytest.fail(f"unexpected delivery: {got}")
    except queue.Empty:
        pass


# ── the six behavioural tests (mirror transport_test.go) ─────────────────────


def test_message_traverses_relay_no_direct_link() -> None:
    a, r, b, b_recv, _ = _build_line(_default_options())

    assert not a.is_connected("B"), "A should not be directly connected to B"
    assert b.reserve("R"), "B.reserve(R) failed"
    a.set_route("B", "R")

    assert a.send("B", b"deadbeef"), "A.send returned false"

    got = _wait_recv(b_recv, "B receiving relayed message")
    assert got.sender == "A"
    assert got.data == "deadbeef"
    assert r.active_bridge_count == 1


def test_bridge_is_bidirectional() -> None:
    a, _, b, b_recv, a_recv = _build_line(_default_options())
    assert b.reserve("R"), "reserve failed"
    a.set_route("B", "R")
    assert a.send("B", b"hi"), "A.send failed"
    _wait_recv(b_recv, "B receiving")

    assert b.send("A", b"reply"), "B.send(A) failed"
    got = _wait_recv(a_recv, "A receiving B's reply")
    assert got.sender == "B"
    assert got.data == "reply"


def test_connect_refused_without_reservation() -> None:
    a, r, _, b_recv, _ = _build_line(_default_options())
    a.set_route("B", "R")  # route known, but B never reserved

    assert not a.send("B", b"x"), "A.send should fail without a reservation"
    _expect_nothing(b_recv, timeout=0.2)
    assert r.active_bridge_count == 0


def test_send_fails_without_route() -> None:
    a, _, b, _, _ = _build_line(_default_options())
    assert b.reserve("R"), "reserve failed"
    # no set_route
    assert not a.send("B", b"x"), "A.send should fail with no relay route known"


def test_relay_enforces_data_budget() -> None:
    opts = _default_options()
    opts.bridge_data_limit_bytes = 10
    a, r, b, b_recv, _ = _build_line(opts)
    assert b.reserve("R"), "reserve failed"
    a.set_route("B", "R")

    assert a.send("B", bytes([1, 2, 3, 4, 5])), "first send failed"  # 5 bytes, within 10
    _wait_recv(b_recv, "first (in-budget) message")

    a.send("B", bytes([6, 7, 8, 9, 10, 11, 12, 13]))  # 8 more -> 13 > 10 -> torn down
    _expect_nothing(b_recv, timeout=0.3)
    assert r.active_bridge_count == 0, "bridge should be torn down on budget breach"


def test_reservation_expiry_refuses_connect() -> None:
    clk = _TestClock()
    opts = _default_options()
    opts.reservation_ttl_seconds = 30 * 60
    a, _, b, b_recv, _ = _build_line(opts, clk.now)

    assert b.reserve("R"), "reserve failed"
    a.set_route("B", "R")

    clk.advance(31 * 60)  # past the reservation TTL on R's clock

    assert not a.send("B", b"x"), "A.send should fail after reservation expiry"
    _expect_nothing(b_recv, timeout=0.2)


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
