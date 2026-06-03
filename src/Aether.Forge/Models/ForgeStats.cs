// SPDX-License-Identifier: MIT
namespace Aether.Forge.Models;

/// <summary>Aggregate statistics for the local Forge mesh cache.</summary>
public sealed class ForgeStats
{
    /// <summary>Total bytes served from the mesh cache instead of the internet.</summary>
    public long TotalBytesSaved { get; set; }

    /// <summary>Total number of distinct peers that have been served cached artifacts.</summary>
    public int TotalPeersServed { get; set; }

    /// <summary>Number of distinct packages in the local cache.</summary>
    public int CatalogueSize { get; set; }

    /// <summary>Top packages by download count (most popular first, up to 10).</summary>
    public IReadOnlyList<ForgeEntry> TopPackages { get; set; } = [];
}
