# SPDX-License-Identifier: MIT

"""VoiceCallService — point-to-point voice calls over the Aether mesh."""

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

from aethernet import constants
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)

# Priority constants (mirrors C# reference)
_FRAME_PRIORITY: int = 64
_SIGNALING_PRIORITY: int = 32

# VoiceFrame binary layout
# [16] CallId UUID (big-endian)
# [4]  Sequence uint32 little-endian
# [8]  TimestampMs int64 little-endian
# [1]  IsSilence uint8
# [N]  EncodedPayload
_FRAME_HEADER_SIZE = 16 + 4 + 8 + 1  # 29 bytes


class VoiceCallState(Enum):
    Outgoing = "outgoing"
    Incoming = "incoming"
    Connected = "connected"
    Ended = "ended"
    Failed = "failed"


@dataclass
class VoiceCallSession:
    call_id: uuid.UUID
    local_uhid: str
    remote_uhid: str
    state: VoiceCallState
    selected_codec: Optional[str] = None
    sample_rate_hz: int = 16000
    sequence: int = 0
    started_at_ms: int = field(default_factory=lambda: int(time.time() * 1000))


def _pack_voice_frame(
    call_id: uuid.UUID,
    sequence: int,
    timestamp_ms: int,
    is_silence: bool,
    encoded_audio: bytes,
) -> bytes:
    header = (
        call_id.bytes
        + struct.pack("<I", sequence)
        + struct.pack("<q", timestamp_ms)
        + struct.pack("B", 1 if is_silence else 0)
    )
    return header + encoded_audio


def _unpack_voice_frame(payload: bytes) -> tuple[uuid.UUID, int, int, bool, bytes] | None:
    if len(payload) < _FRAME_HEADER_SIZE:
        return None
    try:
        call_id = uuid.UUID(bytes=payload[:16])
        sequence = struct.unpack_from("<I", payload, 16)[0]
        timestamp_ms = struct.unpack_from("<q", payload, 20)[0]
        is_silence = bool(struct.unpack_from("B", payload, 28)[0])
        encoded = payload[_FRAME_HEADER_SIZE:]
        return call_id, sequence, timestamp_ms, is_silence, encoded
    except (struct.error, ValueError):
        return None


class VoiceCallService:
    """Point-to-point voice call service.

    Callers:
      - ``send_offer`` to initiate a call.
      - ``accept_call`` to answer an incoming call.
      - ``hang_up`` to terminate.
      - ``send_frame`` to push encoded audio frames during a connected call.
      - ``handle_packet`` must be invoked by the host for PacketType.VoiceSignaling
        and PacketType.VoiceCall packets.

    Events via callback dict:
      - ``on_incoming_call(call_id, from_uhid, codecs, sample_rate_hz)``
      - ``on_call_accepted(call_id, selected_codec)``
      - ``on_call_ended(call_id, reason)``
      - ``on_frame_received(call_id, sequence, timestamp_ms, is_silence, audio)``
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
        self._sessions: dict[uuid.UUID, VoiceCallSession] = {}
        self._lock = asyncio.Lock()

        # Callback hooks — callers assign callables to these keys.
        self.on_incoming_call: Optional[Callable[[uuid.UUID, str, list[str], int], None]] = None
        self.on_call_accepted: Optional[Callable[[uuid.UUID, str], None]] = None
        self.on_call_ended: Optional[Callable[[uuid.UUID, str], None]] = None
        self.on_frame_received: Optional[Callable[[uuid.UUID, int, int, bool, bytes], None]] = None

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    async def send_offer(
        self,
        to_uhid: str,
        codecs: list[str],
        sample_rate_hz: int,
    ) -> uuid.UUID:
        """Initiate a call to *to_uhid*. Returns the new call_id."""
        if not to_uhid:
            raise ValueError("to_uhid must not be empty")

        call_id = uuid.uuid4()
        session = VoiceCallSession(
            call_id=call_id,
            local_uhid=self._local_uhid,
            remote_uhid=to_uhid,
            state=VoiceCallState.Outgoing,
            sample_rate_hz=sample_rate_hz,
        )
        async with self._lock:
            self._sessions[call_id] = session

        payload = _encode_signaling(
            kind="offer",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid=to_uhid,
            proposed_codecs=codecs,
            sample_rate_hz=sample_rate_hz,
        )
        await self._send_signaling(to_uhid, payload)
        return call_id

    async def accept_call(self, call_id: uuid.UUID) -> None:
        """Answer an incoming call identified by *call_id*."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None or session.state != VoiceCallState.Incoming:
            return

        payload = _encode_signaling(
            kind="answer",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid=session.remote_uhid,
            selected_codec=session.selected_codec,
            sample_rate_hz=session.sample_rate_hz,
        )
        async with self._lock:
            session.state = VoiceCallState.Connected
        await self._send_signaling(session.remote_uhid, payload)

    async def hang_up(self, call_id: uuid.UUID) -> None:
        """Terminate or cancel a call."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None:
            return

        kind = "cancel" if session.state == VoiceCallState.Outgoing else "hangup"
        payload = _encode_signaling(
            kind=kind,
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid=session.remote_uhid,
        )
        async with self._lock:
            session.state = VoiceCallState.Ended
        await self._send_signaling(session.remote_uhid, payload)

    async def send_frame(
        self,
        call_id: uuid.UUID,
        encoded_audio: bytes,
        is_silence: bool,
    ) -> None:
        """Send an encoded audio frame for an active call."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None or session.state != VoiceCallState.Connected:
            return

        async with self._lock:
            seq = session.sequence
            session.sequence += 1

        frame_bytes = _pack_voice_frame(
            call_id=call_id,
            sequence=seq,
            timestamp_ms=int(time.time() * 1000),
            is_silence=is_silence,
            encoded_audio=encoded_audio,
        )
        packet = MeshPacket(
            type=PacketType.VoiceCall,
            source_uhid=self._local_uhid,
            destination_uhid=session.remote_uhid,
            ttl=constants.DEFAULT_TTL,
            priority=_FRAME_PRIORITY,
            payload=frame_bytes,
        )
        await self._transport.send(packet, session.remote_uhid)

    async def handle_packet(self, packet: MeshPacket) -> None:
        """Route incoming VoiceSignaling / VoiceCall packets to the right handler."""
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

        if kind == "offer":
            await self._on_offer(call_id, from_uhid, data)
        elif kind == "answer":
            await self._on_answer(call_id, data)
        elif kind in ("hangup", "cancel", "timeout"):
            await self._on_ended(call_id, kind)

    async def _on_offer(self, call_id: uuid.UUID, from_uhid: str, data: dict) -> None:
        codecs: list[str] = list(data.get("proposed_codecs") or [])
        sample_rate_hz: int = int(data.get("sample_rate_hz") or 16000)
        session = VoiceCallSession(
            call_id=call_id,
            local_uhid=self._local_uhid,
            remote_uhid=from_uhid,
            state=VoiceCallState.Incoming,
            selected_codec=codecs[0] if codecs else None,
            sample_rate_hz=sample_rate_hz,
        )
        async with self._lock:
            self._sessions[call_id] = session
        if self.on_incoming_call:
            self.on_incoming_call(call_id, from_uhid, codecs, sample_rate_hz)

    async def _on_answer(self, call_id: uuid.UUID, data: dict) -> None:
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None:
            return
        selected_codec = str(data.get("selected_codec") or "")
        async with self._lock:
            session.state = VoiceCallState.Connected
            session.selected_codec = selected_codec or session.selected_codec
        if self.on_call_accepted:
            self.on_call_accepted(call_id, session.selected_codec or "")

    async def _on_ended(self, call_id: uuid.UUID, reason: str) -> None:
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None:
            return
        async with self._lock:
            session.state = VoiceCallState.Ended
        if self.on_call_ended:
            self.on_call_ended(call_id, reason)

    async def _handle_frame(self, packet: MeshPacket) -> None:
        result = _unpack_voice_frame(packet.payload)
        if result is None:
            return
        call_id, sequence, timestamp_ms, is_silence, audio = result
        if self.on_frame_received:
            self.on_frame_received(call_id, sequence, timestamp_ms, is_silence, audio)


# ------------------------------------------------------------------
# Encoding helpers
# ------------------------------------------------------------------

def _encode_signaling(
    kind: str,
    call_id: uuid.UUID,
    from_uhid: str,
    to_uhid: str,
    proposed_codecs: list[str] | None = None,
    selected_codec: str | None = None,
    sample_rate_hz: int | None = None,
    reason: str | None = None,
) -> bytes:
    msg: dict = {
        "kind": kind,
        "call_id": str(call_id),
        "from_uhid": from_uhid,
        "to_uhid": to_uhid,
    }
    if proposed_codecs is not None:
        msg["proposed_codecs"] = proposed_codecs
    if selected_codec is not None:
        msg["selected_codec"] = selected_codec
    if sample_rate_hz is not None:
        msg["sample_rate_hz"] = sample_rate_hz
    if reason is not None:
        msg["reason"] = reason
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
