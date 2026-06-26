# SPDX-License-Identifier: MIT
"""Offline-capable P2P marketplace (aether-market Phase-2 extension).

Python port of AetherNet.Market.IMarketService / InMemoryMarketService and the listing/escrow models.
Listings are geo-pinned (distributed via aether-space) and may carry a VaultManifest escrow for
document-backed sales; trades run a two-party confirm state machine. Requires aether-space and
aether-vault.
"""
from __future__ import annotations

import uuid
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Callable, Dict, List, Optional

from aethernet.market.pov_token import PoVScore
from aethernet.vault.service import VaultManifest


def _now_utc() -> datetime:
    return datetime.now(timezone.utc)


class MarketCategory(IntEnum):
    """Category of a :class:`MarketListing`."""

    Goods = 0
    Services = 1
    Labour = 2
    Land = 3
    Documents = 4


class TradeRole(IntEnum):
    """Role of the node confirming a trade step."""

    Buyer = 0
    Seller = 1


class TradeState(IntEnum):
    """State machine for a :class:`TradeEscrow`."""

    Initiated = 0
    BuyerConfirmed = 1
    SellerConfirmed = 2
    Complete = 3
    Disputed = 4


@dataclass
class MarketListing:
    """A geo-pinned market listing dropped by a verified seller. May include a VaultManifest escrow for
    document-backed sales (land deeds, certificates)."""

    listing_id: str = field(default_factory=lambda: str(uuid.uuid4()))
    seller_uhid: str = ""
    seller_pov_score: PoVScore = field(default_factory=PoVScore)
    title: str = ""
    description: str = ""
    price_zar: float = 0.0  # South African Rand
    geohash: str = ""  # 6-char geohash of the listing location
    category: MarketCategory = MarketCategory.Goods
    escrow_manifest: Optional[VaultManifest] = None  # optional Vault escrow
    created_at_utc: datetime = field(default_factory=_now_utc)
    expires_at_utc: datetime = field(default_factory=lambda: _now_utc() + timedelta(days=30))

    @property
    def is_expired(self) -> bool:
        return _now_utc() >= self.expires_at_utc


@dataclass
class TradeEscrow:
    """Tracks the lifecycle of a marketplace trade."""

    escrow_id: str = field(default_factory=lambda: str(uuid.uuid4()))
    listing_id: str = ""
    buyer_uhid: str = ""
    seller_uhid: str = ""
    state: TradeState = TradeState.Initiated
    vault_manifest: Optional[VaultManifest] = None
    created_at_utc: datetime = field(default_factory=_now_utc)


class IMarketService(ABC):
    """The offline-capable P2P marketplace."""

    @abstractmethod
    async def create_listing(self, seller_uhid: str, title: str, description: str, price_zar: float,
                             geohash: str, category: MarketCategory) -> MarketListing: ...

    @abstractmethod
    async def browse_nearby(self, center_geohash: str, radius_cells: int = 2) -> List[MarketListing]: ...

    @abstractmethod
    async def search(self, query: str, category: Optional[MarketCategory] = None) -> List[MarketListing]: ...

    @abstractmethod
    async def initiate_trade(self, listing: MarketListing, buyer_uhid: str) -> TradeEscrow: ...

    @abstractmethod
    async def confirm_trade(self, escrow: TradeEscrow, role: TradeRole) -> TradeEscrow: ...

    @abstractmethod
    async def dispute(self, escrow: TradeEscrow, reason: str) -> None: ...


class InMemoryMarketService(IMarketService):
    """In-memory IMarketService for testing / single-node use; state lost on restart."""

    def __init__(self) -> None:
        self._listings: Dict[str, MarketListing] = {}
        self._escrows: Dict[str, TradeEscrow] = {}
        self.on_listing_received: Optional[Callable[[MarketListing], None]] = None

    async def create_listing(self, seller_uhid: str, title: str, description: str, price_zar: float,
                             geohash: str, category: MarketCategory) -> MarketListing:
        now = _now_utc()
        listing = MarketListing(
            seller_uhid=seller_uhid,
            title=title,
            description=description,
            price_zar=price_zar,
            geohash=geohash,
            category=category,
            created_at_utc=now,
            expires_at_utc=now + timedelta(days=30),
        )
        self._listings[listing.listing_id] = listing
        if self.on_listing_received is not None:
            self.on_listing_received(listing)
        return listing

    async def browse_nearby(self, center_geohash: str, radius_cells: int = 2) -> List[MarketListing]:
        prefix_len = max(1, len(center_geohash) - radius_cells + 1)
        prefix = center_geohash[: min(prefix_len, len(center_geohash))].lower()
        return [
            l for l in self._listings.values()
            if not l.is_expired and l.geohash.lower().startswith(prefix)
        ]

    async def search(self, query: str, category: Optional[MarketCategory] = None) -> List[MarketListing]:
        q = query.lower()
        results: List[MarketListing] = []
        for l in self._listings.values():
            if l.is_expired:
                continue
            if category is not None and l.category != category:
                continue
            if q in l.title.lower() or q in l.description.lower():
                results.append(l)
        return results

    async def initiate_trade(self, listing: MarketListing, buyer_uhid: str) -> TradeEscrow:
        escrow = TradeEscrow(
            listing_id=listing.listing_id,
            buyer_uhid=buyer_uhid,
            seller_uhid=listing.seller_uhid,
            state=TradeState.Initiated,
            vault_manifest=listing.escrow_manifest,
        )
        self._escrows[escrow.escrow_id] = escrow
        return escrow

    async def confirm_trade(self, escrow: TradeEscrow, role: TradeRole) -> TradeEscrow:
        if role == TradeRole.Buyer:
            escrow.state = TradeState.BuyerConfirmed
        else:
            escrow.state = (
                TradeState.Complete if escrow.state == TradeState.BuyerConfirmed
                else TradeState.SellerConfirmed
            )
        self._escrows[escrow.escrow_id] = escrow
        return escrow

    async def dispute(self, escrow: TradeEscrow, reason: str) -> None:
        escrow.state = TradeState.Disputed
        self._escrows[escrow.escrow_id] = escrow
