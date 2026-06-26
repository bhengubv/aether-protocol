# SPDX-License-Identifier: MIT
"""Proof-of-Vicinity (PoV) anti-Sybil trust service (single-node, in-memory).

Python port of AetherNet.Market.IPoVService / InMemoryPoVService. Two users meet physically; their
devices exchange a signed token over a short-range transport (BLE/NFC/NearLink). Over time a directed
trust graph maps how many distinct humans have verified a profile.

Signatures are REAL Ed25519 (Ed25519SigningService / PyNaCl) over the canonical token body
(build_signable_token_data = "SubjectUhid + TimestampTicks + Transport"). The single-node service holds
one identity key and produces both the witness and subject signatures with it; the two-party mesh
exchange (each side counter-signs with its own key) is PoVTokenExchangeService.

SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity signal — it attaches
NO value semantics and never touches any money/reward layer.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from datetime import datetime, timezone
from typing import Callable, Dict, List, Optional

from aethernet.market.pov_token import (
    PoVScore,
    PoVToken,
    PoVTransportType,
    build_signable_token_data,
    datetime_to_ticks,
)
from aethernet.security.ed25519_service import Ed25519SigningService


def _now_utc() -> datetime:
    return datetime.now(timezone.utc)


class IPoVService(ABC):
    """The Proof-of-Vicinity trust service."""

    @abstractmethod
    async def issue_token(self, witness_uhid: str, subject_uhid: str,
                          transport: PoVTransportType = PoVTransportType.Ble) -> PoVToken: ...

    @abstractmethod
    async def accept_token(self, token: PoVToken) -> None: ...

    @abstractmethod
    async def get_score(self, uhid: str) -> PoVScore: ...

    @abstractmethod
    async def verify_token(self, token: PoVToken) -> bool: ...

    @abstractmethod
    async def report_defection(self, witness_uhid: str, defector_uhid: str) -> None: ...


class InMemoryPoVService(IPoVService):
    """Single-node, in-memory IPoVService for testing / single-node scenarios."""

    def __init__(self) -> None:
        self._tokens_by_subject: Dict[str, List[PoVToken]] = {}
        self._score_overrides: Dict[str, float] = {}
        # Self-contained real Ed25519 identity; both signatures on a token it issues use this one key.
        self._private_key, self._public_key = Ed25519SigningService.generate_keypair()
        self.on_token_received: Optional[Callable[[PoVToken], None]] = None

    async def issue_token(self, witness_uhid: str, subject_uhid: str,
                          transport: PoVTransportType = PoVTransportType.Ble) -> PoVToken:
        ticks = datetime_to_ticks(_now_utc())
        signable = build_signable_token_data(subject_uhid, ticks, transport)
        # REAL Ed25519 over the canonical body; both signatures from this node's one key (single-node).
        sig = Ed25519SigningService.sign(self._private_key, signable)
        token = PoVToken(
            witness_uhid=witness_uhid,
            subject_uhid=subject_uhid,
            timestamp_ticks=ticks,
            transport_used=transport,
            witness_signature=sig,
            subject_signature=sig,
        )
        if self.on_token_received is not None:
            self.on_token_received(token)
        return token

    async def accept_token(self, token: PoVToken) -> None:
        # Record only a token that cryptographically verifies — both signatures valid + distinct parties.
        if not await self.verify_token(token):
            return
        self._tokens_by_subject.setdefault(token.subject_uhid, []).append(token)
        if self.on_token_received is not None:
            self.on_token_received(token)

    async def get_score(self, uhid: str) -> PoVScore:
        tokens = self._tokens_by_subject.get(uhid, [])
        override = self._score_overrides.get(uhid)

        if not tokens:
            # A UHID with no inbound tokens still surfaces a stored defection override.
            return PoVScore(
                uhid=uhid,
                unique_witnesses=0,
                weighted_score=override if override is not None else 0.0,
                last_updated=_now_utc(),
            )

        unique = len({t.witness_uhid for t in tokens})
        # Sigmoid-ish: w / (w + 1).
        score = unique / (unique + 1.0)
        if override is not None:
            score = override
        return PoVScore(uhid=uhid, unique_witnesses=unique, weighted_score=score, last_updated=_now_utc())

    async def verify_token(self, token: PoVToken) -> bool:
        # Structural: both parties signed, both UHIDs present, and distinct.
        if (
            token is None
            or not token.witness_signature
            or not token.subject_signature
            or not token.witness_uhid
            or not token.subject_uhid
            or token.witness_uhid == token.subject_uhid
        ):
            return False
        # Cryptographic: BOTH signatures valid over the canonical body.
        signable = token.signable_data()
        witness_valid = Ed25519SigningService.verify(self._public_key, signable, token.witness_signature)
        subject_valid = Ed25519SigningService.verify(self._public_key, signable, token.subject_signature)
        return witness_valid and subject_valid

    async def report_defection(self, witness_uhid: str, defector_uhid: str) -> None:
        score = await self.get_score(witness_uhid)
        self._score_overrides[witness_uhid] = score.weighted_score * 0.8
