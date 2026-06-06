# SPDX-License-Identifier: MIT

"""GroupVoiceCallService — multi-party voice calls over the Aether mesh."""

from __future__ import annotations

import asyncio
import json
import logging
import struct
import time
import uuid
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable, Optional

from aethermesh import constants
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)

_FRAME_PRIORITY: int = 64
_SIGNALING_PRIORITY: int = 32

# GroupVoiceFrame binary layout
# [16] CallId UUID (big-endian)
# [4]  Sequence uint32 little-endian
# [8]  TimestampMs int64 little-endian
# [1]  IsSilence uint8
# [4]  KeyGeneration uint32 little-endian
# [N]  EncodedPayload
_GROUP_FRAME_HEADER_SIZE = 16 + 4 + 8 + 1 + 4  # 33 bytes


class GroupCallState(Enum):
    Pending = "pending"
    Active = "active"
    Ended = "ended"


@dataclass
class GroupVoiceCallSession:
    call_id: uuid.UUID
    host_uhid: str
    local_uhid: str
    members: set[str] = field(default_factory=set)
    state: GroupCallState = GroupCallState.Pending
    key_generation: int = 0
    sequence: int = 0
    started_at_ms: int = field(default_factory=lambda: int(time.time() * 1000))


def _pack_group_frame(
    call_id: uuid.UUID,
    sequence: int,
    timestamp_ms: int,
    is_silence: bool,
    key_generation: int,
    encoded_audio: bytes,
) -> bytes:
    header = (
        call_id.bytes
        + struct.pack("<I", sequence)
        + struct.pack("<q", timestamp_ms)
        + struct.pack("B", 1 if is_silence else 0)
        + struct.pack("<I", key_generation)
    )
    return header + encoded_audio


def _unpack_group_frame(
    payload: bytes,
) -> tuple[uuid.UUID, int, int, bool, int, bytes] | None:
    if len(payload) < _GROUP_FRAME_HEADER_SIZE:
        return None
    try:
        call_id = uuid.UUID(bytes=payload[:16])
        sequence = struct.unpack_from("<I", payload, 16)[0]
        timestamp_ms = struct.unpack_from("<q", payload, 20)[0]
        is_silence = bool(struct.unpack_from("B", payload, 28)[0])
        key_generation = struct.unpack_from("<I", payload, 29)[0]
        encoded = payload[_GROUP_FRAME_HEADER_SIZE:]
        return call_id, sequence, timestamp_ms, is_silence, key_generation, encoded
    except (struct.error, ValueError):
        return None


class GroupVoiceCallService:
    """Multi-party voice call service.

    The host creates the call and invites members. Non-hosts call ``join``.
    Audio frames are fanned out to all members in the session.

    Events (assign callables):
      - ``on_invite(call_id, from_uhid, invited_uhids)``
      - ``on_member_joined(call_id, uhid)``
      - ``on_member_left(call_id, uhid)``
      - ``on_member_kicked(call_id, uhid)``
      - ``on_call_ended(call_id)``
      - ``on_key_rotation(call_id, key_generation)``
      - ``on_frame_received(call_id, from_uhid, sequence, timestamp_ms, is_silence, key_generation, audio)``
    """

    def __init__(
        self,
        transport: MeshSender,
        routing_service,
        local_uhid: str,
    ) -> None:
        self._transport = transport
        self._routing = routing_service
        self._local_uhid = local_uhid
        self._sessions: dict[uuid.UUID, GroupVoiceCallSession] = {}
        self._lock = asyncio.Lock()

        self.on_invite: Optional[Callable[[uuid.UUID, str, list[str]], None]] = None
        self.on_member_joined: Optional[Callable[[uuid.UUID, str], None]] = None
        self.on_member_left: Optional[Callable[[uuid.UUID, str], None]] = None
        self.on_member_kicked: Optional[Callable[[uuid.UUID, str], None]] = None
        self.on_call_ended: Optional[Callable[[uuid.UUID], None]] = None
        self.on_key_rotation: Optional[Callable[[uuid.UUID, int], None]] = None
        self.on_frame_received: Optional[
            Callable[[uuid.UUID, str, int, int, bool, int, bytes], None]
        ] = None

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    async def invite(self, call_id: uuid.UUID, member_uhids: list[str]) -> None:
        """Create (or extend) a group call and invite *member_uhids*.

        If *call_id* does not yet exist locally a new session is created with
        the local node as host.
        """
        async with self._lock:
            session = self._sessions.get(call_id)
            if session is None:
                session = GroupVoiceCallSession(
                    call_id=call_id,
                    host_uhid=self._local_uhid,
                    local_uhid=self._local_uhid,
                    members={self._local_uhid},
                    state=GroupCallState.Active,
                )
                self._sessions[call_id] = session
            session.members.update(member_uhids)

        payload = _encode_group_signaling(
            kind="invite",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid="",
            invited_uhids=member_uhids,
        )
        for uhid in member_uhids:
            await self._send_signaling(uhid, payload)

    async def join(self, call_id: uuid.UUID) -> None:
        """Join an existing group call (called by invitees)."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None:
            return

        async with self._lock:
            session.state = GroupCallState.Active
            session.members.add(self._local_uhid)
            members_snapshot = set(session.members)

        payload = _encode_group_signaling(
            kind="join",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid="",
        )
        for uhid in members_snapshot:
            if uhid != self._local_uhid:
                await self._send_signaling(uhid, payload)

    async def leave(self, call_id: uuid.UUID) -> None:
        """Leave the group call."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None:
            return

        async with self._lock:
            session.members.discard(self._local_uhid)
            members_snapshot = set(session.members)

        payload = _encode_group_signaling(
            kind="leave",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid="",
        )
        for uhid in members_snapshot:
            await self._send_signaling(uhid, payload)

    async def kick(self, call_id: uuid.UUID, target_uhid: str) -> None:
        """Remove *target_uhid* from the call (host only)."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None:
            return
        if session.host_uhid != self._local_uhid:
            _LOG.warning("kick attempted by non-host %s", self._local_uhid)
            return

        async with self._lock:
            session.members.discard(target_uhid)
            members_snapshot = set(session.members)

        payload = _encode_group_signaling(
            kind="kick",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid="",
            kicked_uhid=target_uhid,
        )
        # Notify the kicked member and all remaining members.
        for uhid in members_snapshot | {target_uhid}:
            await self._send_signaling(uhid, payload)

    async def send_frame(
        self,
        call_id: uuid.UUID,
        audio: bytes,
        is_silence: bool,
        key_generation: int,
    ) -> None:
        """Broadcast an encoded audio frame to all call members."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None or session.state != GroupCallState.Active:
            return

        async with self._lock:
            seq = session.sequence
            session.sequence += 1
            members_snapshot = set(session.members)

        frame_bytes = _pack_group_frame(
            call_id=call_id,
            sequence=seq,
            timestamp_ms=int(time.time() * 1000),
            is_silence=is_silence,
            key_generation=key_generation,
            encoded_audio=audio,
        )
        for uhid in members_snapshot:
            if uhid == self._local_uhid:
                continue
            packet = MeshPacket(
                type=PacketType.VoiceCall,
                source_uhid=self._local_uhid,
                destination_uhid=uhid,
                ttl=constants.DEFAULT_TTL,
                priority=_FRAME_PRIORITY,
                payload=frame_bytes,
            )
            await self._transport.send(packet, uhid)

    async def handle_packet(self, packet: MeshPacket) -> None:
        """Route incoming packets to the correct handler."""
        if packet.type == PacketType.VoiceSignaling:
            await self._handle_signaling(packet)
        elif packet.type == PacketType.VoiceCall:
            await self._handle_frame(packet)

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    async def _send_signaling(self, to_uhid: str, payload: bytes) -> None:
        packet = MeshPacket(
            type=PacketType.VoiceSignaling,
            source_uhid=self._local_uhid,
            destination_uhid=to_uhid,
            ttl=constants.DEFAULT_TTL,
            priority=_SIGNALING_PRIORITY,
            payload=payload,
        )
        await self._transport.send(packet, to_uhid)

    async def _handle_signaling(self, packet: MeshPacket) -> None:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return

        kind = str(data.get("kind", ""))
        call_id = _try_uuid(data.get("call_id"))
        if call_id is None:
            return
        from_uhid = str(data.get("from_uhid", packet.source_uhid))

        if kind == "invite":
            invited: list[str] = list(data.get("invited_uhids") or [])
            if self._local_uhid in invited:
                # Create a pending session
                async with self._lock:
                    if call_id not in self._sessions:
                        self._sessions[call_id] = GroupVoiceCallSession(
                            call_id=call_id,
                            host_uhid=from_uhid,
                            local_uhid=self._local_uhid,
                            members=set(invited) | {from_uhid},
                        )
                if self.on_invite:
                    self.on_invite(call_id, from_uhid, invited)

        elif kind == "join":
            async with self._lock:
                session = self._sessions.get(call_id)
                if session is not None:
                    session.members.add(from_uhid)
            if self.on_member_joined:
                self.on_member_joined(call_id, from_uhid)

        elif kind == "leave":
            async with self._lock:
                session = self._sessions.get(call_id)
                if session is not None:
                    session.members.discard(from_uhid)
            if self.on_member_left:
                self.on_member_left(call_id, from_uhid)

        elif kind == "kick":
            kicked_uhid = str(data.get("kicked_uhid", ""))
            async with self._lock:
                session = self._sessions.get(call_id)
                if session is not None:
                    session.members.discard(kicked_uhid)
            if self.on_member_kicked:
                self.on_member_kicked(call_id, kicked_uhid)

        elif kind == "end":
            async with self._lock:
                session = self._sessions.pop(call_id, None)
                if session:
                    session.state = GroupCallState.Ended
            if self.on_call_ended:
                self.on_call_ended(call_id)

        elif kind == "key_rotation":
            key_gen = int(data.get("key_generation") or 0)
            async with self._lock:
                session = self._sessions.get(call_id)
                if session is not None:
                    session.key_generation = key_gen
            if self.on_key_rotation:
                self.on_key_rotation(call_id, key_gen)

    async def _handle_frame(self, packet: MeshPacket) -> None:
        result = _unpack_group_frame(packet.payload)
        if result is None:
            return
        call_id, sequence, timestamp_ms, is_silence, key_generation, audio = result
        if self.on_frame_received:
            self.on_frame_received(
                call_id,
                packet.source_uhid,
                sequence,
                timestamp_ms,
                is_silence,
                key_generation,
                audio,
            )


# ------------------------------------------------------------------
# Encoding helpers
# ------------------------------------------------------------------

def _encode_group_signaling(
    kind: str,
    call_id: uuid.UUID,
    from_uhid: str,
    to_uhid: str,
    invited_uhids: list[str] | None = None,
    kicked_uhid: str | None = None,
    key_generation: int | None = None,
) -> bytes:
    msg: dict = {
        "kind": kind,
        "call_id": str(call_id),
        "from_uhid": from_uhid,
        "to_uhid": to_uhid,
    }
    if invited_uhids is not None:
        msg["invited_uhids"] = invited_uhids
    if kicked_uhid is not None:
        msg["kicked_uhid"] = kicked_uhid
    if key_generation is not None:
        msg["key_generation"] = key_generation
    return json.dumps(msg).encode("utf-8")


def _try_uuid(value: object) -> uuid.UUID | None:
    if isinstance(value, uuid.UUID):
        return value
    if isinstance(value, str):
        try:
            return uuid.UUID(value)
        except ValueError:
            return None
    return None
