// SPDX-License-Identifier: MIT

package aethermesh.security

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

private const val SRC  = "src-uhid"
private const val DST  = "dst-uhid"
private const val DELTA = 1e-9

/**
 * Test options with tight windows / thresholds so tests run without wall-clock sleeps.
 *
 * - [AnomalyDetectorOptions.volumeWindowMs]      = 100 ms (roll quickly in tests)
 * - [AnomalyDetectorOptions.ewmaAlpha]           = 0.20
 * - [AnomalyDetectorOptions.volumeSpikeMultiplier] = 2.0  (spike at 2× EWMA)
 * - [AnomalyDetectorOptions.scatterThreshold]    = 3
 * - [AnomalyDetectorOptions.scatterWindowMs]     = 60_000 ms
 * - [AnomalyDetectorOptions.geohashPrefixLength] = 4
 * - [AnomalyDetectorOptions.geohashRateLimitMs]  = 0   (disabled for most geo tests)
 */
private fun testOpts() = AnomalyDetectorOptions(
    volumeWindowMs        = 100L,
    ewmaAlpha             = 0.20,
    volumeSpikeMultiplier = 2.0,
    scatterThreshold      = 3,
    scatterWindowMs       = 60_000L,
    geohashPrefixLength   = 4,
    geohashRateLimitMs    = 0L
)

private fun newPair(opts: AnomalyDetectorOptions = testOpts()): Pair<NodeReputationService, BehavioralAnomalyDetector> {
    val rep = NodeReputationService()
    return rep to BehavioralAnomalyDetector(rep, opts)
}

class BehavioralAnomalyDetectorTest {

    // ── 1. Volume — first window seeds EWMA; no penalty ─────────────────────

    @Test fun `testVolumeNoSpikeFirstWindow`() {
        val (rep, det) = newPair()
        val t0 = 0L
        // Send 10 packets in window 1
        repeat(10) { det.observePacket(SRC, DST, t0 + it) }
        // Roll the window — timestampMs is outside window 1
        det.observePacket(SRC, DST, t0 + 200L)
        // First roll only seeds EWMA; spike check is not done on the first roll
        assertEquals(1.0, rep.getReputationScore(SRC), DELTA)
    }

    // ── 2. Volume — second window at spike fires one flood signal ────────────

    @Test fun `testVolumeSpikeFires`() {
        val (rep, det) = newPair()
        val t0 = 0L
        // Window 1: 5 packets → EWMA seeded to 5.0
        repeat(5) { det.observePacket(SRC, DST, t0 + it) }
        // Roll into window 2 (t = 200 ms)
        val t1 = 200L
        // Window 2: 20 packets — 20 > 2.0 × 5.0 = 10, so spike fires on roll
        repeat(20) { det.observePacket(SRC, DST, t1 + it) }
        // Roll into window 3 — this triggers the spike check for window 2
        det.observePacket(SRC, DST, t1 + 200L)
        // One recordRreqFloodAttempt → −0.05 → 0.95
        assertEquals(0.95, rep.getReputationScore(SRC), DELTA)
    }

    // ── 3. Volume — packets within same window don't fire ───────────────────

    @Test fun `testVolumeNoSpikeSameWindow`() {
        val (rep, det) = newPair()
        val t0 = 0L
        // All within the 100 ms window — no roll, no spike check
        repeat(50) { det.observePacket(SRC, DST, t0 + it) }
        assertEquals(1.0, rep.getReputationScore(SRC), DELTA)
    }

    // ── 4. Scatter — N-1 unique destinations, no fire ───────────────────────

    @Test fun `testScatterBelowThreshold`() {
        val (rep, det) = newPair()
        val t = 1000L
        // scatterThreshold = 3 → 3 unique dests is NOT > 3, so no fire
        repeat(3) { i -> det.observePacket(SRC, "dest-$i", t) }
        assertEquals(1.0, rep.getReputationScore(SRC), DELTA)
    }

    // ── 5. Scatter — N+1 unique destinations fires ───────────────────────────

    @Test fun `testScatterAtThreshold`() {
        val (rep, det) = newPair()
        val t = 1000L
        // 4 unique destinations > scatterThreshold(3) → fires
        repeat(4) { i -> det.observePacket(SRC, "dest-$i", t) }
        // One recordRreqFloodAttempt → −0.05 → 0.95
        assertTrue(rep.getReputationScore(SRC) < 1.0, "Score must have been reduced")
    }

    // ── 6. Scatter — old entries pruned; no false fire ───────────────────────

    @Test fun `testScatterOldEntriesPruned`() {
        val (rep, det) = newPair(
            testOpts().copy(scatterWindowMs = 500L) // 500 ms scatter window
        )
        val tOld = 0L
        // Send 3 unique dests at t=0 (within threshold — no fire)
        repeat(3) { i -> det.observePacket(SRC, "old-dest-$i", tOld) }
        // At t = 1000 ms (> 500 ms window) send 2 more unique dests
        // Old entries should be pruned → only 2 unique dests in window → no fire
        val tNew = 1000L
        repeat(2) { i -> det.observePacket(SRC, "new-dest-$i", tNew) }
        assertEquals(1.0, rep.getReputationScore(SRC), DELTA)
    }

    // ── 7. Geohash — matching prefix, no fire ────────────────────────────────

    @Test fun `testGeohashMatchNoFire`() {
        val (rep, det) = newPair()
        det.observeGeohashClaim(SRC, "abcd1234", "abcd5678", timestampMs = 1000L)
        // First 4 chars ("abcd") match → no signal
        assertEquals(1.0, rep.getReputationScore(SRC), DELTA)
    }

    // ── 8. Geohash — mismatch fires sig failure ───────────────────────────────

    @Test fun `testGeohashMismatchFires`() {
        val (rep, det) = newPair()
        det.observeGeohashClaim(SRC, "abcd1234", "efgh5678", timestampMs = 1000L)
        // "abcd" ≠ "efgh" → recordSignatureFailure → −0.20 → 0.80
        assertEquals(0.80, rep.getReputationScore(SRC), DELTA)
    }

    // ── 9. Geohash — second mismatch within rate limit suppressed ────────────

    @Test fun `testGeohashRateLimit`() {
        // Use a real rate limit of 5 seconds
        val opts = testOpts().copy(geohashRateLimitMs = 5_000L)
        val (rep, det) = newPair(opts)
        val t0 = 1_000L
        det.observeGeohashClaim(SRC, "abcd1234", "efgh5678", timestampMs = t0)
        // First mismatch fires → 0.80
        assertEquals(0.80, rep.getReputationScore(SRC), DELTA)

        // Second mismatch 100 ms later — within rate limit → suppressed
        det.observeGeohashClaim(SRC, "abcd1234", "efgh5678", timestampMs = t0 + 100L)
        // Score must still be 0.80 (no second penalty)
        assertEquals(0.80, rep.getReputationScore(SRC), DELTA)
    }

    // ── 10. SPK sig failure passthrough ──────────────────────────────────────

    @Test fun `testSpkSigFailurePassthrough`() {
        val (rep, det) = newPair()
        det.observeSpkSigFailure(SRC)
        // Direct passthrough → recordSignatureFailure → −0.20 → 0.80
        assertEquals(0.80, rep.getReputationScore(SRC), DELTA)
    }

    // ── 11. Volume — steady traffic doesn't trigger spike ────────────────────

    @Test fun `testVolumeNoSpikeSmallEwma`() {
        val (rep, det) = newPair()
        val windowMs = 100L

        // Window 1: 5 packets → seeds EWMA = 5.0
        repeat(5) { det.observePacket(SRC, DST, it.toLong()) }

        // Window 2: 5 packets (same as window 1 — 5 is NOT > 2.0 × 5.0 = 10)
        repeat(5) { det.observePacket(SRC, DST, windowMs + it) }

        // Roll into window 3 — triggers spike check for window 2 → no spike
        det.observePacket(SRC, DST, windowMs * 2L)

        assertEquals(1.0, rep.getReputationScore(SRC), DELTA)
    }
}
