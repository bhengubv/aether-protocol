// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

private let alice = "alice-uhid"
private let bob   = "bob-uhid"

final class NodeReputationServiceTests: XCTestCase {

    // MARK: - Default score

    func test_unknownPeer_returnsOne() async {
        let svc = NodeReputationService()
        let score = await svc.reputationScore(for: "nobody")
        XCTAssertEqual(score, 1.0, accuracy: 1e-9)
    }

    // MARK: - Negative signals

    func test_rreqFlood_reducesScoreToPointNineFive() async {
        let svc = NodeReputationService()
        await svc.recordRreqFloodAttempt(uhid: alice)
        let score = await svc.reputationScore(for: alice)
        XCTAssertEqual(score, 0.95, accuracy: 1e-9)
    }

    func test_replayAttempt_reducesScoreToPointEightFive() async {
        let svc = NodeReputationService()
        await svc.recordReplayAttempt(uhid: alice)
        let score = await svc.reputationScore(for: alice)
        XCTAssertEqual(score, 0.85, accuracy: 1e-9)
    }

    func test_signatureFailure_reducesScoreToPointEight() async {
        let svc = NodeReputationService()
        await svc.recordSignatureFailure(uhid: alice)
        let score = await svc.reputationScore(for: alice)
        XCTAssertEqual(score, 0.80, accuracy: 1e-9)
    }

    func test_custodyRefusal_reducesScoreToPointNineFive() async {
        let svc = NodeReputationService()
        await svc.recordCustodyRefusal(uhid: alice)
        let score = await svc.reputationScore(for: alice)
        XCTAssertEqual(score, 0.95, accuracy: 1e-9)
    }

    func test_deliveryFailure_reducesScoreToPointNineEight() async {
        let svc = NodeReputationService()
        await svc.recordDeliveryFailure(uhid: alice)
        let score = await svc.reputationScore(for: alice)
        XCTAssertEqual(score, 0.98, accuracy: 1e-9)
    }

    // MARK: - Clamping

    func test_fiveSignatureFailures_clampToZero() async {
        let svc = NodeReputationService()
        // 5 × −0.20 = −1.0 applied to 1.0 → 0.0 (epsilon-snapped)
        for _ in 0..<5 {
            await svc.recordSignatureFailure(uhid: alice)
        }
        let score = await svc.reputationScore(for: alice)
        XCTAssertEqual(score, 0.0, accuracy: 1e-9)
    }

    func test_tenDeliverySuccesses_clampToOne() async {
        let svc = NodeReputationService()
        // 10 × +0.01 applied to 1.0 → still exactly 1.0 (epsilon-snapped)
        for _ in 0..<10 {
            await svc.recordDeliverySuccess(uhid: alice, roundTripMs: 50)
        }
        let score = await svc.reputationScore(for: alice)
        XCTAssertEqual(score, 1.0, accuracy: 1e-9)
    }

    // MARK: - No cross-contamination

    func test_signals_doNotCrossContaminatePeers() async {
        let svc = NodeReputationService()
        await svc.recordSignatureFailure(uhid: alice)
        await svc.recordSignatureFailure(uhid: alice)

        let aliceScore = await svc.reputationScore(for: alice)
        let bobScore   = await svc.reputationScore(for: bob)

        XCTAssertLessThan(aliceScore, 1.0)
        XCTAssertEqual(bobScore, 1.0, accuracy: 1e-9)
    }

    // MARK: - allScores snapshot

    func test_allScores_returnsSnapshot() async {
        let svc = NodeReputationService()
        await svc.recordRreqFloodAttempt(uhid: alice)
        await svc.recordReplayAttempt(uhid: bob)

        let snapshot = await svc.allScores()
        XCTAssertEqual(snapshot.count, 2)
        XCTAssertTrue(snapshot.keys.contains(alice))
        XCTAssertTrue(snapshot.keys.contains(bob))
        XCTAssertLessThan(snapshot[alice]!, 1.0)
        XCTAssertLessThan(snapshot[bob]!, 1.0)
    }

    // MARK: - Compound signals

    func test_compoundSignals_accumulate() async {
        let svc = NodeReputationService()
        await svc.recordRreqFloodAttempt(uhid: alice)  // −0.05 → 0.95
        await svc.recordReplayAttempt(uhid: alice)     // −0.15 → 0.80
        await svc.recordSignatureFailure(uhid: alice)  // −0.20 → 0.60

        let score = await svc.reputationScore(for: alice)
        XCTAssertEqual(score, 0.60, accuracy: 1e-9)
    }
}
