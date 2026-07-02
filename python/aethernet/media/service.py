# SPDX-License-Identifier: MIT

"""Binary media frames — VoicePtt(15) push-to-talk audio + ScreenShare(32) screen video.

Both frames share the exact 29-byte header used by the existing VoiceCall(16)/VideoFrame(31)
frames, so a node can treat them uniformly::

    [0..15]  call_id       — 16 bytes, RFC-4122 BIG-ENDIAN (uuid.UUID(...).bytes)
    [16..19] sequence      — u32 LITTLE-ENDIAN  (struct "<I")
    [20..27] timestamp_ms  — i64 LITTLE-ENDIAN  (struct "<q")
    [28]     flag          — u8 (VoicePtt: is_silence; ScreenShare: is_keyframe)
    [29..]   payload       — opaque encoded audio/video bytes

Byte-identity gate: fixtures/media/vectors.json (expected_hex). The call_id is big-endian
(network order), which is exactly what ``uuid.UUID(...).bytes`` yields — NOT the .NET
mixed-endian layout and NOT ``.bytes_le``. Mirrors the C# ``AetherNet.Media.MediaFrameCodec``
/ ``VoicePttService`` / ``ScreenShareService``.
"""

from __future__ import annotations

import logging
import struct
from dataclasses import dataclass, field
from typing import Callable, Optional
from uuid import UUID

from aethernet import constants
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)

#: Shared header length for both media frames (16 + 4 + 8 + 1).
_HEADER_LENGTH = 16 + 4 + 8 + 1  # 29 bytes


@dataclass
class VoicePttFrame:
    """A push-to-talk audio frame (PacketType.VoicePtt = 15 body)."""

    call_id: UUID = UUID(int=0)
    sequence: int = 0
    timestamp_ms: int = 0
    is_silence: bool = False
    encoded_payload: bytes = field(default_factory=bytes)


@dataclass
class ScreenShareFrame:
    """A screen-share video frame (PacketType.ScreenShare = 32 body)."""

    call_id: UUID = UUID(int=0)
    sequence: int = 0
    timestamp_ms: int = 0
    is_keyframe: bool = False
    encoded_payload: bytes = field(default_factory=bytes)


class MediaFrameCodec:
    """Binary codec for the VoicePtt(15) and ScreenShare(32) media frames.

    Both frames share the exact 29-byte header (call_id big-endian, sequence/timestamp
    little-endian, flag byte), so a node can treat them uniformly. Serialization is
    byte-identical to the C# reference (fixtures/media/vectors.json).
    """

    @staticmethod
    def serialize_voice_ptt(frame: VoicePttFrame) -> bytes:
        return MediaFrameCodec._serialize(
            frame.call_id,
            frame.sequence,
            frame.timestamp_ms,
            frame.is_silence,
            frame.encoded_payload,
        )

    @staticmethod
    def serialize_screen_share(frame: ScreenShareFrame) -> bytes:
        return MediaFrameCodec._serialize(
            frame.call_id,
            frame.sequence,
            frame.timestamp_ms,
            frame.is_keyframe,
            frame.encoded_payload,
        )

    @staticmethod
    def _serialize(
        call_id: UUID,
        sequence: int,
        timestamp_ms: int,
        flag: bool,
        payload: bytes,
    ) -> bytes:
        payload = payload or b""
        header = (
            call_id.bytes  # RFC-4122 big-endian / network order (NOT .bytes_le)
            + struct.pack("<I", sequence)
            + struct.pack("<q", timestamp_ms)
            + struct.pack("B", 1 if flag else 0)
        )
        return header + bytes(payload)

    @staticmethod
    def deserialize_voice_ptt(data: bytes) -> VoicePttFrame:
        if len(data) < _HEADER_LENGTH:
            raise ValueError("VoicePtt frame too short")
        call_id, sequence, timestamp_ms, flag, encoded = MediaFrameCodec._deserialize(data)
        return VoicePttFrame(
            call_id=call_id,
            sequence=sequence,
            timestamp_ms=timestamp_ms,
            is_silence=flag,
            encoded_payload=encoded,
        )

    @staticmethod
    def deserialize_screen_share(data: bytes) -> ScreenShareFrame:
        if len(data) < _HEADER_LENGTH:
            raise ValueError("ScreenShare frame too short")
        call_id, sequence, timestamp_ms, flag, encoded = MediaFrameCodec._deserialize(data)
        return ScreenShareFrame(
            call_id=call_id,
            sequence=sequence,
            timestamp_ms=timestamp_ms,
            is_keyframe=flag,
            encoded_payload=encoded,
        )

    @staticmethod
    def _deserialize(data: bytes) -> tuple[UUID, int, int, bool, bytes]:
        call_id = UUID(bytes=bytes(data[:16]))
        sequence = struct.unpack_from("<I", data, 16)[0]
        timestamp_ms = struct.unpack_from("<q", data, 20)[0]
        flag = data[28] != 0
        encoded = bytes(data[_HEADER_LENGTH:])
        return call_id, sequence, timestamp_ms, flag, encoded


@dataclass
class VoicePttFrameReceived:
    """An inbound VoicePtt frame plus the peer that sent it."""

    frame: VoicePttFrame = field(default_factory=VoicePttFrame)
    from_uhid: str = ""


@dataclass
class ScreenShareFrameReceived:
    """An inbound ScreenShare frame plus the peer that sent it."""

    frame: ScreenShareFrame = field(default_factory=ScreenShareFrame)
    from_uhid: str = ""


class VoicePttService:
    """Binds :class:`PacketType.VoicePtt` (15) to the mesh: directed push-to-talk audio
    frames + inbound event. Mirrors the C# ``AetherNet.Media.VoicePttService``.
    """

    def __init__(self, sender: MeshSender) -> None:
        self._sender = sender
        #: Raised when a VoicePtt frame is received from a peer.
        self.on_frame_received: Optional[Callable[[VoicePttFrameReceived], None]] = None

    async def send_frame(self, peer_uhid: str, frame: VoicePttFrame) -> bool:
        """Directed-send a push-to-talk audio ``frame`` to ``peer_uhid``. Returns delivery success."""
        if not peer_uhid:
            raise ValueError("peer_uhid must not be empty")
        packet = MeshPacket(
            type=PacketType.VoicePtt,
            source_uhid=self._sender.local_uhid,
            destination_uhid=peer_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=MediaFrameCodec.serialize_voice_ptt(frame),
        )
        return await self._sender.send(packet, peer_uhid)

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an incoming :class:`PacketType.VoicePtt` packet.

        Decodes the frame and raises ``on_frame_received`` with the frame and the packet's
        source UHID. Returns ``False`` for the wrong packet type or a too-short/malformed frame.
        """
        if packet.type != PacketType.VoicePtt:
            return False
        try:
            frame = MediaFrameCodec.deserialize_voice_ptt(packet.payload)
        except (ValueError, struct.error):
            _LOG.debug("VoicePtt from %s: malformed — dropped", packet.source_uhid)
            return False
        if self.on_frame_received:
            self.on_frame_received(
                VoicePttFrameReceived(frame=frame, from_uhid=packet.source_uhid)
            )
        return True


class ScreenShareService:
    """Binds :class:`PacketType.ScreenShare` (32) to the mesh: directed screen-share video
    frames + inbound event. Mirrors the C# ``AetherNet.Media.ScreenShareService``.
    """

    def __init__(self, sender: MeshSender) -> None:
        self._sender = sender
        #: Raised when a ScreenShare frame is received from a peer.
        self.on_frame_received: Optional[Callable[[ScreenShareFrameReceived], None]] = None

    async def send_frame(self, peer_uhid: str, frame: ScreenShareFrame) -> bool:
        """Directed-send a screen-share video ``frame`` to ``peer_uhid``. Returns delivery success."""
        if not peer_uhid:
            raise ValueError("peer_uhid must not be empty")
        packet = MeshPacket(
            type=PacketType.ScreenShare,
            source_uhid=self._sender.local_uhid,
            destination_uhid=peer_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=MediaFrameCodec.serialize_screen_share(frame),
        )
        return await self._sender.send(packet, peer_uhid)

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an incoming :class:`PacketType.ScreenShare` packet.

        Decodes the frame and raises ``on_frame_received`` with the frame and the packet's
        source UHID. Returns ``False`` for the wrong packet type or a too-short/malformed frame.
        """
        if packet.type != PacketType.ScreenShare:
            return False
        try:
            frame = MediaFrameCodec.deserialize_screen_share(packet.payload)
        except (ValueError, struct.error):
            _LOG.debug("ScreenShare from %s: malformed — dropped", packet.source_uhid)
            return False
        if self.on_frame_received:
            self.on_frame_received(
                ScreenShareFrameReceived(frame=frame, from_uhid=packet.source_uhid)
            )
        return True
