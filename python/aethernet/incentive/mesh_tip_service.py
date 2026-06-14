# SPDX-License-Identifier: MIT
#
# Default MeshTipService. Sends and receives generic PacketType.TipPacket (24) packets. Python port of
# AetherNet.Security.Services.MeshTipService (and the Go incentive.MeshTipService).
#
# Send path: build a TipPacketPayload -> sign the payload's canonical bytes with the local identity key
# (real Ed25519) -> serialise as snake_case JSON -> wrap in a MeshPacket -> sign the enclosing packet ->
# route toward the recipient (unicast over a discovered route, falling back to broadcast).
#
# Receive path: deserialise the payload -> best-effort signature check (Ed25519 signature must be present
# and well-formed = 64 bytes) -> hand to the host's MeshTipSettlementProvider -> relay the packet onward
# toward its addressed recipient. A malformed or unverifiable payload is logged and dropped, never raised.
#
# This service is purely a protocol mechanism. It attaches NO value semantics to the amount and performs
# NO settlement — settlement is entirely the host's business, expressed through the injected provider. A
# bare node (default no-op provider) accepts and relays tips but settles nothing.

from __future__ import annotations

from typing import Optional, Protocol, runtime_checkable
from uuid import UUID

from aethernet.constants import DEFAULT_TTL
from aethernet.incentive.tip_packet_payload import TipPacketPayload
from aethernet.protocol.mesh_packet import MeshPacket, PacketType

# Ed25519 signature length in bytes — used for the best-effort inbound check.
_ED25519_SIGNATURE_LENGTH = 64


@runtime_checkable
class MeshSender(Protocol):
    """Minimal mesh transport surface needed by MeshTipService."""

    @property
    def local_uhid(self) -> str: ...  # noqa: E704

    def send(self, packet: MeshPacket, next_hop_uhid: str) -> bool: ...  # noqa: E704

    def broadcast(self, packet: MeshPacket) -> int: ...  # noqa: E704


@runtime_checkable
class PacketSigner(Protocol):
    """Signs the enclosing MeshPacket envelope (fills signature/nonce/timestamp)."""

    def sign_packet(self, packet: MeshPacket) -> MeshPacket: ...  # noqa: E704


@runtime_checkable
class IdentitySigner(Protocol):
    """Signs the tip payload's canonical bytes with the local node's Ed25519 identity key."""

    def sign_data(self, data: bytes) -> bytes: ...  # noqa: E704


@runtime_checkable
class RouteResolver(Protocol):
    """Resolves a next-hop toward a destination UHID."""

    def find_next_hop(self, destination_uhid: str) -> Optional[str]: ...  # noqa: E704


class MeshTipSettlementProvider(Protocol):
    """The host's settlement hook — the Python analog of the C#
    ``IAetherNetIncentiveProvider.SettleMeshTip`` (and the Go MeshTipSettlementProvider). It receives the
    full signed ``TipPacketPayload`` off the mesh and decides how (if at all) to interpret its value. The
    default no-op settles nothing.
    """

    def settle_mesh_tip(self, payload: TipPacketPayload) -> None:
        """Invoked for every inbound, well-formed tip payload. Implementations (e.g. SDPKT / BhenguPay)
        wire their wallet settlement here. Raising is logged by the caller but never propagated to the
        wire — a settlement failure must not break relaying.
        """
        ...


class NoopMeshTipSettlementProvider:
    """The default no-op settlement provider — accepts the tip and settles nothing. A bare node carries
    the tip signal but never moves value."""

    def settle_mesh_tip(self, payload: TipPacketPayload) -> None:
        return None


class MeshTipService:
    """Builds, signs, sends, and handles mesh tip packets."""

    def __init__(
        self,
        sender: MeshSender,
        signer: PacketSigner,
        identity: IdentitySigner,
        routing: Optional[RouteResolver] = None,
        settle: Optional[MeshTipSettlementProvider] = None,
        logger=None,
    ) -> None:
        """Construct a MeshTipService.

        Pass ``None`` for ``settle`` to use the default no-op settlement provider; ``None`` for
        ``routing`` to always broadcast; ``None`` for ``logger`` to disable diagnostics.
        """
        self._sender = sender
        self._signer = signer
        self._identity = identity
        self._routing = routing
        self._settle: MeshTipSettlementProvider = settle or NoopMeshTipSettlementProvider()
        self._logger = logger
        self._default_ttl = DEFAULT_TTL

    def _log(self, message: str) -> None:
        if self._logger is not None:
            self._logger.debug(message)

    def send_tip(
        self,
        recipient_uhid: str,
        amount: str,
        traffic_type: str,
        reference_id: Optional[UUID] = None,
        timestamp_unix_ms: int = 0,
    ) -> MeshPacket:
        """Build, sign, and route a TipPacket(24) addressed to ``recipient_uhid``.

        ``amount`` is the caller's input verbatim (the invariant decimal string) — the protocol imposes
        NO policy on it. It is signed into the payload and carried as-is. Returns the signed MeshPacket
        that was routed onto the mesh.
        """
        payload = TipPacketPayload(
            tipper_uhid=self._sender.local_uhid,
            recipient_uhid=recipient_uhid,
            amount=amount,
            traffic_type=traffic_type,
            reference_id=reference_id,
            timestamp_unix_ms=timestamp_unix_ms,
        )

        # Sign the payload's canonical bytes with the local identity key (real Ed25519).
        payload.signature = self._identity.sign_data(payload.build_canonical_data())

        packet = MeshPacket(
            type=PacketType.TipPacket,
            source_uhid=self._sender.local_uhid,
            destination_uhid=recipient_uhid,
            ttl=self._default_ttl,
            priority=0,
            payload=payload.to_json(),
        )

        # Sign the enclosing MeshPacket (fills nonce/timestamp + envelope signature).
        signed = self._signer.sign_packet(packet)

        # Route toward the recipient: unicast over a discovered route, else broadcast.
        if self._routing is not None:
            next_hop = self._routing.find_next_hop(recipient_uhid)
            if next_hop:
                self._sender.send(signed, next_hop)
                self._log(f"MeshTip: sent (unicast) to recipient={recipient_uhid} via {next_hop}")
                return signed

        self._sender.broadcast(signed)
        self._log(f"MeshTip: sent (broadcast) to recipient={recipient_uhid}")
        return signed

    def handle_tip_packet(self, packet: MeshPacket) -> bool:
        """Process an inbound TipPacket(24) received off the mesh.

        Returns ``True`` when the payload was accepted and handed to the settlement provider.
        Returns ``False`` when the packet should be silently discarded (wrong type, malformed payload,
        missing/malformed signature).
        """
        if packet is None:
            return False
        if packet.type != PacketType.TipPacket:
            self._log(f"MeshTip: unexpected packet type {packet.type} — ignored")
            return False

        # 1. Deserialise the payload. A malformed payload is logged and dropped.
        try:
            payload = TipPacketPayload.from_json(packet.payload)
        except (ValueError, KeyError) as exc:
            self._log(f"MeshTip from {packet.source_uhid}: JSON deserialization failed — dropped: {exc}")
            return False
        if not payload.tipper_uhid or not payload.recipient_uhid:
            self._log(f"MeshTip from {packet.source_uhid}: payload missing required fields — dropped")
            return False

        # 2. Best-effort signature check: an Ed25519 signature is exactly 64 bytes. A payload carrying
        #    no signature, or a malformed one, is unverifiable — logged and dropped. The host's
        #    settlement provider is responsible for any stronger, key-bound verification it needs.
        if payload.signature is None or len(payload.signature) != _ED25519_SIGNATURE_LENGTH:
            self._log(f"MeshTip from {payload.tipper_uhid}: missing or malformed signature — dropped")
            return False

        # 3. Hand to the host's settlement provider. Default no-op settles nothing. A settlement error
        #    is logged but never breaks relaying.
        try:
            self._settle.settle_mesh_tip(payload)
        except Exception as exc:  # noqa: BLE001 — a host hook must never break relaying.
            self._log(f"MeshTip from {payload.tipper_uhid}: settlement provider error: {exc}")

        # 4. Relay onward toward the addressed recipient if this node is not the destination and the
        #    packet may still be forwarded. The tip is ordinary addressed traffic.
        if packet.destination_uhid != self._sender.local_uhid and packet.can_forward:
            if self._routing is not None:
                next_hop = self._routing.find_next_hop(packet.destination_uhid)
                if next_hop:
                    self._sender.send(packet, next_hop)
                    return True
            self._sender.broadcast(packet)

        self._log(
            f"MeshTip handled: tipper={payload.tipper_uhid} "
            f"recipient={payload.recipient_uhid} traffic={payload.traffic_type}"
        )
        return True
