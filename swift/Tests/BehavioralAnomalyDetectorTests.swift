// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Build a detector with compact, test-friendly defaults.
///
/// - `volumeWindowMs = 100`   — tiny window so a single function can straddle windows
/// - `ewmaAlpha = 0.20`       — matches spec
/// - `volumeSpikeMultiplier = 2.0`
/// - `scatterThreshold = 3`   — trip after 4 unique dests
/// - `scatterWindowMs = 60_000`
/// - `geohashRateLimitMs = 0` — every mismatch fires (override per-test as needed)
private func makeDetector(
    volumeWindowMs: Int64       = 100,
    volumeSpikeMultiplier: Double = 2.0,
    ewmaAlpha: Double           = 0.20,
    scatterWindowMs: Int64      = 60_000,
    scatterThreshold: Int       = 3,
    geohashPrefixLength: Int    = 4,
    geohashRateLimitMs: Int64   = 0
) -> BehavioralAnomalyDetector {
    var o = AnomalyDetectorOptions()
    o.volumeWindowMs         = volumeWindowMs
    o.volumeSpikeMultiplier  = volumeSpikeMultiplier
    o.ewmaAlpha              = ewmaAlpha
    o.scatterWindowMs        = scatterWindowMs
    o.scatterThreshold       = scatterThreshold
    o.geohashPrefixLength    = geohashPrefixLength
    o.geohashRateLimitMs     = geohashRateLimitMs
    let rep = NodeReputationService()
    return BehavioralAnomalyDetector(reputation: rep, opts: o)
}

/// Retrieve the current reputation score for `uhid` from the detector's
/// embedded reputation service.
private func score(of uhid: String, in det: BehavioralAnomalyDetector) async -> Double {
    await det.reputation.reputationScore(for: uhid)
}

// ---------------------------------------------------------------------------
// Test suite
// ---------------------------------------------------------------------------

final class BehavioralAnomalyDetectorTests: XCTestCase {

    // MARK: 1 — First window only seeds EWMA; no penalty

    func testVolumeNoSpikeFirstWindow() async throws {
        let det = makeDetector()
        let src = "node-A"

        // Send 10 packets inside the first window (t = 0 … 90 ms, window = 100 ms).
        for i in 0..<10 {
            await det.observePacket(sourceUhid: src,
                                    destinationUhid: "dst",
                                    timestampMs: Int64(i * 9))
        }

        // Score must remain pristine — first window only seeds the baseline.
        let s = await score(of: src, in: det)
        XCTAssertEqual(s, 1.0, accuracy: 1e-9,
                       "First window must seed EWMA only, not fire a penalty")
    }

    // MARK: 2 — Second window at >2× EWMA fires one rreqFloodAttempt

    func testVolumeSpikeFires() async throws {
        let det = makeDetector(volumeWindowMs: 100, volumeSpikeMultiplier: 2.0)
        let src = "node-B"

        // Window 1 (t = 0 … 99): 5 packets — opens the window and fills it.
        // The first packet at t=0 just opens the window (windowStart=0, count=1).
        for i in 0..<5 {
            await det.observePacket(sourceUhid: src, destinationUhid: "dst",
                                    timestampMs: Int64(i * 10))       // 0,10,20,30,40
        }

        // Window 2 starts at t=100 (first rollover → seeds ewmaBaseline = 5).
        // Send 5 normal packets inside window 2 (t=100…149).
        for i in 0..<5 {
            await det.observePacket(sourceUhid: src, destinationUhid: "dst",
                                    timestampMs: 100 + Int64(i * 10)) // 100,110,120,130,140
        }

        // Window 3 starts at t=200 (second rollover — baseline=5, hasBaseline=true).
        // Send 20 packets → 20 > 2 × 5 = 10 → spike fires exactly once.
        for i in 0..<20 {
            await det.observePacket(sourceUhid: src, destinationUhid: "dst",
                                    timestampMs: 200 + Int64(i))      // 200..219
        }

        // A packet at t=300 triggers the rollover that evaluates window 3's 20-packet count.
        await det.observePacket(sourceUhid: src, destinationUhid: "dst", timestampMs: 300)

        // One RREQ-flood penalty: 1.0 − 0.05 = 0.95
        let s = await score(of: src, in: det)
        XCTAssertLessThan(s, 1.0,
                          "Volume spike (20 pkts vs EWMA 5) must fire a flood penalty")
        XCTAssertEqual(s, 1.0 - 0.05, accuracy: 1e-9,
                       "Exactly one rreqFloodAttempt penalty expected")
    }

    // MARK: 3 — Packets within the same window never fire

    func testVolumeNoSpikeSameWindow() async throws {
        let det = makeDetector(volumeWindowMs: 100_000)  // huge window
        let src = "node-C"

        for i in 0..<1_000 {
            await det.observePacket(sourceUhid: src, destinationUhid: "dst",
                                    timestampMs: Int64(i))
        }

        let s = await score(of: src, in: det)
        XCTAssertEqual(s, 1.0, accuracy: 1e-9,
                       "All packets in one window must never fire a penalty")
    }

    // MARK: 4 — N−1 unique destinations (below threshold): no penalty

    func testScatterBelowThreshold() async throws {
        // threshold = 3 → need >3 unique dests to fire; 3 unique dests is safe.
        let det = makeDetector(scatterThreshold: 3)
        let src = "node-D"
        let ts: Int64 = 1_000

        for i in 0..<3 {            // exactly 3 unique dests = at threshold, not over
            await det.observePacket(sourceUhid: src, destinationUhid: "dst-\(i)",
                                    timestampMs: ts)
        }

        let s = await score(of: src, in: det)
        XCTAssertEqual(s, 1.0, accuracy: 1e-9,
                       "3 unique destinations with threshold=3 must not fire")
    }

    // MARK: 5 — N+1 unique destinations (above threshold): fires

    func testScatterAtThreshold() async throws {
        // threshold = 3 → 4 unique dests fires
        let det = makeDetector(scatterThreshold: 3)
        let src = "node-E"
        let ts: Int64 = 2_000

        for i in 0..<4 {            // 4 unique dests > 3 → fire
            await det.observePacket(sourceUhid: src, destinationUhid: "dst-\(i)",
                                    timestampMs: ts)
        }

        let s = await score(of: src, in: det)
        XCTAssertLessThan(s, 1.0,
                          "4 unique destinations with threshold=3 must fire a flood penalty")
    }

    // MARK: 6 — Old scatter entries are pruned; stale dests don't count

    func testScatterOldEntriesPruned() async throws {
        // threshold = 5, scatterWindowMs = 60_000.
        // Send 4 unique-dest packets at t=0 (under the threshold of 5 → no fire yet).
        // Then advance time beyond the scatter window and send 1 fresh packet.
        // After pruning only 1 unique dest remains, which is well under threshold.
        let det = makeDetector(scatterWindowMs: 60_000, scatterThreshold: 5)
        let src = "node-F"

        let oldTs: Int64 = 0
        for i in 0..<4 {
            await det.observePacket(sourceUhid: src, destinationUhid: "old-dst-\(i)",
                                    timestampMs: oldTs)
        }

        // Advance time beyond the scatter window so old entries are pruned.
        let newTs: Int64 = oldTs + 60_001      // > scatterWindowMs
        await det.observePacket(sourceUhid: src, destinationUhid: "fresh-dst",
                                timestampMs: newTs)

        // Only "fresh-dst" survives after pruning → 1 unique dest → no fire.
        let s = await score(of: src, in: det)
        XCTAssertEqual(s, 1.0, accuracy: 1e-9,
                       "Entries outside scatterWindowMs must be pruned before counting")
    }

    // MARK: 7 — Matching geohash prefix: no fire

    func testGeohashMatchNoFire() async throws {
        let det = makeDetector(geohashPrefixLength: 4, geohashRateLimitMs: 0)
        let uhid = "geo-node-1"

        await det.observeGeohashClaim(uhid: uhid,
                                      claimedGeohash: "u4pr",
                                      observedRoutingGeohash: "u4pr",
                                      timestampMs: 1_000)

        let s = await score(of: uhid, in: det)
        XCTAssertEqual(s, 1.0, accuracy: 1e-9,
                       "Matching geohash prefix must not fire any penalty")
    }

    // MARK: 8 — Mismatched geohash prefix fires a signature-failure penalty

    func testGeohashMismatchFires() async throws {
        let det = makeDetector(geohashPrefixLength: 4, geohashRateLimitMs: 0)
        let uhid = "geo-node-2"

        await det.observeGeohashClaim(uhid: uhid,
                                      claimedGeohash: "u4pr",   // prefix "u4pr"
                                      observedRoutingGeohash: "zzzy",  // prefix "zzzy"
                                      timestampMs: 1_000)

        // sig failure: 1.0 − 0.20 = 0.80
        let s = await score(of: uhid, in: det)
        XCTAssertEqual(s, 0.80, accuracy: 1e-9,
                       "Geohash prefix mismatch must fire one signature-failure penalty")
    }

    // MARK: 9 — Second mismatch within rate-limit window is suppressed

    func testGeohashRateLimit() async throws {
        // Use a 60-second rate-limit.
        let det = makeDetector(geohashPrefixLength: 4, geohashRateLimitMs: 60_000)
        let uhid = "geo-node-3"

        // First mismatch at t=1000 → fires.
        await det.observeGeohashClaim(uhid: uhid,
                                      claimedGeohash: "u4pr",
                                      observedRoutingGeohash: "zzzy",
                                      timestampMs: 1_000)

        // Second mismatch at t=5000 (only 4 s later, within 60-s window) → suppressed.
        await det.observeGeohashClaim(uhid: uhid,
                                      claimedGeohash: "u4pr",
                                      observedRoutingGeohash: "zzzy",
                                      timestampMs: 5_000)

        // Only one penalty fired: 1.0 − 0.20 = 0.80
        let s = await score(of: uhid, in: det)
        XCTAssertEqual(s, 0.80, accuracy: 1e-9,
                       "Second geohash mismatch within rate-limit window must be suppressed")
    }

    // MARK: 10 — observeSpkSigFailure passes through to reputation service

    func testSpkSigFailurePassthrough() async throws {
        let det = makeDetector()
        let uhid = "spk-node-1"

        await det.observeSpkSigFailure(uhid: uhid)

        // sig failure: 1.0 − 0.20 = 0.80
        let s = await score(of: uhid, in: det)
        XCTAssertEqual(s, 0.80, accuracy: 1e-9,
                       "observeSpkSigFailure must forward a signature-failure penalty")
    }

    // MARK: 11 — EWMA convergence: no false spike on stable traffic

    func testVolumeNoSpikeSmallEwma() async throws {
        // Stable load: each window has exactly 5 packets.
        // EWMA converges to 5; multiplier = 2 → threshold = 10 > 5 → never fires.
        let det = makeDetector(volumeWindowMs: 100, volumeSpikeMultiplier: 2.0, ewmaAlpha: 0.20)
        let src = "stable-node"

        let packetsPerWindow = 5
        let numWindows       = 10

        for w in 0..<numWindows {
            for p in 0..<packetsPerWindow {
                let ts = Int64(w) * 100 + Int64(p)
                await det.observePacket(sourceUhid: src, destinationUhid: "dst",
                                        timestampMs: ts)
            }
        }

        let s = await score(of: src, in: det)
        XCTAssertEqual(s, 1.0, accuracy: 1e-9,
                       "Stable, consistent traffic must never trigger a volume-spike penalty")
    }
}
