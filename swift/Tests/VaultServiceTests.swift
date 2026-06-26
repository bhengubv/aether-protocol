// SPDX-License-Identifier: MIT
//
// Behavioural tests for the in-memory aether-vault service: erasure-coded
// store/recover round-trip, full-health reporting, and the empty-blob edge case.
// (K-of-N shard-loss degradation is covered by the codec's own MDS tests and the
// Go/Rust/C/TS/Python service tests; the service's `shards` map is `private`.)

import XCTest
@testable import AetherNetProtocol

final class VaultServiceTests: XCTestCase {

    func testStoreRecoverRoundTripAndHealth() async throws {
        let svc = InMemoryVaultService()
        var data = [UInt8](repeating: 0, count: 3333)
        for i in 0..<data.count { data[i] = UInt8((i * 7) % 256) }

        let m = try await svc.store(data: data, label: "doc.bin")
        XCTAssertEqual(m.shardHashes.count, vaultK + vaultM)
        XCTAssertEqual(m.sizeBytes, 3333)
        XCTAssertEqual(m.contentHash.count, 64)

        let got = try await svc.recover(manifest: m)
        XCTAssertEqual(got, data)

        let h = svc.checkHealth(manifest: m)
        XCTAssertEqual(h.reachableShards, vaultK + vaultM)
        XCTAssertTrue(h.isRecoverable)
        XCTAssertGreaterThan(h.redundancyScore, 0.99)
    }

    func testEmptyBlobRoundTrips() async throws {
        let svc = InMemoryVaultService()
        let m = try await svc.store(data: [], label: "empty")
        XCTAssertEqual(m.sizeBytes, 0)
        let got = try await svc.recover(manifest: m)
        XCTAssertEqual(got.count, 0)
    }
}
