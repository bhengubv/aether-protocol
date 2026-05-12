// SPDX-License-Identifier: MIT

package aether.transport

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.yield
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import kotlin.test.*

class InProcessTransportTest {

    @BeforeEach
    fun setUp() {
        InProcessTransport.clearAll()
    }

    @AfterEach
    fun tearDown() {
        InProcessTransport.clearAll()
    }

    // ── Constructor / companion registration ──────────────────────────────────

    @Test fun `default name is InProcess`() {
        val t = InProcessTransport()
        assertEquals("InProcess", t.name)
    }

    @Test fun `isAvailable is true`() {
        val t = InProcessTransport()
        assertTrue(t.isAvailable)
    }

    @Test fun `maxBandwidthBps is positive`() {
        val t = InProcessTransport()
        assertTrue(t.maxBandwidthBps > 0)
    }

    @Test fun `maxRangeMeters is positive`() {
        val t = InProcessTransport()
        assertTrue(t.maxRangeMeters > 0)
    }

    @Test fun `powerCostRelative is 1`() {
        val t = InProcessTransport()
        assertEquals(1, t.powerCostRelative)
    }

    @Test fun `metrics is non-null`() {
        val t = InProcessTransport()
        assertNotNull(t.metrics)
    }

    @Test fun `register and getTransport round-trip`() {
        val t = InProcessTransport("alice-transport")
        InProcessTransport.register("alice", t)
        assertSame(t, InProcessTransport.getTransport("alice"))
    }

    @Test fun `getTransport returns null for unregistered UHID`() {
        assertNull(InProcessTransport.getTransport("ghost"))
    }

    @Test fun `clearAll removes all transports`() {
        InProcessTransport.register("a", InProcessTransport())
        InProcessTransport.register("b", InProcessTransport())
        InProcessTransport.clearAll()
        assertNull(InProcessTransport.getTransport("a"))
        assertNull(InProcessTransport.getTransport("b"))
    }

    @Test fun `unregister removes specific transport`() {
        InProcessTransport.register("alice", InProcessTransport())
        InProcessTransport.register("bob", InProcessTransport())
        InProcessTransport.unregister("alice")
        assertNull(InProcessTransport.getTransport("alice"))
        assertNotNull(InProcessTransport.getTransport("bob"))
    }

    // ── isConnected ───────────────────────────────────────────────────────────

    @Test fun `isConnected returns true when peer is registered`() {
        val alice = InProcessTransport()
        InProcessTransport.register("alice", alice)
        val bob = InProcessTransport()
        InProcessTransport.register("bob", bob)
        assertTrue(alice.isConnected("bob"))
    }

    @Test fun `isConnected returns false for unregistered peer`() {
        val alice = InProcessTransport()
        InProcessTransport.register("alice", alice)
        assertFalse(alice.isConnected("ghost"))
    }

    @Test fun `isConnected returns false after peer is unregistered`() {
        val alice = InProcessTransport()
        InProcessTransport.register("alice", alice)
        InProcessTransport.register("bob", InProcessTransport())
        assertTrue(alice.isConnected("bob"))
        InProcessTransport.unregister("bob")
        assertFalse(alice.isConnected("bob"))
    }

    // ── sendAsync ─────────────────────────────────────────────────────────────

    @Test fun `sendAsync delivers data to registered peer`() = runBlocking {
        val alice = InProcessTransport("alice-t")
        val bob = InProcessTransport("bob-t")
        InProcessTransport.register("alice", alice)
        InProcessTransport.register("bob", bob)

        // SharedFlow(replay=0): must subscribe BEFORE emitting or the event is lost.
        val received = CompletableDeferred<Pair<String, ByteArray>>()
        val job = launch { bob.dataReceived.collect { received.complete(it) } }
        yield() // let the collector start

        val payload = byteArrayOf(0xDE.toByte(), 0xAD.toByte(), 0xBE.toByte(), 0xEF.toByte())
        val ok = alice.sendAsync("bob", payload)
        assertTrue(ok)

        val result = withTimeoutOrNull(1000) { received.await() }
        job.cancel()
        assertNotNull(result)
        assertEquals("alice-t", result.first)
        assertContentEquals(payload, result.second)
    }

    @Test fun `sendAsync returns false for unregistered peer`() = runBlocking {
        val alice = InProcessTransport()
        InProcessTransport.register("alice", alice)
        assertFalse(alice.sendAsync("ghost", byteArrayOf(0x01)))
    }

    @Test fun `sendAsync increments sample count in metrics`() = runBlocking {
        val alice = InProcessTransport()
        val bob = InProcessTransport()
        InProcessTransport.register("alice", alice)
        InProcessTransport.register("bob", bob)

        val before = alice.metrics.sampleCount
        alice.sendAsync("bob", byteArrayOf(0x01, 0x02))
        assertTrue(alice.metrics.sampleCount > before)
    }

    @Test fun `sendAsync to unregistered peer still increments sample count`() = runBlocking {
        val alice = InProcessTransport()
        InProcessTransport.register("alice", alice)

        val before = alice.metrics.sampleCount
        alice.sendAsync("ghost", byteArrayOf(0x01))
        assertTrue(alice.metrics.sampleCount > before)
    }

    // ── sendStreamAsync ───────────────────────────────────────────────────────

    @Test fun `sendStreamAsync delegates to sendAsync`() = runBlocking {
        val alice = InProcessTransport("a-t")
        val bob = InProcessTransport("b-t")
        InProcessTransport.register("alice", alice)
        InProcessTransport.register("bob", bob)

        val received = CompletableDeferred<Pair<String, ByteArray>>()
        val job = launch { bob.dataReceived.collect { received.complete(it) } }
        yield()

        val ok = alice.sendStreamAsync("bob", byteArrayOf(0x01, 0x02, 0x03))
        assertTrue(ok)

        val result = withTimeoutOrNull(1000) { received.await() }
        job.cancel()
        assertNotNull(result)
        assertContentEquals(byteArrayOf(0x01, 0x02, 0x03), result.second)
    }

    @Test fun `sendStreamAsync returns false for unregistered peer`() = runBlocking {
        val alice = InProcessTransport()
        InProcessTransport.register("alice", alice)
        assertFalse(alice.sendStreamAsync("ghost", byteArrayOf(0x01)))
    }

    // ── dataReceived flow ─────────────────────────────────────────────────────

    @Test fun `multiple senders each deliver to the correct recipient`() = runBlocking {
        val alice = InProcessTransport("alice-t")
        val bob = InProcessTransport("bob-t")
        val carol = InProcessTransport("carol-t")
        InProcessTransport.register("alice", alice)
        InProcessTransport.register("bob", bob)
        InProcessTransport.register("carol", carol)

        // Subscribe BEFORE emitting — SharedFlow(replay=0) drops unobserved emissions.
        val bobReceived = CompletableDeferred<Pair<String, ByteArray>>()
        val carolReceived = CompletableDeferred<Pair<String, ByteArray>>()
        val jobBob = launch { bob.dataReceived.collect { bobReceived.complete(it) } }
        val jobCarol = launch { carol.dataReceived.collect { carolReceived.complete(it) } }
        yield() // let both collectors start

        alice.sendAsync("bob", byteArrayOf(0xAA.toByte()))
        alice.sendAsync("carol", byteArrayOf(0xBB.toByte()))

        val fromAliceToBob = withTimeoutOrNull(1000) { bobReceived.await() }
        val fromAliceToCarol = withTimeoutOrNull(1000) { carolReceived.await() }
        jobBob.cancel(); jobCarol.cancel()

        assertNotNull(fromAliceToBob)
        assertNotNull(fromAliceToCarol)
        assertEquals("alice-t", fromAliceToBob.first)
        assertEquals("alice-t", fromAliceToCarol.first)
        assertContentEquals(byteArrayOf(0xAA.toByte()), fromAliceToBob.second)
        assertContentEquals(byteArrayOf(0xBB.toByte()), fromAliceToCarol.second)
    }

    // ── close ─────────────────────────────────────────────────────────────────

    @Test fun `close clears connectedPeers without throwing`() {
        val alice = InProcessTransport()
        InProcessTransport.register("alice", alice)
        alice.close() // must not throw
    }

    // ── PerTransportMetrics ───────────────────────────────────────────────────

    @Test fun `initial sampleCount is 0`() {
        val m = PerTransportMetrics()
        assertEquals(0L, m.sampleCount)
    }

    @Test fun `initial ewmaRttMs is 200`() {
        val m = PerTransportMetrics()
        assertEquals(200.0, m.ewmaRttMs)
    }

    @Test fun `initial ewmaLossRate is 0_05`() {
        val m = PerTransportMetrics()
        assertEquals(0.05, m.ewmaLossRate)
    }

    @Test fun `initial ewmaThroughputBps is 0`() {
        val m = PerTransportMetrics()
        assertEquals(0.0, m.ewmaThroughputBps)
    }

    @Test fun `recordSample increments sampleCount`() {
        val m = PerTransportMetrics()
        m.recordSample(10L, true, 1000L)
        assertEquals(1L, m.sampleCount)
        m.recordSample(20L, true, 500L)
        m.recordSample(0L, false, 0L)
        assertEquals(3L, m.sampleCount)
    }

    @Test fun `recordSample success updates rtt`() {
        val m = PerTransportMetrics()
        m.recordSample(100L, true, 1000L)
        // EWMA: 0.2*100 + 0.8*200 = 20 + 160 = 180
        assertEquals(180.0, m.ewmaRttMs, 1e-9)
    }

    @Test fun `recordSample failure updates loss rate`() {
        val m = PerTransportMetrics()
        m.recordSample(0L, false, 0L)
        // EWMA: 0.2*1.0 + 0.8*0.05 = 0.2 + 0.04 = 0.24
        assertEquals(0.24, m.ewmaLossRate, 1e-9)
    }

    @Test fun `recordSample success bootstraps throughput`() {
        val m = PerTransportMetrics()
        m.recordSample(10L, true, 1000L) // 1000 bytes in 10ms = 800000 bps
        assertTrue(m.ewmaThroughputBps > 0.0)
    }

    @Test fun `recordSample failure does not update throughput`() {
        val m = PerTransportMetrics()
        m.recordSample(10L, false, 0L)
        assertEquals(0.0, m.ewmaThroughputBps)
    }

    @Test fun `compositeScore is positive after a successful sample`() {
        val m = PerTransportMetrics()
        m.recordSample(10L, true, 1000L)
        assertTrue(m.compositeScore(1_000_000L, 1) > 0.0)
    }

    @Test fun `compositeScore decreases with higher power cost`() {
        val m1 = PerTransportMetrics()
        m1.recordSample(10L, true, 1000L)
        val score1 = m1.compositeScore(1_000_000L, 1)

        val m2 = PerTransportMetrics()
        m2.recordSample(10L, true, 1000L)
        val score2 = m2.compositeScore(1_000_000L, 100)

        assertTrue(score2 < score1, "higher power cost should yield lower score")
    }
}
