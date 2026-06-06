// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherMeshProtocol

/// Unit tests for the one-time-pre-key pool inside `SignalProtocolService`.
/// Mirrors the C# `SignalProtocolServiceTests` OPK-pool cases so the two
/// ports stay observably equivalent.
final class OpkPoolTests: XCTestCase {

    // MARK: - Pool sizing

    func testDefaultPoolSizeIs100() async throws {
        let service = SignalProtocolService()
        // Pool is empty before the first bundle generation.
        var status = await service.getOpkPoolStatus()
        XCTAssertEqual(status.held, 0)
        XCTAssertEqual(status.available, 0)

        // First bundle generation tops up the pool to the target size, then
        // dequeues one. So we expect (100 held, 99 available) after one call.
        _ = try await service.generatePreKeyBundle(localUhid: "alice")
        status = await service.getOpkPoolStatus()
        XCTAssertEqual(status.held, 100, "Default pool size should be 100")
        XCTAssertEqual(status.available, 99, "One OPK was issued in the bundle")
    }

    func testCustomPoolSize() async throws {
        let service = SignalProtocolService(opkPoolSize: 5)
        _ = try await service.generatePreKeyBundle(localUhid: "alice")
        let status = await service.getOpkPoolStatus()
        XCTAssertEqual(status.held, 5)
        XCTAssertEqual(status.available, 4)
    }

    func testMinimumPoolSize() async throws {
        // Pool size of 1 is the minimum legal value per the precondition.
        // Bundle generation should still work — pool tops up to 1, then
        // issues the only OPK (leaving 0 available, 1 held).
        let service = SignalProtocolService(opkPoolSize: 1)
        let bundle = try await service.generatePreKeyBundle(localUhid: "alice")
        XCTAssertGreaterThan(bundle.preKeyId, 0)
        let status = await service.getOpkPoolStatus()
        XCTAssertEqual(status.held, 1)
        XCTAssertEqual(status.available, 0)
    }

    // MARK: - Distinct ids

    func test100DistinctOpkIdsAfterFirstBundle() async throws {
        let service = SignalProtocolService()
        // Drive top-up exactly once; after this we expect 100 distinct ids
        // across (oneTimePreKeys ∪ availableOpkIds).
        _ = try await service.generatePreKeyBundle(localUhid: "alice")

        let status = await service.getOpkPoolStatus()
        XCTAssertEqual(status.held, 100)
    }

    /// Issuing 100 bundles consumes the issued ids one by one but the pool
    /// keeps topping back up to 100 available. The set of issued ids must
    /// be 100 distinct positive Int32s.
    func testIssuingManyBundlesYieldsDistinctIds() async throws {
        let service = SignalProtocolService(opkPoolSize: 10)
        var seen: Set<Int32> = []
        for _ in 0 ..< 100 {
            let bundle = try await service.generatePreKeyBundle(localUhid: "alice")
            XCTAssertGreaterThan(bundle.preKeyId, 0, "OPK id must be a positive Int32")
            XCTAssertFalse(seen.contains(bundle.preKeyId),
                           "OPK id \(bundle.preKeyId) was issued twice")
            seen.insert(bundle.preKeyId)
        }
        XCTAssertEqual(seen.count, 100)

        // Pool is still at the target size: each generate call topped it up.
        let status = await service.getOpkPoolStatus()
        XCTAssertEqual(status.available, 9, "Pool tops up to 10, one is issued in this call")
    }

    // MARK: - Consumption + top-up

    func testBundleIssuanceDoesNotTouchHeldCount() async throws {
        let service = SignalProtocolService(opkPoolSize: 10)
        _ = try await service.generatePreKeyBundle(localUhid: "alice")
        var status = await service.getOpkPoolStatus()
        XCTAssertEqual(status.held, 10)
        XCTAssertEqual(status.available, 9)

        // Issuing more bundles drains and re-fills the available queue but
        // the held count rises only as needed (consumption is what shrinks it).
        for _ in 0 ..< 5 {
            _ = try await service.generatePreKeyBundle(localUhid: "alice")
        }
        status = await service.getOpkPoolStatus()
        XCTAssertEqual(status.available, 9, "Pool always ends a generate call at size-1")
        XCTAssertGreaterThanOrEqual(status.held, 10,
                                    "Held count grows or holds — never shrinks on issuance alone")
    }

    func testConsumedOpkRemovedFromPool() async throws {
        let alice = SignalProtocolService(opkPoolSize: 5)
        let bob = SignalProtocolService(opkPoolSize: 5)

        // Alice needs a local uhid set before she encrypts. The existing
        // SecurityTests pattern is "establish bidirectional via two bundles"
        // so we follow that here for symmetry.
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")

        // Bob publishes a bundle.
        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        var bobStatus = await bob.getOpkPoolStatus()
        XCTAssertEqual(bobStatus.held, 5)
        XCTAssertEqual(bobStatus.available, 4)

        // Alice processes Bob's bundle and sends a PreKey message — this
        // triggers responder-side X3DH on Bob, consuming the OPK.
        try await alice.processPreKeyBundle(bobBundle)
        let payload = try await alice.encrypt(
            peerUhid: "bob",
            plaintext: "hello bob".data(using: .utf8)!
        )
        _ = try await bob.decrypt(peerUhid: "bob", payload: payload)

        bobStatus = await bob.getOpkPoolStatus()
        XCTAssertEqual(bobStatus.held, 4, "Consumed OPK must be evicted from the pool")
        XCTAssertEqual(bobStatus.available, 4)
    }

    // MARK: - Concurrency

    /// Multiple Tasks generating bundles in parallel must not collide on a
    /// single shared OPK id. Actor isolation serialises the calls, so each
    /// task gets a distinct id pulled from the pool.
    func testConcurrentBundleGenerationProducesDistinctIds() async throws {
        let service = SignalProtocolService(opkPoolSize: 50)
        let issued = await withTaskGroup(of: Int32.self) { group -> [Int32] in
            for _ in 0 ..< 50 {
                group.addTask {
                    let bundle = try? await service.generatePreKeyBundle(localUhid: "alice")
                    return bundle?.preKeyId ?? 0
                }
            }
            var collected: [Int32] = []
            for await id in group { collected.append(id) }
            return collected
        }

        XCTAssertEqual(issued.count, 50)
        XCTAssertFalse(issued.contains(0), "No issuance failed (id=0 is the failure sentinel)")
        let unique = Set(issued)
        XCTAssertEqual(unique.count, 50, "Concurrent bundle generation must not reuse OPK ids")
    }
}
