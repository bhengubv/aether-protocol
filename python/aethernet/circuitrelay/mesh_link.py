# SPDX-License-Identifier: MIT

"""Production RelayLink that carries circuit-relay-v2 frames one hop over the real
mesh — mirrors the C# ``MeshRelayLink`` and Go ``MeshRelayLink``.

Each frame is wrapped in a :class:`~aethernet.protocol.mesh_packet.MeshPacket` of type
:attr:`PacketType.CircuitRelayControl` and handed to the host's send-to-connected-peer
callable; inbound CircuitRelayControl packets are fed back into the engine via
:meth:`MeshRelayLink.handle_incoming_packet`. The two callables are the seam to
whatever real transport the host runs (BLE / Wi-Fi Direct / WebRTC / the HTTP relay).
It never calls a radio directly and never recurses through itself (the host's one-hop
send must exclude the circuit-relay transport).
"""

from __future__ import annotations

from typing import Callable, Optional

from aethernet.circuitrelay.transport import FrameHandler, RelayLink
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

# host callable that sends a MeshPacket one hop to a directly-connected peer.
SendOneHop = Callable[[MeshPacket], bool]
CanReachFn = Callable[[str], bool]


class MeshRelayLink(RelayLink):
    """A mesh-backed :class:`RelayLink`.

    :param local_uhid: this node's UHID (stamped as the packet source).
    :param send_one_hop: sends a MeshPacket to a directly-connected peer; ``True`` if
        handed off.
    :param can_reach: reports whether this node has a direct one-hop link to a peer.
    """

    def __init__(self, local_uhid: str, send_one_hop: SendOneHop, can_reach: CanReachFn) -> None:
        if local_uhid is None:
            raise ValueError("local_uhid is required")
        self._local_uhid = local_uhid
        self._send_one_hop = send_one_hop
        self._can_reach = can_reach
        self._handler: Optional[FrameHandler] = None

    def send_frame(self, node: str, frame: bytes) -> bool:
        pkt = MeshPacket(
            type=PacketType.CircuitRelayControl,
            source_uhid=self._local_uhid,
            destination_uhid=node,
            payload=frame,
            ttl=1,  # relay frames travel exactly one hop; end-to-end routing is the engine's job
        )
        return self._send_one_hop(pkt)

    def can_reach(self, node: str) -> bool:
        return self._can_reach(node)

    def on_frame(self, handler: FrameHandler) -> None:
        self._handler = handler

    def handle_incoming_packet(self, packet: MeshPacket) -> None:
        """Feed an inbound CircuitRelayControl packet from the host's receive path into
        the relay engine. The host must call this for every received
        :attr:`PacketType.CircuitRelayControl` packet (other types are ignored)."""
        if packet is None or packet.type != PacketType.CircuitRelayControl:
            return
        if self._handler is not None:
            self._handler(packet.source_uhid, packet.payload)
