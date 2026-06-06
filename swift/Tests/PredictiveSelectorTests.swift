// SPDX-License-Identifier: MIT
// Unit tests for PredictiveTransportSelector — Kalman RTT filter and scoring.

import XCTest
@testable import AetherMeshProtocol

// ── FakeTransport — minimal TransportService stub ─────────────────────────────

private final class FakeTransport: TransportService, @unchecked Sendable {
    let name:               String
    let isAvailable:        Bool
    let maxBandwidthBps:    Int64
    let maxRangeMeters:     Int32  = 100
    let powerCostRelative:  Int32
    let maxConcurrentPeers: Int32  = 10
    let metrics:            PerTransportMetrics?

    init(
        name:          String,
        bandwidthBps:  Int64   = 500_000,
        powerCost:     Int32   = 1,
        available:     Bool    = true
    ) {
        self.name             = name
        self.maxBandwidthBps  = bandwidthBps
        self.powerCostRelative = powerCost
        self.isAvailable      = available
        self.metrics          = PerTransportMetrics()
    }

    func sendAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool { true }
    func sendStreamAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool { true }
    func isConnected(peerUhid: String) -> Bool { false }
}

// ── Kalman filter (indirect) ──────────────────────────────────────────────────

final class KalmanFilterIndirectTests: XCTestCase {

    func testKalmanConvergesOnSteadyState() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        sel.register(t, initialRttMs: 200.0)

        for _ in 0..<50 { sel.observeMetrics(t, rttMs: 100, success: true, bytesTransferred: 1000) }

        let state = sel.kalmanState(for: t)
        XCTAssertNotNil(state)
        XCTAssertLessThan(abs(state!.rttMs - 100.0), 5.0,
            "Kalman did not converge: rttMs=\(state!.rttMs), want ~100")
    }

    func testVarianceDecreasesWithObservations() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        sel.register(t, initialRttMs: 200.0)
        let initialVar = sel.kalmanState(for: t)!.variance

        for _ in 0..<10 { sel.observeMetrics(t, rttMs: 200, success: true, bytesTransferred: 1000) }

        let afterVar = sel.kalmanState(for: t)!.variance
        XCTAssertLessThan(afterVar, initialVar,
            "posterior variance \(afterVar) should be < initial \(initialVar)")
    }

    func testDriftPositiveForRisingRtt() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        sel.register(t, initialRttMs: 100.0)

        for i in 0..<10 {
            sel.observeMetrics(t, rttMs: Double(100 + (i + 1) * 15), success: true, bytesTransferred: 1000)
        }

        let state = sel.kalmanState(for: t)
        XCTAssertNotNil(state)
        XCTAssertGreaterThan(state!.driftMs, 0.0,
            "drift \(state!.driftMs) should be positive for rising RTT")
    }
}

// ── PredictiveTransportSelector lifecycle ─────────────────────────────────────

final class PredictiveSelectorLifecycleTests: XCTestCase {

    func testRegisterAndRankFastFirst() {
        let sel  = PredictiveTransportSelector()
        let fast = FakeTransport(name: "fast", bandwidthBps: 1_000_000, powerCost: 1)
        let slow = FakeTransport(name: "slow", bandwidthBps: 10_000,    powerCost: 10)
        sel.register(fast, initialRttMs: 50.0)
        sel.register(slow, initialRttMs: 150.0)

        for _ in 0..<5 { sel.observeMetrics(fast, rttMs: 50, success: true, bytesTransferred: 1000) }

        let ranked = sel.rank(payloadBytes: 100)
        XCTAssertEqual(ranked.count, 2)
        XCTAssertEqual(ranked[0].transport.name, "fast",
            "expected 'fast' first, got '\(ranked[0].transport.name)'")
    }

    func testUnavailableTransportExcluded() {
        let sel     = PredictiveTransportSelector()
        let avail   = FakeTransport(name: "avail",   available: true)
        let unavail = FakeTransport(name: "unavail", available: false)
        sel.register(avail,   initialRttMs: 100.0)
        sel.register(unavail, initialRttMs: 100.0)

        let ranked = sel.rank()
        XCTAssertEqual(ranked.count, 1)
        XCTAssertEqual(ranked[0].transport.name, "avail")
    }

    func testUnregisterRemovesTransport() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        sel.register(t, initialRttMs: 100.0)
        sel.unregister(t)
        XCTAssertEqual(sel.rank().count, 0)
    }

    func testSelectBestNilWhenEmpty() {
        let sel = PredictiveTransportSelector()
        XCTAssertNil(sel.selectBest())
    }

    func testDuplicateRegisterIsNoOp() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        sel.register(t, initialRttMs: 100.0)
        sel.register(t, initialRttMs: 200.0)
        XCTAssertEqual(sel.rank().count, 1, "duplicate register should not double-add")
    }

    func testKalmanStateInitialValues() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        sel.register(t, initialRttMs: 123.0)

        let state = sel.kalmanState(for: t)
        XCTAssertNotNil(state)
        XCTAssertEqual(state!.rttMs,   123.0, accuracy: 1e-9)
        XCTAssertEqual(state!.driftMs,   0.0, accuracy: 1e-9)
        XCTAssertGreaterThan(state!.variance, 0.0)
    }

    func testKalmanStateUnregisteredReturnsNil() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        XCTAssertNil(sel.kalmanState(for: t))
    }

    func testRankReturnsPositiveScore() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        sel.register(t, initialRttMs: 100.0)

        let ranked = sel.rank()
        XCTAssertEqual(ranked.count, 1)
        XCTAssertGreaterThan(ranked[0].score, 0.0)
    }

    func testScoreImprovesAfterGoodObservations() {
        let sel = PredictiveTransportSelector()
        let t   = FakeTransport(name: "t")
        sel.register(t, initialRttMs: 200.0)
        let scoreBefore = sel.rank()[0].score

        for _ in 0..<10 { sel.observeMetrics(t, rttMs: 20, success: true, bytesTransferred: 5000) }

        let scoreAfter = sel.rank()[0].score
        XCTAssertGreaterThan(scoreAfter, scoreBefore,
            "score should improve after good observations (before=\(scoreBefore), after=\(scoreAfter))")
    }
}
