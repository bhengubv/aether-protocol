"""Aether Mesh Networking Protocol.

A decentralized mesh networking protocol designed for environments with
intermittent or absent internet connectivity.
"""

__version__ = "2.0.0"
__author__ = "The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V."
__license__ = "MIT"

from aether.models import AetherNode, PeerInfo, RouteEntry
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.security.ed25519_service import Ed25519SigningService
from aether.security.signal_protocol import SignalProtocolService

__all__ = [
    "AetherNode",
    "PeerInfo",
    "RouteEntry",
    "MeshPacket",
    "PacketType",
    "Ed25519SigningService",
    "SignalProtocolService",
]
