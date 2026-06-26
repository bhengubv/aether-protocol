// SPDX-License-Identifier: MIT
//
// Offline-capable P2P marketplace (aether-market Phase-2 extension). Kotlin port of
// AetherNet.Market.IMarketService / InMemoryMarketService and the listing/escrow models. Listings are
// geo-pinned (distributed via aether-space) and may carry a VaultManifest escrow for document-backed
// sales; trades run a two-party confirm state machine. Requires aether-space and aether-vault.

package aethernet.market

import aethernet.vault.VaultManifest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/** Category of a [MarketListing]. */
enum class MarketCategory(val value: Byte) {
    Goods(0), Services(1), Labour(2), Land(3), Documents(4);

    companion object {
        fun fromValue(value: Byte): MarketCategory? = entries.find { it.value == value }
    }
}

/** Role of the node confirming a trade step. */
enum class TradeRole(val value: Byte) { Buyer(0), Seller(1) }

/** State machine for a [TradeEscrow]. */
enum class TradeState(val value: Byte) {
    Initiated(0), BuyerConfirmed(1), SellerConfirmed(2), Complete(3), Disputed(4)
}

/**
 * A geo-pinned market listing dropped by a verified seller. May include a [VaultManifest] escrow for
 * document-backed sales (land deeds, certificates).
 */
data class MarketListing(
    val listingId: String,
    val sellerUhid: String,
    val sellerPoVScore: PoVScore? = null,
    val title: String,
    val description: String,
    val priceZAR: Double, // South African Rand
    val geoHash: String,  // 6-char geohash of the listing location
    val category: MarketCategory,
    val escrowManifest: VaultManifest? = null,
    val createdAtUnixMs: Long,
    val expiresAtUnixMs: Long,
) {
    /** Whether the listing has reached its expiry. */
    val isExpired: Boolean get() = System.currentTimeMillis() >= expiresAtUnixMs
}

/** Tracks the lifecycle of a marketplace trade. */
data class TradeEscrow(
    val escrowId: String,
    val listingId: String,
    val buyerUhid: String,
    val sellerUhid: String,
    var state: TradeState,
    val vaultManifest: VaultManifest? = null,
    val createdAtUnixMs: Long,
)

/** The offline-capable P2P marketplace. */
interface MarketService {
    fun createListing(sellerUhid: String, title: String, description: String, priceZAR: Double,
                      geoHash: String, category: MarketCategory): MarketListing
    fun browseNearby(centerGeoHash: String, radiusCells: Int = 2): List<MarketListing>
    fun search(query: String, category: MarketCategory? = null): List<MarketListing>
    fun initiateTrade(listing: MarketListing, buyerUhid: String): TradeEscrow
    fun confirmTrade(escrow: TradeEscrow, role: TradeRole): TradeEscrow
    fun dispute(escrow: TradeEscrow, reason: String)
}

/** In-memory [MarketService] for testing / single-node use; state lost on restart. */
class InMemoryMarketService : MarketService {
    private val listings = ConcurrentHashMap<String, MarketListing>()
    private val escrows = ConcurrentHashMap<String, TradeEscrow>()

    /** Fired when a new listing is received from the mesh or created locally. */
    var onListingReceived: ((MarketListing) -> Unit)? = null

    private companion object {
        const val THIRTY_DAYS_MS = 30L * 24 * 60 * 60 * 1000
    }

    override fun createListing(sellerUhid: String, title: String, description: String, priceZAR: Double,
                              geoHash: String, category: MarketCategory): MarketListing {
        val now = System.currentTimeMillis()
        val listing = MarketListing(
            listingId = UUID.randomUUID().toString(),
            sellerUhid = sellerUhid,
            title = title,
            description = description,
            priceZAR = priceZAR,
            geoHash = geoHash,
            category = category,
            createdAtUnixMs = now,
            expiresAtUnixMs = now + THIRTY_DAYS_MS,
        )
        listings[listing.listingId] = listing
        onListingReceived?.invoke(listing)
        return listing
    }

    override fun browseNearby(centerGeoHash: String, radiusCells: Int): List<MarketListing> {
        val prefixLen = minOf(centerGeoHash.length, maxOf(1, centerGeoHash.length - radiusCells + 1))
        val prefix = centerGeoHash.substring(0, prefixLen).lowercase()
        return listings.values.filter { !it.isExpired && it.geoHash.lowercase().startsWith(prefix) }
    }

    override fun search(query: String, category: MarketCategory?): List<MarketListing> {
        val q = query.lowercase()
        return listings.values.filter {
            !it.isExpired &&
                (category == null || it.category == category) &&
                (it.title.lowercase().contains(q) || it.description.lowercase().contains(q))
        }
    }

    override fun initiateTrade(listing: MarketListing, buyerUhid: String): TradeEscrow {
        val escrow = TradeEscrow(
            escrowId = UUID.randomUUID().toString(),
            listingId = listing.listingId,
            buyerUhid = buyerUhid,
            sellerUhid = listing.sellerUhid,
            state = TradeState.Initiated,
            vaultManifest = listing.escrowManifest,
            createdAtUnixMs = System.currentTimeMillis(),
        )
        escrows[escrow.escrowId] = escrow
        return escrow
    }

    override fun confirmTrade(escrow: TradeEscrow, role: TradeRole): TradeEscrow {
        escrow.state = if (role == TradeRole.Buyer) {
            TradeState.BuyerConfirmed
        } else if (escrow.state == TradeState.BuyerConfirmed) {
            TradeState.Complete
        } else {
            TradeState.SellerConfirmed
        }
        escrows[escrow.escrowId] = escrow
        return escrow
    }

    override fun dispute(escrow: TradeEscrow, reason: String) {
        escrow.state = TradeState.Disputed
        escrows[escrow.escrowId] = escrow
    }
}
