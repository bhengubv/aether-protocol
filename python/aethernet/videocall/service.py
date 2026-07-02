# SPDX-License-Identifier: MIT

"""Default video call-control service (PacketType.VideoCall).

Video call-control is the caller-intent layer over :class:`PacketType.VideoCall` —
directed ring/accept/decline/hangup signalling between two peers. The caller rings a
peer (minting a call id and directed-sending a "ring"); either side then accepts,
declines, or hangs up by directed-sending the matching action for that call id. Inbound
signals surface via ``on_call_state_changed``.

This is distinct from the media plane (SDP/ICE negotiation via ``VideoSignaling`` and
media via ``VideoFrame``, handled by the streaming video service); it mirrors how voice
call-control carries the caller-intent verbs. Mirrors the C#
``AetherNet.VideoCallControl.VideoCallControlService``.
"""

from __future__ import annotations

import json
import logging
import time
from dataclasses import dataclass
from typing import Callable, Optional
from uuid import UUID, uuid4

from aethernet import constants
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)


@dataclass
class VideoCallControlPayload:
    """JSON payload for :class:`PacketType.VideoCall` call-control packets.

    Wire format: UTF-8 JSON with snake_case keys, field order ``call_id``, ``action``,
    ``sent_at_ms``, no whitespace, lowercase-dashed UUID, ``sent_at_ms`` a bare integer,
    ``action`` an ASCII verb. Byte-identity is locked by fixtures/videocall/vectors.json.
    """

    #: Unique id for this call (minted by the caller on ring; echoed by accept/decline/hangup).
    call_id: UUID = UUID(int=0)
    #: Control verb: "ring", "accept", "decline", or "hangup".
    action: str = ""
    #: Unix timestamp in milliseconds when the control signal was sent.
    sent_at_ms: int = 0


@dataclass
class VideoCallStateChanged:
    """Raised when a video call-control signal arrives from a peer."""

    #: Id of the call the signal refers to.
    call_id: UUID = UUID(int=0)
    #: The control verb received ("ring" / "accept" / "decline" / "hangup").
    action: str = ""
    #: UHID of the peer that sent the signal.
    from_uhid: str = ""


class VideoCallControlService:
    """Video call-control over :class:`PacketType.VideoCall` — directed ring/accept/
    decline/hangup signalling between two peers.

    The caller rings a peer (minting a call id); either side then accepts, declines, or
    hangs up. Inbound signals surface via ``on_call_state_changed``. The media plane
    (SDP/ICE + frames) is handled separately by the streaming video service.
    """

    def __init__(self, sender: MeshSender) -> None:
        self._sender = sender
        # Raised when a call-control signal is received from a peer.
        self.on_call_state_changed: Optional[Callable[[VideoCallStateChanged], None]] = None

    async def ring(self, peer_uhid: str) -> UUID:
        """Ring ``peer_uhid``: mint a call id and directed-send a "ring". Returns the new call id."""
        if not peer_uhid:
            raise ValueError("peer_uhid must not be empty")
        call_id = uuid4()
        await self._send_control(call_id, peer_uhid, "ring")
        return call_id

    async def accept(self, call_id: UUID, peer_uhid: str) -> bool:
        """Directed-send an "accept" for ``call_id`` to ``peer_uhid``. Returns delivery success."""
        return await self._send_control(call_id, peer_uhid, "accept")

    async def decline(self, call_id: UUID, peer_uhid: str) -> bool:
        """Directed-send a "decline" for ``call_id`` to ``peer_uhid``. Returns delivery success."""
        return await self._send_control(call_id, peer_uhid, "decline")

    async def hangup(self, call_id: UUID, peer_uhid: str) -> bool:
        """Directed-send a "hangup" for ``call_id`` to ``peer_uhid``. Returns delivery success."""
        return await self._send_control(call_id, peer_uhid, "hangup")

    async def _send_control(self, call_id: UUID, peer_uhid: str, action: str) -> bool:
        if not peer_uhid:
            raise ValueError("peer_uhid must not be empty")

        body = _encode_video_call_control_payload(call_id, action, _now_ms())

        packet = MeshPacket(
            type=PacketType.VideoCall,
            source_uhid=self._sender.local_uhid,
            destination_uhid=peer_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )

        delivered = await self._sender.send(packet, peer_uhid)
        _LOG.debug(
            "VideoCall %s call=%s -> %s delivered=%s", action, call_id, peer_uhid, delivered
        )
        return delivered

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an incoming :class:`PacketType.VideoCall` packet.

        Parses the payload and raises ``on_call_state_changed`` with the call id, action,
        and the packet's source UHID. Returns ``False`` for the wrong packet type or a
        malformed payload (missing/empty action).
        """
        if packet.type != PacketType.VideoCall:
            return False

        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            _LOG.debug(
                "VideoCall from %s: malformed payload — dropped", packet.source_uhid
            )
            return False
        if not isinstance(data, dict):
            return False

        action = data.get("action")
        if not action or not isinstance(action, str):
            return False

        call_id = _try_uuid(data.get("call_id")) or UUID(int=0)

        if self.on_call_state_changed:
            self.on_call_state_changed(
                VideoCallStateChanged(
                    call_id=call_id,
                    action=action,
                    from_uhid=packet.source_uhid,
                )
            )
        return True


def _encode_video_call_control_payload(call_id: UUID, action: str, sent_at_ms: int) -> bytes:
    """Serialize a VideoCall call-control wire payload to canonical, byte-identical UTF-8 JSON.

    Snake_case keys, field order ``call_id``, ``action``, ``sent_at_ms``, no whitespace,
    UUID lowercase-dashed (36 chars), ``sent_at_ms`` a bare integer, ``action`` an ASCII
    verb. Matches the C# ``VideoCallControlPayload`` serialization and the
    fixtures/videocall byte-identity vectors.
    """
    return json.dumps(
        {
            "call_id": str(call_id),
            "action": action,
            "sent_at_ms": sent_at_ms,
        },
        separators=(",", ":"),
    ).encode("utf-8")


def _now_ms() -> int:
    return int(time.time() * 1000)


def _try_uuid(value: object) -> Optional[UUID]:
    if isinstance(value, UUID):
        return value
    if isinstance(value, str):
        try:
            return UUID(value)
        except ValueError:
            return None
    return None
