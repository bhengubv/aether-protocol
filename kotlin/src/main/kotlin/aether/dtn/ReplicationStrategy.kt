// SPDX-License-Identifier: MIT

package aether.dtn

import aether.models.DtnBundle
import aether.models.PeerInfo

/**
 * Decides which connected peers should receive a copy of a bundle on the next
 * replication pass. Default GeohashEpidemicStrategy matches the C# reference.
 */
interface ReplicationStrategy {
    fun selectTargets(
        bundle: DtnBundle,
        peers: List<PeerInfo>,
        localGeohash: String?
    ): List<String>
}

/**
 * Default geohash-aware epidemic strategy.
 *
 * SOS bundles fan out to every eligible carrier up to the copy cap. Normal
 * bundles prefer peers whose geohash shares a longer prefix with the recipient's
 * last-known geohash than the local node — i.e. peers at least as close to the
 * recipient. Ties broken by reliability score.
 */
class GeohashEpidemicStrategy : ReplicationStrategy {
    override fun selectTargets(
        bundle: DtnBundle,
        peers: List<PeerInfo>,
        localGeohash: String?
    ): List<String> {
        val slots = bundle.maxCopies - bundle.copyCount
        if (slots <= 0) return emptyList()

        val eligible = peers.filter {
            it.uhid.isNotEmpty()
                && it.uhid != bundle.senderUhid
                && it.capabilities.dtnCarrier
        }
        if (eligible.isEmpty()) return emptyList()

        // SOS priority = 3
        if (bundle.priority == 3) {
            return eligible.take(slots).map { it.uhid }
        }

        if (!bundle.recipientLastGeohash.isNullOrEmpty()) {
            val recipient = bundle.recipientLastGeohash
            val localProx = sharedPrefix(localGeohash, recipient)
            return eligible
                .map { p -> Triple(sharedPrefix(p.geohash, recipient), p.reliabilityScore, p) }
                .filter { (prox, _, _) -> prox >= localProx }
                .sortedWith(compareByDescending<Triple<Int, Int, PeerInfo>> { it.first }
                    .thenByDescending { it.second })
                .take(slots)
                .map { it.third.uhid }
        }

        return eligible
            .sortedByDescending { it.reliabilityScore }
            .take(slots)
            .map { it.uhid }
    }

    private fun sharedPrefix(a: String?, b: String): Int {
        if (a.isNullOrEmpty() || b.isEmpty()) return 0
        val n = minOf(a.length, b.length)
        var i = 0
        while (i < n && a[i] == b[i]) i++
        return i
    }
}
