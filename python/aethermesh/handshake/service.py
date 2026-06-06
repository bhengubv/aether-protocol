# SPDX-License-Identifier: MIT

"""HandshakeService — async Hello/HelloAck capability handshake.

Wire flow (mirrors the C# reference):

    A -> B   Hello       { min:1, max:2, caps:[X,Y,Z], impl:"..." }
    A <- B   HelloAck    { min:1, max:2, caps:[X,Y],   impl:"..." }

Negotiation rules:
    * Negotiated version = min(ourMax, theirMax).
    * If min(ourMax, theirMax) < max(ourMin, theirMin) the ranges do not
      overlap -> raise IncompatiblePeer event, refuse to lock in.
    * Locked-in capability set = ourCaps INTERSECT theirCaps.

Backward-compat: a peer that never replies with a HelloAck is assumed to be
running protocol version 1 with no advertised capabilities. Hosts call
`assume_legacy_v1(peer_uhid)` from their own timer / heartbeat loop.
"""

from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timezone
from typing import Awaitable, Callable, Dict, FrozenSet, List, Optional, Set, Tuple

from aethermesh.handshake.models import (
    HelloPayload,
    IncompatiblePeerEvent,
    PeerCapabilities,
)
from aethermesh.protocol.mesh_packet import MeshPacket, PacketType
from aethermesh.routing.sender import MeshSender


# Default capability tags advertised by this implementation. Mirrors the
# C# `HandshakeService.DefaultCapabilities` set — keep in sync to maximize
# the post-handshake intersection with C# peers.
DEFAULT_CAPABILITIES: FrozenSet[str] = frozenset({
    "signal-x3dh",
    "double-ratchet",
    "dtn-custody",
    "sos",
    "voice",
    "stream",
})

# Free-form implementation banner emitted in our Hello/HelloAck.
DEFAULT_IMPLEMENTATION: str = "aether-python/1.0.0"

# Highest protocol version this implementation can speak. Matches the C#
# `ProtocolConstants.CurrentProtocolVersion`. Bump when the wire format
# evolves; the C# side tracks it as `CurrentProtocolVersion = 2`.
DEFAULT_MAX_PROTOCOL_VERSION: int = 2
DEFAULT_MIN_PROTOCOL_VERSION: int = 1


# Event-callback type aliases.
PeerNegotiatedCallback = Callable[[PeerCapabilities], Awaitable[None]]
IncompatiblePeerCallback = Callable[[IncompatiblePeerEvent], Awaitable[None]]


class HandshakeService:
    """Async Hello/HelloAck handshake service.

    Tracks the peers we've Hello'd, the peers we've finished negotiating
    with, and dispatches async callbacks on completion / incompatibility.

    Concurrency: all internal mutations of `_hello_sent` and `_negotiated`
    are guarded by a single asyncio.Lock so concurrent inbound packets
    don't race on the duplicate-Hello suppression check or the locked-in
    record write.
    """

    def __init__(
        self,
        sender: MeshSender,
        our_min_version: Optional[int] = None,
        our_max_version: Optional[int] = None,
        our_capabilities: Optional[Set[str]] = None,
        our_implementation: Optional[str] = None,
        logger: Optional[logging.Logger] = None,
    ) -> None:
        if sender is None:
            raise ValueError("sender cannot be None")

        self._sender = sender
        self._logger = logger or logging.getLogger(__name__)

        self._our_min_version: int = (
            our_min_version
            if our_min_version is not None
            else DEFAULT_MIN_PROTOCOL_VERSION
        )
        self._our_max_version: int = (
            our_max_version
            if our_max_version is not None
            else DEFAULT_MAX_PROTOCOL_VERSION
        )
        if not (0 <= self._our_min_version <= 255 and 0 <= self._our_max_version <= 255):
            raise ValueError(
                f"protocol versions must fit in a byte (got min={self._our_min_version}, "
                f"max={self._our_max_version})"
            )
        if self._our_min_version > self._our_max_version:
            raise ValueError(
                f"our_min_version ({self._our_min_version}) cannot exceed "
                f"our_max_version ({self._our_max_version})"
            )

        self._our_capabilities: FrozenSet[str] = (
            frozenset(our_capabilities) if our_capabilities is not None else DEFAULT_CAPABILITIES
        )
        self._our_implementation: str = our_implementation or DEFAULT_IMPLEMENTATION

        # Peers we've already sent a Hello to, to suppress duplicate sends.
        self._hello_sent: Set[str] = set()

        # Peers we've finished negotiating with.
        self._negotiated: Dict[str, PeerCapabilities] = {}

        # Async event-handler lists — async-callable callbacks; each is
        # invoked sequentially in registration order. We deliberately
        # use simple lists rather than thread-safe primitives because
        # async event delivery serialises naturally on the event loop.
        self._on_peer_negotiated: List[PeerNegotiatedCallback] = []
        self._on_incompatible_peer: List[IncompatiblePeerCallback] = []

        # Single lock protecting _hello_sent + _negotiated. The handshake
        # itself does no heavy work under the lock — payload parsing
        # happens before we acquire it on the receive path.
        self._lock = asyncio.Lock()

    # ─── event subscription ────────────────────────────────────────────

    def add_peer_negotiated_handler(self, handler: PeerNegotiatedCallback) -> None:
        self._on_peer_negotiated.append(handler)

    def add_incompatible_peer_handler(self, handler: IncompatiblePeerCallback) -> None:
        self._on_incompatible_peer.append(handler)

    # ─── public API ────────────────────────────────────────────────────

    async def initiate(self, peer_uhid: str) -> None:
        """Send a Hello towards a freshly discovered peer.

        No-op if we've already sent a Hello to this peer in the current
        session — re-broadcasts can otherwise cause duplicate Hellos.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if peer_uhid == self._sender.local_uhid:
            return

        # Suppress duplicate Hellos.
        async with self._lock:
            if peer_uhid in self._hello_sent:
                return
            self._hello_sent.add(peer_uhid)

        hello = self._build_packet(PacketType.Hello, peer_uhid)
        delivered = await self._sender.send(hello, peer_uhid)
        self._logger.debug(
            "Hello sent to %s delivered=%s", peer_uhid, delivered
        )

    async def handle_hello(self, hello_packet: MeshPacket) -> None:
        """Handle an inbound Hello: lock in their announced capabilities
        and reply with a HelloAck.
        """
        if hello_packet is None:
            raise ValueError("hello_packet cannot be None")
        if hello_packet.type != PacketType.Hello:
            raise ValueError(
                f"Expected Hello, got {hello_packet.type.name}"
            )

        if not hello_packet.source_uhid:
            return
        if hello_packet.source_uhid == self._sender.local_uhid:
            return

        theirs = self._try_parse_payload(hello_packet)
        if theirs is None:
            self._logger.warning(
                "Hello from %s has malformed payload — ignoring",
                hello_packet.source_uhid,
            )
            return

        ok, negotiated = await self._try_negotiate(hello_packet.source_uhid, theirs)
        if not ok or negotiated is None:
            return  # IncompatiblePeer already fired

        async with self._lock:
            self._negotiated[hello_packet.source_uhid] = negotiated

        await self._fire_peer_negotiated(negotiated)
        self._logger.info(
            "Hello accepted from %s -> version=%d caps=[%s] impl=%s",
            hello_packet.source_uhid,
            negotiated.negotiated_version,
            ",".join(sorted(negotiated.capabilities)),
            negotiated.implementation_version,
        )

        # Reply with HelloAck — symmetric, carries our own range/caps.
        ack = self._build_packet(PacketType.HelloAck, hello_packet.source_uhid)
        delivered = await self._sender.send(ack, hello_packet.source_uhid)
        self._logger.debug(
            "HelloAck sent to %s delivered=%s",
            hello_packet.source_uhid, delivered,
        )

    async def handle_hello_ack(self, hello_ack_packet: MeshPacket) -> None:
        """Handle an inbound HelloAck: lock in negotiated capabilities for
        the replying peer.
        """
        if hello_ack_packet is None:
            raise ValueError("hello_ack_packet cannot be None")
        if hello_ack_packet.type != PacketType.HelloAck:
            raise ValueError(
                f"Expected HelloAck, got {hello_ack_packet.type.name}"
            )

        if not hello_ack_packet.source_uhid:
            return
        if hello_ack_packet.source_uhid == self._sender.local_uhid:
            return

        theirs = self._try_parse_payload(hello_ack_packet)
        if theirs is None:
            self._logger.warning(
                "HelloAck from %s has malformed payload — ignoring",
                hello_ack_packet.source_uhid,
            )
            return

        ok, negotiated = await self._try_negotiate(
            hello_ack_packet.source_uhid, theirs
        )
        if not ok or negotiated is None:
            return  # IncompatiblePeer already fired

        async with self._lock:
            self._negotiated[hello_ack_packet.source_uhid] = negotiated

        await self._fire_peer_negotiated(negotiated)
        self._logger.info(
            "HelloAck received from %s -> version=%d caps=[%s] impl=%s",
            hello_ack_packet.source_uhid,
            negotiated.negotiated_version,
            ",".join(sorted(negotiated.capabilities)),
            negotiated.implementation_version,
        )

    async def get_peer_capabilities(self, peer_uhid: str) -> Optional[PeerCapabilities]:
        """Look up the locked-in capabilities for a peer. Returns None if
        the handshake has not yet completed.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        async with self._lock:
            return self._negotiated.get(peer_uhid)

    async def renegotiate(self, peer_uhid: str) -> None:
        """Drop a peer's cached capabilities and re-issue a Hello on the
        next outbound contact. Used when version-mismatch is detected in
        subsequent traffic.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        async with self._lock:
            self._negotiated.pop(peer_uhid, None)
            self._hello_sent.discard(peer_uhid)
        self._logger.info(
            "Cleared cached capabilities for %s; next contact will re-Hello",
            peer_uhid,
        )

    async def get_all_negotiated(self) -> List[PeerCapabilities]:
        """Snapshot of every peer that has finished negotiating."""
        async with self._lock:
            return list(self._negotiated.values())

    async def assume_legacy_v1(self, peer_uhid: str) -> None:
        """Backward-compat: install a "v1, no caps" record for a peer that
        never replied to our Hello within the timeout window.

        Hosts call this from their own timer / heartbeat loop. Idempotent —
        if the peer has since replied with a HelloAck, the existing record
        wins.
        """
        if not peer_uhid:
            raise ValueError("peer_uhid cannot be empty")
        if peer_uhid == self._sender.local_uhid:
            return

        fallback = PeerCapabilities(
            peer_uhid=peer_uhid,
            negotiated_version=1,
            capabilities=frozenset(),
            implementation_version="",
            negotiated_at=datetime.now(timezone.utc),
        )

        added = False
        async with self._lock:
            if peer_uhid not in self._negotiated:
                self._negotiated[peer_uhid] = fallback
                added = True

        if added:
            await self._fire_peer_negotiated(fallback)
            self._logger.warning(
                "No HelloAck from %s after timeout — assuming protocol v1 / "
                "no advertised capabilities",
                peer_uhid,
            )

    # ─── internals ─────────────────────────────────────────────────────

    def _build_packet(self, packet_type: PacketType, destination_uhid: str) -> MeshPacket:
        payload = HelloPayload(
            min_version=self._our_min_version,
            max_version=self._our_max_version,
            capabilities=sorted(self._our_capabilities),
            implementation=self._our_implementation,
        )
        return MeshPacket(
            type=packet_type,
            source_uhid=self._sender.local_uhid,
            destination_uhid=destination_uhid,
            ttl=1,  # direct hop only — handshake never relays
            priority=0,
            protocol_version=self._our_max_version,
            payload=payload.to_json_bytes(),
        )

    def _try_parse_payload(self, packet: MeshPacket) -> Optional[HelloPayload]:
        if packet.payload is None or len(packet.payload) == 0:
            return None
        try:
            return HelloPayload.from_json_bytes(packet.payload)
        except ValueError as ex:
            self._logger.warning(
                "Handshake payload from %s could not be parsed: %s",
                packet.source_uhid, ex,
            )
            return None

    async def _try_negotiate(
        self, peer_uhid: str, theirs: HelloPayload
    ) -> Tuple[bool, Optional[PeerCapabilities]]:
        """Attempt to negotiate a PeerCapabilities record from the peer's
        announced range/caps. On failure, fires IncompatiblePeer and
        returns (False, None).
        """
        if theirs.min_version > theirs.max_version:
            self._logger.warning(
                "Handshake from %s announces inverted range min=%d > max=%d — refusing",
                peer_uhid, theirs.min_version, theirs.max_version,
            )
            await self._fire_incompatible(peer_uhid, theirs, "inverted version range")
            return False, None

        # Overlap check: highest min must be <= lowest max.
        overlap_min = max(self._our_min_version, theirs.min_version)
        overlap_max = min(self._our_max_version, theirs.max_version)
        if overlap_min > overlap_max:
            await self._fire_incompatible(
                peer_uhid, theirs,
                f"no version overlap (ours={self._our_min_version}.."
                f"{self._our_max_version}, theirs={theirs.min_version}.."
                f"{theirs.max_version})",
            )
            return False, None

        chosen_version = overlap_max  # highest mutually-supported

        # Capability intersection (case-sensitive).
        their_caps = theirs.capabilities or []
        intersection = frozenset(
            cap for cap in their_caps
            if cap and cap in self._our_capabilities
        )

        negotiated = PeerCapabilities(
            peer_uhid=peer_uhid,
            negotiated_version=chosen_version,
            capabilities=intersection,
            implementation_version=theirs.implementation or "",
            negotiated_at=datetime.now(timezone.utc),
        )
        return True, negotiated

    async def _fire_incompatible(
        self, peer_uhid: str, theirs: HelloPayload, reason: str
    ) -> None:
        self._logger.warning("Incompatible peer %s: %s", peer_uhid, reason)
        evt = IncompatiblePeerEvent(
            peer_uhid=peer_uhid,
            their_min_version=theirs.min_version,
            their_max_version=theirs.max_version,
            our_min_version=self._our_min_version,
            our_max_version=self._our_max_version,
            reason=reason,
        )
        for handler in list(self._on_incompatible_peer):
            try:
                await handler(evt)
            except Exception as ex:  # noqa: BLE001 — never let a broken handler crash us
                self._logger.exception(
                    "IncompatiblePeer handler raised: %s", ex
                )

    async def _fire_peer_negotiated(self, caps: PeerCapabilities) -> None:
        for handler in list(self._on_peer_negotiated):
            try:
                await handler(caps)
            except Exception as ex:  # noqa: BLE001
                self._logger.exception(
                    "PeerNegotiated handler raised: %s", ex
                )
