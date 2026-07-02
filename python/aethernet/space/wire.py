# SPDX-License-Identifier: MIT
#
# WIRE binding for PacketType.SpaceBreadcrumb (40) — the thin mesh transport for the aether-space
# geo-pinned-noticeboard extension. Python port of AetherNet.Space.SpaceBreadcrumbService, byte-identical
# to the C# reference and every other language SDK (fixtures/space/vectors.json).
#
# Projects the SpaceBreadcrumb model onto a byte-identical JSON shape: snake_case keys in a pinned field
# order (content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature), the UTC creation
# time as a Unix-ms integer (not ISO-8601), the category enum as a bare integer, and the signature as
# STANDARD base64 (empty bytes -> ""). Serialised with json.dumps(..., separators=(",", ":")) so there is
# no whitespace.
#
# Broadcast path: project the breadcrumb -> serialise -> wrap in a MeshPacket addressed to "*" -> broadcast.
# Handle path: reject a wrong packet type or malformed payload (returns False), else rebuild the breadcrumb
# and fire on_breadcrumb_received. Transport only — the host pins accepted breadcrumbs into ISpaceService.

from __future__ import annotations

import base64
import json
from datetime import datetime, timezone
from typing import Callable, Optional

from aethernet.constants import DEFAULT_TTL
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.space.service import BreadcrumbType, SpaceBreadcrumb


def _to_unix_ms(dt: datetime) -> int:
    """UTC datetime -> Unix epoch milliseconds (int). Naive values are treated as UTC."""
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return int(dt.timestamp() * 1000)


def _from_unix_ms(ms: int) -> datetime:
    """Unix epoch milliseconds -> aware UTC datetime."""
    return datetime.fromtimestamp(ms / 1000, tz=timezone.utc)


def encode_space_breadcrumb_payload(breadcrumb: SpaceBreadcrumb) -> bytes:
    """Serialise a SpaceBreadcrumb to its canonical UTF-8 JSON wire bytes.

    Field order is pinned; ``created_at_ms`` / ``ttl_hours`` / ``type`` are bare integers and
    ``signature`` is STANDARD base64 (``""`` when unsigned). Byte-identical to fixtures/space/vectors.json.
    """
    obj = {
        "content_hash": breadcrumb.content_hash,
        "geo_hash": breadcrumb.geo_hash,
        "anchor_uhid": breadcrumb.anchor_uhid,
        "created_at_ms": _to_unix_ms(breadcrumb.created_at_utc),
        "ttl_hours": breadcrumb.ttl_hours,
        "type": int(breadcrumb.type),
        "signature": base64.b64encode(breadcrumb.signature).decode() if breadcrumb.signature else "",
    }
    return json.dumps(obj, separators=(",", ":")).encode("utf-8")


def _decode_space_breadcrumb_payload(data: bytes) -> SpaceBreadcrumb:
    """Deserialise canonical wire bytes back into a SpaceBreadcrumb. Raises on malformed JSON."""
    obj = json.loads(data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else data)
    sig_b64 = obj.get("signature", "")
    return SpaceBreadcrumb(
        content_hash=obj.get("content_hash", ""),
        geo_hash=obj.get("geo_hash", ""),
        anchor_uhid=obj.get("anchor_uhid", ""),
        created_at_utc=_from_unix_ms(int(obj.get("created_at_ms", 0))),
        ttl_hours=int(obj.get("ttl_hours", 0)),
        type=BreadcrumbType(int(obj.get("type", 0))),
        signature=base64.b64decode(sig_b64) if sig_b64 else b"",
    )


class SpaceBreadcrumbService:
    """Binds PacketType.SpaceBreadcrumb (40) to the mesh: broadcast a locally-dropped breadcrumb, and
    surface inbound breadcrumbs via ``on_breadcrumb_received``.
    """

    def __init__(self, sender, logger=None) -> None:
        self._sender = sender
        self._logger = logger
        # Raised when a breadcrumb arrives from a peer. Assign a callable to receive it.
        self.on_breadcrumb_received: Optional[Callable[[SpaceBreadcrumb], None]] = None

    def _log(self, message: str) -> None:
        if self._logger is not None:
            self._logger.debug(message)

    async def broadcast(self, breadcrumb: SpaceBreadcrumb) -> int:
        """Flood a breadcrumb to mesh peers. Returns the number of peers it was delivered to."""
        packet = MeshPacket(
            type=PacketType.SpaceBreadcrumb,
            source_uhid=self._sender.local_uhid,
            destination_uhid="*",
            ttl=DEFAULT_TTL,
            payload=encode_space_breadcrumb_payload(breadcrumb),
        )
        delivered = await self._sender.broadcast(packet)
        self._log(
            f"SpaceBreadcrumb {breadcrumb.content_hash}@{breadcrumb.geo_hash} "
            f"broadcast to {delivered} peers"
        )
        return delivered

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an inbound SpaceBreadcrumb(40). Returns False on wrong type or malformed payload."""
        if packet is None:
            return False
        if packet.type != PacketType.SpaceBreadcrumb:
            return False

        try:
            breadcrumb = _decode_space_breadcrumb_payload(packet.payload)
        except (ValueError, KeyError) as exc:
            self._log(
                f"SpaceBreadcrumb from {packet.source_uhid}: malformed payload — dropped: {exc}"
            )
            return False
        if not breadcrumb.content_hash:
            return False

        if self.on_breadcrumb_received is not None:
            self.on_breadcrumb_received(breadcrumb)
        return True
