# SPDX-License-Identifier: MIT

"""WatchTogetherService — synchronised co-watching with RTT compensation."""

from __future__ import annotations

import asyncio
import json
import logging
import time
import uuid
from dataclasses import dataclass, field
from typing import Callable, Optional

from aether import constants
from aether.protocol.mesh_packet import MeshPacket, PacketType
from aether.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)

_CONTROL_PRIORITY: int = 32


@dataclass
class WatchSession:
    session_id: uuid.UUID
    content_id: str
    local_uhid: str
    members: set[str] = field(default_factory=set)
    position_ms: int = 0
    playback_speed: float = 1.0
    is_playing: bool = False


class WatchTogetherService:
    """Synchronised co-watching service with RTT compensation.

    Position compensation formula (applied on receipt):
        position = position_ms + int((now_ms - sent_at_ms) * playback_speed)

    Callers:
      - ``invite_to_session`` to create and invite members.
      - ``play``, ``pause``, ``seek``, ``set_speed`` to control playback.
      - ``send_reaction`` to broadcast emoji/reaction.
      - ``handle_packet`` must be invoked for PacketType.WatchSync and
        PacketType.WatchReaction.

    Events (assign callables):
      - ``on_invited(session_id, content_id, from_uhid)``
      - ``on_play(session_id, position_ms)``
      - ``on_pause(session_id, position_ms)``
      - ``on_seek(session_id, position_ms)``
      - ``on_speed_change(session_id, playback_speed)``
      - ``on_reaction(session_id, from_uhid, reaction)``
    """

    def __init__(
        self,
        transport: MeshSender,
        local_uhid: str,
    ) -> None:
        self._transport = transport
        self._local_uhid = local_uhid
        self._sessions: dict[uuid.UUID, WatchSession] = {}
        self._lock = asyncio.Lock()

        self.on_invited: Optional[Callable[[uuid.UUID, str, str], None]] = None
        self.on_play: Optional[Callable[[uuid.UUID, int], None]] = None
        self.on_pause: Optional[Callable[[uuid.UUID, int], None]] = None
        self.on_seek: Optional[Callable[[uuid.UUID, int], None]] = None
        self.on_speed_change: Optional[Callable[[uuid.UUID, float], None]] = None
        self.on_reaction: Optional[Callable[[uuid.UUID, str, str], None]] = None

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    async def invite_to_session(
        self,
        session_id: uuid.UUID,
        content_id: str,
        member_uhids: list[str],
    ) -> None:
        """Create a session and invite *member_uhids*."""
        async with self._lock:
            session = self._sessions.get(session_id)
            if session is None:
                session = WatchSession(
                    session_id=session_id,
                    content_id=content_id,
                    local_uhid=self._local_uhid,
                    members={self._local_uhid},
                )
                self._sessions[session_id] = session
            session.members.update(member_uhids)

        payload = json.dumps({
            "session_id": str(session_id),
            "kind": "invite",
            "content_id": content_id,
            "sent_at_ms": int(time.time() * 1000),
        }).encode("utf-8")
        for uhid in member_uhids:
            packet = MeshPacket(
                type=PacketType.WatchSync,
                source_uhid=self._local_uhid,
                destination_uhid=uhid,
                ttl=constants.DEFAULT_TTL,
                priority=_CONTROL_PRIORITY,
                payload=payload,
            )
            await self._transport.send(packet, uhid)

    async def play(self, session_id: uuid.UUID, position_ms: int) -> None:
        """Resume playback at *position_ms*."""
        await self._send_sync(
            session_id=session_id,
            kind="play",
            position_ms=position_ms,
        )
        async with self._lock:
            session = self._sessions.get(session_id)
            if session:
                session.position_ms = position_ms
                session.is_playing = True

    async def pause(self, session_id: uuid.UUID, position_ms: int) -> None:
        """Pause playback at *position_ms*."""
        await self._send_sync(
            session_id=session_id,
            kind="pause",
            position_ms=position_ms,
        )
        async with self._lock:
            session = self._sessions.get(session_id)
            if session:
                session.position_ms = position_ms
                session.is_playing = False

    async def seek(self, session_id: uuid.UUID, position_ms: int) -> None:
        """Seek to *position_ms*."""
        await self._send_sync(
            session_id=session_id,
            kind="seek",
            position_ms=position_ms,
        )
        async with self._lock:
            session = self._sessions.get(session_id)
            if session:
                session.position_ms = position_ms

    async def set_speed(self, session_id: uuid.UUID, playback_speed: float) -> None:
        """Change playback speed."""
        await self._send_sync(
            session_id=session_id,
            kind="speed",
            playback_speed=playback_speed,
        )
        async with self._lock:
            session = self._sessions.get(session_id)
            if session:
                session.playback_speed = playback_speed

    async def send_reaction(self, session_id: uuid.UUID, reaction: str) -> None:
        """Broadcast an emoji/reaction to all session members."""
        async with self._lock:
            session = self._sessions.get(session_id)
            members_snapshot = set(session.members) if session else set()

        payload = json.dumps({
            "session_id": str(session_id),
            "reaction": reaction,
        }).encode("utf-8")
        for uhid in members_snapshot:
            if uhid == self._local_uhid:
                continue
            packet = MeshPacket(
                type=PacketType.WatchReaction,
                source_uhid=self._local_uhid,
                destination_uhid=uhid,
                ttl=constants.DEFAULT_TTL,
                priority=_CONTROL_PRIORITY,
                payload=payload,
            )
            await self._transport.send(packet, uhid)

    async def handle_packet(self, packet: MeshPacket) -> None:
        """Route incoming WatchSync / WatchReaction packets."""
        if packet.type == PacketType.WatchSync:
            await self._handle_sync(packet)
        elif packet.type == PacketType.WatchReaction:
            await self._handle_reaction(packet)

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    async def _send_sync(
        self,
        session_id: uuid.UUID,
        kind: str,
        position_ms: int | None = None,
        playback_speed: float | None = None,
    ) -> None:
        async with self._lock:
            session = self._sessions.get(session_id)
            members_snapshot = set(session.members) if session else set()

        msg: dict = {
            "session_id": str(session_id),
            "kind": kind,
            "sent_at_ms": int(time.time() * 1000),
        }
        if position_ms is not None:
            msg["position_ms"] = position_ms
        if playback_speed is not None:
            msg["playback_speed"] = playback_speed

        payload = json.dumps(msg).encode("utf-8")
        for uhid in members_snapshot:
            if uhid == self._local_uhid:
                continue
            packet = MeshPacket(
                type=PacketType.WatchSync,
                source_uhid=self._local_uhid,
                destination_uhid=uhid,
                ttl=constants.DEFAULT_TTL,
                priority=_CONTROL_PRIORITY,
                payload=payload,
            )
            await self._transport.send(packet, uhid)

    async def _handle_sync(self, packet: MeshPacket) -> None:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return

        session_id = _try_uuid(data.get("session_id"))
        if session_id is None:
            return
        kind = str(data.get("kind", ""))

        if kind == "invite":
            content_id = str(data.get("content_id") or "")
            async with self._lock:
                if session_id not in self._sessions:
                    self._sessions[session_id] = WatchSession(
                        session_id=session_id,
                        content_id=content_id,
                        local_uhid=self._local_uhid,
                        members={self._local_uhid, packet.source_uhid},
                    )
            if self.on_invited:
                self.on_invited(session_id, content_id, packet.source_uhid)
            return

        # For position-bearing events apply RTT compensation
        sent_at_ms = int(data.get("sent_at_ms") or int(time.time() * 1000))
        now_ms = int(time.time() * 1000)
        rtt_ms = max(0, now_ms - sent_at_ms)

        async with self._lock:
            session = self._sessions.get(session_id)
            playback_speed = session.playback_speed if session else 1.0

        raw_position_ms: int | None = data.get("position_ms")

        if kind in ("play", "seek", "pause"):
            if raw_position_ms is None:
                return
            if kind in ("play", "seek"):
                # Compensate for in-flight delay
                compensated = raw_position_ms + int(rtt_ms * playback_speed)
            else:
                compensated = raw_position_ms

            async with self._lock:
                if session:
                    session.position_ms = compensated
                    if kind == "play":
                        session.is_playing = True
                    elif kind == "pause":
                        session.is_playing = False

            if kind == "play" and self.on_play:
                self.on_play(session_id, compensated)
            elif kind == "pause" and self.on_pause:
                self.on_pause(session_id, compensated)
            elif kind == "seek" and self.on_seek:
                self.on_seek(session_id, compensated)

        elif kind == "speed":
            raw_speed = data.get("playback_speed")
            if raw_speed is None:
                return
            speed = float(raw_speed)
            async with self._lock:
                if session:
                    session.playback_speed = speed
            if self.on_speed_change:
                self.on_speed_change(session_id, speed)

    async def _handle_reaction(self, packet: MeshPacket) -> None:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return
        session_id = _try_uuid(data.get("session_id"))
        if session_id is None:
            return
        reaction = str(data.get("reaction", ""))
        if self.on_reaction:
            self.on_reaction(session_id, packet.source_uhid, reaction)


# ------------------------------------------------------------------
# Helpers
# ------------------------------------------------------------------

def _try_uuid(value: object) -> uuid.UUID | None:
    if isinstance(value, uuid.UUID):
        return value
    if isinstance(value, str):
        try:
            return uuid.UUID(value)
        except ValueError:
            return None
    return None
