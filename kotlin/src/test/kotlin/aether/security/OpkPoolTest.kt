// SPDX-License-Identifier: MIT
package aether.security

import org.junit.jupiter.api.Test
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

/**
 * Tests for the one-time pre-key (OPK) pool — the key change at parity with
 * the C# `SignalProtocolService.OpkPoolSize` work.
 *
 * Pre-2026-05-05 the Kotlin module held only a single OPK at a time, so two
 * concurrent initiators against the same node both received the same preKeyId
 * and the second responder side rejected the (now-consumed) OPK. The pool
 * fixes that by handing each initiator a distinct id.
 */
class OpkPoolTest {

    // ─── Pool size after construction ──────────────────────────────────────

    @Test
    fun freshInstance_holdsZeroOpksUntilFirstBundle() {
        val svc = SignalProtocol()
        // Pool fills lazily on first generatePreKeyBundle, so a brand-new
        // instance holds nothing.
        assertEquals(0, svc.heldOneTimePreKeyCount)
        assertEquals(0, svc.availableOneTimePreKeyCount)
    }

    @Test
    fun firstBundle_topsUpToDefaultPoolSize() {
        val svc = SignalProtocol()
        svc.generatePreKeyBundle("alice")

        // Default pool size = 100. After issuing one bundle, 99 remain
        // available (1 was handed out), but the pool has been topped up:
        // total held = 100 (issued + un-issued). The C# reference's contract
        // is identical.
        assertEquals(100, svc.heldOneTimePreKeyCount)
        assertEquals(99, svc.availableOneTimePreKeyCount)
    }

    @Test
    fun customPoolSize_isHonoured() {
        val svc = SignalProtocol(opkPoolSize = 7)
        svc.generatePreKeyBundle("alice")

        assertEquals(7, svc.heldOneTimePreKeyCount)
        assertEquals(6, svc.availableOneTimePreKeyCount)
    }

    @Test
    fun zeroPoolSize_rejected() {
        try {
            SignalProtocol(opkPoolSize = 0)
            kotlin.test.fail("expected IllegalArgumentException for opkPoolSize=0")
        } catch (e: IllegalArgumentException) {
            assertTrue(e.message!!.contains("opkPoolSize"))
        }
    }

    // ─── 100 distinct OPK ids ──────────────────────────────────────────────

    @Test
    fun hundredBundles_yieldHundredDistinctPreKeyIds() {
        val svc = SignalProtocol()

        val ids = HashSet<Int>()
        for (i in 0 until 100) {
            val bundle = svc.generatePreKeyBundle("alice")
            ids.add(bundle.preKeyId)
        }
        assertEquals(100, ids.size, "100 bundles must yield 100 unique preKeyIds")
    }

    @Test
    fun consecutiveBundles_haveDifferentPreKeyIds() {
        val svc = SignalProtocol()
        val a = svc.generatePreKeyBundle("alice")
        val b = svc.generatePreKeyBundle("alice")
        val c = svc.generatePreKeyBundle("alice")
        assertNotEquals(a.preKeyId, b.preKeyId)
        assertNotEquals(b.preKeyId, c.preKeyId)
        assertNotEquals(a.preKeyId, c.preKeyId)
        // Public bytes also differ — these are genuine distinct OPKs.
        assertNotEquals(toHex(a.preKey), toHex(b.preKey))
        assertNotEquals(toHex(b.preKey), toHex(c.preKey))
    }

    @Test
    fun signedPreKey_isReusedAcrossBundles() {
        // SPK is generated lazily on first call and reused thereafter — only
        // OPK rotates per call.
        val svc = SignalProtocol()
        val a = svc.generatePreKeyBundle("alice")
        val b = svc.generatePreKeyBundle("alice")
        assertEquals(a.signedPreKeyId, b.signedPreKeyId)
        assertEquals(toHex(a.signedPreKey), toHex(b.signedPreKey))
    }

    // ─── Consumption + top-up ──────────────────────────────────────────────

    @Test
    fun bundleConsumption_topsUpAvailable() {
        val svc = SignalProtocol(opkPoolSize = 5)

        // First call seeds the pool to 5.
        svc.generatePreKeyBundle("alice")
        assertEquals(5, svc.heldOneTimePreKeyCount)
        assertEquals(4, svc.availableOneTimePreKeyCount)

        // Second call tops up (adds 1 new key), then dequeues → available stays at opkPoolSize-1.
        // C# contract: TopUp fills to opkPoolSize, Dequeue takes one → available = opkPoolSize-1.
        svc.generatePreKeyBundle("alice")
        assertEquals(6, svc.heldOneTimePreKeyCount) // 2 issued + 4 available = grew by 1
        assertEquals(4, svc.availableOneTimePreKeyCount)
    }

    @Test
    fun responderConsumption_reducesHeldCount() {
        val alice = SignalProtocol()
        val bob = SignalProtocol(opkPoolSize = 10)

        val bobBundle = bob.generatePreKeyBundle("bob")
        val before = bob.heldOneTimePreKeyCount

        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)
        val first = alice.encrypt("bob", "hi".toByteArray())
        bob.decrypt("alice", first)

        // Bob has consumed one OPK during responder X3DH.
        assertEquals(before - 1, bob.heldOneTimePreKeyCount)
    }

    @Test
    fun replayWithSameBundle_failsOnSecondResponder() {
        // Both initiators consume the same OPK (id) — the second one MUST
        // fail because Bob removed it on the first establishment.
        val bob = SignalProtocol(opkPoolSize = 10)
        val bobBundle = bob.generatePreKeyBundle("bob")

        val alice = SignalProtocol()
        alice.generatePreKeyBundle("alice")
        alice.processPreKeyBundle(bobBundle)
        bob.decrypt("alice", alice.encrypt("bob", "first".toByteArray()))

        val mallory = SignalProtocol()
        mallory.generatePreKeyBundle("mallory")
        mallory.processPreKeyBundle(bobBundle) // same bundle == same OPK id
        try {
            bob.decrypt("mallory", mallory.encrypt("bob", "replay".toByteArray()))
            kotlin.test.fail("expected exception when responder OPK already consumed")
        } catch (_: Exception) {
            // Expected — replay protection at the bundle layer.
        }
    }

    @Test
    fun statusPair_reportsHeldAndAvailable() {
        val svc = SignalProtocol(opkPoolSize = 8)
        svc.generatePreKeyBundle("alice")
        val (held, available) = svc.getOpkPoolStatus()
        assertEquals(8, held)
        assertEquals(7, available)
    }

    // ─── Concurrent initiators don't collide ──────────────────────────────

    @Test
    fun concurrentBundleGeneration_yieldsDistinctIds() {
        val svc = SignalProtocol(opkPoolSize = 64)
        val threads = 16
        val perThread = 8
        val executor = Executors.newFixedThreadPool(threads)
        val ids = ConcurrentHashMap.newKeySet<Int>()
        val publics = ConcurrentHashMap.newKeySet<String>()
        val start = CountDownLatch(1)
        val done = CountDownLatch(threads)

        try {
            repeat(threads) {
                executor.submit {
                    try {
                        start.await()
                        for (i in 0 until perThread) {
                            val bundle = svc.generatePreKeyBundle("alice-$i")
                            ids.add(bundle.preKeyId)
                            publics.add(toHex(bundle.preKey))
                        }
                    } finally {
                        done.countDown()
                    }
                }
            }
            start.countDown()
            assertTrue(done.await(30, TimeUnit.SECONDS), "concurrent bundles must finish")
        } finally {
            executor.shutdownNow()
        }

        val expected = threads * perThread
        assertEquals(expected, ids.size, "every concurrent bundle must have a unique preKeyId")
        assertEquals(expected, publics.size, "every concurrent bundle must have a unique OPK pub")
    }

    @Test
    fun concurrentResponderConsumption_neverDoubleSpends() {
        // Bob's pool yields N distinct OPKs. N concurrent initiators each
        // process a fresh bundle; Bob's decrypt path must consume each OPK
        // exactly once.
        val n = 16
        val bob = SignalProtocol(opkPoolSize = n)
        val initiators = (0 until n).map { i ->
            val alice = SignalProtocol()
            alice.generatePreKeyBundle("alice-$i")
            val bundle = bob.generatePreKeyBundle("bob")
            alice.processPreKeyBundle(bundle)
            val msg = alice.encrypt("bob", "hello-$i".toByteArray())
            "alice-$i" to msg
        }
        val heldBefore = bob.heldOneTimePreKeyCount

        val executor = Executors.newFixedThreadPool(n)
        val start = CountDownLatch(1)
        val done = CountDownLatch(n)
        val errors = ConcurrentHashMap.newKeySet<String>()

        try {
            for ((peerUhid, msg) in initiators) {
                executor.submit {
                    try {
                        start.await()
                        bob.decrypt(peerUhid, msg)
                    } catch (e: Exception) {
                        errors.add("${peerUhid}: ${e.javaClass.simpleName} ${e.message}")
                    } finally {
                        done.countDown()
                    }
                }
            }
            start.countDown()
            assertTrue(done.await(30, TimeUnit.SECONDS), "concurrent decrypts must finish")
        } finally {
            executor.shutdownNow()
        }

        assertTrue(errors.isEmpty(), "no decrypt should fail under concurrent OPK consumption: $errors")
        // Bob consumed exactly n OPKs.
        assertEquals(heldBefore - n, bob.heldOneTimePreKeyCount)
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private fun toHex(b: ByteArray): String =
        b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
}
