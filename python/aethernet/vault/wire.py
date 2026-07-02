# SPDX-License-Identifier: MIT
#
# WIRE binding for PacketType.VaultShardRequest (42) — the thin mesh transport for the aether-vault
# erasure-coded-storage extension. Python port of AetherNet.Vault.VaultShardRequestService, byte-identical
# to the C# reference and every other language SDK (fixtures/vaultshard/vectors.json).
#
# A node broadcasts a VaultShardRequest when it needs an erasure-coded shard to recover a file. Payload =
# {shard_hash, requester_uhid} — snake_case keys in that pinned order, serialised with
# json.dumps(..., separators=(",", ":")) so there is no whitespace.
#
# Request path: the requester_uhid is filled with the local node's UHID -> serialise -> wrap in a MeshPacket
# addressed to "*" -> broadcast. Handle path: reject a wrong packet type or malformed payload (returns
# False), else fire on_shard_requested with the decoded request. Transport only — the host answers from
# IVaultService if it holds the shard.

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Callable, Optional

from aethernet.constants import DEFAULT_TTL
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


@dataclass
class VaultShardRequest:
    """A peer's request for one erasure-coded shard. Field order is pinned: shard_hash, requester_uhid."""

    shard_hash: str = ""
    requester_uhid: str = ""

    def to_json(self) -> bytes:
        """Serialise to canonical UTF-8 JSON wire bytes. Byte-identical to fixtures/vaultshard/vectors.json."""
        obj = {
            "shard_hash": self.shard_hash,
            "requester_uhid": self.requester_uhid,
        }
        return json.dumps(obj, separators=(",", ":")).encode("utf-8")

    @classmethod
    def from_json(cls, data: bytes) -> "VaultShardRequest":
        """Deserialise canonical wire bytes. Raises on malformed JSON."""
        obj = json.loads(data.decode("utf-8") if isinstance(data, (bytes, bytearray)) else data)
        return cls(
            shard_hash=obj.get("shard_hash", ""),
            requester_uhid=obj.get("requester_uhid", ""),
        )


class VaultShardRequestService:
    """Binds PacketType.VaultShardRequest (42) to the mesh: ask peers for a shard, and surface inbound
    shard requests via ``on_shard_requested``.
    """

    def __init__(self, sender, logger=None) -> None:
        self._sender = sender
        self._logger = logger
        # Raised when a peer requests a shard. Assign a callable to receive it.
        self.on_shard_requested: Optional[Callable[[VaultShardRequest], None]] = None

    def _log(self, message: str) -> None:
        if self._logger is not None:
            self._logger.debug(message)

    async def request_shard(self, shard_hash: str) -> int:
        """Broadcast a request for ``shard_hash``. Returns the number of peers reached. The requester_uhid
        is the sender's local UHID."""
        request = VaultShardRequest(shard_hash=shard_hash, requester_uhid=self._sender.local_uhid)
        packet = MeshPacket(
            type=PacketType.VaultShardRequest,
            source_uhid=self._sender.local_uhid,
            destination_uhid="*",
            ttl=DEFAULT_TTL,
            payload=request.to_json(),
        )
        delivered = await self._sender.broadcast(packet)
        self._log(f"VaultShardRequest {shard_hash} broadcast to {delivered} peers")
        return delivered

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an inbound VaultShardRequest(42). Returns False on wrong type or malformed payload."""
        if packet is None:
            return False
        if packet.type != PacketType.VaultShardRequest:
            return False

        try:
            request = VaultShardRequest.from_json(packet.payload)
        except (ValueError, KeyError) as exc:
            self._log(
                f"VaultShardRequest from {packet.source_uhid}: malformed payload — dropped: {exc}"
            )
            return False
        if not request.shard_hash:
            return False

        if self.on_shard_requested is not None:
            self.on_shard_requested(request)
        return True
