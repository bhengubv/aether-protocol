// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

// MARK: - Test doubles

/// Records every broadcast call and lets tests inspect the packets.
actor FakeGossipSender: GossipMeshSender {
    nonisolated let localUhid: String
    private(set) var broadcasts: [MeshPacket] = []

    init(localUhid: String = "aether:local:01") {
        self.localUhid = localUhid
    }

    func broadcast(packet: MeshPacket) async throws -> Int {
        broadcasts.append(packet)
        return 1
    }
}

/// Controllable packet signer whose `verifyResult` can be flipped per test.
actor FakeGossipSigner: GossipPacketSigner {
    var verifyResult = true

    func sign(packet: inout MeshPacket) async throws {
        packet.packetNonce = Data(repeating: 0, count: 8)
        packet.timestampMs = Int64(Date().timeIntervalSince1970 * 1_000)
        packet.signature   = Data(repeating: 0, count: 64)
    }

    func verify(packet: MeshPacket, senderPublicKey: [UInt8]) async throws -> Bool {
        verifyResult
    }
}

// MARK: - Helpers

/// Build a service wired up to fresh `FakeGossipSender` and `FakeGossipSigner`.
private func makeService(
    localUhid: String = "aether:local:01"
) -> (svc: ReputationGossipService, sender: FakeGossipSender, signer: FakeGossipSigner, rep: NodeReputationService) {
    let sender = FakeGossipSender(localUhid: localUhid)
    let signer = FakeGossipSigner()
    let rep    = NodeReputationService()
    let svc    = ReputationGossipService(sender: sender, signing: signer, reputation: rep)
    return (svc, sender, signer, rep)
}

/// Build a raw `MeshPacket` whose payload is the JSON-encoded `ReputationUpdatePayload`.
private func makeGossipPacket(
    reporterUhid: String,
    targetUhid: String,
    scoreDelta: Double,
    reason: String,
    ageOffsetMs: Int64 = 0        // positive = older than now
) -> MeshPacket {
    let nowMs = Int64(Date().timeIntervalSince1970 * 1_000) - ageOffsetMs
    let pl = ReputationUpdatePayload(
        reporterUhid: reporterUhid,
        targetUhid:   targetUhid,
        scoreDelta:   scoreDelta,
        timestampMs:  nowMs,
        reason:       reason
    )
    let data = (try? JSONEncoder().encode(pl)) ?? Data()
    return MeshPacket(
        type:    .reputationUpdate,
        payload: data
    )
}

// MARK: - Test suite

final class ReputationGossipServiceTests: XCTestCase {

    // MARK: 1 — broadcastReputationUpdate signs and broadcasts exactly one packet

    func testBroadcastSignsAndBroadcastsOnePacket() async throws {
        let (svc, sender, _, _) = makeService()

        try await svc.broadcastReputationUpdate(
            targetUhid: "aether:target:99",
            scoreDelta:  -0.20,
            reason:      "test"
        )

        let broadcasts = await sender.broadcasts
        XCTAssertEqual(broadcasts.count, 1,
                       "Expected exactly one broadcast packet")
        XCTAssertEqual(broadcasts[0].type, .reputationUpdate,
                       "Packet type must be .reputationUpdate")
    }

    // MARK: 2 — Payload fields match the arguments supplied to broadcastReputationUpdate

    func testBroadcastPayloadHasCorrectFields() async throws {
        let localUhid = "aether:local:01"
        let (svc, sender, _, _) = makeService(localUhid: localUhid)

        try await svc.broadcastReputationUpdate(
            targetUhid: "aether:target:42",
            scoreDelta:  -0.30,
            reason:      "bad actor"
        )

        let broadcasts = await sender.broadcasts
        let payloadData = broadcasts[0].payload
        let payload = try JSONDecoder().decode(ReputationUpdatePayload.self, from: payloadData)

        XCTAssertEqual(payload.reporterUhid, localUhid,    "reporterUhid must be sender.localUhid")
        XCTAssertEqual(payload.targetUhid,   "aether:target:42", "targetUhid must match argument")
        XCTAssertEqual(payload.scoreDelta,   -0.30, accuracy: 1e-9, "scoreDelta must match argument")
        XCTAssertEqual(payload.reason,       "bad actor",  "reason must match argument")
    }

    // MARK: 3 — scoreDelta > 1.0 is clamped to 1.0 before encoding

    func testBroadcastClampsDeltaAboveOne() async throws {
        let (svc, sender, _, _) = makeService()

        try await svc.broadcastReputationUpdate(
            targetUhid: "aether:target:01",
            scoreDelta:  5.0,
            reason:      "clamp test"
        )

        let broadcasts = await sender.broadcasts
        let payload = try JSONDecoder().decode(
            ReputationUpdatePayload.self, from: broadcasts[0].payload
        )
        XCTAssertEqual(payload.scoreDelta, 1.0, accuracy: 1e-9,
                       "scoreDelta above 1.0 must be clamped to 1.0")
    }

    // MARK: 4 — scoreDelta < -1.0 is clamped to -1.0 before encoding

    func testBroadcastClampsDeltaBelowMinusOne() async throws {
        let (svc, sender, _, _) = makeService()

        try await svc.broadcastReputationUpdate(
            targetUhid: "aether:target:01",
            scoreDelta:  -9.0,
            reason:      "clamp test"
        )

        let broadcasts = await sender.broadcasts
        let payload = try JSONDecoder().decode(
            ReputationUpdatePayload.self, from: broadcasts[0].payload
        )
        XCTAssertEqual(payload.scoreDelta, -1.0, accuracy: 1e-9,
                       "scoreDelta below -1.0 must be clamped to -1.0")
    }

    // MARK: 5 — Invalid signature → handleGossipPacket returns false

    func testHandleInvalidSignature() async throws {
        let (svc, _, signer, _) = makeService()

        // Tell the signer to reject every packet.
        await signer.setVerifyResult(false)

        let packet = makeGossipPacket(
            reporterUhid: "aether:peer:10",
            targetUhid:   "aether:peer:20",
            scoreDelta:    -0.10,
            reason:        "test"
        )

        let accepted = try await svc.handleGossipPacket(
            packet: packet,
            senderPublicKey: []
        )
        XCTAssertFalse(accepted, "Invalid signature must cause handleGossipPacket to return false")
    }

    // MARK: 6 — Wrong packet type → handleGossipPacket returns false

    func testHandleWrongPacketType() async throws {
        let (svc, _, _, _) = makeService()

        var packet = makeGossipPacket(
            reporterUhid: "aether:peer:10",
            targetUhid:   "aether:peer:20",
            scoreDelta:    -0.10,
            reason:        "test"
        )
        packet.type = .data        // wrong type

        let accepted = try await svc.handleGossipPacket(
            packet: packet,
            senderPublicKey: []
        )
        XCTAssertFalse(accepted, "Wrong packet type must cause handleGossipPacket to return false")
    }

    // MARK: 7 — Stale timestamp (> 5 min old) → handleGossipPacket returns false

    func testHandleStaleTimestamp() async throws {
        let (svc, _, _, _) = makeService()

        // 6 minutes in the past.
        let staleMs: Int64 = 6 * 60 * 1_000
        let packet = makeGossipPacket(
            reporterUhid: "aether:peer:10",
            targetUhid:   "aether:peer:20",
            scoreDelta:    -0.10,
            reason:        "test",
            ageOffsetMs:   staleMs
        )

        let accepted = try await svc.handleGossipPacket(
            packet: packet,
            senderPublicKey: []
        )
        XCTAssertFalse(accepted,
                       "Packet with timestamp older than 5 minutes must be rejected as stale")
    }

    // MARK: 8 — Empty reporterUhid → handleGossipPacket returns false

    func testHandleMissingReporterUhid() async throws {
        let (svc, _, _, _) = makeService()

        let packet = makeGossipPacket(
            reporterUhid: "",              // empty
            targetUhid:   "aether:peer:20",
            scoreDelta:    -0.10,
            reason:        "test"
        )

        let accepted = try await svc.handleGossipPacket(
            packet: packet,
            senderPublicKey: []
        )
        XCTAssertFalse(accepted,
                       "Empty reporterUhid must cause handleGossipPacket to return false")
    }

    // MARK: 9 — Reporter == local UHID (own-echo) → handleGossipPacket returns false

    func testHandleOwnGossip() async throws {
        let localUhid = "aether:local:01"
        let (svc, _, _, _) = makeService(localUhid: localUhid)

        // Reporter is the same node as the local sender.
        let packet = makeGossipPacket(
            reporterUhid: localUhid,
            targetUhid:   "aether:peer:20",
            scoreDelta:    -0.10,
            reason:        "test"
        )

        let accepted = try await svc.handleGossipPacket(
            packet: packet,
            senderPublicKey: []
        )
        XCTAssertFalse(accepted,
                       "Own-echo gossip (reporter == localUhid) must be rejected")
    }

    // MARK: 10 — Unknown reporter (score 1.0) applying −0.20 → target score 0.80

    func testHandleUnknownReporter_AppliesFullDelta() async throws {
        let (svc, _, _, rep) = makeService()

        // reporter is unknown → reputationScore defaults to 1.0
        // raw delta = -0.20; effective = -0.20 × 1.0 = -0.20
        // target starts at 1.0; 1.0 + (-0.20) = 0.80
        let packet = makeGossipPacket(
            reporterUhid: "aether:reporter:unknown",
            targetUhid:   "aether:target:A",
            scoreDelta:    -0.20,
            reason:        "sig fail"
        )

        let accepted = try await svc.handleGossipPacket(
            packet: packet,
            senderPublicKey: []
        )
        XCTAssertTrue(accepted, "Valid packet from unknown reporter must be accepted")

        let score = await rep.reputationScore(for: "aether:target:A")
        XCTAssertEqual(score, 0.80, accuracy: 1e-9,
                       "Unknown reporter (score 1.0): effective delta = -0.20, target must be 0.80")
    }

    // MARK: 11 — Degraded reporter (score 0.50) applying −0.20 → target score 0.90

    func testHandleDegradedReporter_WeightedDelta() async throws {
        let (svc, _, _, rep) = makeService()

        let reporterUhid = "aether:reporter:degraded"
        let targetUhid   = "aether:target:B"

        // Degrade the reporter: 10 × rreqFlood @ −0.05 each = −0.50 → score 0.50
        for _ in 0..<10 {
            await rep.recordRreqFloodAttempt(uhid: reporterUhid)
        }
        let reporterScore = await rep.reputationScore(for: reporterUhid)
        XCTAssertEqual(reporterScore, 0.50, accuracy: 1e-9,
                       "Pre-condition: reporter score must be 0.50 after 10 rreq floods")

        // Now process gossip: raw delta = -0.20; effective = -0.20 × 0.50 = -0.10
        // target starts at 1.0; 1.0 + (-0.10) = 0.90
        let packet = makeGossipPacket(
            reporterUhid: reporterUhid,
            targetUhid:   targetUhid,
            scoreDelta:    -0.20,
            reason:        "weighted"
        )

        let accepted = try await svc.handleGossipPacket(
            packet: packet,
            senderPublicKey: []
        )
        XCTAssertTrue(accepted, "Valid packet from degraded reporter must be accepted")

        let targetScore = await rep.reputationScore(for: targetUhid)
        XCTAssertEqual(targetScore, 0.90, accuracy: 1e-9,
                       "Degraded reporter (0.50): effective = -0.20 × 0.50 = -0.10; target must be 0.90")
    }

    // MARK: 12 — Positive delta from trusted reporter improves target score

    func testHandlePositiveDelta_ImprovesTarget() async throws {
        let (svc, _, _, rep) = makeService()

        let reporterUhid = "aether:reporter:trusted"
        let targetUhid   = "aether:target:C"

        // Degrade target: 1 sig failure → 1.0 - 0.20 = 0.80
        await rep.recordSignatureFailure(uhid: targetUhid)
        let beforeScore = await rep.reputationScore(for: targetUhid)
        XCTAssertEqual(beforeScore, 0.80, accuracy: 1e-9,
                       "Pre-condition: target score must be 0.80 after one sig failure")

        // reporter is unknown → score 1.0
        // raw delta = +0.10; effective = +0.10 × 1.0 = +0.10
        // target: 0.80 + 0.10 = 0.90
        let packet = makeGossipPacket(
            reporterUhid: reporterUhid,
            targetUhid:   targetUhid,
            scoreDelta:    0.10,
            reason:        "recovery"
        )

        let accepted = try await svc.handleGossipPacket(
            packet: packet,
            senderPublicKey: []
        )
        XCTAssertTrue(accepted, "Valid packet with positive delta must be accepted")

        let afterScore = await rep.reputationScore(for: targetUhid)
        XCTAssertEqual(afterScore, 0.90, accuracy: 1e-9,
                       "Trusted reporter (score 1.0), +0.10 delta: target must recover to 0.90")
    }
}

// MARK: - FakeGossipSigner helper

extension FakeGossipSigner {
    /// Allow tests to flip the verify result from outside the actor.
    func setVerifyResult(_ value: Bool) {
        verifyResult = value
    }
}
