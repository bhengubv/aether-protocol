// SPDX-License-Identifier: MIT
package aethernet.routing

import aethernet.models.RouteEntry
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

// ── helpers ───────────────────────────────────────────────────────────────────

private fun freshRoute(dest: String, hop: String = dest, hopCount: Int = 1) = RouteEntry(
    destinationUhid = dest,
    nextHopUhid     = hop,
    hopCount        = hopCount,
    qualityScore    = 50,
    expiresAt       = Instant.now().plusSeconds(300),
)

private fun expiredRoute(dest: String) = RouteEntry(
    destinationUhid = dest,
    nextHopUhid     = dest,
    hopCount        = 1,
    expiresAt       = Instant.now().minusSeconds(1),
)

// ── InMemoryRouteStore ────────────────────────────────────────────────────────

class RouteStoreTest {

    // ── get ───────────────────────────────────────────────────────────────────

    @Test
    fun `get returns null for unknown destination`() = runBlocking {
        val store = InMemoryRouteStore()
        assertNull(store.get("unknown-uhid"))
    }

    @Test
    fun `save and get round-trip`() = runBlocking {
        val store = InMemoryRouteStore()
        val route = freshRoute("node-b")
        store.save(route)
        val loaded = store.get("node-b")
        assertNotNull(loaded)
        assertEquals("node-b", loaded.destinationUhid)
        assertEquals("node-b", loaded.nextHopUhid)
        assertEquals(1,        loaded.hopCount)
        assertEquals(50,       loaded.qualityScore)
    }

    @Test
    fun `save overwrites existing entry for same destination`() = runBlocking {
        val store  = InMemoryRouteStore()
        val first  = freshRoute("node-c", hop = "relay-1")
        store.save(first)
        val second = freshRoute("node-c", hop = "relay-2", hopCount = 2)
        store.save(second)
        val loaded = store.get("node-c")
        assertEquals("relay-2", loaded?.nextHopUhid)
        assertEquals(2,         loaded?.hopCount)
    }

    // ── remove ────────────────────────────────────────────────────────────────

    @Test
    fun `remove deletes route`() = runBlocking {
        val store = InMemoryRouteStore()
        store.save(freshRoute("node-d"))
        store.remove("node-d")
        assertNull(store.get("node-d"))
    }

    @Test
    fun `remove nonexistent destination is ok`() = runBlocking {
        val store = InMemoryRouteStore()
        store.remove("ghost") // must not throw
    }

    // ── getAll ────────────────────────────────────────────────────────────────

    @Test
    fun `getAll returns empty initially`() = runBlocking {
        val store = InMemoryRouteStore()
        assertTrue(store.getAll().isEmpty())
    }

    @Test
    fun `getAll returns all saved routes`() = runBlocking {
        val store = InMemoryRouteStore()
        store.save(freshRoute("node-1"))
        store.save(freshRoute("node-2"))
        store.save(freshRoute("node-3"))
        assertEquals(3, store.getAll().size)
    }

    @Test
    fun `getAll excludes removed routes`() = runBlocking {
        val store = InMemoryRouteStore()
        store.save(freshRoute("keep"))
        store.save(freshRoute("remove"))
        store.remove("remove")
        val all = store.getAll()
        assertEquals(1, all.size)
        assertEquals("keep", all[0].destinationUhid)
    }

    // ── pruneExpired ──────────────────────────────────────────────────────────

    @Test
    fun `pruneExpired returns zero when nothing expired`() = runBlocking {
        val store = InMemoryRouteStore()
        store.save(freshRoute("node-e"))
        assertEquals(0, store.pruneExpired())
    }

    @Test
    fun `pruneExpired removes expired routes and returns count`() = runBlocking {
        val store = InMemoryRouteStore()
        store.save(expiredRoute("stale-1"))
        store.save(expiredRoute("stale-2"))
        store.save(freshRoute("fresh"))
        val pruned = store.pruneExpired()
        assertEquals(2, pruned)
        assertNull(store.get("stale-1"))
        assertNull(store.get("stale-2"))
        assertNotNull(store.get("fresh"))
    }

    @Test
    fun `pruneExpired returns zero on empty store`() = runBlocking {
        val store = InMemoryRouteStore()
        assertEquals(0, store.pruneExpired())
    }

    @Test
    fun `pruneExpired does not touch fresh routes`() = runBlocking {
        val store = InMemoryRouteStore()
        val route = freshRoute("node-f")
        store.save(route)
        store.pruneExpired()
        assertNotNull(store.get("node-f"))
    }
}
