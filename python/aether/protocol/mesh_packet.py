"""MeshPacket definition and PacketType enumeration."""

from dataclasses import dataclass, field
from enum import IntEnum
from uuid import UUID, uuid4
from datetime import datetime
import time


class PacketType(IntEnum):
    """Enumeration of Aether mesh packet types."""

    RouteRequest = 1
    RouteReply = 2
    Data = 3
    Ack = 4
    SosBroadcast = 5
    SosAck = 6
    ChannelMessage = 7
    ChunkRequest = 8
    ChunkData = 9
    Heartbeat = 10
    StreamAnnounce = 11
    StreamSegment = 12
    StreamSubscribe = 13
    StreamUnsubscribe = 14
    VoicePtt = 15
    VoiceCall = 16
    VoiceSignaling = 17
    DtnBundle = 18
    DtnCustodyAck = 19
    DtnDeliveryReceipt = 20
    PresenceBeacon = 21
    PresenceQuery = 22
    ProfileSync = 23
    TipPacket = 24
    PreKeyRequest = 25
    PreKeyResponse = 26
    VideoCall = 27
    VideoSignaling = 28
    WatchSync = 29
    WatchReaction = 30
    VideoFrame = 31
    ScreenShare = 32
    WatchChunkRequest = 33
    TorrentMetadata = 34

    # Capability handshake — sender announces supported protocol-version range
    # + capability tags. Sent on first contact with an unknown peer. The
    # payload is a UTF-8 JSON-encoded HelloPayload. Unauthenticated and
    # unencrypted — peer identity is verified later via Ed25519 packet
    # signatures.
    Hello = 50

    # Reply to a Hello — receiver echoes back the agreed (highest mutually-
    # supported) protocol version and the intersection of capability tags.
    # Same JSON payload shape as Hello.
    HelloAck = 51


@dataclass
class MeshPacket:
    """
    The core packet transmitted across the Aether mesh network.

    Every piece of data — route discovery, messages, SOS broadcasts, voice,
    streaming, DTN bundles — travels as a MeshPacket.
    """

    # Packet identity and type
    id: UUID = field(default_factory=uuid4)
    type: PacketType = PacketType.Data

    # Source and destination
    source_uhid: str = ""
    destination_uhid: str = ""

    # TTL and priority
    ttl: int = 7
    priority: int = 0  # SOS packets use priority 999

    # Payload
    payload: bytes = field(default_factory=bytes)

    # Cryptography
    packet_nonce: bytes = field(default_factory=bytes)
    signature: bytes = field(default_factory=bytes)

    # Timing
    created_at: datetime = field(default_factory=datetime.utcnow)
    timestamp_ms: int = field(default_factory=lambda: int(time.time() * 1000))

    # Protocol version (1 = unsigned, 2 = signed)
    protocol_version: int = 2

    def is_expired(self, max_age_seconds: int = 300) -> bool:
        """Check if this packet has exceeded the maximum allowed age."""
        current_time_ms = int(time.time() * 1000)
        age_ms = current_time_ms - self.timestamp_ms
        return age_ms > max_age_seconds * 1000

    @property
    def can_forward(self) -> bool:
        """Check if the packet can still be forwarded (TTL > 0)."""
        return self.ttl > 0

    def __str__(self) -> str:
        return (
            f"[{self.type.name}] {self.id.hex[:8]} "
            f"src={self.source_uhid} dst={self.destination_uhid} "
            f"ttl={self.ttl} pri={self.priority} ver={self.protocol_version}"
        )
