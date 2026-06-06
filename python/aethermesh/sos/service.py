# SPDX-License-Identifier: MIT

"""Default SOS service. Originates and re-floods SOS broadcasts."""

from __future__ import annotations

import asyncio
import json
import logging
from collections import deque
from datetime import datetime, timedelta
from typing import Callable, Optional, Set
from uuid import UUID, uuid4

from aethermesh import constants
from aethermesh.extensibility import (
    BackendClient,
    IncentiveProvider,
    NoopBackendClient,
    NoopIncentiveProvider,
)
from aethermesh.models import SosAlert
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.routing.sender import MeshSender


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

        if packet.ttl > 1:
            packet.ttl -= 1
            await self._sender.broadcast(packet)
            await self._incentives.record_relay(self._sender.local_uhid, packet)

    def _prune_old_origins(self) -> None:
        cutoff = datetime.utcnow() - timedelta(hours=1)
        while self._recent_origins and self._recent_origins[0] < cutoff:
            self._recent_origins.popleft()


def _try_uuid(value: object) -> Optional[UUID]:
    if isinstance(value, UUID):
        return value
    if isinstance(value, str):
        try:
            return UUID(value)
        except ValueError:
            return None
    return None
