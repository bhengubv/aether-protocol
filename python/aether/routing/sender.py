# SPDX-License-Identifier: MIT

"""MeshSender abstraction — minimal sending interface routing/DTN/SOS depend on."""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import List, Optional

from aether.protocol.mesh_packet import MeshPacket
from aether.models import PeerInfo


class MeshSender(ABC):
    """Hosts wire this with a thin adapter over their transport so the protocol
    services don't take a hard dependency on a specific transport implementation.
    """

    @property
    @abstractmethod
    def local_uhid(self) -> str:
        """The local node's UHID. Used as packet.source_uhid on outbound packets."""

    @property
    def local_geohash(self) -> Optional[str]:
        """Local node's last-known geohash, or None if not shared."""
        return None

    def get_connected_peers(self) -> List[PeerInfo]:
        """Snapshot of currently directly-connected peers."""
        return []

    @abstractmethod
    async def send(self, packet: MeshPacket, next_hop_uhid: str) -> bool:
        """Forward a packet to a single next-hop peer. Returns True if delivered."""

    @abstractmethod
    async def broadcast(self, packet: MeshPacket) -> int:
        """Broadcast a packet to every connected peer. Returns the fan-out count."""
