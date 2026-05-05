"""Test doubles for unit tests across routing / DTN / SOS."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import List, Optional

from aether.models import PeerInfo
from aether.protocol.mesh_packet import MeshPacket
from aether.routing.sender import MeshSender


@dataclass
class UnicastRecord:
    packet: MeshPacket
    next_hop_uhid: str


class FakeMeshSender(MeshSender):
    """In-memory MeshSender that records every send and broadcast.

    Mirrors the C# / Go FakeMeshSender used in those languages' canonical test suites.
    """

    def __init__(self, local_uhid: str, local_geohash: Optional[str] = None) -> None:
        self._local_uhid = local_uhid
        self._local_geohash = local_geohash
        self._peers: List[PeerInfo] = []
        self._fail_peers: set[str] = set()
        self.unicasts: List[UnicastRecord] = []
        self.broadcasts: List[MeshPacket] = []

    @property
    def local_uhid(self) -> str:
        return self._local_uhid

    @property
    def local_geohash(self) -> Optional[str]:
        return self._local_geohash

    def get_connected_peers(self) -> List[PeerInfo]:
        return list(self._peers)

    def add_peer(self, peer: PeerInfo) -> None:
        self._peers.append(peer)

    def fail_sends_to(self, uhid: str) -> None:
        self._fail_peers.add(uhid)

    async def send(self, packet: MeshPacket, next_hop_uhid: str) -> bool:
        if next_hop_uhid in self._fail_peers:
            return False
        self.unicasts.append(UnicastRecord(packet=_clone(packet), next_hop_uhid=next_hop_uhid))
        return True

    async def broadcast(self, packet: MeshPacket) -> int:
        self.broadcasts.append(_clone(packet))
        return len(self._peers)

    def clear(self) -> None:
        self.unicasts.clear()
        self.broadcasts.clear()


def _clone(packet: MeshPacket) -> MeshPacket:
    return MeshPacket(
        id=packet.id,
        type=packet.type,
        source_uhid=packet.source_uhid,
        destination_uhid=packet.destination_uhid,
        ttl=packet.ttl,
        priority=packet.priority,
        payload=bytes(packet.payload),
        signature=bytes(packet.signature),
        packet_nonce=bytes(packet.packet_nonce),
        timestamp_ms=packet.timestamp_ms,
        protocol_version=packet.protocol_version,
        created_at=packet.created_at,
    )
