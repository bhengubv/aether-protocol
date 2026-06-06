// SPDX-License-Identifier: MIT
// Unit tests for PerTransportMetrics, rankTransports(), and GeohashEpidemicStrategy.

import XCTest
@testable import AetherNetProtocol

// ── StubTransport ─────────────────────────────────────────────────────────────

private final class StubTransport: TransportService, @unchecked Sendable {
    let name:               String
    let isAvailable:        Bool
    let maxBandwidthBps:    Int64
    let maxRangeMeters:     Int32  = 100
    let powerCostRelative:  Int32
    let maxConcurrentPeers: Int32  = 10
    let metrics:            PerTransportMetrics?

    init(
        name:           String,
        isAvailable:    Bool  = true,
        bandwidthBps:   Int64 = 100_000,
        powerCost:      Int32 = 1,
        metrics:        PerTransportMetrics? = nil
    ) {
        self.name              = name
        self.isAvailable       = isAvailable
        self.maxBandwidthBps   = bandwidthBps
        self.powerCostRelative = powerCost
        self.metrics           = metrics
    }

    func sendAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool { true }
    func sendStreamAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool { true }
    func isConnected(peerUhid: String) -> Bool { false }
}

// ── PerTransportMetrics ───────────────────────────────────────────────────────

final class PerTransportMetricsTests: XCTestCase {

    // ── initial state ─────────────────────────────────────────────────────────

    func testInitialSampleCountIsZero() {
        XCTAssertEqual(0, PerTransportMetrics().sampleCount)
    }

    func testInitialEwmaRttMsIs200() {
        XCTAssertEqual(200.0, PerTransportMetrics().ewmaRttMs, accuracy: 1e-9)
    }

    func testInitialEwmaLossRateIs5Percent() {
        XCTAssertEqual(0.05, PerTransportMetrics().ewmaLossRate, accuracy: 1e-9)
    }

    func testInitialEwmaThroughputBpsIsZero() {
        XCTAssertEqual(0.0, PerTransportMetrics().ewmaThroughputBps, accuracy: 1e-9)
    }

    // ── recordSample — sample count ───────────────────────────────────────────

    func testRecordSampleIncrementsSampleCount() {
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 100, success: true, bytesTransferred: 1000)
        XCTAssertEqual(1, m.sampleCount)
        m.recordSample(rttMs: 100, success: true, bytesTransferred: 1000)
        XCTAssertEqual(2, m.sampleCount)
    }

    // ── recordSample — RTT EWMA ───────────────────────────────────────────────

    func testRecordSampleUpdatesRttEwma() {
        // α×100 + (1−α)×200 = 0.2×100 + 0.8×200 = 180
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 100, success: true, bytesTransferred: 1000)
        XCTAssertEqual(180.0, m.ewmaRttMs, accuracy: 1e-9)
    }

    func testRecordSampleZeroRttSkipsRttUpdate() {
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 0, success: true, bytesTransferred: 0)
        XCTAssertEqual(200.0, m.ewmaRttMs, accuracy: 1e-9)
    }

    func testRecordSampleRttConvergesAfterManyIdenticalSamples() {
        let m = PerTransportMetrics()
        for _ in 0..<50 { m.recordSample(rttMs: 100, success: true, bytesTransferred: 1000) }
        XCTAssertLessThan(abs(m.ewmaRttMs - 100.0), 2.0)
    }

    // ── recordSample — loss rate EWMA ─────────────────────────────────────────

    func testRecordSampleFailureRaisesLossRate() {
        // α×1 + (1−α)×0.05 = 0.2 + 0.04 = 0.24
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 100, success: false, bytesTransferred: 0)
        XCTAssertEqual(0.24, m.ewmaLossRate, accuracy: 1e-9)
    }

    func testRecordSampleSuccessLowersLossRate() {
        // α×0 + (1−α)×0.05 = 0 + 0.04 = 0.04
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 100, success: true, bytesTransferred: 1000)
        XCTAssertEqual(0.04, m.ewmaLossRate, accuracy: 1e-9)
    }

    // ── recordSample — throughput EWMA ────────────────────────────────────────

    func testRecordSampleBootstrapsThroughputOnFirstSuccess() {
        // bytes=1000, rtt=100ms → tput = 1000×8×1000/100 = 80_000 bps
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 100, success: true, bytesTransferred: 1000)
        XCTAssertEqual(80_000.0, m.ewmaThroughputBps, accuracy: 0.01)
    }

    func testRecordSampleBlendsThroughputEwmaOnSecondSuccess() {
        // bootstrap 80_000; second: 160_000 → 0.2×160_000 + 0.8×80_000 = 96_000
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 100, success: true, bytesTransferred: 1000)
        m.recordSample(rttMs: 100, success: true, bytesTransferred: 2000)
        XCTAssertEqual(96_000.0, m.ewmaThroughputBps, accuracy: 0.01)
    }

    func testRecordSampleFailureDoesNotChangeThroughput() {
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 100, success: true, bytesTransferred: 1000)  // 80_000
        m.recordSample(rttMs: 100, success: false, bytesTransferred: 0)
        XCTAssertEqual(80_000.0, m.ewmaThroughputBps, accuracy: 0.01)
    }

    func testRecordSampleZeroRttDoesNotUpdateThroughput() {
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 0, success: true, bytesTransferred: 1000)
        XCTAssertEqual(0.0, m.ewmaThroughputBps, accuracy: 1e-9)
    }

    // ── compositeScore ────────────────────────────────────────────────────────

    func testCompositeScoreIsPositiveWithDefaults() {
        XCTAssertGreaterThan(PerTransportMetrics().compositeScore(maxBandwidthBps: 500_000, powerCostRelative: 1), 0.0)
    }

    func testCompositeScoreZeroPowerClampedToOne() {
        let m = PerTransportMetrics()
        XCTAssertEqual(
            m.compositeScore(maxBandwidthBps: 500_000, powerCostRelative: 0),
            m.compositeScore(maxBandwidthBps: 500_000, powerCostRelative: 1),
            accuracy: 1e-9
        )
    }

    func testCompositeScoreFormulaWithNoThroughput() {
        // effective = 500_000 × 0.1 = 50_000; score = (50_000/1)×(1-0.05)/200 = 237.5
        let m = PerTransportMetrics()
        let expected = (500_000.0 * 0.1 / 1.0) * (1.0 - 0.05) / 200.0
        XCTAssertEqual(expected, m.compositeScore(maxBandwidthBps: 500_000, powerCostRelative: 1), accuracy: 1e-9)
    }

    func testCompositeScoreHigherBandwidthYieldsHigherScore() {
        let m = PerTransportMetrics()
        XCTAssertGreaterThan(
            m.compositeScore(maxBandwidthBps: 1_000_000, powerCostRelative: 1),
            m.compositeScore(maxBandwidthBps: 100_000, powerCostRelative: 1)
        )
    }

    func testCompositeScoreHigherPowerCostYieldsLowerScore() {
        let m = PerTransportMetrics()
        XCTAssertGreaterThan(
            m.compositeScore(maxBandwidthBps: 500_000, powerCostRelative: 1),
            m.compositeScore(maxBandwidthBps: 500_000, powerCostRelative: 10)
        )
    }

    func testCompositeScoreImprovesAfterFastLosslessSamples() {
        let m = PerTransportMetrics()
        let before = m.compositeScore(maxBandwidthBps: 500_000, powerCostRelative: 1)
        for _ in 0..<20 { m.recordSample(rttMs: 10, success: true, bytesTransferred: 5000) }
        XCTAssertGreaterThan(
            m.compositeScore(maxBandwidthBps: 500_000, powerCostRelative: 1),
            before
        )
    }
}

// ── rankTransports ────────────────────────────────────────────────────────────

final class RankTransportsTests: XCTestCase {

    func testEmptyInputReturnsEmptyArray() {
        XCTAssertTrue(rankTransports([]).isEmpty)
    }

    func testUnavailableTransportIsExcluded() {
        let t = StubTransport(name: "ble", isAvailable: false)
        XCTAssertTrue(rankTransports([t]).isEmpty)
    }

    func testAllUnavailableReturnsEmpty() {
        let ts: [any TransportService] = [
            StubTransport(name: "ble",  isAvailable: false),
            StubTransport(name: "wifi", isAvailable: false),
        ]
        XCTAssertTrue(rankTransports(ts).isEmpty)
    }

    func testAvailableTransportIsIncluded() {
        let t = StubTransport(name: "ble")
        let result = rankTransports([t])
        XCTAssertEqual(1, result.count)
    }

    func testResultsSortedByScoreDescending() {
        let low  = StubTransport(name: "low",  bandwidthBps: 10_000,    powerCost: 10)
        let high = StubTransport(name: "high", bandwidthBps: 1_000_000, powerCost: 1)
        let result = rankTransports([low, high])
        XCTAssertEqual(2, result.count)
        XCTAssertGreaterThanOrEqual(result[0].score, result[1].score)
        XCTAssertEqual("high", result[0].transport.name)
    }

    func testStaticScoreEqualsBandwidthDividedByPower() {
        // power=1, bandwidth=500_000 → score = 500_000
        let t = StubTransport(name: "wifi", bandwidthBps: 500_000, powerCost: 1)
        let result = rankTransports([t])
        XCTAssertEqual(1, result.count)
        XCTAssertEqual(500_000.0 / 1.0, result[0].score, accuracy: 0.001)
    }

    func testStaticScoreClampsPowerToAtLeastOne() {
        let t = StubTransport(name: "zero-cost", bandwidthBps: 200_000, powerCost: 0)
        let result = rankTransports([t])
        XCTAssertEqual(200_000.0, result[0].score, accuracy: 0.001)
    }

    func testTransportWithLiveMetricsUsesCompositeScore() {
        let m = PerTransportMetrics()
        m.recordSample(rttMs: 50, success: true, bytesTransferred: 1000)
        let t = StubTransport(name: "ble-live", bandwidthBps: 100_000, powerCost: 2, metrics: m)
        let result = rankTransports([t])
        XCTAssertEqual(1, result.count)
        XCTAssertGreaterThan(result[0].score, 0.0)
    }

    func testOnlyAvailableFromMixedList() {
        let a: any TransportService = StubTransport(name: "avail",   isAvailable: true)
        let u: any TransportService = StubTransport(name: "unavail", isAvailable: false)
        let result = rankTransports([a, u])
        XCTAssertEqual(1, result.count)
        XCTAssertEqual("avail", result[0].transport.name)
    }
}

// ── GeohashEpidemicStrategy ───────────────────────────────────────────────────

final class GeohashEpidemicStrategyTests: XCTestCase {

    private let strategy = GeohashEpidemicStrategy()

    // ── helpers ───────────────────────────────────────────────────────────────

    private func carrier(_ uhid: String, reliability: Int = 50, geohash: String? = nil) -> PeerInfo {
        PeerInfo(uhid: uhid, reliabilityScore: reliability,
                 capabilities: NodeCapabilityBits.dtnCarrier, geohash: geohash)
    }

    private func nonCarrier(_ uhid: String) -> PeerInfo {
        PeerInfo(uhid: uhid) // capabilities defaults to 0 → no DTN carrier flag
    }

    private func bundle(
        sender: String = "alice",
        priority: Int32 = BundlePriority.normal.rawValue,
        copyCount: Int32 = 1,
        maxCopies: Int32 = 3,
        recipientGeo: String? = nil
    ) -> DtnBundle {
        DtnBundle(senderUhid: sender, recipientUhid: "bob",
                  encryptedPayload: Data(), priority: priority,
                  copyCount: copyCount, maxCopies: maxCopies,
                  recipientLastGeohash: recipientGeo)
    }

    // ── slots exhausted ───────────────────────────────────────────────────────

    func testReturnsEmptyWhenCopyCountEqualsMaxCopies() {
        let b = bundle(copyCount: 3, maxCopies: 3)
        XCTAssertTrue(strategy.selectTargets(bundle: b, peers: [carrier("p1")], localGeohash: nil).isEmpty)
    }

    func testReturnsEmptyWhenCopyCountExceedsMaxCopies() {
        let b = bundle(copyCount: 5, maxCopies: 3)
        XCTAssertTrue(strategy.selectTargets(bundle: b, peers: [carrier("p1")], localGeohash: nil).isEmpty)
    }

    // ── empty / ineligible peer lists ─────────────────────────────────────────

    func testReturnsEmptyForEmptyPeerList() {
        XCTAssertTrue(strategy.selectTargets(bundle: bundle(), peers: [], localGeohash: nil).isEmpty)
    }

    func testExcludesPeersWithoutDtnCarrierCapability() {
        XCTAssertTrue(strategy.selectTargets(bundle: bundle(), peers: [nonCarrier("nc1")], localGeohash: nil).isEmpty)
    }

    func testExcludesEmptyUhidPeers() {
        let peer = PeerInfo(uhid: "", capabilities: NodeCapabilityBits.dtnCarrier)
        XCTAssertTrue(strategy.selectTargets(bundle: bundle(), peers: [peer], localGeohash: nil).isEmpty)
    }

    func testExcludesBundleSender() {
        let senderPeer = carrier("alice") // same UHID as bundle sender
        XCTAssertTrue(strategy.selectTargets(bundle: bundle(sender: "alice"), peers: [senderPeer], localGeohash: nil).isEmpty)
    }

    func testExcludesBlockedPeer() {
        let blocked = PeerInfo(uhid: "p1", reliabilityScore: 80,
                               capabilities: NodeCapabilityBits.dtnCarrier, isBlocked: true)
        XCTAssertTrue(strategy.selectTargets(bundle: bundle(), peers: [blocked], localGeohash: nil).isEmpty)
    }

    // ── SOS floods ────────────────────────────────────────────────────────────

    func testSosBundleFloodsToAllEligibleUpToSlots() {
        let b = bundle(priority: BundlePriority.sos.rawValue, copyCount: 1, maxCopies: 6)
        let peers = (1...4).map { carrier("p\($0)") }
        let result = strategy.selectTargets(bundle: b, peers: peers, localGeohash: nil)
        XCTAssertEqual(4, result.count)
    }

    func testSosBundleRespectsSlotCap() {
        let b = bundle(priority: BundlePriority.sos.rawValue, copyCount: 4, maxCopies: 5) // 1 slot
        let peers = (1...3).map { carrier("p\($0)") }
        let result = strategy.selectTargets(bundle: b, peers: peers, localGeohash: nil)
        XCTAssertEqual(1, result.count)
    }

    // ── geohash proximity ─────────────────────────────────────────────────────

    func testPrefersPeerWithLongerGeohashPrefixMatchToRecipient() {
        // Recipient is at "gcpv"; local shares 2 chars with it; peerClose shares 4
        let b = bundle(copyCount: 1, maxCopies: 3, recipientGeo: "gcpv")
        let peerClose = carrier("close", reliability: 50, geohash: "gcpv") // 4 chars shared
        let peerFar   = carrier("far",   reliability: 50, geohash: "gcAA") // 2 chars shared
        let result = strategy.selectTargets(bundle: b, peers: [peerClose, peerFar], localGeohash: "gc00")
        XCTAssertTrue(result.contains("close"), "peer with closer geohash must be selected")
    }

    func testExcludesPeersGeographicallyFartherThanLocal() {
        // local shares 4 chars; peer shares only 1 char → excluded
        let b      = bundle(copyCount: 1, maxCopies: 3, recipientGeo: "gcpvxy")
        let farPeer = carrier("far", geohash: "gA") // 1 char shared
        let result  = strategy.selectTargets(bundle: b, peers: [farPeer], localGeohash: "gcpv")
        XCTAssertTrue(result.isEmpty, "peer farther than local should be excluded")
    }

    // ── reliability fallback ──────────────────────────────────────────────────

    func testWithoutRecipientGeohashSelectsByReliabilityDescending() {
        let b    = bundle(copyCount: 1, maxCopies: 2) // 1 slot
        let low  = carrier("low",  reliability: 20)
        let high = carrier("high", reliability: 90)
        let result = strategy.selectTargets(bundle: b, peers: [low, high], localGeohash: nil)
        XCTAssertEqual(1, result.count)
        XCTAssertEqual("high", result[0])
    }

    func testRespectsSlotCapInReliabilityFallback() {
        let b     = bundle(copyCount: 1, maxCopies: 2) // 1 slot
        let peers = (1...5).map { carrier("p\($0)") }
        let result = strategy.selectTargets(bundle: b, peers: peers, localGeohash: nil)
        XCTAssertEqual(1, result.count)
    }
}
