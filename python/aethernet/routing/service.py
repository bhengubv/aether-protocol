# SPDX-License-Identifier: MIT

"""AODV-inspired reactive routing service."""

from __future__ import annotations

import asyncio
import logging
import time
from datetime import datetime, timedelta
from typing import Dict, List, Optional
from uuid import UUID, uuid4

from aethernet import constants
from aethernet.extensibility import IncentiveProvider, NoopIncentiveProvider
from aethernet.models import RouteEntry
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender
from aethernet.routing.store import RouteStore, InMemoryRouteStore
from aethernet.routing.verifier import RouteReplyVerifier, AcceptAllRouteReplyVerifier


_LOG = logging.getLogger(__name__)


class RoutingService:
    """AODV-inspired reactive routing.

    Lifecycle:
      - Callers invoke ``find_route(destination_uhid)`` to get a route.
        Cached routes return immediately; otherwise an RREQ is broadcast and
        the call awaits the matching RREP (subject to ``ROUTE_TIMEOUT_MS``).
      - Hosts pump received RREQ / RREP packets through ``handle_route_request``
        and ``handle_route_reply`` respectively.
      - Hosts call ``prune`` periodically to clear expired routes and trim the
        RREQ deduplication cache.
    """

    def __init__(
        self,
        sender: MeshSender,
        store: Optional[RouteStore] = None,
        verifier: Optional[RouteReplyVerifier] = None,
        incentives: Optional[IncentiveProvider] = None,
    ) -> None:
        self._sender = sender
        self._store = store or InMemoryRouteStore()
        self._verifier = verifier or AcceptAllRouteReplyVerifier()
        self._incentives = incentives or NoopIncentiveProvider()
        self._cache: Dict[str, RouteEntry] = {}
        self._pending: Dict[str, asyncio.Future[Optional[RouteEntry]]] = {}
        self._seen_rreqs: set[UUID] = set()
        self._rreq_sources: Dict[str, List[float]] = {}   # per-source Unix timestamps
        self._reputation = None  # optional NodeReputationService; None = disabled
        self._loaded = False
        self._lock = asyncio.Lock()

    async def find_route(self, destination_uhid: str) -> Optional[RouteEntry]:
        if not destination_uhid:
            raise ValueError("destination_uhid must not be empty")

        await self._ensure_loaded()

        async with self._lock:
            cached = self._cache.get(destination_uhid)
            if cached and not cached.is_expired:
                return cached

        stored = await self._store.get(destination_uhid)
        if stored and not stored.is_expired:
            async with self._lock:
                self._cache[destination_uhid] = stored
            return stored

        return await self._discover(destination_uhid)

    def get_cached_route(self, destination_uhid: str) -> Optional[RouteEntry]:
        if not destination_uhid:
            return None
        cached = self._cache.get(destination_uhid)
        if cached is None or cached.is_expired:
            return None
        return cached

    def get_all_routes(self) -> List[RouteEntry]:
        return [r for r in self._cache.values() if not r.is_expired]

    def set_reputation(self, reputation) -> None:
        """Attach an optional NodeReputationService. Pass None to disable."""
        self._reputation = reputation

    async def handle_route_request(self, rreq: MeshPacket) -> None:
        if rreq.type != PacketType.RouteRequest:
            raise ValueError("expected PacketType.RouteRequest")

        async with self._lock:
            if rreq.id in self._seen_rreqs:
                return
            # Per-source RREQ rate limiting — mirrors Go/Rust RoutingService.
            # Unique packet IDs only count against the limit; duplicates caught above.
            now_ts = time.time()
            window_start = now_ts - constants.RREQ_RATE_LIMIT_WINDOW_SECONDS
            recent = [ts for ts in self._rreq_sources.get(rreq.source_uhid, [])
                      if ts > window_start]
            if len(recent) >= constants.RREQ_RATE_LIMIT_MAX:
                self._rreq_sources[rreq.source_uhid] = recent
                if self._reputation is not None:
                    self._reputation.record_rreq_flood_attempt(rreq.source_uhid)
                return  # silently drop: source is flooding unique RREQs
            recent.append(now_ts)
            self._rreq_sources[rreq.source_uhid] = recent
            self._seen_rreqs.add(rreq.id)

        local = self._sender.local_uhid
        if not rreq.source_uhid or rreq.source_uhid == local:
            return

        hop_count = max(1, constants.DEFAULT_TTL - rreq.ttl + 1)
        reverse = RouteEntry(
            destination_uhid=rreq.source_uhid,
            next_hop_uhid=rreq.source_uhid,
            hop_count=hop_count,
            quality_score=50,
            expires_at=datetime.utcnow() + timedelta(seconds=constants.ROUTE_EXPIRY_SECONDS),
        )
        async with self._lock:
            self._cache[reverse.destination_uhid] = reverse
        await self._store.save(reverse)

        if rreq.destination_uhid == local:
            await self._send_rrep(local, rreq)
            return

        async with self._lock:
            known = self._cache.get(rreq.destination_uhid)
        if known and not known.is_expired:
            await self._send_rrep(rreq.destination_uhid, rreq)
            return

        if rreq.ttl > 1:
            rreq.ttl -= 1
            await self._sender.broadcast(rreq)
            await self._incentives.record_relay(local, rreq)

    async def handle_route_reply(self, rrep: MeshPacket) -> None:
        if rrep.type != PacketType.RouteReply:
            raise ValueError("expected PacketType.RouteReply")

        if not await self._verifier.verify(rrep):
            return

        local = self._sender.local_uhid
        if not rrep.source_uhid or rrep.source_uhid == local:
            return

        hop_count = max(1, constants.DEFAULT_TTL - rrep.ttl + 1)
        forward = RouteEntry(
            destination_uhid=rrep.source_uhid,
            next_hop_uhid=rrep.source_uhid,
            hop_count=hop_count,
            quality_score=50,
            expires_at=datetime.utcnow() + timedelta(seconds=constants.ROUTE_EXPIRY_SECONDS),
        )
        async with self._lock:
            self._cache[forward.destination_uhid] = forward
            future = self._pending.pop(forward.destination_uhid, None) if rrep.destination_uhid == local else None
        await self._store.save(forward)

        if future is not None and not future.done():
            future.set_result(forward)

        if rrep.destination_uhid == local:
            return

        if rrep.ttl <= 1:
            return

        async with self._lock:
            next_hop = self._cache.get(rrep.destination_uhid)
        if next_hop is not None and not next_hop.is_expired:
            rrep.ttl -= 1
            delivered = await self._sender.send(rrep, next_hop.next_hop_uhid)
            if delivered:
                await self._incentives.record_relay(local, rrep)

    async def prune(self) -> None:
        async with self._lock:
            expired = [k for k, r in self._cache.items() if r.is_expired]
            for k in expired:
                del self._cache[k]
            if len(self._seen_rreqs) > 10_000:
                self._seen_rreqs.clear()
        await self._store.prune_expired()

    async def _send_rrep(self, replied_source: str, rreq: MeshPacket) -> None:
        rrep = MeshPacket(
            type=PacketType.RouteReply,
            source_uhid=replied_source,
            destination_uhid=rreq.source_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=rreq.payload,
        )
        async with self._lock:
            reverse = self._cache.get(rreq.source_uhid)
        if reverse is not None and not reverse.is_expired:
            await self._sender.send(rrep, reverse.next_hop_uhid)
        else:
            await self._sender.broadcast(rrep)

    async def _discover(self, destination_uhid: str) -> Optional[RouteEntry]:
        loop = asyncio.get_event_loop()
        future: asyncio.Future[Optional[RouteEntry]] = loop.create_future()
        async with self._lock:
            self._pending[destination_uhid] = future

        rreq = MeshPacket(
            type=PacketType.RouteRequest,
            source_uhid=self._sender.local_uhid,
            destination_uhid=destination_uhid,
            ttl=constants.DEFAULT_TTL,
        )
        fanout = await self._sender.broadcast(rreq)
        if fanout == 0:
            async with self._lock:
                self._pending.pop(destination_uhid, None)
            return None

        try:
            return await asyncio.wait_for(future, timeout=constants.ROUTE_TIMEOUT_MS / 1000)
        except asyncio.TimeoutError:
            return None
        finally:
            async with self._lock:
                self._pending.pop(destination_uhid, None)

    async def _ensure_loaded(self) -> None:
        async with self._lock:
            if self._loaded:
                return
            self._loaded = True

        try:
            for r in await self._store.get_all():
                if not r.is_expired:
                    async with self._lock:
                        self._cache[r.destination_uhid] = r
        except Exception:  # noqa: BLE001
            _LOG.exception("Failed to load routes from store; starting with empty cache")
            async with self._lock:
                self._loaded = False
