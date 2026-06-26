// SPDX-License-Identifier: MIT
//
// Behavioural test for the in-memory aether-forge package cache: cache (with the
// new-entry announcement + idempotent first-write-wins), query hit/miss, the fetch
// download-count increment, and aggregate stats.

import XCTest
@testable import AetherNetProtocol

final class ForgeServiceTests: XCTestCase {

    func testCacheQueryFetchStats() async {
        let svc = InMemoryForgeService()
        var fired = 0
        svc.onNewEntryAnnounced = { _ in fired += 1 }

        let e = await svc.cache(packageId: "npm:react@18.2.0", contentHash: "hash1", sizeBytes: 1000)
        XCTAssertEqual(e.downloadCount, 0)
        XCTAssertEqual(fired, 1)

        // Idempotent re-cache: first write wins, no second announcement.
        let e2 = await svc.cache(packageId: "npm:react@18.2.0", contentHash: "hash2", sizeBytes: 9999)
        XCTAssertEqual(e2.contentHash, "hash1")
        XCTAssertEqual(fired, 1)

        // Query hit + miss.
        let q = await svc.query(packageId: "npm:react@18.2.0")
        XCTAssertEqual(q?.contentHash, "hash1")
        let miss = await svc.query(packageId: "missing")
        XCTAssertNil(miss)

        // Fetch increments the download counter; miss returns nil.
        let f1 = await svc.fetch(packageId: "npm:react@18.2.0")
        XCTAssertEqual(f1?.downloadCount, 1)
        _ = await svc.fetch(packageId: "npm:react@18.2.0")
        let fmiss = await svc.fetch(packageId: "missing")
        XCTAssertNil(fmiss)

        // Stats: bytes-saved = downloads * size; one entry catalogued.
        let st = await svc.getStats()
        XCTAssertEqual(st.catalogueSize, 1)
        XCTAssertEqual(st.totalBytesSaved, 2000) // 2 downloads * 1000 bytes
    }
}
