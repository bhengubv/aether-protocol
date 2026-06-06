// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using AetherMesh.Market.Models;

namespace AetherMesh.Market;

/// <summary>
/// In-memory <see cref="IMarketService"/> implementation for testing and
/// single-node scenarios.
/// </summary>
public sealed class InMemoryMarketService : IMarketService
{
    private readonly ConcurrentDictionary<Guid, MarketListing> _listings = new();
    private readonly ConcurrentDictionary<Guid, TradeEscrow>   _escrows  = new();

    /// <inheritdoc/>
    public event EventHandler<MarketListing>? ListingReceived;

    event EventHandler<MarketListing> IMarketService.ListingReceived
    {
        add    => ListingReceived += value;
        remove => ListingReceived -= value;
    }

    /// <inheritdoc/>
    public Task<MarketListing> CreateListingAsync(
        string sellerUhid,
        string title,
        string description,
        decimal priceZAR,
        string geoHash,
        MarketCategory category,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var listing = new MarketListing
        {
            SellerUhid   = sellerUhid,
            Title        = title,
            Description  = description,
            PriceZAR     = priceZAR,
            GeoHash      = geoHash,
            Category     = category,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
        };

        _listings[listing.ListingId] = listing;
        ListingReceived?.Invoke(this, listing);
        return Task.FromResult(listing);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MarketListing>> BrowseNearbyAsync(
        string centerGeoHash,
        int radiusCells = 2,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Filter by geohash prefix match (length = centerGeoHash.Length - radiusCells + 1,
        // floored at 1) and not expired.
        int prefixLen = Math.Max(1, centerGeoHash.Length - radiusCells + 1);
        var prefix = centerGeoHash[..Math.Min(prefixLen, centerGeoHash.Length)];

        var results = _listings.Values
            .Where(l => !l.IsExpired && l.GeoHash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<MarketListing>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MarketListing>> SearchAsync(
        string query,
        MarketCategory? category = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var results = _listings.Values
            .Where(l => !l.IsExpired)
            .Where(l => category == null || l.Category == category)
            .Where(l => l.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || l.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<MarketListing>>(results);
    }

    /// <inheritdoc/>
    public Task<TradeEscrow> InitiateTradeAsync(
        MarketListing listing,
        string buyerUhid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var escrow = new TradeEscrow
        {
            ListingId    = listing.ListingId,
            BuyerUhid    = buyerUhid,
            SellerUhid   = listing.SellerUhid,
            State        = TradeState.Initiated,
            VaultManifest = listing.EscrowManifest,
            CreatedAtUtc = DateTime.UtcNow,
        };

        _escrows[escrow.EscrowId] = escrow;
        return Task.FromResult(escrow);
    }

    /// <inheritdoc/>
    public Task<TradeEscrow> ConfirmTradeAsync(
        TradeEscrow escrow,
        TradeRole role,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (role == TradeRole.Buyer)
        {
            escrow.State = TradeState.BuyerConfirmed;
        }
        else // Seller
        {
            escrow.State = escrow.State == TradeState.BuyerConfirmed
                ? TradeState.Complete
                : TradeState.SellerConfirmed;
        }

        _escrows[escrow.EscrowId] = escrow;
        return Task.FromResult(escrow);
    }

    /// <inheritdoc/>
    public Task DisputeAsync(TradeEscrow escrow, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        escrow.State = TradeState.Disputed;
        _escrows[escrow.EscrowId] = escrow;
        return Task.CompletedTask;
    }
}
