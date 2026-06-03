// SPDX-License-Identifier: MIT
using Aether.Vault.Models;

namespace Aether.Market.Models;

/// <summary>
/// A geo-pinned market listing dropped by a verified seller.
/// Listings are distributed via <c>aether-space</c> and may include a
/// <c>VaultManifest</c> escrow for document-backed sales (land deeds, certificates).
/// </summary>
public sealed class MarketListing
{
    public Guid ListingId { get; set; } = Guid.NewGuid();
    public string SellerUhid { get; set; } = string.Empty;

    /// <summary>Seller's PoV trust score at the time of listing.</summary>
    public PoVScore SellerPoVScore { get; set; } = new();

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Price in South African Rand.</summary>
    public decimal PriceZAR { get; set; }

    /// <summary>6-character geohash of the listing location.</summary>
    public string GeoHash { get; set; } = string.Empty;

    public MarketCategory Category { get; set; } = MarketCategory.Goods;

    /// <summary>Optional Vault escrow for document-backed transactions.</summary>
    public VaultManifest? EscrowManifest { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(30);

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
}
