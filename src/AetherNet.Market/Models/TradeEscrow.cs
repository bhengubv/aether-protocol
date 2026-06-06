// SPDX-License-Identifier: MIT
using AetherNet.Vault.Models;

namespace AetherNet.Market.Models;

/// <summary>Tracks the lifecycle of a marketplace trade.</summary>
public sealed class TradeEscrow
{
    public Guid EscrowId { get; set; } = Guid.NewGuid();
    public Guid ListingId { get; set; }
    public string BuyerUhid { get; set; } = string.Empty;
    public string SellerUhid { get; set; } = string.Empty;
    public TradeState State { get; set; } = TradeState.Initiated;
    public VaultManifest? VaultManifest { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
