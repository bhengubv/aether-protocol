// SPDX-License-Identifier: MIT

package aethernet.circuitrelay

import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Test
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.BlockingQueue
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Behavioural proof of the native circuit-relay-v2 engine ([Transport]): a three-node
 * topology where A and B can each reach relay R but NOT each other directly. A message
 * from A must traverse the relay bridge to reach B — server off, no libp2p. Mirrors the
 * Go `transport_test.go` (6 cases) and the C# `CircuitRelayBridgeTests`.
 *
 * No kotlinx.coroutines: the in-process link delivers frames on a cached thread pool
 * (like a real async transport, and to avoid re-entrant deadlock), and the test awaits
 * receipts / status via [BlockingQueue].
 */
class RelayEngineTest {

    // ── in-process one-hop mesh ──────────────────────────────────────────────

    private class Mesh(private val pool: ExecutorService) {
        private val lock = ReentrantLock()
        private val edges = HashSet<String>()
        private val links = HashMap<String, ProcLink>()

        fun connect(x: String, y: String) = lock.withLock {
            edges.add("$x|$y")
            edges.add("$y|$x")
        }

        fun adjacent(x: String, y: String): Boolean = lock.withLock { edges.contains("$x|$y") }

        fun link(node: String): ProcLink = lock.withLock {
            links.getOrPut(node) { ProcLink(this, node) }
        }

        fun deliver(from: String, to: String, frame: ByteArray) {
            if (!adjacent(from, to)) return
            val l = link(to)
            pool.execute { // async hop, like a real transport
                l.handler()?.invoke(from, frame)
            }
        }
    }

    private class ProcLink(private val mesh: Mesh, private val node: String) : RelayLink {
        private val hlock = ReentrantLock()
        private var h: ((String, ByteArray) -> Unit)? = null

        fun handler(): ((String, ByteArray) -> Unit)? = hlock.withLock { h }

        override fun sendFrame(node: String, frame: ByteArray): Boolean {
            if (!mesh.adjacent(this.node, node)) return false
            mesh.deliver(this.node, node, frame)
            return true
        }

        override fun canReach(node: String): Boolean = mesh.adjacent(this.node, node)

        override fun onFrame(handler: (String, ByteArray) -> Unit) {
            hlock.withLock { h = handler }
        }
    }

    // ── controllable clock ───────────────────────────────────────────────────

    private class TestClock {
        private val lock = ReentrantLock()

        // Start at 2026-01-01T00:00:00Z, in epoch ms.
        private var t: Long = 1_767_225_600_000L

        fun now(): Long = lock.withLock { t }
        fun advanceMs(d: Long) = lock.withLock { t += d }
    }

    private data class Recv(val sender: String, val data: String)

    // ── shared fixture: A ── R ── B with NO A-B edge ─────────────────────────

    private lateinit var pool: ExecutorService

    private class Line(
        val a: Transport,
        val r: Transport,
        val b: Transport,
        val bRecv: BlockingQueue<Recv>,
        val aRecv: BlockingQueue<Recv>
    )

    /** Wires A ── R ── B with no A-B edge. [relayOpts]/[relayClock] configure R. */
    private fun buildLine(
        relayOpts: RelayOptions = RelayOptions(),
        relayClock: (() -> Long)? = null
    ): Line {
        val mesh = Mesh(pool)
        mesh.connect("A", "R")
        mesh.connect("R", "B")
        val a = Transport("A", mesh.link("A"))
        val r = if (relayClock != null) {
            Transport("R", mesh.link("R"), relayOpts, relayClock)
        } else {
            Transport("R", mesh.link("R"), relayOpts)
        }
        val b = Transport("B", mesh.link("B"))
        val bRecv: BlockingQueue<Recv> = ArrayBlockingQueue(8)
        val aRecv: BlockingQueue<Recv> = ArrayBlockingQueue(8)
        b.setOnData { s, d -> bRecv.offer(Recv(s, String(d, Charsets.UTF_8))) }
        a.setOnData { s, d -> aRecv.offer(Recv(s, String(d, Charsets.UTF_8))) }
        return Line(a, r, b, bRecv, aRecv)
    }

    private fun waitRecv(ch: BlockingQueue<Recv>, what: String): Recv =
        ch.poll(3, TimeUnit.SECONDS) ?: error("timeout waiting for $what")

    @org.junit.jupiter.api.BeforeEach
    fun setUp() {
        pool = Executors.newCachedThreadPool()
    }

    @AfterEach
    fun tearDown() {
        pool.shutdownNow()
        pool.awaitTermination(2, TimeUnit.SECONDS)
    }

    // ── (a) A -> R -> B relay, B receives, relay bridge count == 1 ───────────

    @Test
    fun `message traverses relay with no direct link`() {
        val line = buildLine()

        assertFalse(line.a.isConnected("B"), "A should not be directly connected to B")
        assertTrue(line.b.reserve("R"), "B.reserve(R) failed")
        line.a.setRoute("B", "R")

        assertTrue(line.a.send("B", "deadbeef".toByteArray(Charsets.UTF_8)), "A.send returned false")

        val got = waitRecv(line.bRecv, "B receiving relayed message")
        assertEquals("A", got.sender, "sender")
        assertEquals("deadbeef", got.data, "payload")
        assertEquals(1, line.r.activeBridgeCount(), "relay bridge count")
    }

    // ── (b) bidirectional ────────────────────────────────────────────────────

    @Test
    fun `bridge is bidirectional`() {
        val line = buildLine()
        assertTrue(line.b.reserve("R"), "reserve failed")
        line.a.setRoute("B", "R")
        assertTrue(line.a.send("B", "hi".toByteArray(Charsets.UTF_8)), "A.send failed")
        waitRecv(line.bRecv, "B receiving")

        assertTrue(line.b.send("A", "reply".toByteArray(Charsets.UTF_8)), "B.send(A) failed")
        val got = waitRecv(line.aRecv, "A receiving B's reply")
        assertEquals("B", got.sender, "sender")
        assertEquals("reply", got.data, "payload")
    }

    // ── (c) connect refused without reservation ──────────────────────────────

    @Test
    fun `connect refused without reservation`() {
        val line = buildLine()
        line.a.setRoute("B", "R") // route known, but B never reserved

        assertFalse(line.a.send("B", "x".toByteArray(Charsets.UTF_8)), "A.send should fail without a reservation")

        assertNull(line.bRecv.poll(200, TimeUnit.MILLISECONDS), "B should not have received anything")
        assertEquals(0, line.r.activeBridgeCount(), "relay bridge count")
    }

    // ── (d) send fails with no route ─────────────────────────────────────────

    @Test
    fun `send fails without route`() {
        val line = buildLine()
        assertTrue(line.b.reserve("R"), "reserve failed")
        // no setRoute
        assertFalse(line.a.send("B", "x".toByteArray(Charsets.UTF_8)), "A.send should fail with no relay route known")
    }

    // ── (e) data budget 10: first 5B delivered, second 8B (cum 13) dropped ───

    @Test
    fun `relay enforces data budget`() {
        val opts = RelayOptions(bridgeDataLimitBytes = 10)
        val line = buildLine(opts)
        assertTrue(line.b.reserve("R"), "reserve failed")
        line.a.setRoute("B", "R")

        assertTrue(
            line.a.send("B", byteArrayOf(1, 2, 3, 4, 5)), // 5 bytes, within 10
            "first send failed"
        )
        waitRecv(line.bRecv, "first (in-budget) message")

        line.a.send("B", byteArrayOf(6, 7, 8, 9, 10, 11, 12, 13)) // 8 more -> 13 > 10 -> torn down
        assertNull(line.bRecv.poll(300, TimeUnit.MILLISECONDS), "over-budget message should not arrive")
        assertEquals(0, line.r.activeBridgeCount(), "bridge should be torn down on budget breach")
    }

    // ── (f) reservation expiry via injectable clock ──────────────────────────

    @Test
    fun `reservation expiry refuses connect`() {
        val clk = TestClock()
        val opts = RelayOptions(reservationTtlMs = 30L * 60L * 1000L)
        val line = buildLine(opts, clk::now)

        assertTrue(line.b.reserve("R"), "reserve failed")
        line.a.setRoute("B", "R")

        clk.advanceMs(31L * 60L * 1000L) // past the reservation TTL on R's clock

        assertFalse(line.a.send("B", "x".toByteArray(Charsets.UTF_8)), "A.send should fail after reservation expiry")
        assertNull(line.bRecv.poll(200, TimeUnit.MILLISECONDS), "B should not receive after expiry")
    }
}
