# SPDX-License-Identifier: MIT
#
# On-mesh Proof-of-Vicinity token exchange — the directed, two-key witness->subject co-presence proof,
# carried over PacketType.PoVTokenExchange (43). Python port of AetherNet.Market.PoVTokenExchangeService.
# Mirrors the AetherNet handler idiom established by MeshTipService (sign payload with the identity key
# -> wrap in a signed MeshPacket -> send) and ReputationGossipService (verify the enclosing packet
# against the supplied sender public key, which also enforces freshness + nonce replay-dedup).
#
# CRYPTO: signatures are real Ed25519 over the canonical token body (build_signable_token_data =
# "SubjectUhid + TimestampTicks + Transport"), byte-identical to every other language implementation,
# so a token exchanged here interoperates on one mesh.
#
# SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity signal. It attaches
# NO value semantics and never touches any money/reward layer.

from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Callable, Dict, List, Optional, Protocol, runtime_checkable

from aethernet.market.pov_token import (
    PoVScore,
    PoVToken,
    PoVTransportType,
    datetime_to_ticks,
)
from aethernet.protocol.mesh_packet import MeshPacket, PacketType


@runtime_checkable
class MeshSender(Protocol):
    """Minimal mesh transport surface needed by PoVTokenExchangeService."""

    @property
    def local_uhid(self) -> str: ...  # noqa: E704

    def send(self, packet: MeshPacket, subject_uhid: str) -> bool: ...  # noqa: E704


@runtime_checkable
class PacketSigner(Protocol):
    """Signs and verifies the enclosing MeshPacket envelope. ``verify_packet`` MUST also enforce
    freshness and nonce replay-dedup (mirroring the C# IPacketSigningService), so a replayed or stale
    PoV exchange is rejected before any crypto on the body."""

    def sign_packet(self, packet: MeshPacket) -> MeshPacket: ...  # noqa: E704

    def verify_packet(self, packet: MeshPacket, sender_public_key: bytes) -> bool: ...  # noqa: E704


@runtime_checkable
class IdentitySigner(Protocol):
    """Signs/verifies canonical token bodies with Ed25519 identity keys."""

    def sign_data(self, data: bytes) -> bytes: ...  # noqa: E704

    def verify_signature(self, public_key: bytes, data: bytes, signature: bytes) -> bool: ...  # noqa: E704


class PoVTokenExchangeService:
    """Issues and accepts on-mesh PoV tokens over packet type 43.

    Issue path: refuse self-vouch / non-short-range -> build a witness-signed ``PoVToken`` (real Ed25519
    over the canonical body, subject signature left empty) -> serialise as snake_case JSON -> wrap in a
    signed point-to-point ``MeshPacket`` (type 43, TTL 1 — the subject is one short-range hop away) ->
    send to the subject.

    Receive path: verify the enclosing packet signature (freshness + nonce dedup) against the supplied
    sender key -> deserialise -> reject self-echo / not-addressed-to-us / missing witness signature ->
    verify the witness's Ed25519 signature over the token body -> counter-sign as the subject with the
    local identity key -> record the token (increment the witness's contribution to the local node's
    score).
    """

    def __init__(
        self,
        sender: MeshSender,
        signer: PacketSigner,
        identity: IdentitySigner,
        logger=None,
    ) -> None:
        self._sender = sender
        self._signer = signer
        self._identity = identity
        self._logger = logger
        self._lock = threading.Lock()
        # Accepted tokens indexed by subject_uhid -> the tokens vouching for that subject.
        self._tokens_by_subject: Dict[str, List[PoVToken]] = {}
        # Fires once a counter-signed token has been recorded locally.
        self.on_token_received: Optional[Callable[[PoVToken], None]] = None

    def _log(self, message: str) -> None:
        if self._logger is not None:
            self._logger.debug(message)

    def issue_token(
        self,
        subject_uhid: str,
        transport: PoVTransportType = PoVTransportType.Ble,
    ) -> Optional[PoVToken]:
        """Mint a witness-signed PoV token for ``subject_uhid`` and send it directed (TTL 1) over packet
        43. Refuses to mint over a non-short-range transport or to vouch for itself. Returns the token
        that was issued (with an empty subject signature — the subject fills it on receipt), or ``None``
        when issuance was refused."""
        if not subject_uhid:
            self._log("PoV issue skipped — empty subject UHID")
            return None

        # ANTI-REMOTE-MINTING: a vicinity proof is only meaningful over a short-range channel.
        if not transport.is_short_range():
            self._log(f"PoV issue refused — transport {transport} is not short-range")
            return None

        local_uhid = self._sender.local_uhid
        if not local_uhid:
            self._log("PoV issue skipped — local node not initialized")
            return None

        # A node cannot vouch for itself — that would be a free, unbounded self-attestation.
        if local_uhid == subject_uhid:
            self._log("PoV issue refused — witness and subject are the same node")
            return None

        timestamp_ticks = datetime_to_ticks(datetime.now(timezone.utc))

        # Witness signs the canonical token body with the node's REAL Ed25519 identity key.
        token = PoVToken(
            witness_uhid=local_uhid,
            subject_uhid=subject_uhid,
            timestamp_ticks=timestamp_ticks,
            transport_used=transport,
        )
        token.witness_signature = self._identity.sign_data(token.signable_data())
        # subject_signature is filled by the subject when it counter-signs on receipt.

        packet = MeshPacket(
            type=PacketType.PoVTokenExchange,
            source_uhid=local_uhid,
            destination_uhid=subject_uhid,  # directed — NOT a broadcast.
            ttl=1,  # co-present: the subject is one short-range hop away.
            payload=token.to_json(),
        )

        signed = self._signer.sign_packet(packet)
        sent = self._sender.send(signed, subject_uhid)

        self._log(
            f"PoV token issued: witness={local_uhid} subject={subject_uhid} "
            f"transport={transport} sent={sent}"
        )
        return token

    def handle_token_exchange(self, packet: MeshPacket, sender_public_key: bytes) -> bool:
        """Process an inbound PoV exchange packet (type 43).

        Returns ``True`` when the token was accepted, counter-signed, and recorded.
        Returns ``False`` when the packet should be silently discarded (wrong type, bad/stale/replayed
        envelope, malformed payload, self-echo, not addressed to us, missing/invalid witness signature,
        witness == subject).
        """
        if packet is None or sender_public_key is None:
            return False
        if packet.type != PacketType.PoVTokenExchange:
            self._log(f"PoV exchange: unexpected packet type {packet.type} — ignored")
            return False

        # 1. Verify the enclosing MeshPacket signature (also enforces freshness + nonce replay-dedup).
        if not self._signer.verify_packet(packet, sender_public_key):
            self._log(
                f"PoV exchange from {packet.source_uhid}: packet signature invalid/stale/replayed — dropped"
            )
            return False

        # 2. Deserialise the token body.
        try:
            token = PoVToken.from_json(packet.payload)
        except (ValueError, KeyError) as exc:
            self._log(f"PoV exchange from {packet.source_uhid}: JSON deserialization failed — dropped: {exc}")
            return False
        if not token.witness_uhid or not token.subject_uhid:
            self._log(f"PoV exchange from {packet.source_uhid}: payload missing required fields — dropped")
            return False

        # 3. The incoming token must already carry the witness's signature.
        if not token.witness_signature:
            self._log(f"PoV exchange from {token.witness_uhid}: token has no witness signature — dropped")
            return False

        local_uhid = self._sender.local_uhid

        # 4. Ignore our own token echoed back to us (witness == us).
        if local_uhid and token.witness_uhid == local_uhid:
            return False

        # 5. The token must be addressed to us — we are the subject being vouched for.
        if local_uhid and token.subject_uhid != local_uhid:
            self._log(f"PoV exchange: token subject {token.subject_uhid} is not us — ignored")
            return False

        # 6. Verify the WITNESS's Ed25519 signature over the canonical body, against the verified sender
        #    key (the witness is the packet source, so the envelope and the body share a signing key). A
        #    forged or tampered witness signature is rejected here before we counter-sign anything.
        signable = token.signable_data()
        if not self._identity.verify_signature(sender_public_key, signable, token.witness_signature):
            self._log(f"PoV exchange from {token.witness_uhid}: witness Ed25519 signature invalid — dropped")
            return False

        # 6b. A witness must not be vouching for itself — distinct parties is a hard PoV invariant.
        if token.witness_uhid == token.subject_uhid:
            self._log(f"PoV exchange from {token.witness_uhid}: witness == subject — dropped")
            return False

        # 7. Counter-sign the SAME canonical body as the subject, with our REAL Ed25519 identity key. The
        #    token now carries BOTH signatures and becomes valid.
        token.subject_signature = self._identity.sign_data(signable)

        # 8. Record it (increments the witness's contribution to OUR score) and notify.
        self._record_token(token)
        if self.on_token_received is not None:
            self.on_token_received(token)

        self._log(
            f"PoV token accepted: witness={token.witness_uhid} subject={token.subject_uhid} "
            f"transport={token.transport_used}"
        )
        return True

    def get_score(self, uhid: str) -> PoVScore:
        """Return the local PoV trust score for ``uhid``, derived from recorded tokens."""
        with self._lock:
            tokens = list(self._tokens_by_subject.get(uhid, ()))

        unique_witnesses = len({t.witness_uhid for t in tokens})
        weighted = unique_witnesses / (unique_witnesses + 1.0) if unique_witnesses > 0 else 0.0

        return PoVScore(
            uhid=uhid,
            unique_witnesses=unique_witnesses,
            weighted_score=weighted,
            last_updated=datetime.now(timezone.utc),
        )

    def accepted_subjects(self) -> List[str]:
        """The sorted list of subject UHIDs with at least one recorded token. Mainly useful for tests
        and diagnostics."""
        with self._lock:
            return sorted(self._tokens_by_subject.keys())

    def _record_token(self, token: PoVToken) -> None:
        with self._lock:
            self._tokens_by_subject.setdefault(token.subject_uhid, []).append(token)
