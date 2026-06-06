# SPDX-License-Identifier: MIT

"""StreamingService — live segment streaming over the Aether mesh."""

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

_SEGMENT_PRIORITY: int = 48
_CONTROL_PRIORITY: int = 32

# StreamSegment binary layout
# [16] StreamId UUID (big-endian)
# [4]  Sequence uint32 little-endian
# [8]  TimestampMs int64 little-endian
# [1]  IsKeyframe uint8
# [N]  EncodedPayload
_SEGMENT_HEADER_SIZE = 16 + 4 + 8 + 1  # 29 bytes


class StreamState(Enum):
    Live = "live"
    Ended = "ended"


@dataclass
class StreamSession:
    stream_id: uuid.UUID
    publisher_uhid: str
    title: str
    content_type: str
    codec: str
    segment_duration_ms: int
    state: StreamState = StreamState.Live
    sequence: int = 0
    started_at_ms: int = field(default_factory=lambda: int(time.time() * 1000))


def _pack_segment(
    stream_id: uuid.UUID,
    sequence: int,
    timestamp_ms: int,
    is_keyframe: bool,
    data: bytes,
) -> bytes:
    header = (
        stream_id.bytes
        + struct.pack("<I", sequence)
        + struct.pack("<q", timestamp_ms)
        + struct.pack("B", 1 if is_keyframe else 0)
    )
    return header + data


def _unpack_segment(payload: bytes) -> tuple[uuid.UUID, int, int, bool, bytes] | None:
    if len(payload) < _SEGMENT_HEADER_SIZE:
        return None
    try:
        stream_id = uuid.UUID(bytes=payload[:16])
        sequence = struct.unpack_from("<I", payload, 16)[0]
        timestamp_ms = struct.unpack_from("<q", payload, 20)[0]
        is_keyframe = bool(struct.unpack_from("B", payload, 28)[0])
        encoded = payload[_SEGMENT_HEADER_SIZE:]
        return stream_id, sequence, timestamp_ms, is_keyframe, encoded
    except (struct.error, ValueError):
        return None


class StreamingService:
    """Live segment streaming service.

    Publishers:
      - ``start_stream`` to begin a stream.
      - ``publish_segment`` to push encoded segments.
      - ``end_stream`` to terminate.

    Subscribers:
      - ``subscribe`` to start receiving segments from a publisher.
      - ``unsubscribe`` to stop.

    Events (assign callables):
      - ``on_stream_announced(stream_id, publisher_uhid, title, content_type, codec, segment_duration_ms, state, started_at_ms)``
      - ``on_stream_ended(stream_id)``
      - ``on_segment_received(stream_id, sequence, timestamp_ms, is_keyframe, data)``
    """

    def __init__(
        self,
        transport: MeshSender,
        local_uhid: str,
    ) -> None:
        self._transport = transport
        self._local_uhid = local_uhid
        # Streams published locally
        self._published: dict[uuid.UUID, StreamSession] = {}
        # Streams from remote publishers we have announced/seen
        self._known: dict[uuid.UUID, StreamSession] = {}
        # subscriber_uhids per stream: dict[stream_id, set[uhid]]
        self._subscribers: dict[uuid.UUID, set[str]] = {}
        self._lock = asyncio.Lock()

        self.on_stream_announced: Optional[
            Callable[[uuid.UUID, str, str, str, str, int, str, int], None]
        ] = None
        self.on_stream_ended: Optional[Callable[[uuid.UUID], None]] = None
        self.on_segment_received: Optional[
            Callable[[uuid.UUID, int, int, bool, bytes], None]
        ] = None

    # ------------------------------------------------------------------
    # Publisher API
    # ------------------------------------------------------------------

    async def start_stream(
        self,
        title: str,
        content_type: str,
        codec: str,
        segment_duration_ms: int,
    ) -> uuid.UUID:
        """Begin a new stream. Returns the stream_id."""
        stream_id = uuid.uuid4()
        started_at_ms = int(time.time() * 1000)
        session = StreamSession(
            stream_id=stream_id,
            publisher_uhid=self._local_uhid,
            title=title,
            content_type=content_type,
            codec=codec,
            segment_duration_ms=segment_duration_ms,
            state=StreamState.Live,
            started_at_ms=started_at_ms,
        )
        async with self._lock:
            self._published[stream_id] = session
            self._subscribers[stream_id] = set()

        announce_payload = _encode_announce(session)
        packet = MeshPacket(
            type=PacketType.StreamAnnounce,
            source_uhid=self._local_uhid,
            destination_uhid="",
            ttl=constants.DEFAULT_TTL,
            priority=_CONTROL_PRIORITY,
            payload=announce_payload,
        )
        await self._transport.broadcast(packet)
        return stream_id

    async def end_stream(self, stream_id: uuid.UUID) -> None:
        """Terminate the stream and notify subscribers."""
        async with self._lock:
            session = self._published.get(stream_id)
            subscriber_snapshot = set(self._subscribers.get(stream_id, set()))
        if session is None:
            return

        async with self._lock:
            session.state = StreamState.Ended

        announce_payload = _encode_announce(session)
        packet = MeshPacket(
            type=PacketType.StreamAnnounce,
            source_uhid=self._local_uhid,
            destination_uhid="",
            ttl=constants.DEFAULT_TTL,
            priority=_CONTROL_PRIORITY,
            payload=announce_payload,
        )
        await self._transport.broadcast(packet)

        async with self._lock:
            self._published.pop(stream_id, None)
            self._subscribers.pop(stream_id, None)

    async def publish_segment(
        self,
        stream_id: uuid.UUID,
        data: bytes,
        is_keyframe: bool,
    ) -> None:
        """Push an encoded segment to all current subscribers."""
        async with self._lock:
            session = self._published.get(stream_id)
            subscriber_snapshot = set(self._subscribers.get(stream_id, set()))
        if session is None or session.state != StreamState.Live:
            return

        async with self._lock:
            seq = session.sequence
            session.sequence += 1

        segment_bytes = _pack_segment(
            stream_id=stream_id,
            sequence=seq,
            timestamp_ms=int(time.time() * 1000),
            is_keyframe=is_keyframe,
            data=data,
        )
        for uhid in subscriber_snapshot:
            packet = MeshPacket(
                type=PacketType.StreamSegment,
                source_uhid=self._local_uhid,
                destination_uhid=uhid,
                ttl=constants.DEFAULT_TTL,
                priority=_SEGMENT_PRIORITY,
                payload=segment_bytes,
            )
            await self._transport.send(packet, uhid)

    # ------------------------------------------------------------------
    # Subscriber API
    # ------------------------------------------------------------------

    async def subscribe(
        self,
        stream_id: uuid.UUID,
        publisher_uhid: str,
        live_only: bool,
    ) -> None:
        """Subscribe to a stream from *publisher_uhid*."""
        async with self._lock:
            if stream_id not in self._subscribers:
                self._subscribers[stream_id] = set()
            self._subscribers[stream_id].add(self._local_uhid)

        payload = json.dumps({
            "stream_id": str(stream_id),
            "live_only": live_only,
        }).encode("utf-8")
        packet = MeshPacket(
            type=PacketType.StreamSubscribe,
            source_uhid=self._local_uhid,
            destination_uhid=publisher_uhid,
            ttl=constants.DEFAULT_TTL,
            priority=_CONTROL_PRIORITY,
            payload=payload,
        )
        await self._transport.send(packet, publisher_uhid)

    async def unsubscribe(
        self,
        stream_id: uuid.UUID,
        publisher_uhid: str,
    ) -> None:
        """Unsubscribe from a stream."""
        async with self._lock:
            subs = self._subscribers.get(stream_id)
            if subs is not None:
                subs.discard(self._local_uhid)

        payload = json.dumps({"stream_id": str(stream_id)}).encode("utf-8")
        packet = MeshPacket(
            type=PacketType.StreamUnsubscribe,
            source_uhid=self._local_uhid,
            destination_uhid=publisher_uhid,
            ttl=constants.DEFAULT_TTL,
            priority=_CONTROL_PRIORITY,
            payload=payload,
        )
        await self._transport.send(packet, publisher_uhid)

    # ------------------------------------------------------------------
    # Packet handler
    # ------------------------------------------------------------------

    async def handle_packet(self, packet: MeshPacket) -> None:
        if packet.type == PacketType.StreamAnnounce:
            await self._handle_announce(packet)
        elif packet.type == PacketType.StreamSubscribe:
            await self._handle_subscribe(packet)
        elif packet.type == PacketType.StreamUnsubscribe:
            await self._handle_unsubscribe(packet)
        elif packet.type == PacketType.StreamSegment:
            await self._handle_segment(packet)

    # ------------------------------------------------------------------
    # Internal handlers
    # ------------------------------------------------------------------

    async def _handle_announce(self, packet: MeshPacket) -> None:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return
        stream_id = _try_uuid(data.get("stream_id"))
        if stream_id is None:
            return

        state_str = str(data.get("state", "live"))
        started_at_ms = int(data.get("started_at_ms") or 0)
        title = str(data.get("title", ""))
        content_type = str(data.get("content_type", ""))
        codec = str(data.get("codec", ""))
        segment_duration_ms = int(data.get("segment_duration_ms") or 0)

        session = StreamSession(
            stream_id=stream_id,
            publisher_uhid=packet.source_uhid,
            title=title,
            content_type=content_type,
            codec=codec,
            segment_duration_ms=segment_duration_ms,
            state=StreamState.Live if state_str == "live" else StreamState.Ended,
            started_at_ms=started_at_ms,
        )
        async with self._lock:
            self._known[stream_id] = session

        if state_str == "ended" and self.on_stream_ended:
            self.on_stream_ended(stream_id)
        elif state_str == "live" and self.on_stream_announced:
            self.on_stream_announced(
                stream_id,
                packet.source_uhid,
                title,
                content_type,
                codec,
                segment_duration_ms,
                state_str,
                started_at_ms,
            )

    async def _handle_subscribe(self, packet: MeshPacket) -> None:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return
        stream_id = _try_uuid(data.get("stream_id"))
        if stream_id is None:
            return
        async with self._lock:
            if stream_id in self._published:
                if stream_id not in self._subscribers:
                    self._subscribers[stream_id] = set()
                self._subscribers[stream_id].add(packet.source_uhid)

    async def _handle_unsubscribe(self, packet: MeshPacket) -> None:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return
        stream_id = _try_uuid(data.get("stream_id"))
        if stream_id is None:
            return
        async with self._lock:
            subs = self._subscribers.get(stream_id)
            if subs is not None:
                subs.discard(packet.source_uhid)

    async def _handle_segment(self, packet: MeshPacket) -> None:
        result = _unpack_segment(packet.payload)
        if result is None:
            return
        stream_id, sequence, timestamp_ms, is_keyframe, data = result
        if self.on_segment_received:
            self.on_segment_received(stream_id, sequence, timestamp_ms, is_keyframe, data)


# ------------------------------------------------------------------
# Encoding helpers
# ------------------------------------------------------------------

def _encode_announce(session: StreamSession) -> bytes:
    payload = {
        "stream_id": str(session.stream_id),
        "title": session.title,
        "content_type": session.content_type,
        "codec": session.codec,
        "segment_duration_ms": session.segment_duration_ms,
        "state": session.state.value,
        "started_at_ms": session.started_at_ms,
    }
    return json.dumps(payload).encode("utf-8")


def _try_uuid(value: object) -> uuid.UUID | None:
    if isinstance(value, uuid.UUID):
        return value
    if isinstance(value, str):
        try:
            return uuid.UUID(value)
        except ValueError:
            return None
    return None
