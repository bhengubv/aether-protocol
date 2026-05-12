// SPDX-License-Identifier: MIT
package aether.dtn

import aether.models.DtnBundle
import aether.models.NodeCapabilities
import aether.models.PeerInfo
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

// ── helpers ───────────────────────────────────────────────────────────────────

private fun carrier(
    uhid: String,
    geohash: String? = null,
    reliabilityScore: Int = 50,
) = PeerInfo(
    uhid             = uhid,
    identityKey      = byteArrayOf(),
    capabilities     = NodeCapabilities(dtnCarrier = true),
    reliabilityScore = reliabilityScore,
    geohash          = geohash,
)

private fun nonCarrier(uhid: String) = PeerInfo(
    uhid        = uhid,
    identityKey = byteArrayOf(),
    capabilities = NodeCapabilities(dtnCarrier = false),
)

private fun bundle(
    sender: String = "alice",
    recipient: String = "bob",
    priority: Int = 1,          // 1 = Normal
    copyCount: Int = 1,
    maxCopies: Int = 3,
    recipientLastGeohash: String? = null,
) = DtnBundle(
    senderUhid           = sender,
    recipientUhid        = recipient,
    encryptedPayload     = byteArrayOf(),
    priority             = priority,
    copyCount            = copyCount,
    maxCopies            = maxCopies,
    recipientLastGeohash = recipientLastGeohash,
)

// ── GeohashEpidemicStrategy ────────────────────────────────────────────────────

class ReplicationStrategyTest {

    private val strategy = GeohashEpidemicStrategy()

    // ── slots exhausted ───────────────────────────────────────────────────────

    @Test
    fun `returns empty when copy count equals max copies`() {
        val b    = bundle(copyCount = 3, maxCopies = 3)
        val peer = carrier("peer-1")
        val result = strategy.selectTargets(b, listOf(peer), null)
        assertTrue(result.isEmpty())
    }

    @Test
    fun `returns empty when copy count exceeds max copies`() {
        val b    = bundle(copyCount = 5, maxCopies = 3)
        val peer = carrier("peer-1")
        val result = strategy.selectTargets(b, listOf(peer), null)
        assertTrue(result.isEmpty())
    }

    // ── empty/ineligible peer lists ───────────────────────────────────────────

    @Test
    fun `returns empty for empty peer list`() {
        val result = strategy.selectTargets(bundle(), emptyList(), null)
        assertTrue(result.isEmpty())
    }

    @Test
    fun `excludes peers without dtnCarrier capability`() {
        val result = strategy.selectTargets(bundle(), listOf(nonCarrier("nc-1")), null)
        assertTrue(result.isEmpty())
    }

    @Test
    fun `excludes empty-uhid peers`() {
        val peer   = PeerInfo(uhid = "", identityKey = byteArrayOf(),
                              capabilities = NodeCapabilities(dtnCarrier = true))
        val result = strategy.selectTargets(bundle(), listOf(peer), null)
        assertTrue(result.isEmpty())
    }

    @Test
    fun `excludes bundle sender from targets`() {
        val peer   = carrier(uhid = "alice") // same as bundle sender
        val result = strategy.selectTargets(bundle(sender = "alice"), listOf(peer), null)
        assertTrue(result.isEmpty())
    }

    // ── SOS priority (3) floods ───────────────────────────────────────────────

    @Test
    fun `SOS bundle floods to all eligible carriers up to slots`() {
        val sosBun = bundle(priority = 3, copyCount = 1, maxCopies = 5)
        val peers  = (1..4).map { carrier("peer-$it") }
        val result = strategy.selectTargets(sosBun, peers, null)
        assertEquals(4, result.size, "all 4 eligible carriers should receive SOS")
    }

    @Test
    fun `SOS bundle respects slot cap`() {
        val sosBun = bundle(priority = 3, copyCount = 4, maxCopies = 5) // 1 slot left
        val peers  = (1..3).map { carrier("peer-$it") }
        val result = strategy.selectTargets(sosBun, peers, null)
        assertEquals(1, result.size)
    }

    // ── geohash-aware routing ─────────────────────────────────────────────────

    @Test
    fun `prefers peer with longer geohash prefix match to recipient`() {
        // recipient is at "gcpv"; local is at "gc00"; peer-close is at "gcpv"; peer-far is at "gcAA"
        // peer-close shares 4 chars, local shares 2 chars → peer-close qualifies
        val b = bundle(recipientLastGeohash = "gcpv", copyCount = 1, maxCopies = 3)
        val peerClose = carrier("close", geohash = "gcpv")   // 4 chars shared
        val peerFar   = carrier("far",   geohash = "gcAA")   // 2 chars shared
        val localGeohash = "gc00"                            // 2 chars shared

        val result = strategy.selectTargets(b, listOf(peerClose, peerFar), localGeohash)
        // peerFar shares same prefix length as local (2 = 2) → might include; peerClose is definitely included
        assertTrue(result.contains("close"), "peer with closer geohash must be selected")
    }

    @Test
    fun `excludes peers geographically farther than local`() {
        // local geohash shares 4 chars; peer shares only 1 char → peer is farther, should be excluded
        val b = bundle(recipientLastGeohash = "gcpvxy", copyCount = 1, maxCopies = 3)
        val farPeer  = carrier("far", geohash = "gA")     // 1 char shared
        val localGeo = "gcpv"                             // 4 chars shared

        val result = strategy.selectTargets(b, listOf(farPeer), localGeo)
        assertTrue(result.isEmpty(), "peer farther than local should be excluded")
    }

    // ── fallback: no recipient geohash ────────────────────────────────────────

    @Test
    fun `without recipient geohash selects by reliability score descending`() {
        val b    = bundle(copyCount = 1, maxCopies = 2) // 1 slot
        val low  = carrier("low",  reliabilityScore = 20)
        val high = carrier("high", reliabilityScore = 90)
        val result = strategy.selectTargets(b, listOf(low, high), null)
        assertEquals(1, result.size)
        assertEquals("high", result[0])
    }

    @Test
    fun `respects slot cap in reliability fallback`() {
        val b    = bundle(copyCount = 1, maxCopies = 2) // 1 slot
        val peers = (1..5).map { carrier("peer-$it") }
        val result = strategy.selectTargets(b, peers, null)
        assertEquals(1, result.size)
    }
}
