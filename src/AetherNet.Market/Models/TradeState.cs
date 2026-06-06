// SPDX-License-Identifier: MIT
namespace AetherNet.Market.Models;

/// <summary>State machine for a <see cref="TradeEscrow"/>.</summary>
public enum TradeState : byte
{
    Initiated = 0,
    BuyerConfirmed = 1,
    SellerConfirmed = 2,
    Complete = 3,
    Disputed = 4,
}
