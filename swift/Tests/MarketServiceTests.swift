// SPDX-License-Identifier: MIT
//
// Behavioural tests for the in-memory aether-market services: the marketplace
// (create/browse/search + the trade-escrow state machine) and the single-node
// Proof-of-Vicinity service (issue/verify/accept/score + the defection penalty).

import XCTest
@testable import AetherNetProtocol

final class MarketServiceTests: XCTestCase {

    func testMarketplaceLifecycle() async {
        let m = InMemoryMarketService()
        var receivedId: String?
        m.onListingReceived = { receivedId = $0.listingId }

        let l = await m.createListing(
            sellerUhid: "seller1", title: "Bicycle", description: "Red mountain bike",
            priceZAR: 1500, geoHash: "k3vf9z", category: .goods)
        XCTAssertFalse(l.listingId.isEmpty)
        XCTAssertEqual(receivedId, l.listingId)

        let near = await m.browseNearby(centerGeoHash: "k3vf9z", radiusCells: 2)
        XCTAssertEqual(near.count, 1)
        let far = await m.browseNearby(centerGeoHash: "xxxxxx", radiusCells: 2)
        XCTAssertEqual(far.count, 0)
        let byText = await m.search(query: "bike", category: nil)
        XCTAssertEqual(byText.count, 1)
        let byWrongCat = await m.search(query: "bike", category: .services)
        XCTAssertEqual(byWrongCat.count, 0)

        var e = await m.initiateTrade(listing: l, buyerUhid: "buyer1")
        XCTAssertEqual(e.state, .initiated)
        e = await m.confirmTrade(escrow: e, role: .buyer)
        XCTAssertEqual(e.state, .buyerConfirmed)
        e = await m.confirmTrade(escrow: e, role: .seller)
        XCTAssertEqual(e.state, .complete)

        let e2 = await m.initiateTrade(listing: l, buyerUhid: "buyer2")
        let disputed = await m.dispute(escrow: e2, reason: "item not as described")
        XCTAssertEqual(disputed.state, .disputed)
    }

    func testPoVScoreAndDefection() async throws {
        let p = InMemoryPoVService()

        let tok = try await p.issueToken(witnessUhid: "w1", subjectUhid: "A", transport: .ble)
        XCTAssertTrue(p.verifyToken(tok))
        await p.acceptToken(tok)

        let sc = await p.getScore(uhid: "A")
        XCTAssertEqual(sc.uniqueWitnesses, 1)
        XCTAssertEqual(sc.weightedScore, 0.5, accuracy: 1e-9)

        // Tampering with the body invalidates the signatures.
        var bad = tok
        bad.subjectUhid = "C"
        XCTAssertFalse(p.verifyToken(bad))

        // A node cannot vouch for itself.
        let selfTok = try await p.issueToken(witnessUhid: "x", subjectUhid: "x", transport: .nfc)
        XCTAssertFalse(p.verifyToken(selfTok))

        // Defection penalty: A's score 0.5 -> 0.4.
        await p.reportDefection(witnessUhid: "A", defectorUhid: "victim")
        let after = await p.getScore(uhid: "A")
        XCTAssertEqual(after.weightedScore, 0.4, accuracy: 1e-9)
    }
}
