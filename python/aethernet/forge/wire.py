# SPDX-License-Identifier: MIT
#
# WIRE binding for PacketType.ForgeAnnounce (41) — the thin mesh transport for the aether-forge
# package-cache extension. Python port of AetherNet.Forge.ForgeAnnounceService, byte-identical to the C#
# reference and every other language SDK (fixtures/forge/vectors.json).
#
# A node broadcasts a ForgeAnnounce when it caches a new package artifact, so mesh peers learn where the
# artifact lives. Payload = {package_id, content_hash, size_bytes, announced_at_ms} — snake_case keys in
# that pinned order, size + ms as bare integers, serialised with json.dumps(..., separators=(",", ":")) so
# there is no whitespace.
#
# Broadcast path: build the payload -> serialise -> wrap in a MeshPacket addressed to "*" -> broadcast.
# Handle path: reject a wrong packet type or malformed payload (returns False), else fire
# on_announce_received with the decoded payload. Transport only — the host records accepted announcements
# in IForgeService.

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Callable, Optional

from aethernet.constants import DEFAULT_TTL
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


@dataclass
class ForgeAnnouncePayload:
    """The JSON body (snake_case) carried inside a ForgeAnnounce(41). Field order is pinned:
    package_id, content_hash, size_bytes, announced_at_ms."""

    package_id: str = ""
    content_hash: str = ""
    size_bytes: int = 0
    announced_at_ms: int = 0

    def to_json(self) -> bytes:
        """Serialise to canonical UTF-8 JSON wire bytes. Byte-identical to fixtures/forge/vectors.json."""
        obj = {
            "package_id": self.package_id,
            "content_hash": self.content_hash,
            "size_bytes": self.size_bytes,
            "announced_at_ms": self.announced_at_ms,
        }
        return json.dumps(obj, separators=(",", ":")).encode("utf-8")

    @classmethod
    def from_json(cls, data: bytes) -> "ForgeAnnouncePayload":
        """Deserialise canonical wire bytes. Raises on malformed JSON."""
        obj = json.loads(data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else data)
        return cls(
            package_id=obj.get("package_id", ""),
            content_hash=obj.get("content_hash", ""),
            size_bytes=int(obj.get("size_bytes", 0)),
            announced_at_ms=int(obj.get("announced_at_ms", 0)),
        )


class ForgeAnnounceService:
    """Binds PacketType.ForgeAnnounce (41) to the mesh: broadcast a freshly-cached artifact announcement,
    and surface inbound announcements via ``on_announce_received``.
    """

    def __init__(self, sender, logger=None) -> None:
        self._sender = sender
        self._logger = logger
        # Raised when a forge announcement arrives from a peer. Assign a callable to receive it.
        self.on_announce_received: Optional[Callable[[ForgeAnnouncePayload], None]] = None

    def _log(self, message: str) -> None:
        if self._logger is not None:
            self._logger.debug(message)

    async def broadcast(
        self, package_id: str, content_hash: str, size_bytes: int, announced_at_ms: int
    ) -> int:
        """Announce a cached artifact to mesh peers. Returns the number of peers reached."""
        payload = ForgeAnnouncePayload(
            package_id=package_id,
            content_hash=content_hash or "",
            size_bytes=size_bytes,
            announced_at_ms=announced_at_ms,
        )
        packet = MeshPacket(
            type=PacketType.ForgeAnnounce,
            source_uhid=self._sender.local_uhid,
            destination_uhid="*",
            ttl=DEFAULT_TTL,
            payload=payload.to_json(),
        )
        delivered = await self._sender.broadcast(packet)
        self._log(f"ForgeAnnounce {package_id} broadcast to {delivered} peers")
        return delivered

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an inbound ForgeAnnounce(41). Returns False on wrong type or malformed payload."""
        if packet is None:
            return False
        if packet.type != PacketType.ForgeAnnounce:
            return False

        try:
            payload = ForgeAnnouncePayload.from_json(packet.payload)
        except (ValueError, KeyError) as exc:
            self._log(
                f"ForgeAnnounce from {packet.source_uhid}: malformed payload — dropped: {exc}"
            )
            return False
        if not payload.package_id:
            return False

        if self.on_announce_received is not None:
            self.on_announce_received(payload)
        return True
