// SPDX-License-Identifier: MIT
package aethernet.dtn

import aethernet.models.CustodyRecord
import aethernet.models.DtnBundle
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

// ── helpers ───────────────────────────────────────────────────────────────────

private fun freshBundle(
    sender: String = "alice",
    recipient: String = "bob",
    status: String = "Pending",
    expiresAt: Instant = Instant.now().plusSeconds(3_600),
    id: UUID = UUID.randomUUID(),
) = DtnBundle(
    id           = id,
    senderUhid   = sender,
    recipientUhid = recipient,
    encryptedPayload = byteArrayOf(1, 2, 3),
    status       = status,
    expiresAt    = expiresAt,
)

private fun expiredBundle(sender: String = "alice", recipient: String = "bob") =
    freshBundle(sender, recipient, expiresAt = Instant.now().minusSeconds(1))

// ── InMemoryBundleStore ────────────────────────────────────────────────────────

class BundleStoreTest {

    // ── get ───────────────────────────────────────────────────────────────────

    @Test
    fun `get returns null for unknown bundle`() = runBlocking {
        val store = InMemoryBundleStore()
        assertNull(store.get(UUID.randomUUID()))
    }

    @Test
    fun `save and get round-trip`() = runBlocking {
        val store  = InMemoryBundleStore()
        val bundle = freshBundle()
        store.save(bundle)
        val loaded = store.get(bundle.id)
        assertNotNull(loaded)
        assertEquals(bundle.id,            loaded.id)
        assertEquals(bundle.senderUhid,    loaded.senderUhid)
        assertEquals(bundle.recipientUhid, loaded.recipientUhid)
    }

    @Test
    fun `save overwrites existing bundle`() = runBlocking {
        val store  = InMemoryBundleStore()
        val bundle = freshBundle()
        store.save(bundle)
        val updated = bundle.copy(status = "InCustody")
        store.save(updated)
        val loaded = store.get(bundle.id)
        assertEquals("InCustody", loaded?.status)
    }

    // ── remove ────────────────────────────────────────────────────────────────

    @Test
    fun `remove deletes bundle`() = runBlocking {
        val store  = InMemoryBundleStore()
        val bundle = freshBundle()
        store.save(bundle)
        store.remove(bundle.id)
        assertNull(store.get(bundle.id))
    }

    @Test
    fun `remove nonexistent bundle is ok`() = runBlocking {
        val store = InMemoryBundleStore()
        store.remove(UUID.randomUUID()) // must not throw
    }

    // ── getActive ─────────────────────────────────────────────────────────────

    @Test
    fun `getActive returns pending non-expired bundles`() = runBlocking {
        val store = InMemoryBundleStore()
        val b     = freshBundle(status = "Pending")
        store.save(b)
        val active = store.getActive()
        assertEquals(1, active.size)
        assertEquals(b.id, active[0].id)
    }

    @Test
    fun `getActive returns in-custody non-expired bundles`() = runBlocking {
        val store = InMemoryBundleStore()
        val b     = freshBundle(status = "InCustody")
        store.save(b)
        assertEquals(1, store.getActive().size)
    }

    @Test
    fun `getActive excludes expired bundles`() = runBlocking {
        val store = InMemoryBundleStore()
        store.save(expiredBundle())
        assertTrue(store.getActive().isEmpty())
    }

    @Test
    fun `getActive excludes delivered bundles`() = runBlocking {
        val store = InMemoryBundleStore()
        store.save(freshBundle(status = "Delivered"))
        assertTrue(store.getActive().isEmpty())
    }

    // ── getActiveCount ────────────────────────────────────────────────────────

    @Test
    fun `getActiveCount returns correct count`() = runBlocking {
        val store = InMemoryBundleStore()
        store.save(freshBundle(id = UUID.randomUUID()))
        store.save(freshBundle(id = UUID.randomUUID()))
        store.save(freshBundle(status = "Delivered", id = UUID.randomUUID()))
        assertEquals(2, store.getActiveCount())
    }

    // ── custody records ───────────────────────────────────────────────────────

    @Test
    fun `saveCustody and getCustodyRecords round-trip`() = runBlocking {
        val store    = InMemoryBundleStore()
        val bundleId = UUID.randomUUID()
        val record   = CustodyRecord(
            bundleId  = bundleId,
            fromUhid  = "alice",
            toUhid    = "bob",
            accepted  = true,
        )
        store.saveCustody(record)
        val records = store.getCustodyRecords(bundleId)
        assertEquals(1, records.size)
        assertEquals(bundleId, records[0].bundleId)
        assertEquals("alice",  records[0].fromUhid)
        assertEquals("bob",    records[0].toUhid)
        assertTrue(records[0].accepted)
    }

    @Test
    fun `getCustodyRecords returns empty for unknown bundle`() = runBlocking {
        val store = InMemoryBundleStore()
        assertTrue(store.getCustodyRecords(UUID.randomUUID()).isEmpty())
    }

    @Test
    fun `getCustodyRecords filters by bundleId`() = runBlocking {
        val store   = InMemoryBundleStore()
        val bundleA = UUID.randomUUID()
        val bundleB = UUID.randomUUID()
        store.saveCustody(CustodyRecord(bundleId = bundleA, fromUhid = "a", toUhid = "b", accepted = true))
        store.saveCustody(CustodyRecord(bundleId = bundleB, fromUhid = "c", toUhid = "d", accepted = true))
        val records = store.getCustodyRecords(bundleA)
        assertEquals(1, records.size)
        assertEquals(bundleA, records[0].bundleId)
    }

    // ── expireStale ───────────────────────────────────────────────────────────

    @Test
    fun `expireStale marks expired bundles as Expired`() = runBlocking {
        val store  = InMemoryBundleStore()
        val bundle = expiredBundle()
        store.save(bundle)
        val count = store.expireStale()
        assertEquals(1, count)
        val loaded = store.get(bundle.id)
        assertEquals("Expired", loaded?.status)
    }

    @Test
    fun `expireStale returns zero when nothing expired`() = runBlocking {
        val store = InMemoryBundleStore()
        store.save(freshBundle()) // not expired
        assertEquals(0, store.expireStale())
    }

    @Test
    fun `expireStale does not double-mark already-expired bundles`() = runBlocking {
        val store  = InMemoryBundleStore()
        val bundle = expiredBundle().copy(status = "Expired")
        store.save(bundle)
        val count = store.expireStale()
        assertEquals(0, count, "already-Expired bundles should not be counted again")
    }

    @Test
    fun `expireStale leaves fresh bundles unchanged`() = runBlocking {
        val store  = InMemoryBundleStore()
        val fresh  = freshBundle()
        val stale  = expiredBundle(sender = "carol", recipient = "dave")
        store.save(fresh)
        store.save(stale)
        store.expireStale()
        assertEquals("Pending", store.get(fresh.id)?.status)
    }
}
