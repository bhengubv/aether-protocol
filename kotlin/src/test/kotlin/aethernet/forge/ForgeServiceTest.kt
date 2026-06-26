// SPDX-License-Identifier: MIT
//
// Behavioural test for the in-memory aether-forge package cache: cache (with the
// new-entry announcement + idempotent first-write-wins), query hit/miss, the fetch
// download-count increment, and aggregate stats.

package aethernet.forge

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ForgeServiceTest {

    @Test
    fun cacheQueryFetchStats() = runBlocking {
        val svc = InMemoryForgeService()
        var fired = 0
        svc.onNewEntryAnnounced = { fired++ }

        val e = svc.cache("npm:react@18.2.0", "hash1", 1000)
        assertEquals(0, e.downloadCount)
        assertEquals(1, fired)

        // Idempotent re-cache: first write wins, no second announcement.
        val e2 = svc.cache("npm:react@18.2.0", "hash2", 9999)
        assertEquals("hash1", e2.contentHash)
        assertEquals(1, fired)

        // Query hit + miss.
        assertEquals("hash1", svc.query("npm:react@18.2.0")?.contentHash)
        assertNull(svc.query("missing"))

        // Fetch increments the download counter; miss returns null.
        assertEquals(1, svc.fetch("npm:react@18.2.0")?.downloadCount)
        svc.fetch("npm:react@18.2.0")
        assertNull(svc.fetch("missing"))

        // Stats: bytes-saved = downloads * size; one entry catalogued.
        val st = svc.getStats()
        assertEquals(1, st.catalogueSize)
        assertEquals(2000L, st.totalBytesSaved) // 2 downloads * 1000 bytes
    }
}
