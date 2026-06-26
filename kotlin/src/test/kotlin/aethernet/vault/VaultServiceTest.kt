// SPDX-License-Identifier: MIT
//
// Behavioural tests for the in-memory aether-vault service: erasure-coded
// store/recover round-trip, any-K-of-N recovery, unrecoverable below K, and the
// empty-blob edge case.

package aethernet.vault

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class VaultServiceTest {

    // Reach the private shard store for white-box loss simulation.
    @Suppress("UNCHECKED_CAST")
    private fun shardStore(svc: InMemoryVaultService): MutableMap<String, ByteArray> {
        val f = InMemoryVaultService::class.java.getDeclaredField("shards")
        f.isAccessible = true
        return f.get(svc) as MutableMap<String, ByteArray>
    }

    @Test
    fun storeRecoverRoundTripAndHealth() = runBlocking {
        val svc = InMemoryVaultService()
        val data = ByteArray(3333) { ((it * 7) % 256).toByte() }

        val m = svc.store(data, "doc.bin")
        assertEquals(VAULT_K + VAULT_M, m.shardHashes.size)
        assertEquals(3333L, m.sizeBytes)
        assertEquals(64, m.contentHash.length)

        assertContentEquals(data, svc.recover(m))

        val h = svc.checkHealth(m)
        assertEquals(VAULT_K + VAULT_M, h.reachableShards)
        assertTrue(h.isRecoverable)
        assertTrue(h.redundancyScore > 0.99)
    }

    @Test
    fun recoversFromAnyKShardsThenUnrecoverable() = runBlocking {
        val svc = InMemoryVaultService()
        val data = ByteArray(12) { (it + 1).toByte() }
        val m = svc.store(data, "x")
        val store = shardStore(svc)

        // Drop M shards: K survive -> still recoverable.
        for (i in 0 until VAULT_M) store.remove(m.shardHashes[i])
        var h = svc.checkHealth(m)
        assertEquals(VAULT_K, h.reachableShards)
        assertTrue(h.isRecoverable)
        assertContentEquals(data, svc.recover(m))

        // Drop one more -> only K-1 remain -> unrecoverable.
        store.remove(m.shardHashes[VAULT_M])
        h = svc.checkHealth(m)
        assertFalse(h.isRecoverable)
        assertFailsWith<IllegalArgumentException> { svc.recover(m) }
        Unit
    }

    @Test
    fun emptyBlobRoundTrips() = runBlocking {
        val svc = InMemoryVaultService()
        val m = svc.store(ByteArray(0), "empty")
        assertEquals(0L, m.sizeBytes)
        assertEquals(0, svc.recover(m).size)
    }
}
