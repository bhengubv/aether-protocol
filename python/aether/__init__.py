"""Aether Mesh Networking Protocol.

A decentralized mesh networking protocol designed for environments with
intermittent or absent internet connectivity.
"""

from __future__ import annotations

__version__ = "2.0.0"
__author__ = "The Other Bhengu (Pty) Ltd t/a The Geek and Bhengu B.V."
__license__ = "MIT"

# Eager re-exports — pure-Python, no third-party deps. Safe to import unconditionally.
from aether.models import AetherNode, PeerInfo, RouteEntry
from aether.protocol.mesh_packet import MeshPacket, PacketType

# Security primitives use pynacl, which is an optional dep for hosts that don't
# need crypto (e.g. wire-format-only verifiers). Keep imports lazy so importing
# `aether` does not require pynacl to be installed.
def __getattr__(name: str):
    if name in ("Ed25519SigningService",):
        from aether.security.ed25519_service import Ed25519SigningService
        return Ed25519SigningService
    if name in ("SignalProtocolService",):
        from aether.security.signal_protocol import SignalProtocolService
        return SignalProtocolService
    raise AttributeError(f"module 'aether' has no attribute {name!r}")

__all__ = [
    "AetherNode",
    "PeerInfo",
    "RouteEntry",
    "MeshPacket",
    "PacketType",
    "Ed25519SigningService",
    "SignalProtocolService",
]
