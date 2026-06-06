# SPDX-License-Identifier: MIT

"""VideoCallService — point-to-point video calls over the Aether mesh."""

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

# VideoFrame binary layout
# [16] CallId UUID (big-endian)
# [4]  Sequence uint32 little-endian
# [8]  TimestampMs int64 little-endian
# [1]  IsKeyframe uint8
# [N]  EncodedPayload
_VIDEO_FRAME_HEADER_SIZE = 16 + 4 + 8 + 1  # 29 bytes


class VideoCallState(Enum):
    Outgoing = "outgoing"
    Incoming = "incoming"
    Connected = "connected"
    Ended = "ended"
    Failed = "failed"


@dataclass
class VideoCallSession:
    call_id: uuid.UUID
    local_uhid: str
    remote_uhid: str
    state: VideoCallState
    selected_codec: Optional[str] = None
    width: int = 0
    height: int = 0
    fps: int = 0
    bitrate_kbps: int = 0
    sequence: int = 0
    started_at_ms: int = field(default_factory=lambda: int(time.time() * 1000))


def _pack_video_frame(
    call_id: uuid.UUID,
    sequence: int,
    timestamp_ms: int,
    is_keyframe: bool,
    encoded_video: bytes,
) -> bytes:
    header = (
        call_id.bytes
        + struct.pack("<I", sequence)
        + struct.pack("<q", timestamp_ms)
        + struct.pack("B", 1 if is_keyframe else 0)
    )
    return header + encoded_video


def _unpack_video_frame(payload: bytes) -> tuple[uuid.UUID, int, int, bool, bytes] | None:
    if len(payload) < _VIDEO_FRAME_HEADER_SIZE:
        return None
    try:
        call_id = uuid.UUID(bytes=payload[:16])
        sequence = struct.unpack_from("<I", payload, 16)[0]
        timestamp_ms = struct.unpack_from("<q", payload, 20)[0]
        is_keyframe = bool(struct.unpack_from("B", payload, 28)[0])
        encoded = payload[_VIDEO_FRAME_HEADER_SIZE:]
        return call_id, sequence, timestamp_ms, is_keyframe, encoded
    except (struct.error, ValueError):
        return None


class VideoCallService:
    """Point-to-point video call service.

    Callers:
      - ``send_offer`` to initiate a video call.
      - ``accept_call`` to answer.
      - ``hang_up`` to terminate.
      - ``send_frame`` to push encoded video frames.
      - ``request_keyframe`` to ask the remote to send an IDR frame.
      - ``notify_quality_change`` to signal a resolution/bitrate change.
      - ``handle_packet`` must be invoked for PacketType.VideoSignaling and VideoFrame.

    Events (assign callables):
      - ``on_incoming_call(call_id, from_uhid, codecs, width, height, fps, bitrate_kbps)``
      - ``on_call_accepted(call_id, selected_codec)``
      - ``on_call_ended(call_id, reason)``
      - ``on_keyframe_requested(call_id)``
      - ``on_quality_changed(call_id, width, height, fps, bitrate_kbps)``
      - ``on_frame_received(call_id, sequence, timestamp_ms, is_keyframe, video)``
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
        self._sessions: dict[uuid.UUID, VideoCallSession] = {}
        self._lock = asyncio.Lock()

        self.on_incoming_call: Optional[
            Callable[[uuid.UUID, str, list[str], int, int, int, int], None]
        ] = None
        self.on_call_accepted: Optional[Callable[[uuid.UUID, str], None]] = None
        self.on_call_ended: Optional[Callable[[uuid.UUID, str], None]] = None
        self.on_keyframe_requested: Optional[Callable[[uuid.UUID], None]] = None
        self.on_quality_changed: Optional[Callable[[uuid.UUID, int, int, int, int], None]] = None
        self.on_frame_received: Optional[
            Callable[[uuid.UUID, int, int, bool, bytes], None]
        ] = None

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    async def send_offer(
        self,
        to_uhid: str,
        codecs: list[str],
        width: int,
        height: int,
        fps: int,
        bitrate_kbps: int,
    ) -> uuid.UUID:
        """Initiate a video call. Returns the new call_id."""
        if not to_uhid:
            raise ValueError("to_uhid must not be empty")

        call_id = uuid.uuid4()
        session = VideoCallSession(
            call_id=call_id,
            local_uhid=self._local_uhid,
            remote_uhid=to_uhid,
            state=VideoCallState.Outgoing,
            width=width,
            height=height,
            fps=fps,
            bitrate_kbps=bitrate_kbps,
        )
        async with self._lock:
            self._sessions[call_id] = session

        payload = _encode_video_signaling(
            kind="offer",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid=to_uhid,
            proposed_codecs=codecs,
            width=width,
            height=height,
            fps=fps,
            bitrate_kbps=bitrate_kbps,
        )
        await self._send_signaling(to_uhid, payload)
        return call_id

    async def accept_call(self, call_id: uuid.UUID) -> None:
        """Answer an incoming video call."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None or session.state != VideoCallState.Incoming:
            return

        payload = _encode_video_signaling(
            kind="answer",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid=session.remote_uhid,
            selected_codec=session.selected_codec,
            width=session.width,
            height=session.height,
            fps=session.fps,
            bitrate_kbps=session.bitrate_kbps,
        )
        async with self._lock:
            session.state = VideoCallState.Connected
        await self._send_signaling(session.remote_uhid, payload)

    async def hang_up(self, call_id: uuid.UUID) -> None:
        """Terminate or cancel a video call."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None:
            return

        kind = "cancel" if session.state == VideoCallState.Outgoing else "hangup"
        payload = _encode_video_signaling(
            kind=kind,
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid=session.remote_uhid,
        )
        async with self._lock:
            session.state = VideoCallState.Ended
        await self._send_signaling(session.remote_uhid, payload)

    async def send_frame(
        self,
        call_id: uuid.UUID,
        encoded_video: bytes,
        is_keyframe: bool,
    ) -> None:
        """Send an encoded video frame."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None or session.state != VideoCallState.Connected:
            return

        async with self._lock:
            seq = session.sequence
            session.sequence += 1

        frame_bytes = _pack_video_frame(
            call_id=call_id,
            sequence=seq,
            timestamp_ms=int(time.time() * 1000),
            is_keyframe=is_keyframe,
            encoded_video=encoded_video,
        )
        packet = MeshPacket(
            type=PacketType.VideoFrame,
            source_uhid=self._local_uhid,
            destination_uhid=session.remote_uhid,
            ttl=constants.DEFAULT_TTL,
            priority=_FRAME_PRIORITY,
            payload=frame_bytes,
        )
        await self._transport.send(packet, session.remote_uhid)

    async def request_keyframe(self, call_id: uuid.UUID) -> None:
        """Ask the remote peer to send an IDR/keyframe."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None or session.state != VideoCallState.Connected:
            return

        payload = _encode_video_signaling(
            kind="keyframe_request",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid=session.remote_uhid,
        )
        await self._send_signaling(session.remote_uhid, payload)

    async def notify_quality_change(
        self,
        call_id: uuid.UUID,
        width: int,
        height: int,
        fps: int,
        bitrate_kbps: int,
    ) -> None:
        """Notify the remote peer of a resolution/bitrate change."""
        async with self._lock:
            session = self._sessions.get(call_id)
        if session is None or session.state != VideoCallState.Connected:
            return

        async with self._lock:
            session.width = width
            session.height = height
            session.fps = fps
            session.bitrate_kbps = bitrate_kbps

        payload = _encode_video_signaling(
            kind="quality_change",
            call_id=call_id,
            from_uhid=self._local_uhid,
            to_uhid=session.remote_uhid,
            width=width,
            height=height,
            fps=fps,
            bitrate_kbps=bitrate_kbps,
        )
        await self._send_signaling(session.remote_uhid, payload)

    async def handle_packet(self, packet: MeshPacket) -> None:
        """Route incoming VideoSignaling / VideoFrame packets."""
        if packet.type == PacketType.VideoSignaling:
            await self._handle_signaling(packet)
        elif packet.type == PacketType.VideoFrame:
            await self._handle_frame(packet)

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    async def _send_signaling(self, to_uhid: str, payload: bytes) -> None:
        packet = MeshPacket(
            type=PacketType.VideoSignaling,
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
            codecs: list[str] = list(data.get("proposed_codecs") or [])
            width = int(data.get("width") or 0)
            height = int(data.get("height") or 0)
            fps = int(data.get("fps") or 0)
            bitrate_kbps = int(data.get("bitrate_kbps") or 0)
            session = VideoCallSession(
                call_id=call_id,
                local_uhid=self._local_uhid,
                remote_uhid=from_uhid,
                state=VideoCallState.Incoming,
                selected_codec=codecs[0] if codecs else None,
                width=width,
                height=height,
                fps=fps,
                bitrate_kbps=bitrate_kbps,
            )
            async with self._lock:
                self._sessions[call_id] = session
            if self.on_incoming_call:
                self.on_incoming_call(call_id, from_uhid, codecs, width, height, fps, bitrate_kbps)

        elif kind == "answer":
            async with self._lock:
                session = self._sessions.get(call_id)
            if session is None:
                return
            selected_codec = str(data.get("selected_codec") or "")
            async with self._lock:
                session.state = VideoCallState.Connected
                session.selected_codec = selected_codec or session.selected_codec
            if self.on_call_accepted:
                self.on_call_accepted(call_id, session.selected_codec or "")

        elif kind in ("hangup", "cancel", "timeout"):
            reason = kind
            async with self._lock:
                session = self._sessions.get(call_id)
                if session:
                    session.state = VideoCallState.Ended
            if self.on_call_ended:
                self.on_call_ended(call_id, reason)

        elif kind == "keyframe_request":
            if self.on_keyframe_requested:
                self.on_keyframe_requested(call_id)

        elif kind == "quality_change":
            width = int(data.get("width") or 0)
            height = int(data.get("height") or 0)
            fps = int(data.get("fps") or 0)
            bitrate_kbps = int(data.get("bitrate_kbps") or 0)
            async with self._lock:
                session = self._sessions.get(call_id)
                if session:
                    session.width = width
                    session.height = height
                    session.fps = fps
                    session.bitrate_kbps = bitrate_kbps
            if self.on_quality_changed:
                self.on_quality_changed(call_id, width, height, fps, bitrate_kbps)

    async def _handle_frame(self, packet: MeshPacket) -> None:
        result = _unpack_video_frame(packet.payload)
        if result is None:
            return
        call_id, sequence, timestamp_ms, is_keyframe, video = result
        if self.on_frame_received:
            self.on_frame_received(call_id, sequence, timestamp_ms, is_keyframe, video)


# ------------------------------------------------------------------
# Encoding helpers
# ------------------------------------------------------------------

def _encode_video_signaling(
    kind: str,
    call_id: uuid.UUID,
    from_uhid: str,
    to_uhid: str,
    proposed_codecs: list[str] | None = None,
    selected_codec: str | None = None,
    width: int | None = None,
    height: int | None = None,
    fps: int | None = None,
    bitrate_kbps: int | None = None,
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
    if width is not None:
        msg["width"] = width
    if height is not None:
        msg["height"] = height
    if fps is not None:
        msg["fps"] = fps
    if bitrate_kbps is not None:
        msg["bitrate_kbps"] = bitrate_kbps
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
