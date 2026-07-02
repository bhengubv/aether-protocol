# SPDX-License-Identifier: MIT

"""Default heartbeat service. Broadcasts liveness beacons and tracks peer liveness.

A node periodically broadcasts a Heartbeat packet (TTL 1 — direct neighbours only) so
peers can track liveness. Receivers maintain a per-peer :class:`PeerLiveness` table keyed
by the heartbeat originator's UHID and can query which peers are currently live.

Unauthenticated by design — like SOS, a heartbeat is a low-stakes liveness hint, not a
security assertion. Mirrors the C# ``AetherNet.Heartbeat.HeartbeatService``.
"""

from __future__ import annotations

import asyncio
import json
import logging
import time
from dataclasses import dataclass, field
from typing import Callable, Optional

from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)


@dataclass
class HeartbeatPayload:
    """JSON payload for :class:`PacketType.Heartbeat` packets.

    Wire format: UTF-8 JSON with snake_case keys, field order ``sequence`` then
    ``sent_at_ms``, no whitespace, both values bare integers. Byte-identical across all
    language ports (locked by fixtures/heartbeat/vectors.json).
    """

    #: Monotonic heartbeat sequence number from the sender (starts at 1, increments per beat).
    sequence: int = 0
    #: Unix timestamp in milliseconds when the sender emitted this heartbeat.
    sent_at_ms: int = 0


@dataclass
class PeerLiveness:
    """A peer's last observed liveness, maintained on the receiving node."""

    #: UHID of the peer this liveness record describes.
    uhid: str = ""
    #: The :attr:`HeartbeatPayload.sequence` of the most recent heartbeat seen from the peer.
    last_sequence: int = 0
    #: The peer-stamped :attr:`HeartbeatPayload.sent_at_ms` of the most recent heartbeat.
    last_sent_at_ms: int = 0
    #: Local Unix-ms timestamp when the most recent heartbeat was received.
    received_at_ms: int = 0


class HeartbeatService:
    """Broadcasts Heartbeat liveness beacons and tracks the liveness of peers.

    The sequence number increments on every :meth:`send_heartbeat` call. Receivers keep a
    per-peer :class:`PeerLiveness` record keyed by the heartbeat's source UHID.
    """

    def __init__(self, sender: MeshSender) -> None:
        self._sender = sender
        self._sequence = 0
        self._peers: dict[str, PeerLiveness] = {}
        self._lock = asyncio.Lock()
        # Raised when a heartbeat is received from a peer (new or refreshed liveness).
        self.on_peer_seen: Optional[Callable[[PeerLiveness], None]] = None

    async def send_heartbeat(self) -> int:
        """Broadcast a single heartbeat to all directly connected peers (TTL 1).

        The sequence number increments on every call. Returns the number of peers the
        beacon was delivered to.
        """
        async with self._lock:
            self._sequence += 1
            seq = self._sequence

        body = _encode_heartbeat_payload(seq, int(time.time() * 1000))

        packet = MeshPacket(
            type=PacketType.Heartbeat,
            source_uhid=self._sender.local_uhid,
            destination_uhid="*",
            ttl=1,  # heartbeats are single-hop: liveness of DIRECT neighbours only
            payload=body,
        )

        delivered = await self._sender.broadcast(packet)
        _LOG.debug("Heartbeat seq=%d broadcast to %d peers", seq, delivered)
        return delivered

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an incoming Heartbeat packet.

        Refreshes the sender's liveness record (keyed by ``packet.source_uhid``) and fires
        ``on_peer_seen``. Returns ``False`` (no-op) for the wrong packet type, a
        self-originated heartbeat, or a malformed payload; ``True`` otherwise.
        """
        if packet.type != PacketType.Heartbeat:
            return False

        # Ignore our own heartbeat echoed back.
        if packet.source_uhid == self._sender.local_uhid:
            return False

        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            _LOG.debug("Heartbeat from %s: malformed payload — dropped", packet.source_uhid)
            return False
        if not isinstance(data, dict):
            return False

        liveness = PeerLiveness(
            uhid=packet.source_uhid,
            last_sequence=int(data.get("sequence", 0)),
            last_sent_at_ms=int(data.get("sent_at_ms", 0)),
            received_at_ms=int(time.time() * 1000),
        )
        async with self._lock:
            self._peers[packet.source_uhid] = liveness
        if self.on_peer_seen:
            self.on_peer_seen(liveness)
        return True

    def get_known_peers(self) -> list[PeerLiveness]:
        """Snapshot of every peer this node has ever seen a heartbeat from."""
        return list(self._peers.values())

    def get_live_peers(self, within_seconds: int) -> list[PeerLiveness]:
        """Peers whose most recent heartbeat was received within the last ``within_seconds``."""
        cutoff = int(time.time() * 1000) - within_seconds * 1000
        return [p for p in self._peers.values() if p.received_at_ms >= cutoff]


def _encode_heartbeat_payload(sequence: int, sent_at_ms: int) -> bytes:
    """Serialize a Heartbeat wire payload to canonical, byte-identical UTF-8 JSON.

    Snake_case keys, field order ``sequence`` then ``sent_at_ms``, no whitespace, both
    values bare integers. Matches the C# ``HeartbeatPayload`` serialization and the
    fixtures/heartbeat byte-identity vectors.
    """
    return json.dumps(
        {
            "sequence": sequence,
            "sent_at_ms": sent_at_ms,
        },
        separators=(",", ":"),
    ).encode("utf-8")
