// SPDX-License-Identifier: MIT
//
// Behavioural tests for the in-memory aether-market services: the marketplace
// (create/browse/search + the trade-escrow state machine) and the single-node
// Proof-of-Vicinity service (issue/verify/accept/score + the defection penalty).

package aethernet.market

import org.junit.jupiter.api.Test
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class MarketServiceTest {

    @Test
    fun marketplaceLifecycle() {
        val m = InMemoryMarketService()
        var received: MarketListing? = null
        m.onListingReceived = { received = it }

        val l = m.createListing("seller1", "Bicycle", "Red mountain bike", 1500.0, "k3vf9z", MarketCategory.Goods)
        assertTrue(l.listingId.isNotEmpty())
        assertEquals(l.listingId, received?.listingId)

        assertEquals(1, m.browseNearby("k3vf9z", 2).size)
        assertEquals(0, m.browseNearby("xxxxxx", 2).size)
        assertEquals(1, m.search("bike").size)
        assertEquals(0, m.search("bike", MarketCategory.Services).size)

        var e = m.initiateTrade(l, "buyer1")
        assertEquals(TradeState.Initiated, e.state)
        e = m.confirmTrade(e, TradeRole.Buyer)
        assertEquals(TradeState.BuyerConfirmed, e.state)
        e = m.confirmTrade(e, TradeRole.Seller)
        assertEquals(TradeState.Complete, e.state)

        val e2 = m.initiateTrade(l, "buyer2")
        m.dispute(e2, "item not as described")
        assertEquals(TradeState.Disputed, e2.state)
    }

    @Test
    fun povScoreAndDefection() {
        val p = InMemoryPoVService()

        val tok = p.issueToken("w1", "A", PoVTransportType.Ble)
        assertTrue(p.verifyToken(tok))
        p.acceptToken(tok)

        val sc = p.getScore("A")
        assertEquals(1, sc.uniqueWitnesses)
        assertTrue(abs(sc.weightedScore - 0.5) < 1e-9)

        // Tampering with the body invalidates the signatures.
        val bad = tok.copy(subjectUhid = "C")
        assertFalse(p.verifyToken(bad))

        // A node cannot vouch for itself.
        val self = p.issueToken("x", "x", PoVTransportType.Nfc)
        assertFalse(p.verifyToken(self))

        // Defection penalty: A's score 0.5 -> 0.4.
        p.reportDefection("A", "victim")
        assertTrue(abs(p.getScore("A").weightedScore - 0.4) < 1e-9)
    }
}
