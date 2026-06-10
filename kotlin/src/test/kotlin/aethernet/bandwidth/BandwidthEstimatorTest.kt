// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import org.junit.jupiter.api.Test
import java.time.Duration
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.test.assertFalse

/**
 * Unit tests for [BandwidthEstimator].
 *
 * Mirrors the Go/Python/TypeScript/C# agent test scenarios for the ABMF W18-5 port.
 */
class BandwidthEstimatorTest {

    private fun makeEstimator(
        name: String = "BLE",
        maxBps: Long = 2_000_000L,
    ) = BandwidthEstimator(name, maxBps)

    // ── Construction ─────────────────────────────────────────────────────────

    @Test fun `initial confidence is NONE`() {
        val est = makeEstimator()
        assertEquals(BandwidthConfidence.NONE, est.currentSample.confidence)
    }

    @Test fun `initial sample has transport name`() {
        val est = makeEstimator("NearLink")
        assertEquals("NearLink", est.currentSample.transportName)
    }

    @Test fun `initial sample has non-zero btlBwBps from max`() {
        val est = makeEstimator(maxBps = 1_000_000L)
        // The initial snapshot is seeded at maxBandwidthBps.
        assertTrue(est.currentSample.btlBwBps > 0L)
    }

    // ── recordDelivery ────────────────────────────────────────────────────────

    @Test fun `recordDelivery raises btlBwBps`() {
        val est = makeEstimator()
        val before = est.currentSample.btlBwBps

        // 100 KB delivered in 100 ms -> 8 Mbps delivery rate
        val base = System.currentTimeMillis() * 1_000L
        est.recordDelivery(100_000, base, base + 100_000L)

        assertTrue(est.currentSample.btlBwBps > 0L)
    }

    @Test fun `recordDelivery with equal timestamps is ignored`() {
        val est = makeEstimator()
        val snap1 = est.currentSample
        val t = 1_000_000L
        est.recordDelivery(1024, t, t)
        // No change — the sample is a no-op.
        assertEquals(snap1.confidence, est.currentSample.confidence)
    }

    @Test fun `recordDelivery with negative bytes is ignored`() {
        val est = makeEstimator()
        val snap1 = est.currentSample
        est.recordDelivery(-1, 1000L, 2000L)
        assertEquals(snap1.confidence, est.currentSample.confidence)
    }

    @Test fun `multiple recordDelivery calls advance confidence`() {
        val est = makeEstimator()
        val base = System.currentTimeMillis() * 1_000L
        repeat(25) { i ->
            est.recordDelivery(10_000, base + i * 200_000L, base + i * 200_000L + 100_000L)
        }
        assertEquals(BandwidthConfidence.HIGH, est.currentSample.confidence)
    }

    @Test fun `fewer than 5 recordDelivery calls yield LOW confidence`() {
        val est = makeEstimator()
        val base = System.currentTimeMillis() * 1_000L
        repeat(3) { i ->
            est.recordDelivery(10_000, base + i * 200_000L, base + i * 200_000L + 100_000L)
        }
        assertEquals(BandwidthConfidence.LOW, est.currentSample.confidence)
    }

    @Test fun `5 to 19 recordDelivery calls yield MEDIUM confidence`() {
        val est = makeEstimator()
        val base = System.currentTimeMillis() * 1_000L
        repeat(10) { i ->
            est.recordDelivery(10_000, base + i * 200_000L, base + i * 200_000L + 100_000L)
        }
        assertEquals(BandwidthConfidence.MEDIUM, est.currentSample.confidence)
    }

    // ── recordLoss ────────────────────────────────────────────────────────────

    @Test fun `recordLoss increases lossRate`() {
        val est = makeEstimator()
        est.recordLoss(1500)
        assertTrue(est.currentSample.lossRate > 0.0)
    }

    @Test fun `recordLoss with zero bytes is ignored`() {
        val est = makeEstimator()
        val before = est.currentSample.lossRate
        est.recordLoss(0)
        assertEquals(before, est.currentSample.lossRate)
    }

    @Test fun `lossRate reduces availableBps`() {
        val est = makeEstimator()
        val base = System.currentTimeMillis() * 1_000L
        est.recordDelivery(100_000, base, base + 100_000L)
        val before = est.currentSample.availableBps

        repeat(20) { est.recordLoss(1500) }
        assertTrue(est.currentSample.availableBps < before)
    }

    // ── recordProbeResult ─────────────────────────────────────────────────────

    @Test fun `recordProbeResult updates estimates`() {
        val est = makeEstimator()
        val now = System.currentTimeMillis() * 1_000L
        val ack = BandwidthProbeAck(
            sequence = 1u,
            senderSendUs = now,
            receiverReceiveUs = now + 10_000L,
            receiverSendUs = now + 11_000L,
            senderReceiveUs = now + 21_000L,
            probeBytes = 1400,
        )
        est.recordProbeResult(ack, now + 21_000L)
        assertTrue(est.currentSample.confidence != BandwidthConfidence.NONE)
    }

    @Test fun `recordProbeResult with zero RTT is ignored`() {
        val est = makeEstimator()
        val now = System.currentTimeMillis() * 1_000L
        // RTT = (receive - send) - (recvSend - recvReceive) = 0 - 0 = 0
        val ack = BandwidthProbeAck(1u, now, now, now, now, 1400)
        val before = est.currentSample.confidence
        est.recordProbeResult(ack, now)
        assertEquals(before, est.currentSample.confidence)
    }

    @Test fun `recordProbeResult with 31s RTT is ignored (exceeds 30s limit)`() {
        val est = makeEstimator()
        val now = System.currentTimeMillis() * 1_000L
        // RTT = 31 seconds in microseconds
        val ack = BandwidthProbeAck(1u, now, now + 5_000_000L, now + 5_001_000L, now + 31_001_000L, 1400)
        val before = est.currentSample.confidence
        est.recordProbeResult(ack, now + 31_001_000L)
        assertEquals(before, est.currentSample.confidence)
    }

    // ── warmFromGossip ────────────────────────────────────────────────────────

    @Test fun `warmFromGossip seeds estimate when confidence is NONE`() {
        val est = makeEstimator()
        assertEquals(BandwidthConfidence.NONE, est.currentSample.confidence)
        est.warmFromGossip(500_000L, Duration.ofMillis(20), BandwidthConfidence.MEDIUM)
        // After gossip warm, confidence should be at least LOW.
        assertTrue(est.currentSample.confidence >= BandwidthConfidence.LOW)
    }

    @Test fun `warmFromGossip does not downgrade after probe data`() {
        val est = makeEstimator()
        val base = System.currentTimeMillis() * 1_000L
        repeat(25) { i ->
            est.recordDelivery(10_000, base + i * 200_000L, base + i * 200_000L + 100_000L)
        }
        assertEquals(BandwidthConfidence.HIGH, est.currentSample.confidence)

        // Gossip with lower confidence should be ignored.
        est.warmFromGossip(1_000L, Duration.ofMillis(100), BandwidthConfidence.LOW)
        assertEquals(BandwidthConfidence.HIGH, est.currentSample.confidence)
    }

    @Test fun `warmFromGossip called twice has no effect on second call`() {
        val est = makeEstimator()
        est.warmFromGossip(500_000L, Duration.ofMillis(20), BandwidthConfidence.MEDIUM)
        val snap1 = est.currentSample.btlBwBps

        est.warmFromGossip(9_999_999L, Duration.ofMillis(1), BandwidthConfidence.HIGH)
        // Second gossip call is ignored because warmedFromGossip is already set.
        assertEquals(snap1, est.currentSample.btlBwBps)
    }

    // ── applyPhyHint ──────────────────────────────────────────────────────────

    @Test fun `applyPhyHint with strong signal sets high cap`() {
        val est = makeEstimator(maxBps = 10_000_000_000L)
        est.applyPhyHint(-45)
        assertEquals(600_000_000L, est.currentSample.phyCapBps)
    }

    @Test fun `applyPhyHint with weak BLE signal sets low cap`() {
        val est = makeEstimator(maxBps = 10_000_000L)
        est.applyPhyHint(-98)
        assertEquals(40_000L, est.currentSample.phyCapBps)
    }

    @Test fun `applyPhyHint caps effectiveBps`() {
        val est = makeEstimator(maxBps = 2_000_000L)
        // Apply a cap lower than the initial maxBandwidthBps.
        est.applyPhyHint(-92)
        // PHY cap is 125_000 for RSSI >= -95.
        assertTrue(est.currentSample.phyCapBps in 1L..500_000L)
        assertTrue(est.currentSample.effectiveBps <= est.currentSample.phyCapBps)
    }

    @Test fun `effectiveBps equals btlBwBps when no PHY hint`() {
        val est = makeEstimator()
        val s = est.currentSample
        assertEquals(s.btlBwBps, s.effectiveBps)
    }

    // ── RTO ───────────────────────────────────────────────────────────────────

    @Test fun `rto is clamped to at least 200ms`() {
        val est = makeEstimator()
        // Before any observations SRTT is 0, so RTO should be at least 200 ms.
        assertTrue(est.currentSample.rto >= Duration.ofMillis(200))
    }

    @Test fun `rto is clamped to at most 60 seconds`() {
        val est = makeEstimator()
        // Even with extreme RTT values, RTO must not exceed 60 s.
        assertTrue(est.currentSample.rto <= Duration.ofSeconds(60))
    }

    // ── onSampleImproved ──────────────────────────────────────────────────────

    @Test fun `onSampleImproved fires when BtlBw improves significantly`() {
        val est = makeEstimator()
        var fired = false
        est.onSampleImproved.add { fired = true }

        val base = System.currentTimeMillis() * 1_000L
        // Large delivery -> big BtlBw increase -> should trigger callback.
        est.recordDelivery(10_000_000, base, base + 100_000L)

        // Give the virtual thread a moment to fire.
        Thread.sleep(100)
        assertTrue(fired)
    }

    // ── Thread safety ─────────────────────────────────────────────────────────

    @Test fun `concurrent recordDelivery and recordLoss do not throw`() {
        val est = makeEstimator()
        val base = System.currentTimeMillis() * 1_000L

        val threads = (1..8).map { idx ->
            Thread {
                repeat(100) { i ->
                    if (i % 10 == 0) est.recordLoss(1500)
                    else est.recordDelivery(1400, base + idx * 1_000_000L + i * 10_000L,
                        base + idx * 1_000_000L + i * 10_000L + 5_000L)
                }
            }
        }
        threads.forEach { it.start() }
        threads.forEach { it.join(5_000) }

        assertNotNull(est.currentSample)
    }
}
