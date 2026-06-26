# SPDX-License-Identifier: MIT
"""Behavioural tests for the in-memory aether-market services.

Covers the marketplace (create / browse / search + the trade-escrow state
machine) and the single-node Proof-of-Vicinity service (issue / verify / accept /
score + the defection penalty).
"""
from __future__ import annotations

import asyncio
import unittest

from aethernet.market import (
    InMemoryMarketService,
    InMemoryPoVService,
    MarketCategory,
    PoVTransportType,
    TradeRole,
    TradeState,
)


class MarketServiceTests(unittest.TestCase):
    def test_marketplace_lifecycle(self) -> None:
        async def run() -> None:
            m = InMemoryMarketService()
            received = {}
            m.on_listing_received = lambda l: received.update(id=l.listing_id)

            l = await m.create_listing("seller1", "Bicycle", "Red mountain bike", 1500.0, "k3vf9z",
                                       MarketCategory.Goods)
            self.assertTrue(l.listing_id)
            self.assertEqual(received["id"], l.listing_id)

            self.assertEqual(len(await m.browse_nearby("k3vf9z", 2)), 1)
            self.assertEqual(len(await m.browse_nearby("xxxxxx", 2)), 0)
            self.assertEqual(len(await m.search("bike")), 1)
            self.assertEqual(len(await m.search("bike", MarketCategory.Services)), 0)

            e = await m.initiate_trade(l, "buyer1")
            self.assertEqual(e.state, TradeState.Initiated)
            e = await m.confirm_trade(e, TradeRole.Buyer)
            self.assertEqual(e.state, TradeState.BuyerConfirmed)
            e = await m.confirm_trade(e, TradeRole.Seller)
            self.assertEqual(e.state, TradeState.Complete)

            e2 = await m.initiate_trade(l, "buyer2")
            await m.dispute(e2, "item not as described")
            self.assertEqual(e2.state, TradeState.Disputed)

        asyncio.run(run())

    def test_pov_score_and_defection(self) -> None:
        async def run() -> None:
            p = InMemoryPoVService()

            tok = await p.issue_token("w1", "A", PoVTransportType.Ble)
            self.assertTrue(await p.verify_token(tok))
            await p.accept_token(tok)

            sc = await p.get_score("A")
            self.assertEqual(sc.unique_witnesses, 1)
            self.assertAlmostEqual(sc.weighted_score, 0.5)

            # Tampering with the body invalidates the signatures.
            tok.subject_uhid = "C"
            self.assertFalse(await p.verify_token(tok))

            # A node cannot vouch for itself.
            self_tok = await p.issue_token("x", "x", PoVTransportType.Nfc)
            self.assertFalse(await p.verify_token(self_tok))

            # Defection penalty: 0.5 -> 0.4.
            await p.report_defection("A", "victim")
            self.assertAlmostEqual((await p.get_score("A")).weighted_score, 0.4)

        asyncio.run(run())


if __name__ == "__main__":
    unittest.main()
