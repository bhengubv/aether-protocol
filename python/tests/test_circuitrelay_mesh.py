# SPDX-License-Identifier: MIT

"""3-node mesh-integration test for circuit-relay-v2: the engine relays A->B through R
over real MeshPacket frames (type CircuitRelayControl) with NO direct A-B link,
surfacing at B via the transport on_data callback — exactly how a host mesh consumes
it. Mirrors the C# CircuitRelayMeshIntegrationTests and Go TestRelayWorksAsMeshTransport.
"""

import threading

from aethernet.circuitrelay.mesh_link import MeshRelayLink
from aethernet.circuitrelay.transport import CircuitRelayOptions, Transport
from aethernet.protocol.mesh_packet import MeshPacket


class _MeshHub:
    """In-process mesh whose adjacency is A-R-B with NO direct A-B edge; routes each
    MeshPacket one hop to the destination node's link. Stands in for the real radios
    (the send_one_hop callable in production)."""

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
                # async one-hop delivery
                threading.Thread(target=link.handle_incoming_packet, args=(pkt,), daemon=True).start()
            return True

        return _send

    def can_reach_from(self, node: str):
        return lambda other: self.adjacent(node, other)


def test_relay_works_as_mesh_transport() -> None:
    hub = _MeshHub()
    hub.connect("A", "R")
    hub.connect("R", "B")  # deliberately NO A-B edge

    a_link = MeshRelayLink("A", hub.send_from("A"), hub.can_reach_from("A"))
    r_link = MeshRelayLink("R", hub.send_from("R"), hub.can_reach_from("R"))
    b_link = MeshRelayLink("B", hub.send_from("B"), hub.can_reach_from("B"))
    hub.register("A", a_link)
    hub.register("R", r_link)
    hub.register("B", b_link)

    a = Transport("A", a_link, CircuitRelayOptions())
    r = Transport("R", r_link, CircuitRelayOptions())
    b = Transport("B", b_link, CircuitRelayOptions())

    received: list = []
    done = threading.Event()

    def on_data(sender: str, data: bytes) -> None:
        received.append((sender, data))
        done.set()

    b.set_on_data(on_data)

    assert not a.is_connected("B")            # no direct path
    assert b.reserve("R")                     # B reserves on the relay
    a.set_route("B", "R")                     # A learns B is reachable via R

    payload = bytes([0xDE, 0xAD, 0xBE, 0xEF])
    assert a.send("B", payload)               # relayed A -> R -> B

    assert done.wait(3.0), "B never received the relayed message via the mesh link"
    assert received[0][0] == "A"
    assert received[0][1] == payload
    assert r.active_bridge_count == 1         # R is genuinely bridging over real packets
