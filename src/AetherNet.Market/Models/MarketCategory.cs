// SPDX-License-Identifier: MIT
namespace AetherNet.Market.Models;

/// <summary>Category of a <see cref="MarketListing"/>.</summary>
public enum MarketCategory : byte
{
    Goods = 0,
    Services = 1,
    Labour = 2,
    Land = 3,
    Documents = 4,
}
