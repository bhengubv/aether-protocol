# SPDX-License-Identifier: MIT

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

    # PoVTokenExchange — on-mesh Proof-of-Vicinity token exchange. A witness node
    # sends a directed, witness-signed PoVToken to the subject one short-range hop
    # away (TTL 1); the subject verifies the witness's Ed25519 signature over the
    # canonical token body, counter-signs as the subject, and records it as a local
    # anti-Sybil routing/identity signal. Payload is a UTF-8 JSON-encoded PoVToken
    # (see the aethernet.market package). Carries NO value semantics. Mirrors the C#
    # AetherNet.Market.PoVTokenExchangeService.
    PoVTokenExchange = 43

    # NamePublish — application-layer name resolution. Sent by IDirectoryService
    # to announce a (name -> ContentDescriptor) binding to the mesh, or in
    # response to an inbound NameQuery from a peer that asked for the binding.
    # Payload is a UTF-8 JSON-encoded NamePublishPayload. Added in v1.2.0 —
    # closes Issue #60 surfaced by Wave 16.
    NamePublish = 38

    # NameQuery — application-layer name resolution. Sent by IDirectoryService
    # when resolve() misses the local cache; flooded across the mesh so any
    # node holding the binding can reply with a NamePublish carrying the
    # matching ContentDescriptor. Payload is a UTF-8 JSON-encoded
    # NameQueryPayload. Added in v1.2.0 — closes Issue #60.
    NameQuery = 39

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

    # ABMF (W18-5) — Bandwidth Measurement Framework packet types.
    # BandwidthProbe (53): active probe packet sent to a peer to measure RTT
    #   and delivery rate. Payload carries a sequence number and probe size.
    # BandwidthAck (54): four-timestamp reply to a BandwidthProbe; used to
    #   derive RTT (clock-sync-free) and forward OWD (RFC 5136 §3).
    # BandwidthGossip (55): warm-start payload broadcast during handshake so
    #   a new session starts with a non-zero BtlBw estimate.
    BandwidthProbe  = 53
    BandwidthAck    = 54
    BandwidthGossip = 55

    # CircuitRelayControl — carries one native circuit-relay-v2 hop's frame
    # (reserve/connect/stop/data + responses) as a serialized RelayFrame in the packet
    # body. Wire byte 57 matches the C# PacketType.CircuitRelayControl so a relayed hop
    # is byte-identical across languages; an un-upgraded node drops the unknown type.
    # The relay Transport processes these via its MeshRelayLink; only a DATA frame
    # delivered to the final destination surfaces as tunnelled app data.
    CircuitRelayControl = 57


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
