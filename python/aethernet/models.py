# SPDX-License-Identifier: MIT

"""Core data models for the Aether mesh networking protocol."""

from dataclasses import dataclass, field
from enum import IntEnum, IntFlag
from typing import Optional, Set
from datetime import datetime, timedelta
from uuid import UUID, uuid4

from aethernet import constants


class NodeCapabilities(IntFlag):
    """Bitfield representing node capabilities."""

    NONE = 0
    BLE = 1
    WIFI_DIRECT = 2
    GATEWAY = 4
    RELAY = 8
    SOS = 16
    STREAMING = 32
    VOICE = 64
    DTN_CARRIER = 128
    NEAR_LINK = 256
    VIDEO = 512


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
    is_blocked: bool = False


@dataclass
class RouteEntry:
    """A routing table entry."""

    destination_uhid: str
    next_hop_uhid: str
    hop_count: int
    expires_at: datetime
    quality_score: int = 50  # Range: 0-100

    @property
    def is_expired(self) -> bool:
        return datetime.utcnow() >= self.expires_at


@dataclass
class AetherNetNode:
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


class BundleStatus(IntEnum):
    """Lifecycle state of a DTN bundle."""

    PENDING = 0
    IN_CUSTODY = 1
    DELIVERED = 2
    EXPIRED = 3
    FAILED = 4


class BundlePriority(IntEnum):
    """Priority class influencing replication aggressiveness."""

    LOW = 0
    NORMAL = 1
    HIGH = 2
    SOS = 3


@dataclass
class DtnBundle:
    """A delay-tolerant network bundle. Store-and-forward unit."""

    sender_uhid: str
    recipient_uhid: str
    encrypted_payload: bytes
    id: UUID = field(default_factory=uuid4)
    priority: BundlePriority = BundlePriority.NORMAL
    status: BundleStatus = BundleStatus.PENDING
    copy_count: int = 1
    max_copies: int = constants.DTN_MAX_COPIES
    sender_geohash: Optional[str] = None
    recipient_last_geohash: Optional[str] = None
    hop_count: int = 0
    created_at: datetime = field(default_factory=datetime.utcnow)
    expires_at: datetime = field(
        default_factory=lambda: datetime.utcnow()
        + timedelta(hours=constants.DTN_BUNDLE_TTL_HOURS)
    )

    @property
    def is_expired(self) -> bool:
        return datetime.utcnow() >= self.expires_at


@dataclass
class CustodyRecord:
    """Record of a custody transfer between two nodes."""

    bundle_id: UUID
    from_uhid: str
    to_uhid: str
    accepted: bool
    id: UUID = field(default_factory=uuid4)
    transferred_at: datetime = field(default_factory=datetime.utcnow)


@dataclass
class DtnDeliveryReceipt:
    """Receipt sent back to the original sender once a bundle is delivered."""

    bundle_id: UUID
    recipient_uhid: str
    total_hops: int
    total_custody_transfers: int
    delivered_at: datetime = field(default_factory=datetime.utcnow)


@dataclass
class SosAlert:
    """An SOS alert observed on the mesh — locally originated or received."""

    sender_uhid: str
    broadcast_type: str = "sos"
    message: Optional[str] = None
    latitude: float = 0.0
    longitude: float = 0.0
    geohash: Optional[str] = None
    id: UUID = field(default_factory=uuid4)
    received_at: datetime = field(default_factory=datetime.utcnow)
    # Distinct UHIDs of peers that have acknowledged receiving this alert. Populated on
    # the ORIGINATING node only, as SosAck packets arrive back — it lets the sender see
    # how many devices their emergency reached. Access is synchronised by the SOS service.
    acknowledged_by: Set[str] = field(default_factory=set)


@dataclass
class SosAcknowledgement:
    """Raised on the originating node when a peer acknowledges receipt of one of its
    active SOS alerts — proof the emergency reached at least one device."""

    broadcast_id: UUID
    responder_uhid: str
    total_acknowledgements: int
