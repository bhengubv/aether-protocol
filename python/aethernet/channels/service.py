# SPDX-License-Identifier: MIT

"""Default named-channel pub/sub service (PacketType.ChannelMessage).

A named channel is an application-layer pub/sub topic ("res-floor-3", a society, a
project team). A node subscribes to the channel ids it cares about; publishing floods a
:class:`PacketType.ChannelMessage` to every peer; subscribed receivers surface the message
via ``on_message_received``. Messages are de-duplicated by ``message_id`` and re-flooded
(TTL-bounded) so they reach subscribers several hops away.

The original author is carried in ``sender_uhid`` so it survives relay hops (the enclosing
packet's ``source_uhid`` changes at each hop). Mirrors the C#
``AetherNet.Channels.ChannelMessageService``.
"""

from __future__ import annotations

import asyncio
import json
import logging
from dataclasses import dataclass
from typing import Callable, Optional
from uuid import UUID, uuid4

from aethernet import constants
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)


@dataclass
class ChannelMessagePayload:
    """JSON payload for :class:`PacketType.ChannelMessage` packets.

    Wire format: UTF-8 JSON with snake_case keys, field order ``channel_id``,
    ``message_id``, ``sender_uhid``, ``content``, ``sent_at_ms``, no whitespace,
    lowercase-dashed UUID, ``sent_at_ms`` a bare integer. Byte-identity is locked by
    fixtures/channels/vectors.json (content is ASCII in those vectors; escaping of
    non-ASCII content follows standard JSON).
    """

    #: Application-defined channel identifier (opaque to the protocol).
    channel_id: str = ""
    #: Unique id for this message — used for flood de-duplication.
    message_id: UUID = UUID(int=0)
    #: UHID of the original author (preserved across relay hops).
    sender_uhid: str = ""
    #: Message body.
    content: str = ""
    #: Unix timestamp in milliseconds when the author published the message.
    sent_at_ms: int = 0


@dataclass
class ChannelMessageReceived:
    """Raised when a channel message arrives on a channel this node is subscribed to."""

    #: Channel the message was published to.
    channel_id: str = ""
    #: Unique id of the message.
    message_id: UUID = UUID(int=0)
    #: UHID of the original author.
    sender_uhid: str = ""
    #: Message body.
    content: str = ""
    #: Unix-ms timestamp the author published the message.
    sent_at_ms: int = 0


class ChannelMessageService:
    """Application-layer named-channel pub/sub over :class:`PacketType.ChannelMessage`.

    A node subscribes to channel ids it cares about; :meth:`publish` floods the mesh;
    subscribed receivers surface the message via ``on_message_received``. Messages are
    de-duplicated by ``message_id`` and re-flooded (TTL-bounded) so they reach subscribers
    several hops away.
    """

    def __init__(self, sender: MeshSender) -> None:
        self._sender = sender
        self._subscriptions: set[str] = set()
        self._seen: set[UUID] = set()
        self._lock = asyncio.Lock()
        # Raised when a message arrives on a subscribed channel (not raised for this
        # node's own messages).
        self.on_message_received: Optional[Callable[[ChannelMessageReceived], None]] = None

    def subscribe(self, channel_id: str) -> None:
        """Subscribe to a channel — messages on it will raise ``on_message_received``."""
        if not channel_id:
            raise ValueError("channel_id must not be empty")
        self._subscriptions.add(channel_id)

    def unsubscribe(self, channel_id: str) -> None:
        """Stop surfacing messages for a channel."""
        self._subscriptions.discard(channel_id)

    def get_subscriptions(self) -> list[str]:
        """The channels this node is currently subscribed to."""
        return list(self._subscriptions)

    async def publish(self, channel_id: str, content: str) -> int:
        """Publish ``content`` to ``channel_id``.

        Floods a signed-by-nobody :class:`PacketType.ChannelMessage` to all peers
        (source=local, dest="*", ttl=default). Returns the number of peers reached
        directly.
        """
        if not channel_id:
            raise ValueError("channel_id must not be empty")
        if content is None:
            raise ValueError("content must not be None")

        message_id = uuid4()
        # Never re-handle our own message when it floods back.
        async with self._lock:
            self._seen.add(message_id)

        body = _encode_channel_message_payload(
            channel_id, message_id, self._sender.local_uhid, content, _now_ms()
        )

        packet = MeshPacket(
            type=PacketType.ChannelMessage,
            source_uhid=self._sender.local_uhid,
            destination_uhid="*",
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )

        delivered = await self._sender.broadcast(packet)
        _LOG.debug(
            "Channel %s publish %s to %d peers", channel_id, message_id, delivered
        )
        return delivered

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an incoming :class:`PacketType.ChannelMessage` packet.

        De-dups by ``message_id``, surfaces it via ``on_message_received`` if we are
        subscribed to its channel (and it is not our own), and re-floods while TTL allows
        (even if we aren't subscribed — pure relay). Returns ``False`` for the wrong packet
        type, a malformed payload, or a duplicate.
        """
        if packet.type != PacketType.ChannelMessage:
            return False

        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            _LOG.debug(
                "ChannelMessage from %s: malformed payload — dropped", packet.source_uhid
            )
            return False
        if not isinstance(data, dict):
            return False

        channel_id = data.get("channel_id")
        if not channel_id or not isinstance(channel_id, str):
            return False

        message_id = _try_uuid(data.get("message_id"))
        if message_id is None:
            return False

        # Flood de-duplication: only the first copy of a given message id is processed.
        async with self._lock:
            if message_id in self._seen:
                return False
            self._seen.add(message_id)

        sender_uhid = str(data.get("sender_uhid", ""))
        is_own = sender_uhid == self._sender.local_uhid

        if not is_own and channel_id in self._subscriptions:
            if self.on_message_received:
                self.on_message_received(
                    ChannelMessageReceived(
                        channel_id=channel_id,
                        message_id=message_id,
                        sender_uhid=sender_uhid,
                        content=str(data.get("content", "")),
                        sent_at_ms=int(data.get("sent_at_ms", 0)),
                    )
                )

        # Re-flood so subscribers further out receive it — even if WE aren't subscribed.
        if packet.ttl > 1 and not is_own:
            packet.ttl -= 1
            await self._sender.broadcast(packet)

        return True


def _encode_channel_message_payload(
    channel_id: str,
    message_id: UUID,
    sender_uhid: str,
    content: str,
    sent_at_ms: int,
) -> bytes:
    """Serialize a ChannelMessage wire payload to canonical, byte-identical UTF-8 JSON.

    Snake_case keys, field order ``channel_id``, ``message_id``, ``sender_uhid``,
    ``content``, ``sent_at_ms``, no whitespace, UUID lowercase-dashed (36 chars),
    ``sent_at_ms`` a bare integer. Matches the C# ``ChannelMessagePayload`` serialization
    and the fixtures/channels byte-identity vectors.
    """
    return json.dumps(
        {
            "channel_id": channel_id,
            "message_id": str(message_id),
            "sender_uhid": sender_uhid,
            "content": content,
            "sent_at_ms": sent_at_ms,
        },
        separators=(",", ":"),
    ).encode("utf-8")


def _now_ms() -> int:
    import time

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
