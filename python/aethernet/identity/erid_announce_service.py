# SPDX-License-Identifier: MIT
"""Binds PacketType.EridAnnounce (56) to the mesh.

A node shares its rotating-address routing key with an established peer by sending the
(already Signal-encrypted) announcement directly. Transport only — the plaintext framing
(:mod:`aethernet.identity.erid_announcement_codec`) and the encryption (the Signal
protocol) are done by the host / EridExchangeService; this service just carries the opaque
encrypted blob as a directed packet and surfaces inbound ones via ``on_announce_received``.

Python port of the C# reference (AetherNet.Identity.EridAnnounceService); wire byte 56
matches PacketType.EridAnnounce so a directed announcement is byte-identical across
languages.
"""

from __future__ import annotations

from typing import Callable, Optional

from aethernet.constants import DEFAULT_TTL
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


class EridAnnounceService:
    """Binds PacketType.EridAnnounce (56) to the mesh: send an encrypted ERID
    announcement directly to a peer, and surface inbound ones via ``on_announce_received``.

    Assign a callable to ``on_announce_received`` to receive events; the callback gets
    ``(encrypted_announcement: bytes, from_uhid: str)``. The payload is still encrypted —
    the host decrypts and frames it via the codec.
    """

    def __init__(self, sender, logger=None) -> None:
        self._sender = sender
        self._logger = logger
        self.on_announce_received: Optional[Callable[[bytes, str], None]] = None

    def _log(self, message: str) -> None:
        if self._logger is not None:
            self._logger.debug(message)

    async def send_announce(self, peer_uhid: str, encrypted_announcement: bytes) -> bool:
        """Send an encrypted ERID announcement directly to ``peer_uhid``.

        Returns delivery success. Raises ``ValueError`` if ``peer_uhid`` or
        ``encrypted_announcement`` is empty.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if encrypted_announcement is None or len(encrypted_announcement) == 0:
            raise ValueError("encrypted_announcement cannot be empty")

        packet = MeshPacket(
            type=PacketType.EridAnnounce,
            source_uhid=self._sender.local_uhid,
            destination_uhid=peer_uhid,
            ttl=DEFAULT_TTL,
            payload=bytes(encrypted_announcement),
        )
        delivered = await self._sender.send(packet, peer_uhid)
        self._log(
            f"ERID announce -> {peer_uhid} ({len(encrypted_announcement)} B) "
            f"delivered={delivered}"
        )
        return delivered

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an inbound PacketType.EridAnnounce: fire ``on_announce_received``.

        Returns False on wrong type or empty body.
        """
        if packet is None:
            return False
        if packet.type != PacketType.EridAnnounce:
            return False
        if packet.payload is None or len(packet.payload) == 0:
            return False

        if self.on_announce_received is not None:
            self.on_announce_received(packet.payload, packet.source_uhid)
        return True
