// SPDX-License-Identifier: MIT

package aethernet.circuitrelay

import aethernet.protocol.MeshPacket
import org.junit.jupiter.api.Test
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * 3-node mesh-integration proof for circuit-relay-v2: the engine relays A->B through R over
 * real MeshPacket frames (type CircuitRelayControl) with NO direct A-B link, surfacing at B
 * via the transport onData callback — exactly how a host mesh consumes it. Mirrors the C#
 * CircuitRelayMeshIntegrationTests and the Go / Python / TS / Rust mesh tests.
 *
 * No kotlinx.coroutines: the mesh delivers each packet on a cached thread pool (like a real
 * async transport, and to avoid re-entrant deadlock while the engine blocks on its
 * CONNECT/RESERVE waits).
 */
class RelayMeshTest {

    /** In-process mesh, adjacency A-R-B with NO direct A-B edge; routes each MeshPacket one hop. */
    private class MeshHub(private val pool: ExecutorService) {
        private val lock = ReentrantLock()
        private val edges = HashSet<String>()
        private val links = HashMap<String, MeshRelayLink>()

        fun connect(x: String, y: String) = lock.withLock {
            edges.add("$x|$y"); edges.add("$y|$x")
        }
        fun adjacent(x: String, y: String): Boolean = lock.withLock { edges.contains("$x|$y") }
        fun register(node: String, link: MeshRelayLink) = lock.withLock { links[node] = link }

        fun sendFrom(node: String): (MeshPacket) -> Boolean = { pkt ->
            if (!adjacent(node, pkt.destinationUhid)) {
                false
            } else {
                val l = lock.withLock { links[pkt.destinationUhid] }
                if (l != null) pool.execute { l.handleIncomingPacket(pkt) } // async one-hop delivery
                true
            }
        }
        fun canReachFrom(node: String): (String) -> Boolean = { other -> adjacent(node, other) }
    }

    @Test
    fun `relay works as a mesh transport over real MeshPacket frames`() {
        val pool = Executors.newCachedThreadPool()
        try {
            val hub = MeshHub(pool)
            hub.connect("A", "R")
            hub.connect("R", "B") // deliberately NO A-B edge

            val aLink = MeshRelayLink("A", hub.sendFrom("A"), hub.canReachFrom("A"))
            val rLink = MeshRelayLink("R", hub.sendFrom("R"), hub.canReachFrom("R"))
            val bLink = MeshRelayLink("B", hub.sendFrom("B"), hub.canReachFrom("B"))
            hub.register("A", aLink)
            hub.register("R", rLink)
            hub.register("B", bLink)

            val a = Transport("A", aLink)
            val r = Transport("R", rLink)
            val b = Transport("B", bLink)

            val received = ArrayBlockingQueue<Pair<String, ByteArray>>(1)
            b.setOnData { sender, data -> received.offer(sender to data) }

            assertFalse(a.isConnected("B"))          // no direct path
            assertTrue(b.reserve("R"))               // B reserves on the relay
            a.setRoute("B", "R")                     // A learns B is reachable via R

            val payload = byteArrayOf(0xDE.toByte(), 0xAD.toByte(), 0xBE.toByte(), 0xEF.toByte())
            assertTrue(a.send("B", payload))         // relayed A -> R -> B

            val got = received.poll(3, TimeUnit.SECONDS)
            assertNotNull(got, "B never received the relayed message via the mesh link")
            assertEquals("A", got.first)
            assertTrue(payload.contentEquals(got.second))
            assertEquals(1, r.activeBridgeCount())   // R is genuinely bridging over real packets
        } finally {
            pool.shutdownNow()
        }
    }
}
