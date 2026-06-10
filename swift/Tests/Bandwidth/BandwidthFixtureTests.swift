// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Drives the Swift ABMF SDK through the cross-language conformance corpus at
/// `tests/cross-language/bandwidth-fixtures.json`. Every AetherNet SDK drives the
/// SAME corpus and MUST produce identical results — this is the oracle that proves
/// numeric parity across all 8 languages. Mirrors the C# reference driver in
/// `AetherNet.Core.Tests/Bandwidth/BandwidthFixtureTests.cs` case-for-case.
///
/// Integer/string/enum fields are asserted EXACTLY; floating-point fields
/// (srttMs, rttVarMs, rtPropMs, lossRate) within `toleranceAbs` of the expected.
///
/// The estimator/director are `actor`s, so every drive method is `async` and uses
/// `await` to reach `currentSample`, `applyPhyHint`, `recommendTransport`, etc.
final class BandwidthFixtureTests: XCTestCase {

    // MARK: - Corpus model

    private struct ProbeAckCase: Decodable {
        let name: String
        let senderSendUs: Int64
        let receiverReceiveUs: Int64
        let receiverSendUs: Int64
        let senderReceiveUs: Int64
        let probeBytes: Int32
        let expectRttUs: Int64
        let expectForwardOwdUs: Int64
    }

    private struct RtoCase: Decodable {
        let name: String
        let srttMs: Double
        let rttVarMs: Double
        let expectRtoMs: Double
    }

    private struct PhyCapCase: Decodable {
        let name: String
        let rssiDbm: Int
        let expectCapBps: Int64
    }

    /// One operation in an estimator drive. Field set depends on `op`, so every
    /// payload field is optional (mirrors the C# `TryGetProperty` switch).
    private struct EstimatorOp: Decodable {
        let op: String
        let bytes: Int?
        let sendUs: Int64?
        let deliverUs: Int64?
        let rssiDbm: Int?
        let btlBwBps: Int64?
        let rtPropMs: Double?
        let confidence: String?
    }

    /// Expected sample fields. All optional — a case asserts only the fields it
    /// declares (mirrors the C# `TryGetProperty` blocks).
    private struct EstimatorExpect: Decodable {
        let btlBwBps: Int64?
        let effectiveBps: Int64?
        let availableBps: Int64?
        let bdpBytes: Int64?
        let phyCapBps: Int64?
        let confidence: String?
        let srttMs: Double?
        let rttVarMs: Double?
        let rtPropMs: Double?
        let lossRate: Double?
    }

    private struct EstimatorCase: Decodable {
        let name: String
        let transport: String
        let maxBps: Int64
        let ops: [EstimatorOp]
        let expect: EstimatorExpect
    }

    private struct DirectorGossip: Decodable {
        let peerUhid: String
        let transport: String
        let btlBwBps: Int64
        let rtPropUs: Int64
        let confidence: String
    }

    private struct DirectorRecommend: Decodable {
        let peerUhid: String
        let payloadBytes: Int64
    }

    private struct DirectorCase: Decodable {
        let name: String
        let register: [String]
        let gossips: [DirectorGossip]
        let recommend: DirectorRecommend
        /// JSON null → nil (no recommendation expected).
        let expectTransport: String?
    }

    private struct Corpus: Decodable {
        let toleranceAbs: Double
        let probeAck: [ProbeAckCase]
        let rto: [RtoCase]
        let phyCap: [PhyCapCase]
        let estimator: [EstimatorCase]
        let director: [DirectorCase]
    }

    // MARK: - Fixture loader

    /// Locate `tests/cross-language/bandwidth-fixtures.json` by walking up from this
    /// source file's directory (`#file`) to the repo root — independent of CWD, the
    /// same parent-traversal idiom as the URI CrossLanguageFixtureTests.
    private func loadCorpus() throws -> Corpus {
        var url = URL(fileURLWithPath: #file).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = url
                .appendingPathComponent("tests")
                .appendingPathComponent("cross-language")
                .appendingPathComponent("bandwidth-fixtures.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                let data = try Data(contentsOf: candidate)
                return try JSONDecoder().decode(Corpus.self, from: data)
            }
            let parent = url.deletingLastPathComponent()
            if parent.path == url.path { break }
            url = parent
        }
        XCTFail("Could not locate tests/cross-language/bandwidth-fixtures.json walking up from \(#file)")
        throw CocoaError(.fileNoSuchFile)
    }

    private func parseConfidence(_ s: String) -> BandwidthConfidence {
        switch s {
        case "None":   return .none
        case "Low":    return .low
        case "Medium": return .medium
        case "High":   return .high
        default:
            XCTFail("bad confidence \(s)")
            return .none
        }
    }

    /// Round seconds → microseconds the same way the corpus was generated.
    private func toMicros(_ seconds: TimeInterval) -> Int64 {
        Int64((seconds * 1_000_000.0).rounded())
    }

    // MARK: - probeAck

    func testProbeAck_RttAndOwd_Exact() throws {
        let corpus = try loadCorpus()
        for f in corpus.probeAck {
            let ack = BandwidthProbeAck(
                sequence: 1,
                senderSendUs: f.senderSendUs,
                receiverReceiveUs: f.receiverReceiveUs,
                receiverSendUs: f.receiverSendUs,
                senderReceiveUs: f.senderReceiveUs,
                probeBytes: f.probeBytes
            )

            XCTAssertEqual(toMicros(ack.rtt), f.expectRttUs,
                "[\(f.name)] rtt µs mismatch")
            XCTAssertEqual(toMicros(ack.forwardOwd), f.expectForwardOwdUs,
                "[\(f.name)] forwardOwd µs mismatch")
        }
    }

    // MARK: - rto

    func testRto_Clamped_MatchesRfc6298() throws {
        let corpus = try loadCorpus()
        for f in corpus.rto {
            let sample = BandwidthSample(
                transportName: "T",
                btlBwBps:      1_000_000,
                availableBps:  900_000,
                bdpBytes:      1000,
                srtt:          f.srttMs / 1000.0,
                rttVar:        f.rttVarMs / 1000.0,
                rtProp:        0.010,
                lossRate:      0.0,
                phyCapBps:     0,
                confidence:    .high,
                measuredAt:    Date()
            )

            XCTAssertEqual(sample.rto * 1000.0, f.expectRtoMs, accuracy: 0.1,
                "[\(f.name)] rto ms mismatch")
        }
    }

    // MARK: - phyCap

    func testPhyCap_FromRssi_Exact() async throws {
        let corpus = try loadCorpus()
        for f in corpus.phyCap {
            let est = BandwidthEstimator(transportName: "T", maxBandwidthBps: 10_000_000_000)
            await est.applyPhyHint(rssiDbm: f.rssiDbm)
            let sample = await est.currentSample
            XCTAssertEqual(sample.phyCapBps, f.expectCapBps,
                "[\(f.name)] phyCapBps mismatch")
        }
    }

    // MARK: - estimator

    func testEstimator_DrivesToExpectedSample() async throws {
        let corpus = try loadCorpus()
        let tol = corpus.toleranceAbs

        for f in corpus.estimator {
            let est = BandwidthEstimator(transportName: f.transport, maxBandwidthBps: f.maxBps)

            for op in f.ops {
                switch op.op {
                case "delivery":
                    await est.recordDelivery(
                        bytes: op.bytes!,
                        sendUs: op.sendUs!,
                        deliverUs: op.deliverUs!
                    )
                case "loss":
                    await est.recordLoss(bytes: op.bytes!)
                case "phyHint":
                    await est.applyPhyHint(rssiDbm: op.rssiDbm!)
                case "gossip":
                    // JSON rtPropMs is milliseconds; warmFromGossip takes seconds.
                    await est.warmFromGossip(
                        btlBwBps: op.btlBwBps!,
                        rtProp: op.rtPropMs! / 1000.0,
                        confidence: parseConfidence(op.confidence!)
                    )
                default:
                    XCTFail("[\(f.name)] unknown op \(op.op)")
                }
            }

            let s = await est.currentSample
            let exp = f.expect

            // Integer / enum fields — exact.
            if let v = exp.btlBwBps     { XCTAssertEqual(s.btlBwBps, v,     "[\(f.name)] btlBwBps") }
            if let v = exp.effectiveBps { XCTAssertEqual(s.effectiveBps, v, "[\(f.name)] effectiveBps") }
            if let v = exp.availableBps { XCTAssertEqual(s.availableBps, v, "[\(f.name)] availableBps") }
            if let v = exp.bdpBytes     { XCTAssertEqual(s.bdpBytes, v,     "[\(f.name)] bdpBytes") }
            if let v = exp.phyCapBps    { XCTAssertEqual(s.phyCapBps, v,    "[\(f.name)] phyCapBps") }
            if let v = exp.confidence   { XCTAssertEqual(s.confidence, parseConfidence(v), "[\(f.name)] confidence") }

            // Float fields — tolerance (TimeInterval is seconds → compare in ms).
            if let v = exp.srttMs   { XCTAssertEqual(s.srtt * 1000.0, v,   accuracy: tol, "[\(f.name)] srttMs") }
            if let v = exp.rttVarMs { XCTAssertEqual(s.rttVar * 1000.0, v, accuracy: tol, "[\(f.name)] rttVarMs") }
            if let v = exp.rtPropMs { XCTAssertEqual(s.rtProp * 1000.0, v, accuracy: tol, "[\(f.name)] rtPropMs") }
            if let v = exp.lossRate { XCTAssertEqual(s.lossRate, v,        accuracy: tol, "[\(f.name)] lossRate") }
        }
    }

    // MARK: - director

    func testDirector_RecommendsExpectedTransport() async throws {
        let corpus = try loadCorpus()

        for f in corpus.director {
            let director = BandwidthDirector()

            // Register one estimator per declared transport. Generous maxBps so the
            // PHY default does not cap gossip-seeded values.
            for t in f.register {
                await director.register(BandwidthEstimator(transportName: t, maxBandwidthBps: 10_000_000_000))
            }

            for g in f.gossips {
                await director.applyGossip(BandwidthGossipPayload(
                    peerUhid:      g.peerUhid,
                    transportName: g.transport,
                    btlBwBps:      g.btlBwBps,
                    rtPropUs:      g.rtPropUs,
                    confidence:    parseConfidence(g.confidence),
                    measuredAt:    Date()
                ))
            }

            let result = await director.recommendTransport(
                peerUhid: f.recommend.peerUhid,
                payloadBytes: f.recommend.payloadBytes
            )

            if let expected = f.expectTransport {
                XCTAssertEqual(result, expected, "[\(f.name)] recommended transport mismatch")
            } else {
                XCTAssertNil(result, "[\(f.name)] expected no recommendation")
            }
        }
    }
}
