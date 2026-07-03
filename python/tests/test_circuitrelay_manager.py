# SPDX-License-Identifier: MIT

"""Gap-2 acceptance test: the native circuit-relay-v2 transport must be *auto-selected*
by :class:`~aethernet.transport.manager.TransportManager` as the last-resort, serverless
fallback — NOT called directly.

A 3-node in-process hub is wired A-R-B with NO direct A-B edge. Each node's relay is
wrapped in a manager as that node's ONLY transport (no BLE / Wi-Fi Direct / NearLink).
B reserves on R; A routes B via R; A sends through the MANAGER, which must fall through
to step 6 (additional transports, ascending power cost) and pick the relay (cost 90). B
receives the exact payload tagged with the relay transport's name — proving selection,
not hand-wiring — and R shows one active bridge (a real relayed hop over MeshPacket
type-57 frames).

Mirrors the C# ``CircuitRelayMeshIntegrationTests.Relay_Is_Auto_Selected_By_TransportManager_As_Fallback``.
The relay engine is synchronous; the adapter/manager are async, so the test drives it
with asyncio. Run from the python/ directory:

    python -m pytest tests/test_circuitrelay_manager.py -q
"""

from __future__ import annotations

import asyncio
import threading

import pytest

from aethernet.circuitrelay import (
    CIRCUIT_RELAY_NAME,
    CIRCUIT_RELAY_POWER_COST,
    MeshCircuitRelay,
    MeshRelayLink,
)
from aethernet.protocol.mesh_packet import MeshPacket
from aethernet.transport.manager import TransportManager


class _MeshHub:
    """In-process mesh whose adjacency is A-R-B with NO direct A-B edge; routes each
    MeshPacket one hop to the destination node's link on a background thread (an async
    hop, like a real radio's send). Stands in for the ``send_one_hop`` delegate."""

    def __init__(self) -> None:
        self._links: dict[str, MeshRelayLink] = {}
        self._edges: set[str] = set()

    def connect(self, x: str, y: str) -> None:
        self._edges.add(f"{x}|{y}")
        self._edges.add(f"{y}|{x}")

    def adjacent(self, x: str, y: str) -> bool:
        return f"{x}|{y}" in self._edges

    def register(self, node: str, link: MeshRelayLink) -> None:
        self._links[node] = link

    def send_from(self, node: str):
        def _send(pkt: MeshPacket) -> bool:
            if not self.adjacent(node, pkt.destination_uhid):
                return False
            link = self._links.get(pkt.destination_uhid)
            if link is not None:
                threading.Thread(
                    target=link.handle_incoming_packet, args=(pkt,), daemon=True
                ).start()
            return True

        return _send

    def can_reach_from(self, node: str):
        return lambda other: self.adjacent(node, other)


@pytest.mark.asyncio
async def test_relay_is_auto_selected_by_transport_manager_as_fallback() -> None:
    hub = _MeshHub()
    hub.connect("A", "R")
    hub.connect("R", "B")  # deliberately NO A-B edge

    a_t, a_l = MeshCircuitRelay.create("A", hub.send_from("A"), hub.can_reach_from("A"))
    r_t, r_l = MeshCircuitRelay.create("R", hub.send_from("R"), hub.can_reach_from("R"))
    b_t, b_l = MeshCircuitRelay.create("B", hub.send_from("B"), hub.can_reach_from("B"))
    hub.register("A", a_l)
    hub.register("R", r_l)
    hub.register("B", b_l)

    # A and B each run a TransportManager whose ONLY transport is the relay
    # (no BLE / Wi-Fi Direct / NearLink) — so a successful send PROVES the manager
    # selected the relay at step 6, not that a cheaper transport carried it.
    a_mgr = TransportManager(additional_transports=[a_t])
    b_mgr = TransportManager(additional_transports=[b_t])

    loop = asyncio.get_running_loop()
    received: asyncio.Future = loop.create_future()

    def on_data(sender: str, data: bytes, via: str) -> None:
        # Fires from a hub delivery thread; marshal back onto the loop.
        if not received.done():
            loop.call_soon_threadsafe(received.set_result, (sender, bytes(data), via))

    b_mgr.on_data_received(on_data)

    assert await b_t.reserve_async("R")  # B reserves on the relay
    a_t.set_route("B", "R")              # A learns B is reachable via R

    payload = bytes([0x11, 0x22, 0x33, 0x44])
    assert await a_mgr.send_async("B", payload)  # via the MANAGER, which must select the relay

    sender, data, via = await asyncio.wait_for(received, timeout=3.0)
    assert sender == "A"
    assert data == payload
    assert via == CIRCUIT_RELAY_NAME  # the manager chose the relay transport, by name
    assert r_t.active_bridge_count == 1  # R is genuinely bridging over real packets

    # And it was auto-selected via the additional-transport path, not a typed slot.
    assert a_mgr.additional_send_count == 1


def test_relay_adapter_advertises_fallback_power_cost() -> None:
    """The adapter's power cost (90) must sit below the HTTP relay's last-resort 100 so
    the manager ranks it last among additional transports — the serverless fallback."""
    hub = _MeshHub()
    hub.connect("A", "R")
    t, _ = MeshCircuitRelay.create("A", hub.send_from("A"), hub.can_reach_from("A"))
    assert t.power_cost_relative == CIRCUIT_RELAY_POWER_COST == 90
    assert t.name == "Circuit Relay (v2)"
    assert t.is_available is True
    t.dispose()
    assert t.is_available is False


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
