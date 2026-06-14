// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Tipping.Models;
using Microsoft.Extensions.Logging;

namespace AetherNet.Tipping.Services;

/// <summary>
/// Maps a tipper's consistency standing to a quality-of-service preference. Known
/// consistent tippers earn a routing-quality boost (a preference, never an access
/// gate — non-tippers always get service). Reputations are cached in-memory and the
/// local node's tier is recomputed from its stored consistency score on refresh.
/// </summary>
public sealed class TipperQoSService : ITipperQoSService
{
    private readonly IAetherTipStore _store;
    private readonly ILocalNodeProvider _localNode;
    private readonly ILogger<TipperQoSService> _logger;
    private readonly ConcurrentDictionary<string, TipperReputation> _cache = new();

    public TipperQoSService(IAetherTipStore store, ILocalNodeProvider localNode, ILogger<TipperQoSService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _localNode = localNode ?? throw new ArgumentNullException(nameof(localNode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public short GetQoSBoost(string tipperUhid)
    {
        var tier = GetTier(tipperUhid);
        return tier switch
        {
            QoSTier.Bronze => TipPolicyConstants.QoSBoostBronze,
            QoSTier.Silver => TipPolicyConstants.QoSBoostSilver,
            QoSTier.Gold => TipPolicyConstants.QoSBoostGold,
            _ => 0
        };
    }

    public QoSTier GetTier(string tipperUhid)
    {
        if (_cache.TryGetValue(tipperUhid, out var rep))
            return rep.Tier;

        return QoSTier.Standard;
    }

    public async Task RefreshScoresAsync()
    {
        try
        {
            // Load all known tipper reputations from local storage
            // In production this would also pull from server
            var localUhid = await _localNode.GetLocalUhidAsync();
            if (string.IsNullOrEmpty(localUhid)) return;

            var rep = await _store.GetTipperReputationAsync(localUhid);
            if (rep is not null)
            {
                // Recalculate tier from consistency score
                rep.Tier = rep.ConsistencyScore switch
                {
                    >= TipPolicyConstants.QoSGoldThreshold => QoSTier.Gold,
                    >= TipPolicyConstants.QoSSilverThreshold => QoSTier.Silver,
                    >= TipPolicyConstants.QoSBronzeThreshold => QoSTier.Bronze,
                    _ => QoSTier.Standard
                };

                _cache[localUhid] = rep;
                await _store.SaveTipperReputationAsync(rep);
            }

            _logger.LogDebug("QoS scores refreshed — {Count} tippers cached", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QoS score refresh failed");
        }
    }
}
