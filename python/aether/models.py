"""Core data models for the Aether mesh networking protocol."""

from dataclasses import dataclass, field
from typing import Optional
from datetime import datetime
from uuid import UUID


@dataclass
class PeerInfo:
    """Information about a mesh network peer."""

    uhid: str
    public_key: bytes
    last_seen: datetime
    reliability_score: int = 50  # Range: 0-100
    hop_count: Optional[int] = None
    geohash: Optional[str] = None
    capabilities: int = 0  # Bitfield of node capabilities


@dataclass
class RouteEntry:
    """A routing table entry."""

    destination_uhid: str
    next_hop_uhid: str
    hop_count: int
    expires_at: datetime
    quality_score: int = 50  # Range: 0-100


@dataclass
class AetherNode:
    """Represents a local Aether mesh node."""

    uhid: str
    private_key: bytes  # Ed25519 private key (32 bytes)
    public_key: bytes  # Ed25519 public key (32 bytes)
    created_at: datetime = field(default_factory=datetime.utcnow)
    capabilities: int = 0  # Bitfield of node capabilities
    peers: dict[str, PeerInfo] = field(default_factory=dict)
    routing_table: dict[str, RouteEntry] = field(default_factory=dict)

    def has_route_to(self, destination_uhid: str) -> bool:
        """Check if a route exists to the destination."""
        route = self.routing_table.get(destination_uhid)
        if route is None:
            return False
        return route.expires_at > datetime.utcnow()

    def get_route_to(self, destination_uhid: str) -> Optional[RouteEntry]:
        """Get the route entry to a destination if it exists and is not expired."""
        route = self.routing_table.get(destination_uhid)
        if route is None or route.expires_at <= datetime.utcnow():
            return None
        return route
