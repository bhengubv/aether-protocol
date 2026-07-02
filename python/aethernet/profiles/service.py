# SPDX-License-Identifier: MIT

"""Default profile service (PacketType.ProfileSync).

Shares this node's profile directly with a chosen peer and caches profiles received from
peers. Directed (point-to-point, not broadcast) to avoid leaking identity metadata to the
whole mesh: a peer you interact with learns your profile; strangers do not.

Mirrors the C# ``AetherNet.Profiles.ProfileService``.
"""

from __future__ import annotations

import asyncio
import json
import logging
import time
from dataclasses import dataclass
from typing import Callable, Optional

from aethernet import constants
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender


_LOG = logging.getLogger(__name__)


@dataclass
class ProfileSyncPayload:
    """JSON payload for :class:`PacketType.ProfileSync` packets.

    Wire format: UTF-8 JSON with snake_case keys, field order ``uhid``, ``display_name``,
    ``avatar_ref``, ``status_message``, ``updated_at_ms``, no whitespace, ``updated_at_ms``
    a bare integer, all string fields always present (empty when unset — no nulls). Byte-
    identity is locked by fixtures/profiles/vectors.json.
    """

    #: UHID this profile describes (the sender). Self-identifying so a cached profile stays
    #: attributable.
    uhid: str = ""
    #: Human-readable display name (empty if unset).
    display_name: str = ""
    #: Content-addressed reference to an avatar (e.g. "blake3:…"), empty if none.
    avatar_ref: str = ""
    #: Free-text status / presence message (empty if unset).
    status_message: str = ""
    #: Unix timestamp in milliseconds when the profile was last updated by its owner.
    updated_at_ms: int = 0


class ProfileService:
    """Exchanges peer profile metadata over :class:`PacketType.ProfileSync`.

    Profiles are shared *directed* (to a specific peer), not broadcast, for privacy.
    Received profiles are cached (keyed by their ``uhid``) and surfaced via
    ``on_profile_updated``.
    """

    def __init__(self, sender: MeshSender) -> None:
        self._sender = sender
        self._local = ProfileSyncPayload(uhid=sender.local_uhid)
        self._peer_profiles: dict[str, ProfileSyncPayload] = {}
        self._lock = asyncio.Lock()
        # Raised when a peer's profile is received or refreshed.
        self.on_profile_updated: Optional[Callable[[ProfileSyncPayload], None]] = None

    def set_local_profile(
        self, display_name: str, avatar_ref: str, status_message: str
    ) -> None:
        """Set this node's own profile (stamps ``updated_at_ms`` to now)."""
        self._local = ProfileSyncPayload(
            uhid=self._sender.local_uhid,
            display_name=display_name or "",
            avatar_ref=avatar_ref or "",
            status_message=status_message or "",
            updated_at_ms=int(time.time() * 1000),
        )

    def get_local_profile(self) -> ProfileSyncPayload:
        """This node's current local profile."""
        return self._local

    async def publish_profile_to(self, peer_uhid: str) -> bool:
        """Send this node's local profile directly to ``peer_uhid``.

        Best-effort: delivers via the sender's directed send when the peer is reachable as
        a next hop. Returns delivery success.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid must not be empty")

        body = _encode_profile_sync_payload(self._local)

        packet = MeshPacket(
            type=PacketType.ProfileSync,
            source_uhid=self._sender.local_uhid,
            destination_uhid=peer_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )

        delivered = await self._sender.send(packet, peer_uhid)
        _LOG.debug("Profile sent to %s delivered=%s", peer_uhid, delivered)
        return delivered

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an incoming :class:`PacketType.ProfileSync` packet.

        Caches the sender's profile (keyed by its ``uhid``) and raises
        ``on_profile_updated``. Returns ``False`` for the wrong packet type, a malformed
        payload, or our own profile echoed back.
        """
        if packet.type != PacketType.ProfileSync:
            return False

        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            _LOG.debug(
                "ProfileSync from %s: malformed payload — dropped", packet.source_uhid
            )
            return False
        if not isinstance(data, dict):
            return False

        uhid = data.get("uhid")
        if not uhid or not isinstance(uhid, str):
            return False

        # Ignore our own profile echoed back.
        if uhid == self._sender.local_uhid:
            return False

        profile = ProfileSyncPayload(
            uhid=uhid,
            display_name=str(data.get("display_name", "")),
            avatar_ref=str(data.get("avatar_ref", "")),
            status_message=str(data.get("status_message", "")),
            updated_at_ms=int(data.get("updated_at_ms", 0)),
        )
        async with self._lock:
            self._peer_profiles[uhid] = profile
        if self.on_profile_updated:
            self.on_profile_updated(profile)
        return True

    def get_profile(self, uhid: str) -> Optional[ProfileSyncPayload]:
        """The cached profile for ``uhid``, or ``None`` if none is known."""
        return self._peer_profiles.get(uhid)

    def get_known_profiles(self) -> list[ProfileSyncPayload]:
        """Snapshot of every peer profile this node has cached."""
        return list(self._peer_profiles.values())


def _encode_profile_sync_payload(profile: ProfileSyncPayload) -> bytes:
    """Serialize a ProfileSync wire payload to canonical, byte-identical UTF-8 JSON.

    Snake_case keys, field order ``uhid``, ``display_name``, ``avatar_ref``,
    ``status_message``, ``updated_at_ms``, no whitespace, ``updated_at_ms`` a bare integer,
    all string fields always present. Matches the C# ``ProfileSyncPayload`` serialization
    and the fixtures/profiles byte-identity vectors.
    """
    return json.dumps(
        {
            "uhid": profile.uhid,
            "display_name": profile.display_name,
            "avatar_ref": profile.avatar_ref,
            "status_message": profile.status_message,
            "updated_at_ms": profile.updated_at_ms,
        },
        separators=(",", ":"),
    ).encode("utf-8")
