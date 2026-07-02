# SPDX-License-Identifier: MIT

"""Default SOS service. Originates and re-floods SOS broadcasts."""

from __future__ import annotations

import asyncio
import json
import logging
import time
from collections import deque
from datetime import datetime, timedelta
from typing import Callable, Optional, Set
from uuid import UUID, uuid4

from aethernet import constants
from aethernet.extensibility import (
    BackendClient,
    IncentiveProvider,
    NoopBackendClient,
    NoopIncentiveProvider,
)
from aethernet.models import SosAcknowledgement, SosAlert
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)


class SosBroadcastService:
    """Originates SOS broadcasts and re-floods inbound ones.

    Dedups by packet ID; rate-limited to ``MAX_SOS_BROADCASTS_PER_HOUR``
    originations per rolling hour.
    """

    def __init__(
        self,
        sender: MeshSender,
        backend: Optional[BackendClient] = None,
        incentives: Optional[IncentiveProvider] = None,
    ) -> None:
        self._sender = sender
        self._backend = backend or NoopBackendClient()
        self._incentives = incentives or NoopIncentiveProvider()
        self._recent_origins: deque[datetime] = deque()
        self._seen: Set[UUID] = set()
        self._active: dict[UUID, SosAlert] = {}
        self._lock = asyncio.Lock()
        self.on_sos_received: Optional[Callable[[SosAlert], None]] = None
        self.on_sos_resolved: Optional[Callable[[UUID], None]] = None
        # Raised on the ORIGINATING node when a peer acknowledges receiving one of our
        # active SOS alerts — proof the emergency reached at least one device.
        self.on_sos_acknowledged: Optional[Callable[[SosAcknowledgement], None]] = None

    async def broadcast(
        self,
        broadcast_type: str,
        message: Optional[str],
        latitude: float,
        longitude: float,
        geohash: Optional[str] = None,
    ) -> bool:
        if not broadcast_type:
            raise ValueError("broadcast_type must not be empty")

        async with self._lock:
            self._prune_old_origins()
            if len(self._recent_origins) >= constants.MAX_SOS_BROADCASTS_PER_HOUR:
                _LOG.warning(
                    "SOS rate limited — %d/%d originations in the last hour",
                    len(self._recent_origins),
                    constants.MAX_SOS_BROADCASTS_PER_HOUR,
                )
                return False
            self._recent_origins.append(datetime.utcnow())

        alert = SosAlert(
            sender_uhid=self._sender.local_uhid,
            broadcast_type=broadcast_type,
            message=message,
            latitude=latitude,
            longitude=longitude,
            geohash=geohash,
        )
        async with self._lock:
            self._active[alert.id] = alert

        body = json.dumps(
            {
                "broadcast_id": str(alert.id),
                "broadcast_type": broadcast_type,
                "message": message,
                "latitude": latitude,
                "longitude": longitude,
                "geohash": geohash,
            }
        ).encode("utf-8")

        packet = MeshPacket(
            type=PacketType.SosBroadcast,
            source_uhid=self._sender.local_uhid,
            destination_uhid="",
            ttl=constants.SOS_TTL,
            priority=constants.SOS_PRIORITY,
            payload=body,
        )
        async with self._lock:
            self._seen.add(packet.id)

        await self._sender.broadcast(packet)
        await self._backend.sync_sos(alert)
        return True

    async def resolve(self, broadcast_id: UUID) -> None:
        async with self._lock:
            removed = self._active.pop(broadcast_id, None)
        if removed and self.on_sos_resolved:
            self.on_sos_resolved(broadcast_id)

    def get_active_alerts(self) -> list[SosAlert]:
        return list(self._active.values())

    async def handle(self, packet: MeshPacket) -> None:
        if packet.type != PacketType.SosBroadcast:
            raise ValueError("expected PacketType.SosBroadcast")

        async with self._lock:
            if packet.id in self._seen:
                return
            self._seen.add(packet.id)

        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return

        if packet.source_uhid == self._sender.local_uhid:
            return

        alert_id = _try_uuid(data.get("broadcast_id")) or uuid4()
        alert = SosAlert(
            id=alert_id,
            sender_uhid=packet.source_uhid,
            broadcast_type=str(data.get("broadcast_type", "sos")),
            message=data.get("message"),
            latitude=float(data.get("latitude", 0.0)),
            longitude=float(data.get("longitude", 0.0)),
            geohash=data.get("geohash"),
        )
        async with self._lock:
            self._active[alert.id] = alert
        if self.on_sos_received:
            self.on_sos_received(alert)

        # Acknowledge back to the originator so the sender learns their SOS reached a device.
        await self._send_ack(alert.id, packet.source_uhid)

        if packet.ttl > 1:
            packet.ttl -= 1
            await self._sender.broadcast(packet)
            await self._incentives.record_relay(self._sender.local_uhid, packet)

    async def handle_ack(self, packet: MeshPacket) -> None:
        """Pump an incoming SosAck into the service.

        On the ORIGINATING node it records the responder against the matching active
        alert (deduping by responder UHID) and fires ``on_sos_acknowledged``. No-op if
        the ack references an SOS this node did not originate (or already resolved), or
        if the responder is this node itself.
        """
        if packet.type != PacketType.SosAck:
            raise ValueError("expected PacketType.SosAck")

        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return

        broadcast_id = _try_uuid(data.get("broadcast_id"))
        if broadcast_id is None:
            return

        responder = packet.source_uhid
        if not responder or responder == self._sender.local_uhid:
            return

        acknowledgement: Optional[SosAcknowledgement] = None
        async with self._lock:
            # Only the ORIGINATOR holds this alert in _active; every other node ignores.
            alert = self._active.get(broadcast_id)
            if alert is None:
                return
            if responder in alert.acknowledged_by:
                return  # already counted this responder — dedup
            alert.acknowledged_by.add(responder)
            acknowledgement = SosAcknowledgement(
                broadcast_id=broadcast_id,
                responder_uhid=responder,
                total_acknowledgements=len(alert.acknowledged_by),
            )

        if self.on_sos_acknowledged:
            self.on_sos_acknowledged(acknowledgement)

    async def _send_ack(self, broadcast_id: UUID, originator_uhid: str) -> None:
        # Send a directed SosAck back to the alert originator so the sender learns their
        # emergency reached this device. Best-effort: delivers when the originator is
        # reachable as a next hop.
        if not originator_uhid or originator_uhid == self._sender.local_uhid:
            return

        body = _encode_ack_payload(broadcast_id, int(time.time() * 1000))

        ack = MeshPacket(
            type=PacketType.SosAck,
            source_uhid=self._sender.local_uhid,
            destination_uhid=originator_uhid,
            ttl=constants.SOS_TTL,
            priority=constants.SOS_PRIORITY,
            payload=body,
        )
        await self._sender.send(ack, originator_uhid)

    def _prune_old_origins(self) -> None:
        cutoff = datetime.utcnow() - timedelta(hours=1)
        while self._recent_origins and self._recent_origins[0] < cutoff:
            self._recent_origins.popleft()


def _encode_ack_payload(broadcast_id: UUID, received_at_ms: int) -> bytes:
    """Serialize a SosAck wire payload to canonical, byte-identical UTF-8 JSON.

    Snake_case keys, field order ``broadcast_id`` then ``received_at_ms``, no whitespace,
    UUID lowercase-dashed (36 chars), ``received_at_ms`` a bare integer. Matches the C#
    ``SosAckPayload`` serialization and the fixtures/sos byte-identity vectors.
    """
    return json.dumps(
        {
            "broadcast_id": str(broadcast_id),
            "received_at_ms": received_at_ms,
        },
        separators=(",", ":"),
    ).encode("utf-8")


def _try_uuid(value: object) -> Optional[UUID]:
    if isinstance(value, UUID):
        return value
    if isinstance(value, str):
        try:
            return UUID(value)
        except ValueError:
            return None
    return None
