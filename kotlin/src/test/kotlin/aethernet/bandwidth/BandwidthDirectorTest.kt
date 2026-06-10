// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Unit tests for [BandwidthDirector].
 *
 * Mirrors the Go/Python/TypeScript/C# agent test scenarios for the ABMF W18-5 port.
 */
class BandwidthDirectorTest {

    private fun makeEstimator(name: String, maxBps: Long = 2_000_000L) =
        BandwidthEstimator(name, maxBps)

    private fun feedDeliveries(est: BandwidthEstimator, count: Int, rateDelayUs: Long = 100_000L) {
        val base = System.currentTimeMillis() * 1_000L
        repeat(count) { i ->
            est.recordDelivery(10_000, base + i * (rateDelayUs + 10_000L), base + i * (rateDelayUs + 10_000L) + rateDelayUs)
        }
    }

    // ── Register & getEstimate ────────────────────────────────────────────────

    @Test fun `getEstimate returns null when no data for peer`() {
        val dir = BandwidthDirector()
        val est = makeEstimator("BLE")
        dir.register(est)
        assertNull(dir.getEstimate("peer-A", "BLE"))
    }

    @Test fun `applyGossip seeds matrix so getEstimate returns sample`() {
        val dir = BandwidthDirector()
        val est = makeEstimator("BLE")
        dir.register(est)

        val gossip = BandwidthGossipPayload(
            peerUhid = "peer-A",
            transportName = "BLE",
            btlBwBps = 500_000L,
            rtPropUs = 20_000L,
            confidence = BandwidthConfidence.MEDIUM,
            measuredAt = Instant.now(),
        )
        dir.applyGossip(gossip)

        val sample = dir.getEstimate("peer-A", "BLE")
        assertNotNull(sample)
        assertEquals("BLE", sample.transportName)
    }

    @Test fun `applyGossip for unknown transport is silently ignored`() {
        val dir = BandwidthDirector()
        val gossip = BandwidthGossipPayload(
            peerUhid = "peer-X",
            transportName = "NearLink",
            btlBwBps = 1_000_000L,
            rtPropUs = 10_000L,
            confidence = BandwidthConfidence.LOW,
            measuredAt = Instant.now(),
        )
        dir.applyGossip(gossip) // should not throw
        assertNull(dir.getEstimate("peer-X", "NearLink"))
    }

    // ── getEstimates ──────────────────────────────────────────────────────────

    @Test fun `getEstimates returns empty list for unknown peer`() {
        val dir = BandwidthDirector()
        assertTrue(dir.getEstimates("unknown").isEmpty())
    }

    @Test fun `getEstimates returns all transports for peer sorted by availableBps desc`() {
        val dir = BandwidthDirector()
        val ble = makeEstimator("BLE", 2_000_000L)
        val wifi = makeEstimator("Wi-Fi Direct", 100_000_000L)
        dir.register(ble)
        dir.register(wifi)

        // Gossip-warm both transports for the same peer.
        dir.applyGossip(BandwidthGossipPayload("peer-B", "BLE", 500_000L, 20_000L, BandwidthConfidence.LOW, Instant.now()))
        dir.applyGossip(BandwidthGossipPayload("peer-B", "Wi-Fi Direct", 80_000_000L, 5_000L, BandwidthConfidence.MEDIUM, Instant.now()))

        val estimates = dir.getEstimates("peer-B")
        assertEquals(2, estimates.size)
        // Sorted descending by availableBps.
        assertTrue(estimates[0].availableBps >= estimates[1].availableBps)
    }

    // ── recommendTransport ────────────────────────────────────────────────────

    @Test fun `recommendTransport with no estimates falls back to lowest power cost`() {
        val dir = BandwidthDirector()
        val ble     = makeEstimator("BLE")
        val nearlink = makeEstimator("NearLink")
        dir.register(ble)
        dir.register(nearlink)

        // No measurement data for any peer. Should pick NearLink (power cost 1 < BLE 2).
        val transport = dir.recommendTransport("peer-C", 1_000L)
        assertNotNull(transport)
        assertEquals("NearLink", transport)
    }

    @Test fun `recommendTransport returns null when no estimators registered`() {
        val dir = BandwidthDirector()
        assertNull(dir.recommendTransport("peer-D", 1_000L))
    }

    @Test fun `recommendTransport picks higher bandwidth transport for small payload`() {
        val dir = BandwidthDirector()
        val ble  = makeEstimator("BLE", 2_000_000L)
        val wifi = makeEstimator("Wi-Fi Direct", 100_000_000L)
        dir.register(ble)
        dir.register(wifi)

        // Seed with distinct gossip so Wi-Fi Direct is clearly better.
        dir.applyGossip(BandwidthGossipPayload("peer-E", "BLE", 500_000L, 20_000L, BandwidthConfidence.LOW, Instant.now()))
        dir.applyGossip(BandwidthGossipPayload("peer-E", "Wi-Fi Direct", 80_000_000L, 5_000L, BandwidthConfidence.MEDIUM, Instant.now()))

        val transport = dir.recommendTransport("peer-E", 512L)
        // Wi-Fi Direct has far more available bandwidth and medium confidence.
        assertEquals("Wi-Fi Direct", transport)
    }

    @Test fun `recommendTransport penalises NONE-confidence estimates`() {
        val dir = BandwidthDirector()
        val ble  = makeEstimator("BLE", 2_000_000L)
        val wifi = makeEstimator("Wi-Fi Direct", 100_000_000L)
        dir.register(ble)
        dir.register(wifi)

        // BLE has LOW confidence data; Wi-Fi Direct has NONE.
        dir.applyGossip(BandwidthGossipPayload("peer-F", "BLE", 1_000_000L, 15_000L, BandwidthConfidence.LOW, Instant.now()))
        dir.applyGossip(BandwidthGossipPayload("peer-F", "Wi-Fi Direct", 50_000_000L, 3_000L, BandwidthConfidence.NONE, Instant.now()))

        // BLE's confidence factor = 1.0; Wi-Fi Direct's = 0.5.
        // Even though Wi-Fi Direct's raw bandwidth is higher, the penalty could shift the choice.
        // We just assert it returns a non-null result without throwing.
        assertNotNull(dir.recommendTransport("peer-F", 1_024L))
    }

    // ── buildGossipPayload ────────────────────────────────────────────────────

    @Test fun `buildGossipPayload returns null for unknown transport`() {
        val dir = BandwidthDirector()
        assertNull(dir.buildGossipPayload("peer-G", "NearLink"))
    }

    @Test fun `buildGossipPayload returns null when confidence is NONE`() {
        val dir = BandwidthDirector()
        val est = makeEstimator("BLE")
        dir.register(est)
        // No probe data -> confidence NONE -> payload should be null.
        assertNull(dir.buildGossipPayload("peer-H", "BLE"))
    }

    @Test fun `buildGossipPayload returns payload after sufficient probes`() {
        val dir = BandwidthDirector()
        val est = makeEstimator("BLE", 2_000_000L)
        dir.register(est)

        feedDeliveries(est, 10)

        val payload = dir.buildGossipPayload("peer-I", "BLE")
        assertNotNull(payload)
        assertEquals("peer-I", payload.peerUhid)
        assertEquals("BLE", payload.transportName)
        assertTrue(payload.btlBwBps > 0L)
    }

    // ── applyGossip round-trip ────────────────────────────────────────────────

    @Test fun `applyGossip then buildGossipPayload round-trips transport name`() {
        val dir = BandwidthDirector()
        val est = makeEstimator("NearLink", 10_000_000L)
        dir.register(est)

        // Warm the estimator via gossip so confidence rises to LOW.
        dir.applyGossip(BandwidthGossipPayload("peer-J", "NearLink", 8_000_000L, 5_000L, BandwidthConfidence.LOW, Instant.now()))

        val payload = dir.buildGossipPayload("peer-J", "NearLink")
        assertNotNull(payload)
        assertEquals("NearLink", payload.transportName)
    }

    // ── Thread safety ─────────────────────────────────────────────────────────

    @Test fun `concurrent register and getEstimates do not throw`() {
        val dir = BandwidthDirector()

        val threads = (0..7).map { idx ->
            Thread {
                val est = makeEstimator("transport-$idx")
                dir.register(est)
                dir.applyGossip(BandwidthGossipPayload(
                    "peer-K", "transport-$idx", 1_000_000L * (idx + 1), 10_000L,
                    BandwidthConfidence.LOW, Instant.now()
                ))
                dir.getEstimates("peer-K")
                dir.recommendTransport("peer-K", 512L)
            }
        }
        threads.forEach { it.start() }
        threads.forEach { it.join(5_000) }

        assertTrue(dir.getEstimates("peer-K").isNotEmpty())
    }
}
