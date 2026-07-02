# SPDX-License-Identifier: MIT

"""Mesh pre-key exchange service (PacketType.PreKeyRequest 25 / PreKeyResponse 26).

Pre-key exchange is the mesh TRANSPORT of a :class:`PreKeyBundle` — directed
request/response so a peer can obtain another peer's published bundle over the
mesh (closing the "how does a peer fetch a bundle while the owner is offline"
gap the messaging layer previously left out-of-band).

A node publishes its current bundle via :meth:`set_local_bundle` (the host
produces it with the Signal protocol service). A peer asks for it with
:meth:`request_bundle`, minting a request id and directed-sending a
``PreKeyRequest``; the responder replies with its bundle (``PreKeyResponse``);
the requester caches the received bundle and fires ``on_bundle_received``.

Transport only — NO X3DH happens here. The host performs the actual key
agreement by feeding the received bundle to the Signal protocol service
(Signal-canonical: no key agreement in this layer). Directed request/response —
never broadcast — so bundle requests do not leak identity-interest to the whole
mesh. Mirrors the C# ``AetherNet.PreKeys.PreKeyExchangeService``.
"""

from __future__ import annotations

import base64
import json
import logging
from dataclasses import dataclass
from typing import Callable, Optional
from uuid import UUID, uuid4

from aethernet import constants
from aethernet.protocol.mesh_packet import MeshPacket, PacketType
from aethernet.routing.sender import MeshSender
from aethernet.security.signal_protocol import PreKeyBundle


_LOG = logging.getLogger(__name__)


@dataclass
class PreKeyBundleReceived:
    """Raised when a peer's pre-key bundle arrives in a ``PreKeyResponse``."""

    #: Request id echoed from the original ``PreKeyRequest`` (UUID(int=0) if unsolicited).
    request_id: UUID = UUID(int=0)
    #: UHID of the peer that sent the bundle.
    from_uhid: str = ""
    #: The received pre-key bundle — feed to the Signal protocol service to run X3DH.
    bundle: Optional[PreKeyBundle] = None


class PreKeyExchangeService:
    """Mesh pre-key exchange over :class:`PacketType.PreKeyRequest` (25) and
    :class:`PacketType.PreKeyResponse` (26).

    A node publishes its bundle via :meth:`set_local_bundle`; a peer asks with
    :meth:`request_bundle`; the responder replies with its bundle; the requester
    caches it (keyed by ``uhid``) and surfaces it via ``on_bundle_received``.
    Transport of bundles only — the host runs X3DH out of band.
    """

    def __init__(self, sender: MeshSender) -> None:
        self._sender = sender
        self._local: Optional[PreKeyBundle] = None
        self._received: dict[str, PreKeyBundle] = {}
        # Raised when a peer's pre-key bundle is received in a PreKeyResponse.
        self.on_bundle_received: Optional[Callable[[PreKeyBundleReceived], None]] = None

    def set_local_bundle(self, bundle: PreKeyBundle) -> None:
        """Set (or replace) this node's published bundle — served in reply to requests."""
        if bundle is None:
            raise ValueError("bundle must not be None")
        self._local = bundle

    def get_local_bundle(self) -> Optional[PreKeyBundle]:
        """The currently-published local bundle, or ``None`` if none has been set."""
        return self._local

    async def request_bundle(self, peer_uhid: str) -> UUID:
        """Ask ``peer_uhid`` for its pre-key bundle: mint a request id and directed-send a
        :class:`PacketType.PreKeyRequest`. Returns the new request id (echoed by the response).
        """
        if not peer_uhid:
            raise ValueError("peer_uhid must not be empty")

        request_id = uuid4()
        body = _encode_pre_key_request_payload(request_id, self._sender.local_uhid)

        packet = MeshPacket(
            type=PacketType.PreKeyRequest,
            source_uhid=self._sender.local_uhid,
            destination_uhid=peer_uhid,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )

        delivered = await self._sender.send(packet, peer_uhid)
        _LOG.debug(
            "PreKey request %s -> %s delivered=%s", request_id, peer_uhid, delivered
        )
        return request_id

    async def handle(self, packet: MeshPacket) -> bool:
        """Process an incoming pre-key packet.

        On :class:`PacketType.PreKeyRequest`, reply with the local bundle (if set).
        On :class:`PacketType.PreKeyResponse`, cache the peer bundle and raise
        ``on_bundle_received``. Returns ``False`` for the wrong packet type, a malformed
        payload, or a request received when no local bundle is set.
        """
        if packet.type == PacketType.PreKeyRequest:
            return await self._handle_request(packet)
        if packet.type == PacketType.PreKeyResponse:
            return self._handle_response(packet)
        return False

    async def _handle_request(self, packet: MeshPacket) -> bool:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            _LOG.debug(
                "PreKeyRequest from %s: malformed payload — dropped", packet.source_uhid
            )
            return False
        if not isinstance(data, dict):
            return False

        local = self._local
        if local is None:
            _LOG.debug(
                "PreKeyRequest from %s: no local bundle set — ignored", packet.source_uhid
            )
            return False

        request_id = _try_uuid(data.get("request_id")) or UUID(int=0)
        requester_uhid = data.get("requester_uhid")
        reply_to = (
            requester_uhid
            if isinstance(requester_uhid, str) and requester_uhid
            else packet.source_uhid
        )

        body = _encode_pre_key_response_payload(request_id, local)
        reply = MeshPacket(
            type=PacketType.PreKeyResponse,
            source_uhid=self._sender.local_uhid,
            destination_uhid=reply_to,
            ttl=constants.DEFAULT_TTL,
            payload=body,
        )

        delivered = await self._sender.send(reply, reply_to)
        _LOG.debug(
            "PreKey response %s -> %s delivered=%s", request_id, reply_to, delivered
        )
        return True

    def _handle_response(self, packet: MeshPacket) -> bool:
        try:
            data = json.loads(packet.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            _LOG.debug(
                "PreKeyResponse from %s: malformed payload — dropped", packet.source_uhid
            )
            return False
        if not isinstance(data, dict):
            return False

        uhid = data.get("uhid")
        if not uhid or not isinstance(uhid, str):
            return False

        try:
            bundle = PreKeyBundle(
                uhid=uhid,
                identity_key=base64.b64decode(data["identity_key"]),
                identity_key_x25519=base64.b64decode(data["identity_key_x25519"]),
                pre_key_id=int(data["pre_key_id"]),
                pre_key=base64.b64decode(data["pre_key"]),
                signed_pre_key_id=int(data["signed_pre_key_id"]),
                signed_pre_key=base64.b64decode(data["signed_pre_key"]),
                signed_pre_key_signature=base64.b64decode(data["signed_pre_key_signature"]),
            )
        except (KeyError, ValueError, TypeError):
            _LOG.debug(
                "PreKeyResponse from %s: malformed bundle — dropped", packet.source_uhid
            )
            return False

        self._received[uhid] = bundle
        request_id = _try_uuid(data.get("request_id")) or UUID(int=0)
        if self.on_bundle_received:
            self.on_bundle_received(
                PreKeyBundleReceived(
                    request_id=request_id,
                    from_uhid=packet.source_uhid,
                    bundle=bundle,
                )
            )
        return True

    def get_received_bundle(self, uhid: str) -> Optional[PreKeyBundle]:
        """The most recently received bundle for ``uhid``, or ``None``."""
        return self._received.get(uhid)


def _encode_pre_key_request_payload(request_id: UUID, requester_uhid: str) -> bytes:
    """Serialize a PreKeyRequest wire payload to canonical, byte-identical UTF-8 JSON.

    Field order ``request_id``, ``requester_uhid``, no whitespace, UUID lowercase-dashed
    (36 chars). Matches the C# ``PreKeyRequestPayload`` serialization and the
    fixtures/prekey byte-identity vectors.
    """
    return json.dumps(
        {
            "request_id": str(request_id),
            "requester_uhid": requester_uhid,
        },
        separators=(",", ":"),
    ).encode("utf-8")


def _encode_pre_key_response_payload(request_id: UUID, bundle: PreKeyBundle) -> bytes:
    """Serialize a PreKeyResponse wire payload to canonical, byte-identical UTF-8 JSON.

    Field order ``request_id``, ``uhid``, ``identity_key``, ``identity_key_x25519``,
    ``pre_key_id``, ``pre_key``, ``signed_pre_key_id``, ``signed_pre_key``,
    ``signed_pre_key_signature``. No whitespace, UUID lowercase-dashed, integer ids bare,
    and every byte field STANDARD base64 (RFC 4648, ``+/`` alphabet, ``=`` padding).
    Matches the C# ``PreKeyResponsePayload`` serialization and the fixtures/prekey vectors.
    """
    return json.dumps(
        {
            "request_id": str(request_id),
            "uhid": bundle.uhid,
            "identity_key": base64.b64encode(bundle.identity_key).decode(),
            "identity_key_x25519": base64.b64encode(bundle.identity_key_x25519).decode(),
            "pre_key_id": bundle.pre_key_id,
            "pre_key": base64.b64encode(bundle.pre_key).decode(),
            "signed_pre_key_id": bundle.signed_pre_key_id,
            "signed_pre_key": base64.b64encode(bundle.signed_pre_key).decode(),
            "signed_pre_key_signature": base64.b64encode(
                bundle.signed_pre_key_signature
            ).decode(),
        },
        separators=(",", ":"),
    ).encode("utf-8")


def _try_uuid(value: object) -> Optional[UUID]:
    if isinstance(value, UUID):
        return value
    if isinstance(value, str):
        try:
            return UUID(value)
        except ValueError:
            return None
    return None
