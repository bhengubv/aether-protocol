# SPDX-License-Identifier: MIT

"""Market / Proof-of-Vicinity layer.

A Proof-of-Vicinity (PoV) token is a directed, two-key witness->subject co-presence proof carried over
``PacketType.PoVTokenExchange`` (43). Both the witness and the subject sign the canonical token body
with their real Ed25519 identity keys, so a token issued by one node verifies on any other. The
resulting ``PoVScore`` is a purely local anti-Sybil routing/identity signal — it attaches NO value
semantics and never touches any money/reward layer. Byte-identical to the C#
``AetherNet.Market.PoVTokenCodec`` / ``PoVTokenExchangeService`` and every other language port.
"""

from __future__ import annotations

from aethernet.market.pov_token import (
    PoVScore,
    PoVToken,
    PoVTransportType,
    build_signable_token_data,
    ticks_to_datetime,
    datetime_to_ticks,
)
from aethernet.market.pov_exchange_service import PoVTokenExchangeService
from aethernet.market.market_service import (
    IMarketService,
    InMemoryMarketService,
    MarketCategory,
    MarketListing,
    TradeEscrow,
    TradeRole,
    TradeState,
)
from aethernet.market.pov_service import IPoVService, InMemoryPoVService

__all__ = [
    "PoVScore",
    "PoVToken",
    "PoVTransportType",
    "build_signable_token_data",
    "ticks_to_datetime",
    "datetime_to_ticks",
    "PoVTokenExchangeService",
    "IMarketService",
    "InMemoryMarketService",
    "MarketCategory",
    "MarketListing",
    "TradeEscrow",
    "TradeRole",
    "TradeState",
    "IPoVService",
    "InMemoryPoVService",
]
