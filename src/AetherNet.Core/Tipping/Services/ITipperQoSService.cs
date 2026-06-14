// SPDX-License-Identifier: MIT

using AetherNet.Tipping.Models;

namespace AetherNet.Tipping.Services;

/// <summary>
/// Maps a tipper's consistency standing to a quality-of-service preference. Known
/// consistent tippers earn a routing-quality boost (a preference, never an access
/// gate — non-tippers always get service). Scores are cached in-memory and refreshed
/// from local storage.
/// </summary>
public interface ITipperQoSService
{
    /// <summary>
    /// QoS boost to add to a route's quality score for this tipper, derived from their
    /// <see cref="QoSTier"/>. Zero for <see cref="QoSTier.Standard"/> (the default for
    /// any unknown or non-tipping peer).
    /// </summary>
    short GetQoSBoost(string tipperUhid);

    /// <summary>The tipper's current QoS tier (cached); <see cref="QoSTier.Standard"/> if unknown.</summary>
    QoSTier GetTier(string tipperUhid);

    /// <summary>
    /// Recompute the local node's tier from its stored consistency score and refresh
    /// the in-memory cache. Never throws.
    /// </summary>
    Task RefreshScoresAsync();
}
