// SPDX-License-Identifier: MIT
namespace AetherNet.Market.Models;

/// <summary>Proof-of-Vicinity trust score for a node.</summary>
public sealed class PoVScore
{
    /// <summary>UHID of the scored node.</summary>
    public string Uhid { get; set; } = string.Empty;

    /// <summary>Number of distinct human witnesses who have issued PoV tokens to this node.</summary>
    public int UniqueWitnesses { get; set; }

    /// <summary>
    /// Weighted score (0.0–1.0, decays over time, penalised on vouched-for defection).
    /// </summary>
    public double WeightedScore { get; set; }

    /// <summary>UTC timestamp of the most recent score update.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
