// SPDX-License-Identifier: MIT
using Aether.Market.Models;

namespace Aether.Market;

/// <summary>
/// Offline-capable P2P marketplace (aether-market Phase-2 extension).
/// Requires both aether-space and aether-vault.
/// </summary>
public interface IMarketService
{
    Task<MarketListing> CreateListingAsync(string sellerUhid, string title, string description,
        decimal priceZAR, string geoHash, MarketCategory category,
        CancellationToken ct = default);

    Task<IReadOnlyList<MarketListing>> BrowseNearbyAsync(string centerGeoHash,
        int radiusCells = 2, CancellationToken ct = default);

    Task<IReadOnlyList<MarketListing>> SearchAsync(string query,
        MarketCategory? category = null, CancellationToken ct = default);

    Task<TradeEscrow> InitiateTradeAsync(MarketListing listing, string buyerUhid,
        CancellationToken ct = default);

    /// <summary>
    /// Both buyer and seller must confirm. Returns updated escrow state.
    /// BuyerConfirmed → SellerConfirmed → Complete (or Disputed).
    /// </summary>
    Task<TradeEscrow> ConfirmTradeAsync(TradeEscrow escrow, TradeRole role,
        CancellationToken ct = default);

    Task DisputeAsync(TradeEscrow escrow, string reason, CancellationToken ct = default);

    /// <summary>Fired when a new listing is received from the mesh or created locally.</summary>
    event EventHandler<MarketListing> ListingReceived;
}
