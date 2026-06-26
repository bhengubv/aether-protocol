// SPDX-License-Identifier: MIT
//
// Behavioural test for the in-memory aether-space breadcrumb noticeboard: drop
// (TTL clamp + emergency override + received callback), geohash-prefix scan,
// creator-only delete, and prune.

package aethernet.space

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class SpaceServiceTest {

    @Test
    fun dropScanDeletePrune() = runBlocking {
        val svc = InMemorySpaceService()
        var received = 0
        svc.onBreadcrumbReceived = { received++ }

        val a = svc.drop("k3vf9z", "hashA", "anchor1", BreadcrumbType.NOTICE, 24)
        assertEquals(24, a.ttlHours)
        assertEquals(1, received)

        // Emergency breadcrumbs get the fixed 720h TTL.
        val e = svc.drop("k3vf9z", "hashE", "anchor1", BreadcrumbType.EMERGENCY, 1)
        assertEquals(720, e.ttlHours)

        // Scan: prefix-proximity hit vs a far cell.
        assertEquals(2, svc.scan("k3vf9z", 1).size)
        assertEquals(0, svc.scan("xxxxxx", 1).size)

        // Creator-only delete.
        assertFalse(svc.delete(a, "wrong"))
        assertTrue(svc.delete(a, "anchor1"))
        assertEquals(1, svc.scan("k3vf9z", 1).size)

        // Nothing is past its TTL yet.
        assertEquals(0, svc.pruneExpired())
    }
}
