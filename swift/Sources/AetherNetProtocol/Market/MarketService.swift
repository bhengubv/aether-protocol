// SPDX-License-Identifier: MIT
//
// Offline-capable P2P marketplace (aether-market Phase-2 extension). Swift port of
// AetherNet.Market.IMarketService / InMemoryMarketService and the listing/escrow models. Listings are
// geo-pinned (distributed via aether-space) and may carry a VaultManifest escrow for document-backed
// sales; trades run a two-party confirm state machine. Requires aether-space and aether-vault.

import Foundation

/// Category of a `MarketListing`.
public enum MarketCategory: UInt8 {
    case goods = 0
    case services = 1
    case labour = 2
    case land = 3
    case documents = 4
}

/// Role of the node confirming a trade step.
public enum TradeRole: UInt8 {
    case buyer = 0
    case seller = 1
}

/// State machine for a `TradeEscrow`.
public enum TradeState: UInt8 {
    case initiated = 0
    case buyerConfirmed = 1
    case sellerConfirmed = 2
    case complete = 3
    case disputed = 4
}

/// A geo-pinned market listing dropped by a verified seller. May include a `VaultManifest` escrow for
/// document-backed sales (land deeds, certificates).
public struct MarketListing {
    public var listingId: String
    public var sellerUhid: String
    public var sellerPoVScore: PoVScore?
    public var title: String
    public var description: String
    public var priceZAR: Double // South African Rand
    public var geoHash: String  // 6-char geohash of the listing location
    public var category: MarketCategory
    public var escrowManifest: VaultManifest?
    public var createdAtUtc: Date
    public var expiresAtUtc: Date

    /// Whether the listing has reached its expiry.
    public var isExpired: Bool { Date() >= expiresAtUtc }
}

/// Tracks the lifecycle of a marketplace trade.
public struct TradeEscrow {
    public var escrowId: String
    public var listingId: String
    public var buyerUhid: String
    public var sellerUhid: String
    public var state: TradeState
    public var vaultManifest: VaultManifest?
    public var createdAtUtc: Date
}

/// The offline-capable P2P marketplace.
public protocol MarketServiceProtocol {
    func createListing(sellerUhid: String, title: String, description: String, priceZAR: Double,
                       geoHash: String, category: MarketCategory) async -> MarketListing
    func browseNearby(centerGeoHash: String, radiusCells: Int) async -> [MarketListing]
    func search(query: String, category: MarketCategory?) async -> [MarketListing]
    func initiateTrade(listing: MarketListing, buyerUhid: String) async -> TradeEscrow
    func confirmTrade(escrow: TradeEscrow, role: TradeRole) async -> TradeEscrow
    func dispute(escrow: TradeEscrow, reason: String) async -> TradeEscrow
}

/// In-memory `MarketServiceProtocol` for testing / single-node use; state lost on deinit.
public final class InMemoryMarketService: MarketServiceProtocol {
    private var listings: [String: MarketListing] = [:]
    private var escrows: [String: TradeEscrow] = [:]

    /// Fired when a new listing is received from the mesh or created locally.
    public var onListingReceived: ((MarketListing) -> Void)?

    public init() {}

    private static let thirtyDays: TimeInterval = 30 * 24 * 60 * 60

    public func createListing(sellerUhid: String, title: String, description: String, priceZAR: Double,
                              geoHash: String, category: MarketCategory) async -> MarketListing {
        let now = Date()
        let listing = MarketListing(
            listingId: UUID().uuidString,
            sellerUhid: sellerUhid,
            sellerPoVScore: nil,
            title: title,
            description: description,
            priceZAR: priceZAR,
            geoHash: geoHash,
            category: category,
            escrowManifest: nil,
            createdAtUtc: now,
            expiresAtUtc: now.addingTimeInterval(Self.thirtyDays)
        )
        listings[listing.listingId] = listing
        onListingReceived?(listing)
        return listing
    }

    public func browseNearby(centerGeoHash: String, radiusCells: Int = 2) async -> [MarketListing] {
        let prefixLen = min(centerGeoHash.count, max(1, centerGeoHash.count - radiusCells + 1))
        let prefix = String(centerGeoHash.prefix(prefixLen)).lowercased()
        return listings.values.filter { !$0.isExpired && $0.geoHash.lowercased().hasPrefix(prefix) }
    }

    public func search(query: String, category: MarketCategory? = nil) async -> [MarketListing] {
        let q = query.lowercased()
        return listings.values.filter {
            !$0.isExpired &&
                (category == nil || $0.category == category) &&
                ($0.title.lowercased().contains(q) || $0.description.lowercased().contains(q))
        }
    }

    public func initiateTrade(listing: MarketListing, buyerUhid: String) async -> TradeEscrow {
        let escrow = TradeEscrow(
            escrowId: UUID().uuidString,
            listingId: listing.listingId,
            buyerUhid: buyerUhid,
            sellerUhid: listing.sellerUhid,
            state: .initiated,
            vaultManifest: listing.escrowManifest,
            createdAtUtc: Date()
        )
        escrows[escrow.escrowId] = escrow
        return escrow
    }

    public func confirmTrade(escrow: TradeEscrow, role: TradeRole) async -> TradeEscrow {
        var updated = escrow
        if role == .buyer {
            updated.state = .buyerConfirmed
        } else {
            updated.state = (escrow.state == .buyerConfirmed) ? .complete : .sellerConfirmed
        }
        escrows[updated.escrowId] = updated
        return updated
    }

    @discardableResult
    public func dispute(escrow: TradeEscrow, reason: String) async -> TradeEscrow {
        var updated = escrow
        updated.state = .disputed
        escrows[updated.escrowId] = updated
        return updated
    }
}
