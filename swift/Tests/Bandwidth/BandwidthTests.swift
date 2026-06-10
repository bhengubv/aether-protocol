// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

// MARK: - BandwidthModels tests

final class BandwidthModelsTests: XCTestCase {

    // MARK: BandwidthConfidence ordering

    func testConfidenceOrdering() {
        XCTAssertLessThan(BandwidthConfidence.none,   .low)
        XCTAssertLessThan(BandwidthConfidence.low,    .medium)
        XCTAssertLessThan(BandwidthConfidence.medium, .high)
    }

    // MARK: BandwidthSample computed properties

    func testRtoClampedToMinimum() {
        // srtt = 1 ms, rttVar = 0 → raw = 1ms + 0 = 1ms < 200ms floor → clamped to 200ms
        let sample = makeSample(srtt: 0.001, rttVar: 0.0)
        XCTAssertEqual(sample.rto, 0.200, accuracy: 0.001)
    }

    func testRtoClampedToMaximum() {
        // srtt = 100 s → clamped to 60 s
        let sample = makeSample(srtt: 100.0, rttVar: 0.0)
        XCTAssertEqual(sample.rto, 60.0, accuracy: 0.001)
    }

    func testRtoMidRange() {
        // srtt = 100 ms, rttVar = 25 ms → raw = 0.100 + max(0.001, 4×0.025) = 0.200 s
        let sample = makeSample(srtt: 0.100, rttVar: 0.025)
        XCTAssertEqual(sample.rto, 0.200, accuracy: 0.001)
    }

    func testEffectiveBpsUsesPhyCap() {
        let sample = makeSample(btlBwBps: 10_000_000, phyCapBps: 2_000_000)
        XCTAssertEqual(sample.effectiveBps, 2_000_000)
    }

    func testEffectiveBpsIgnoresZeroPhyCap() {
        let sample = makeSample(btlBwBps: 10_000_000, phyCapBps: 0)
        XCTAssertEqual(sample.effectiveBps, 10_000_000)
    }

    // MARK: BandwidthProbeAck

    func testProbeAckRtt() {
        // SenderSend=1000, ReceiverReceive=1500, ReceiverSend=1600, SenderReceive=2200
        // RTT = (2200-1000) - (1600-1500) = 1200 - 100 = 1100 µs = 0.0011 s
        let ack = BandwidthProbeAck(
            sequence: 1,
            senderSendUs: 1_000,
            receiverReceiveUs: 1_500,
            receiverSendUs: 1_600,
            senderReceiveUs: 2_200,
            probeBytes: 512
        )
        XCTAssertEqual(ack.rtt, 0.0011, accuracy: 1e-9)
    }

    func testProbeAckForwardOwd() {
        // ForwardOWD = ReceiverReceive - SenderSend = 1500 - 1000 = 500 µs = 0.0005 s
        let ack = BandwidthProbeAck(
            sequence: 1,
            senderSendUs: 1_000,
            receiverReceiveUs: 1_500,
            receiverSendUs: 1_600,
            senderReceiveUs: 2_200,
            probeBytes: 512
        )
        XCTAssertEqual(ack.forwardOwd, 0.0005, accuracy: 1e-9)
    }

    // MARK: NodeActivitySnapshot helpers

    func testTotalBps() {
        let snap = makeNodeSnapshot(ingressBps: 100, egressBps: 200)
        XCTAssertEqual(snap.totalBps, 300)
    }

    func testHasActivityWhenActive() {
        XCTAssertTrue(makeNodeSnapshot(state: .active).hasActivity)
        XCTAssertTrue(makeNodeSnapshot(state: .busy).hasActivity)
        XCTAssertTrue(makeNodeSnapshot(state: .degraded).hasActivity)
    }

    func testNoActivityWhenIdleOrOffline() {
        XCTAssertFalse(makeNodeSnapshot(state: .idle).hasActivity)
        XCTAssertFalse(makeNodeSnapshot(state: .offline).hasActivity)
    }

    func testUtilizationPercent() {
        let snap = TransportActivitySnapshot(
            transportName: "BLE",
            isAvailable: true,
            ingressBps: 0,
            egressBps: 1_000_000,
            srtt: 0.010,
            btlBwBps: 2_000_000,
            utilizationFraction: 0.5,
            state: .busy,
            confidence: .medium
        )
        XCTAssertEqual(snap.utilizationPercent, "50 %")
    }

    // MARK: - Helpers

    private func makeSample(
        btlBwBps: Int64 = 1_000_000,
        srtt: TimeInterval = 0.020,
        rttVar: TimeInterval = 0.005,
        phyCapBps: Int64 = 0,
        lossRate: Double = 0.0
    ) -> BandwidthSample {
        BandwidthSample(
            transportName: "TestTransport",
            btlBwBps:      btlBwBps,
            availableBps:  btlBwBps,
            bdpBytes:      0,
            srtt:          srtt,
            rttVar:        rttVar,
            rtProp:        srtt,
            lossRate:      lossRate,
            phyCapBps:     phyCapBps,
            confidence:    .none,
            measuredAt:    Date()
        )
    }

    private func makeNodeSnapshot(
        state: NodeActivityState = .active,
        ingressBps: Int64 = 0,
        egressBps: Int64 = 0
    ) -> NodeActivitySnapshot {
        NodeActivitySnapshot(
            state:                state,
            ingressBps:           ingressBps,
            egressBps:            egressBps,
            activePeers:          0,
            activeTransports:     0,
            transports:           [],
            primaryTransportName: nil,
            timestamp:            Date()
        )
    }
}

// MARK: - BandwidthEstimator tests

final class BandwidthEstimatorTests: XCTestCase {

    // MARK: Initial state

    func testInitialConfidenceIsNone() async {
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        let sample = await est.currentSample
        XCTAssertEqual(sample.confidence, .none)
    }

    func testInitialTransportName() async {
        let est = BandwidthEstimator(transportName: "NearLink", maxBandwidthBps: 1_000_000)
        let sample = await est.currentSample
        XCTAssertEqual(sample.transportName, "NearLink")
    }

    // MARK: recordDelivery

    func testRecordDeliveryRaisesConfidence() async {
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        // Feed 5 delivery samples to reach Low confidence.
        for i in 0..<5 {
            let base: Int64 = 1_000_000
            await est.recordDelivery(
                bytes: 10_000,
                sendUs: base + Int64(i) * 100_000,
                deliverUs: base + Int64(i) * 100_000 + 50_000
            )
        }
        let sample = await est.currentSample
        XCTAssertGreaterThanOrEqual(sample.confidence, .low)
    }

    func testRecordDeliveryIgnoredWhenInvalidTimestamps() async {
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        // deliverUs <= sendUs → should be ignored
        await est.recordDelivery(bytes: 1000, sendUs: 1_000_000, deliverUs: 999_999)
        let sample = await est.currentSample
        XCTAssertEqual(sample.confidence, .none)
    }

    // MARK: recordLoss

    func testRecordLossIncreasesLossRate() async {
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        // Seed with a delivery first so we have non-zero state.
        await est.recordDelivery(bytes: 10_000, sendUs: 1_000_000, deliverUs: 1_050_000)
        await est.recordLoss(bytes: 1_000)
        let sample = await est.currentSample
        XCTAssertGreaterThan(sample.lossRate, 0.0)
    }

    // MARK: recordProbeResult

    func testRecordProbeResultUpdatesRtt() async {
        let est = BandwidthEstimator(transportName: "Wi-Fi Direct", maxBandwidthBps: 100_000_000)
        let ack = BandwidthProbeAck(
            sequence: 1,
            senderSendUs:      1_000_000,
            receiverReceiveUs: 1_010_000,
            receiverSendUs:    1_010_100,
            senderReceiveUs:   1_020_000,
            probeBytes:        1024
        )
        await est.recordProbeResult(ack, localReceiveUs: 1_020_000)
        let sample = await est.currentSample
        // srtt should be around the RTT = (1020000-1000000)-(1010100-1010000) = 19900µs ≈ 0.02 s
        XCTAssertGreaterThan(sample.srtt, 0)
        XCTAssertLessThan(sample.srtt, 1.0)
    }

    func testRecordProbeResultRejectsNegativeRtt() async {
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        // Make RTT go negative by swapping sender timestamps.
        let ack = BandwidthProbeAck(
            sequence: 1,
            senderSendUs:      2_000_000,
            receiverReceiveUs: 1_000_000,
            receiverSendUs:    1_000_100,
            senderReceiveUs:   1_500_000,
            probeBytes:        256
        )
        await est.recordProbeResult(ack, localReceiveUs: 1_500_000)
        let sample = await est.currentSample
        // Should remain .none — bad ack was rejected.
        XCTAssertEqual(sample.confidence, .none)
    }

    // MARK: warmFromGossip

    func testWarmFromGossipSeedsEstimate() async {
        let est = BandwidthEstimator(transportName: "NearLink", maxBandwidthBps: 5_000_000)
        await est.warmFromGossip(btlBwBps: 3_000_000, rtProp: 0.010, confidence: .medium)
        let sample = await est.currentSample
        // Warmed, so confidence should be at least .low
        XCTAssertGreaterThanOrEqual(sample.confidence, .low)
    }

    func testWarmFromGossipDoesNotDowngrade() async {
        let est = BandwidthEstimator(transportName: "NearLink", maxBandwidthBps: 5_000_000)
        // Seed 20 rounds → high confidence
        for i in 0..<20 {
            await est.recordDelivery(
                bytes: 50_000,
                sendUs: Int64(i) * 200_000,
                deliverUs: Int64(i) * 200_000 + 20_000
            )
        }
        let before = await est.currentSample
        // Gossip attempt should be ignored (probeRounds > 0).
        await est.warmFromGossip(btlBwBps: 100, rtProp: 0.500, confidence: .none)
        let after = await est.currentSample
        XCTAssertGreaterThanOrEqual(after.confidence, before.confidence)
        XCTAssertGreaterThan(after.btlBwBps, 100)
    }

    // MARK: applyPhyHint

    func testApplyPhyHintCapsEstimate() async {
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 600_000_000)
        // -85 dBm → BLE cap = 500 kbps
        await est.applyPhyHint(rssiDbm: -85)
        let sample = await est.currentSample
        XCTAssertEqual(sample.phyCapBps, 500_000)
    }

    func testPhyHintStrongSignalHighCap() async {
        let est = BandwidthEstimator(transportName: "Wi-Fi", maxBandwidthBps: 600_000_000)
        await est.applyPhyHint(rssiDbm: -50)
        let sample = await est.currentSample
        XCTAssertEqual(sample.phyCapBps, 600_000_000)
    }

    // MARK: onSampleImproved callback

    func testSampleImprovedCallbackFiredOnFirstDelivery() async throws {
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)

        let expectation = XCTestExpectation(description: "onSampleImproved fired")
        await est.onSampleImproved.append({ _ in
            expectation.fulfill()
        })

        await est.recordDelivery(bytes: 10_000, sendUs: 0, deliverUs: 50_000)

        await fulfillment(of: [expectation], timeout: 2.0)
    }

    // MARK: Confidence progression

    func testConfidenceProgrammeProgresses() async {
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)

        // < 5 probes → low
        for i in 0..<3 {
            await est.recordDelivery(
                bytes: 1000,
                sendUs: Int64(i) * 100_000,
                deliverUs: Int64(i) * 100_000 + 10_000
            )
        }
        var sample = await est.currentSample
        XCTAssertEqual(sample.confidence, .low)

        // 5–19 probes → medium
        for i in 3..<20 {
            await est.recordDelivery(
                bytes: 1000,
                sendUs: Int64(i) * 100_000,
                deliverUs: Int64(i) * 100_000 + 10_000
            )
        }
        sample = await est.currentSample
        XCTAssertEqual(sample.confidence, .medium)

        // ≥ 20 probes → high
        for i in 20..<25 {
            await est.recordDelivery(
                bytes: 1000,
                sendUs: Int64(i) * 100_000,
                deliverUs: Int64(i) * 100_000 + 10_000
            )
        }
        sample = await est.currentSample
        XCTAssertEqual(sample.confidence, .high)
    }
}

// MARK: - BandwidthDirector tests

final class BandwidthDirectorTests: XCTestCase {

    func testGetEstimateReturnsNilBeforeRegistration() async {
        let director = BandwidthDirector()
        let sample = await director.getEstimate(peerUhid: "peer1", transport: "BLE")
        XCTAssertNil(sample)
    }

    func testRecommendTransportFallsBackToLowestPowerCost() async {
        let director = BandwidthDirector()
        let ble = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        let relay = BandwidthEstimator(transportName: "HTTP Relay", maxBandwidthBps: 50_000_000)
        await director.register(ble)
        await director.register(relay)

        // No measurements for any peer → should return the lowest-power-cost transport.
        let recommended = await director.recommendTransport(peerUhid: "peer1", payloadBytes: 1024)
        // BLE has power cost 2, HTTP Relay has 10 → expect BLE
        XCTAssertEqual(recommended, "BLE")
    }

    func testGetEstimatesReturnsEmptyForUnknownPeer() async {
        let director = BandwidthDirector()
        let estimates = await director.getEstimates(peerUhid: "unknown-peer")
        XCTAssertTrue(estimates.isEmpty)
    }

    func testBuildGossipPayloadReturnsNilWithNoConfidence() async {
        let director = BandwidthDirector()
        let ble = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        await director.register(ble)

        // No probes → confidence = .none → gossip payload should be nil
        let payload = await director.buildGossipPayload(peerUhid: "peer1", transport: "BLE")
        XCTAssertNil(payload)
    }

    func testBuildGossipPayloadSucceedsAfterProbes() async {
        let director = BandwidthDirector()
        let ble = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        await director.register(ble)

        // Feed enough probes to get at least .low confidence.
        for i in 0..<5 {
            await ble.recordDelivery(
                bytes: 10_000,
                sendUs: Int64(i) * 100_000,
                deliverUs: Int64(i) * 100_000 + 40_000
            )
        }

        let payload = await director.buildGossipPayload(peerUhid: "peer1", transport: "BLE")
        XCTAssertNotNil(payload)
        XCTAssertEqual(payload?.transportName, "BLE")
        XCTAssertEqual(payload?.peerUhid, "peer1")
        XCTAssertGreaterThan(payload?.btlBwBps ?? 0, 0)
    }

    func testApplyGossipSeedsMatrix() async {
        let director = BandwidthDirector()
        let ble = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        await director.register(ble)

        let payload = BandwidthGossipPayload(
            peerUhid:      "peer2",
            transportName: "BLE",
            btlBwBps:      1_500_000,
            rtPropUs:      15_000,  // 15 ms
            confidence:    .low,
            measuredAt:    Date()
        )
        await director.applyGossip(payload)

        let sample = await director.getEstimate(peerUhid: "peer2", transport: "BLE")
        XCTAssertNotNil(sample)
    }

    func testApplyGossipForUnknownTransportIsNoOp() async {
        let director = BandwidthDirector()
        let payload = BandwidthGossipPayload(
            peerUhid:      "peer3",
            transportName: "NearLink",  // not registered
            btlBwBps:      5_000_000,
            rtPropUs:      5_000,
            confidence:    .medium,
            measuredAt:    Date()
        )
        // Should not crash.
        await director.applyGossip(payload)
        let sample = await director.getEstimate(peerUhid: "peer3", transport: "NearLink")
        XCTAssertNil(sample)
    }
}

// MARK: - NodeActivityMonitor tests

final class NodeActivityMonitorTests: XCTestCase {

    func testInitialStateIsOffline() async {
        let monitor = NodeActivityMonitor()
        let snap = await monitor.current
        XCTAssertEqual(snap.state, .offline)
    }

    func testRegisterTransportAndRecordIngress() async {
        let monitor = NodeActivityMonitor()
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        await monitor.register(name: "BLE", estimator: est)
        await monitor.recordIngress(transport: "BLE", bytes: 1024)
        // Recording should not crash; snapshot is updated on next tick.
    }

    func testRecordEgressDoesNotCrash() async {
        let monitor = NodeActivityMonitor()
        let est = BandwidthEstimator(transportName: "NearLink", maxBandwidthBps: 5_000_000)
        await monitor.register(name: "NearLink", estimator: est)
        await monitor.recordEgress(transport: "NearLink", bytes: 2048)
    }

    func testRecordEgressWithPeersCountsActivePeers() async throws {
        let monitor = NodeActivityMonitor()
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        await monitor.register(name: "BLE", estimator: est)

        // Two distinct peers within the idle window.
        await monitor.recordEgress(transport: "BLE", peerUhid: "peer-a", bytes: 1000)
        await monitor.recordEgress(transport: "BLE", peerUhid: "peer-b", bytes: 1000)

        await monitor.start()
        defer { Task { await monitor.stop() } }

        // Poll until a tick publishes the peer count (or time out).
        var activePeers = 0
        for _ in 0..<30 {
            try await Task.sleep(nanoseconds: 100_000_000)
            activePeers = await monitor.current.activePeers
            if activePeers >= 2 { break }
        }
        XCTAssertGreaterThanOrEqual(activePeers, 2)
        await monitor.stop()
    }

    func testRecordEgressWithoutPeerLeavesActivePeersZero() async throws {
        let monitor = NodeActivityMonitor()
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        await monitor.register(name: "BLE", estimator: est)

        // Transport-only egress must NOT count toward activePeers.
        await monitor.recordEgress(transport: "BLE", bytes: 5000)

        await monitor.start()
        defer { Task { await monitor.stop() } }

        // Let several ticks elapse, then confirm activePeers never rose above 0.
        try await Task.sleep(nanoseconds: 700_000_000)
        let activePeers = await monitor.current.activePeers
        XCTAssertEqual(activePeers, 0)
        await monitor.stop()
    }

    func testStartAndStopDoNotCrash() async {
        let monitor = NodeActivityMonitor()
        await monitor.start()
        await monitor.stop()
    }

    func testSubscribeReceivesSnapshot() async throws {
        let monitor = NodeActivityMonitor()
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        await monitor.register(name: "BLE", estimator: est)
        await monitor.recordEgress(transport: "BLE", bytes: 5000)

        let expectation = XCTestExpectation(description: "Received snapshot")

        let unsubscribe = await monitor.subscribe { snap in
            if snap.transports.count > 0 { expectation.fulfill() }
        }

        await monitor.start()
        await fulfillment(of: [expectation], timeout: 3.0)
        await monitor.stop()
        unsubscribe()
    }

    func testSubscribeUnsubscribeStopsCallbacks() async throws {
        let monitor = NodeActivityMonitor()
        let est = BandwidthEstimator(transportName: "BLE", maxBandwidthBps: 2_000_000)
        await monitor.register(name: "BLE", estimator: est)

        var callCount = 0
        let unsubscribe = await monitor.subscribe { _ in callCount += 1 }
        unsubscribe()

        await monitor.start()
        // Give the monitor a chance to tick.
        try await Task.sleep(nanoseconds: 700_000_000)
        await monitor.stop()

        // callCount may be 0 or 1 (race between unsub and first tick); must not be > 1.
        XCTAssertLessThanOrEqual(callCount, 1)
    }

    func testSampleIntervalClampedToMinimum() async {
        let monitor = NodeActivityMonitor()
        await { monitor.sampleIntervalMs = 10 }()
        let val = await monitor.sampleIntervalMs
        XCTAssertGreaterThanOrEqual(val, 100)
    }

    func testIdleThresholdClampedToMinimum() async {
        let monitor = NodeActivityMonitor()
        await { monitor.idleThresholdSeconds = 0 }()
        let val = await monitor.idleThresholdSeconds
        XCTAssertGreaterThanOrEqual(val, 1)
    }
}

// MARK: - PacketType tests

final class BandwidthPacketTypeTests: XCTestCase {

    func testBandwidthProbeRawValue() {
        XCTAssertEqual(PacketType.bandwidthProbe.rawValue, 53)
    }

    func testBandwidthAckRawValue() {
        XCTAssertEqual(PacketType.bandwidthAck.rawValue, 54)
    }

    func testBandwidthGossipRawValue() {
        XCTAssertEqual(PacketType.bandwidthGossip.rawValue, 55)
    }

    func testBandwidthPacketTypesAreDistinct() {
        XCTAssertNotEqual(PacketType.bandwidthProbe, PacketType.bandwidthAck)
        XCTAssertNotEqual(PacketType.bandwidthAck, PacketType.bandwidthGossip)
        XCTAssertNotEqual(PacketType.bandwidthProbe, PacketType.bandwidthGossip)
    }

    func testBandwidthPacketTypesDoNotCollide() {
        // Ensure raw values do not overlap with pre-existing types.
        let existingRawValues: Set<UInt8> = [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
            11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
            21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
            31, 32, 33, 34, 38, 39, 50, 51, 52
        ]
        XCTAssertFalse(existingRawValues.contains(53))
        XCTAssertFalse(existingRawValues.contains(54))
        XCTAssertFalse(existingRawValues.contains(55))
    }

    func testBandwidthPacketTypesRoundTripFromRawValue() {
        XCTAssertEqual(PacketType(rawValue: 53), .bandwidthProbe)
        XCTAssertEqual(PacketType(rawValue: 54), .bandwidthAck)
        XCTAssertEqual(PacketType(rawValue: 55), .bandwidthGossip)
    }
}
