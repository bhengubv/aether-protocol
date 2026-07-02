# SPDX-License-Identifier: MIT
"""Presence over PacketType.PresenceBeacon (21) and PacketType.PresenceQuery (22).

A node broadcasts a privacy-preserving "I'm here" beacon advertising its ROTATING erid
(Ephemeral Routing Id — never the stable UHID), a COARSE geohash (host-truncated; empty
when hidden), its capability bitmask, a presence status, and a send timestamp; and it can
broadcast a query soliciting beacon replies for a (possibly empty) geohash. Inbound
beacons/queries are surfaced via ``on_beacon_received`` / ``on_query_received``.

Transport only — the ERID rotation + geohash coarsening are the host's concern (this
service never touches the stable UHID or precise location). Python port of the C#
reference (AetherNet.Presence.PresenceService), byte-identical to every other language
SDK (fixtures/presence/vectors.json).

Wire format: UTF-8 JSON, serialised with ``json.dumps(..., separators=(",", ":"))`` so
there is no whitespace, snake_case keys in a pinned field order.
  * Beacon(21): erid, geohash, capabilities, status, sent_at_ms — strings + bare ints
    (geohash may be "").
  * Query(22): query_id, geohash — query_id a lowercase-dashed UUID string.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Callable, Optional
from uuid import UUID, uuid4

from aethernet.constants import DEFAULT_TTL
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


@dataclass
class PresenceBeaconPayload:
    """JSON payload for PacketType.PresenceBeacon (21).

    Advertises the node's rotating ``erid`` (Crockford base-32, NOT the UHID), a coarse
    ``geohash`` (empty string = hidden), its ``capabilities`` (NodeCapabilities bitmask),
    a ``status`` (PresenceStatus value), and ``sent_at_ms`` (Unix ms). Field order:
    erid, geohash, capabilities, status, sent_at_ms.
    """

    erid: str = ""
    geohash: str = ""
    capabilities: int = 0
    status: int = 0
    sent_at_ms: int = 0


@dataclass
class PresenceQueryPayload:
    """JSON payload for PacketType.PresenceQuery (22).

    Solicits beacon replies. Field order: query_id, geohash. An empty ``geohash`` means
    "anywhere".
    """

    query_id: UUID = field(default_factory=uuid4)
    geohash: str = ""


def encode_beacon_payload(beacon: PresenceBeaconPayload) -> bytes:
    """Serialise a beacon to its canonical UTF-8 JSON wire bytes (byte-identity gate)."""
    obj = {
        "erid": beacon.erid,
        "geohash": beacon.geohash,
        "capabilities": int(beacon.capabilities),
        "status": int(beacon.status),
        "sent_at_ms": int(beacon.sent_at_ms),
    }
    return json.dumps(obj, separators=(",", ":")).encode("utf-8")


def encode_query_payload(query: PresenceQueryPayload) -> bytes:
    """Serialise a query to its canonical UTF-8 JSON wire bytes (byte-identity gate).

    ``query_id`` is emitted as a lowercase-dashed UUID string, matching C#'s
    ``Guid.ToString()``.
    """
    obj = {
        "query_id": str(query.query_id),
        "geohash": query.geohash,
    }
    return json.dumps(obj, separators=(",", ":")).encode("utf-8")


def _decode_beacon_payload(data: bytes) -> PresenceBeaconPayload:
    """Deserialise canonical wire bytes back into a beacon. Raises on malformed JSON."""
    obj = json.loads(data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else data)
    return PresenceBeaconPayload(
        erid=obj.get("erid", ""),
        geohash=obj.get("geohash", ""),
        capabilities=int(obj.get("capabilities", 0)),
        status=int(obj.get("status", 0)),
        sent_at_ms=int(obj.get("sent_at_ms", 0)),
    )


def _decode_query_payload(data: bytes) -> PresenceQueryPayload:
    """Deserialise canonical wire bytes back into a query. Raises on malformed JSON."""
    obj = json.loads(data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else data)
    return PresenceQueryPayload(
        query_id=UUID(str(obj["query_id"])),
        geohash=obj.get("geohash", ""),
    )


class PresenceService:
    """Binds PacketType.PresenceBeacon (21) + PacketType.PresenceQuery (22) to the mesh.

    Broadcast a beacon (host builds it with the rotating erid + coarse geohash), broadcast
    a query, and surface inbound beacons/queries via ``on_beacon_received`` /
    ``on_query_received``. Assign a callable to either attribute to receive events; the
    callback gets ``(payload, from_uhid)``.
    """

    def __init__(self, sender, logger=None) -> None:
        self._sender = sender
        self._logger = logger
        self.on_beacon_received: Optional[Callable[[PresenceBeaconPayload, str], None]] = None
        self.on_query_received: Optional[Callable[[PresenceQueryPayload, str], None]] = None

    def _log(self, message: str) -> None:
        if self._logger is not None:
            self._logger.debug(message)

    async def broadcast_beacon(self, beacon: PresenceBeaconPayload) -> int:
        """Broadcast a presence beacon. Returns the number of peers it reached."""
        if beacon is None:
            raise ValueError("beacon cannot be None")
        packet = MeshPacket(
            type=PacketType.PresenceBeacon,
            source_uhid=self._sender.local_uhid,
            destination_uhid="*",
            ttl=DEFAULT_TTL,
            payload=encode_beacon_payload(beacon),
        )
        delivered = await self._sender.broadcast(packet)
        self._log(f"Presence beacon (erid={beacon.erid}) broadcast to {delivered} peers")
        return delivered

    async def query(self, geohash: str) -> UUID:
        """Broadcast a presence query for ``geohash`` (coarse, possibly empty).

        Returns the new query id.
        """
        query_id = uuid4()
        payload = PresenceQueryPayload(query_id=query_id, geohash=geohash or "")
        packet = MeshPacket(
            type=PacketType.PresenceQuery,
            source_uhid=self._sender.local_uhid,
            destination_uhid="*",
            ttl=DEFAULT_TTL,
            payload=encode_query_payload(payload),
        )
        await self._sender.broadcast(packet)
        return query_id

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an inbound presence packet (beacon or query).

        Returns False on wrong type, malformed payload, or a beacon with an empty erid.
        """
        if packet is None:
            return False

        if packet.type == PacketType.PresenceBeacon:
            try:
                beacon = _decode_beacon_payload(packet.payload)
            except (ValueError, KeyError) as exc:
                self._log(
                    f"Presence beacon from {packet.source_uhid}: malformed payload — "
                    f"dropped: {exc}"
                )
                return False
            if not beacon.erid:
                return False
            if self.on_beacon_received is not None:
                self.on_beacon_received(beacon, packet.source_uhid)
            return True

        if packet.type == PacketType.PresenceQuery:
            try:
                query = _decode_query_payload(packet.payload)
            except (ValueError, KeyError) as exc:
                self._log(
                    f"Presence query from {packet.source_uhid}: malformed payload — "
                    f"dropped: {exc}"
                )
                return False
            if self.on_query_received is not None:
                self.on_query_received(query, packet.source_uhid)
            return True

        return False
