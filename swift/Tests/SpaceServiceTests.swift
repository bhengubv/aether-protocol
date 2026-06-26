// SPDX-License-Identifier: MIT
//
// Behavioural test for the in-memory aether-space breadcrumb noticeboard: drop
// (TTL clamp + emergency override + received callback), geohash-prefix scan,
// creator-only delete, and prune.

import XCTest
@testable import AetherNetProtocol

final class SpaceServiceTests: XCTestCase {

    func testDropScanDeletePrune() async {
        let svc = InMemorySpaceService()
        var received = 0
        svc.onBreadcrumbReceived = { _ in received += 1 }

        let a = await svc.drop(geoHash: "k3vf9z", contentHash: "hashA", anchorUhid: "anchor1",
                               type: .notice, ttlHours: 24)
        XCTAssertEqual(a.ttlHours, 24)
        XCTAssertEqual(received, 1)

        // Emergency breadcrumbs get the fixed 720h TTL.
        let e = await svc.drop(geoHash: "k3vf9z", contentHash: "hashE", anchorUhid: "anchor1",
                               type: .emergency, ttlHours: 1)
        XCTAssertEqual(e.ttlHours, 720)

        // Scan: prefix-proximity hit vs a far cell.
        let near = await svc.scan(centerGeoHash: "k3vf9z", radiusCells: 1)
        XCTAssertEqual(near.count, 2)
        let far = await svc.scan(centerGeoHash: "xxxxxx", radiusCells: 1)
        XCTAssertEqual(far.count, 0)

        // Creator-only delete.
        let wrong = await svc.delete(a, requestorUhid: "wrong")
        XCTAssertFalse(wrong)
        let okDelete = await svc.delete(a, requestorUhid: "anchor1")
        XCTAssertTrue(okDelete)
        let after = await svc.scan(centerGeoHash: "k3vf9z", radiusCells: 1)
        XCTAssertEqual(after.count, 1)

        // Nothing is past its TTL yet.
        XCTAssertEqual(svc.pruneExpired(), 0)
    }
}
