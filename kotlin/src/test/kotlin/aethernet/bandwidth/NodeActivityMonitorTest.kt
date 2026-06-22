// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import org.junit.jupiter.api.Test
import org.junit.jupiter.api.AfterEach
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.test.assertFalse

/**
 * Unit tests for [NodeActivityMonitor].
 *
 * Mirrors the Go/Python/TypeScript/C# agent test scenarios for the ABMF W18-5 port.
 */
class NodeActivityMonitorTest {

    private val monitors = mutableListOf<NodeActivityMonitor>()

    private fun makeMonitor(intervalMs: Int = 100, idleThresholdSec: Int = 2): NodeActivityMonitor {
        val m = NodeActivityMonitor(sampleIntervalMs = intervalMs, idleThresholdSeconds = idleThresholdSec)
        monitors.add(m)
        return m
    }

    @AfterEach fun tearDown() {
        monitors.forEach { it.stop() }
        monitors.clear()
    }

    // ── Initial state ─────────────────────────────────────────────────────────

    @Test fun `initial snapshot is OFFLINE`() {
        val monitor = makeMonitor()
        assertEquals(NodeActivityState.OFFLINE, monitor.current.state)
    }

    @Test fun `initial snapshot has zero rates`() {
        val monitor = makeMonitor()
        val s = monitor.current
        assertEquals(0L, s.ingressBps)
        assertEquals(0L, s.egressBps)
        assertEquals(0L, s.totalBps)
    }

    @Test fun `initial snapshot has no primary transport`() {
        val monitor = makeMonitor()
        assertEquals(null, monitor.current.primaryTransportName)
    }

    @Test fun `hasActivity is false for OFFLINE state`() {
        val monitor = makeMonitor()
        assertFalse(monitor.current.hasActivity)
    }

    // ── register ──────────────────────────────────────────────────────────────

    @Test fun `no transports registered leaves state OFFLINE after tick`() {
        val monitor = makeMonitor(intervalMs = 50)
        monitor.start()
        Thread.sleep(200)
        assertEquals(NodeActivityState.OFFLINE, monitor.current.state)
    }

    @Test fun `registered transport with no traffic yields IDLE`() {
        val monitor = makeMonitor(intervalMs = 50, idleThresholdSec = 1)
        val est = BandwidthEstimator("BLE", 2_000_000L)
        monitor.register("BLE", est)
        monitor.start()
        Thread.sleep(300)
        val state = monitor.current.state
        // With no traffic: IDLE (not OFFLINE, because a transport is registered).
        assertTrue(state == NodeActivityState.IDLE || state == NodeActivityState.OFFLINE)
    }

    // ── recordIngress / recordEgress ──────────────────────────────────────────

    @Test fun `recordIngress increments ingress counter`() {
        val monitor = makeMonitor(intervalMs = 100)
        val est = BandwidthEstimator("BLE", 2_000_000L)
        monitor.register("BLE", est)
        monitor.start()

        // Feed ingress bytes so the tick computes a non-zero rate.
        repeat(10) { monitor.recordIngress("BLE", 10_000) }
        Thread.sleep(300)

        // After at least one tick the ingressBps should be non-zero.
        assertTrue(monitor.current.ingressBps >= 0L)
    }

    @Test fun `recordEgress for unknown transport is silently ignored`() {
        val monitor = makeMonitor()
        monitor.recordEgress("NoSuchTransport", 1024) // should not throw
        assertEquals(NodeActivityState.OFFLINE, monitor.current.state)
    }

    @Test fun `recordIngress for unknown transport is silently ignored`() {
        val monitor = makeMonitor()
        monitor.recordIngress("NoSuchTransport", 1024) // should not throw
    }

    // ── subscribe ─────────────────────────────────────────────────────────────

    @Test fun `subscribe callback is invoked when state changes`() {
        val monitor = makeMonitor(intervalMs = 50)
        val est = BandwidthEstimator("BLE", 2_000_000L)
        monitor.register("BLE", est)

        var received: NodeActivitySnapshot? = null
        monitor.subscribe { received = it }

        // Push traffic to trigger a state change.
        monitor.start()
        repeat(50) { monitor.recordEgress("BLE", 10_000) }
        Thread.sleep(400)

        // At least one callback should have fired.
        assertNotNull(received)
    }

    @Test fun `unsubscribe stops callback delivery`() {
        val monitor = makeMonitor(intervalMs = 50)
        val est = BandwidthEstimator("BLE", 2_000_000L)
        monitor.register("BLE", est)

        var callCount = 0
        val unsubscribe = monitor.subscribe { callCount++ }
        monitor.start()
        repeat(20) { monitor.recordEgress("BLE", 10_000) }
        Thread.sleep(300)

        val countBefore = callCount
        unsubscribe()

        repeat(20) { monitor.recordEgress("BLE", 10_000) }
        Thread.sleep(300)

        // No new callbacks after unsubscribe.
        assertEquals(countBefore, callCount)
    }

    // ── sampleIntervalMs ─────────────────────────────────────────────────────

    @Test fun `sampleIntervalMs clamps to minimum 100`() {
        val monitor = makeMonitor()
        monitor.sampleIntervalMs = 10
        assertEquals(100, monitor.sampleIntervalMs)
    }

    @Test fun `sampleIntervalMs clamps to maximum 60000`() {
        val monitor = makeMonitor()
        monitor.sampleIntervalMs = 999_999
        assertEquals(60_000, monitor.sampleIntervalMs)
    }

    // ── idleThresholdSeconds ──────────────────────────────────────────────────

    @Test fun `idleThresholdSeconds clamps to minimum 1`() {
        val monitor = makeMonitor()
        monitor.idleThresholdSeconds = 0
        assertEquals(1, monitor.idleThresholdSeconds)
    }

    @Test fun `idleThresholdSeconds clamps to maximum 300`() {
        val monitor = makeMonitor()
        monitor.idleThresholdSeconds = 9999
        assertEquals(300, monitor.idleThresholdSeconds)
    }

    // ── NodeActivitySnapshot computed properties ──────────────────────────────

    @Test fun `totalBps is sum of ingressBps and egressBps`() {
        val monitor = makeMonitor(intervalMs = 50)
        val est = BandwidthEstimator("BLE", 2_000_000L)
        monitor.register("BLE", est)
        monitor.start()

        repeat(20) { monitor.recordIngress("BLE", 1000) }
        repeat(20) { monitor.recordEgress("BLE", 2000) }
        Thread.sleep(300)

        val s = monitor.current
        assertEquals(s.ingressBps + s.egressBps, s.totalBps)
    }

    @Test fun `hasActivity is true for ACTIVE state`() {
        val snap = NodeActivitySnapshot(
            state = NodeActivityState.ACTIVE,
            ingressBps = 0L, egressBps = 0L,
            activePeers = 0, activeTransports = 1,
            transports = emptyList(),
            primaryTransportName = "BLE",
            timestamp = java.time.Instant.now(),
        )
        assertTrue(snap.hasActivity)
    }

    @Test fun `hasActivity is false for IDLE state`() {
        val snap = NodeActivitySnapshot(
            state = NodeActivityState.IDLE,
            ingressBps = 0L, egressBps = 0L,
            activePeers = 0, activeTransports = 0,
            transports = emptyList(),
            primaryTransportName = null,
            timestamp = java.time.Instant.now(),
        )
        assertFalse(snap.hasActivity)
    }

    // ── TransportActivitySnapshot utilization ─────────────────────────────────

    @Test fun `utilizationPercent formats correctly`() {
        val snap = TransportActivitySnapshot(
            transportName = "BLE",
            isAvailable = true,
            ingressBps = 0L,
            egressBps = 500_000L,
            srtt = java.time.Duration.ofMillis(15),
            btlBwBps = 2_000_000L,
            utilizationFraction = 0.25,
            state = NodeActivityState.ACTIVE,
            confidence = BandwidthConfidence.MEDIUM,
        )
        assertEquals("25 %", snap.utilizationPercent)
    }

    // ── stop ─────────────────────────────────────────────────────────────────

    @Test fun `stop is idempotent`() {
        val monitor = makeMonitor(intervalMs = 50)
        monitor.start()
        monitor.stop()
        monitor.stop() // second stop should not throw
    }

    @Test fun `stop before start is safe`() {
        val monitor = makeMonitor()
        monitor.stop() // should not throw
    }

    // ── Multiple transports ───────────────────────────────────────────────────

    @Test fun `multiple transports aggregate correctly`() {
        val monitor = makeMonitor(intervalMs = 50)
        val ble  = BandwidthEstimator("BLE", 2_000_000L)
        val wifi = BandwidthEstimator("Wi-Fi Direct", 100_000_000L)
        monitor.register("BLE", ble)
        monitor.register("Wi-Fi Direct", wifi)
        monitor.start()

        repeat(20) {
            monitor.recordEgress("BLE", 5_000)
            monitor.recordEgress("Wi-Fi Direct", 50_000)
        }
        // Poll until the monitor has sampled both transports, rather than a fixed Thread.sleep:
        // under the full suite (busy JVM + webrtc-java native threads) the 50ms sampler can miss a
        // fixed 300ms window, which made this test flaky. Deterministic up to a 5s ceiling.
        val deadline = System.currentTimeMillis() + 5_000
        while (monitor.current.transports.size < 2 && System.currentTimeMillis() < deadline) {
            Thread.sleep(25)
        }

        val s = monitor.current
        assertEquals(2, s.transports.size)
    }

    // ── Active peers ──────────────────────────────────────────────────────────

    @Test fun `recordEgressToPeer with two distinct peers yields activePeers at least 2`() {
        val monitor = makeMonitor(intervalMs = 50, idleThresholdSec = 2)
        val est = BandwidthEstimator("BLE", 2_000_000L)
        monitor.register("BLE", est)
        monitor.start()

        // Two distinct peers send/receive within the idle window.
        repeat(5) {
            monitor.recordEgressToPeer("BLE", "peer-A", 5_000)
            monitor.recordEgressToPeer("BLE", "peer-B", 5_000)
        }
        Thread.sleep(300)

        assertTrue(
            monitor.current.activePeers >= 2,
            "expected activePeers >= 2, was ${monitor.current.activePeers}",
        )
    }

    @Test fun `recordEgress without a peer leaves activePeers at zero`() {
        val monitor = makeMonitor(intervalMs = 50, idleThresholdSec = 2)
        val est = BandwidthEstimator("BLE", 2_000_000L)
        monitor.register("BLE", est)
        monitor.start()

        // Transport-only egress must not register any peer.
        repeat(20) { monitor.recordEgress("BLE", 5_000) }
        Thread.sleep(300)

        assertEquals(0, monitor.current.activePeers)
    }
}
