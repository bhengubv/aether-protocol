// SPDX-License-Identifier: MIT
import Foundation
import XCTest
@testable import AetherNetProtocol

/// Tests for the v1.2.0 addition ``IncentiveProvider/recordCreatorTip(creatorUhid:amount:contentHash:)``
/// (Issue #61).
final class IncentiveProviderCreatorTipTests: XCTestCase {

    func test_recordCreatorTip_defaultImpl_isNoOpAndCompletes() async throws {
        let provider: any IncentiveProvider = NoopIncentiveProvider()

        // Must not throw, must return.
        try await provider.recordCreatorTip(
            creatorUhid: "creator-uhid",
            amount: Decimal(string: "5.00")!,
            contentHash: "deadbeef"
        )
    }

    func test_recordCreatorTip_customImpl_receivesArgumentsVerbatim() async throws {
        let capturer = CapturingIncentiveProvider()
        let provider: any IncentiveProvider = capturer

        try await provider.recordCreatorTip(
            creatorUhid: "creator-zulu",
            amount: Decimal(string: "12.50")!,
            contentHash: "rootHash-abc"
        )

        let tips = capturer.tips
        XCTAssertEqual(tips.count, 1)
        XCTAssertEqual(tips.first?.creatorUhid, "creator-zulu")
        XCTAssertEqual(tips.first?.amount, Decimal(string: "12.50")!)
        XCTAssertEqual(tips.first?.contentHash, "rootHash-abc")
    }

    func test_recordCreatorTip_andRelayCredit_areIndependentRecordingPaths() async throws {
        let capturer = CapturingIncentiveProvider()
        let provider: any IncentiveProvider = capturer

        try await provider.recordCreatorTip(
            creatorUhid: "author",
            amount: Decimal(string: "1.00")!,
            contentHash: "h1"
        )
        await provider.recordRelay(
            localUhid: "node-uhid",
            packet: MeshPacket(type: .data)
        )

        XCTAssertEqual(capturer.tips.count, 1)
        XCTAssertEqual(capturer.relays.count, 1)
    }
}

// MARK: - Capturing fake

/// Captures every call made through the ``IncentiveProvider`` surface so a test
/// can assert exact arguments. Thread-safe via NSLock — used in single-threaded
/// XCTest contexts but written defensively because the protocol requires Sendable.
private final class CapturingIncentiveProvider: IncentiveProvider, @unchecked Sendable {
    struct Tip: Equatable, Sendable {
        let creatorUhid: String
        let amount: Decimal
        let contentHash: String
    }
    struct Relay: Equatable, Sendable {
        let nodeUhid: String
        let packetId: UUID
    }

    private let lock = NSLock()
    private var _tips: [Tip] = []
    private var _relays: [Relay] = []

    var tips: [Tip] {
        lock.lock(); defer { lock.unlock() }
        return _tips
    }
    var relays: [Relay] {
        lock.lock(); defer { lock.unlock() }
        return _relays
    }

    func recordRelay(localUhid: String, packet: MeshPacket) async {
        lock.lock()
        _relays.append(Relay(nodeUhid: localUhid, packetId: packet.id))
        lock.unlock()
    }

    func shouldPrioritize(packet: MeshPacket) async -> Bool { false }

    func recordCreatorTip(creatorUhid: String, amount: Decimal, contentHash: String) async throws {
        lock.lock()
        _tips.append(Tip(creatorUhid: creatorUhid, amount: amount, contentHash: contentHash))
        lock.unlock()
    }
}
