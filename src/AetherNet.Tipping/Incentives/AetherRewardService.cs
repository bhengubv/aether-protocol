// SPDX-License-Identifier: MIT

using AetherNet.ApiClients;
using AetherNet.Incentive;
using Microsoft.Extensions.Logging;

namespace AetherNet.Tipping.Incentives;

/// <summary>
/// Queues reward events to the local <see cref="IAetherRewardStore"/>, then
/// batch-syncs them (50 per call) to the backend via <see cref="IAetherApiClient"/>.
/// The on-device queue means a node earns XP for relay work performed offline and
/// reconciles it the next time it has connectivity.
/// </summary>
public sealed class AetherRewardService : IAetherRewardService
{
    private readonly IAetherRewardStore _store;
    private readonly IAetherApiClient _apiClient;
    private readonly ILogger<AetherRewardService> _logger;

    private const int BatchSize = 50;

    public AetherRewardService(
        IAetherRewardStore store,
        IAetherApiClient apiClient,
        ILogger<AetherRewardService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task QueueRewardAsync(string actionType, int xpAmount, string? description = null, Guid? referenceId = null)
    {
        await _store.QueueRewardAsync(actionType, xpAmount, description, referenceId);
        _logger.LogDebug("Queued reward: {Action} +{Xp}XP", actionType, xpAmount);
    }

    public async Task<int> GetPendingCountAsync()
        => await _store.GetPendingRewardCountAsync();

    public async Task<int> SyncToServerAsync(CancellationToken cancellationToken = default)
    {
        var totalSynced = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = await _store.GetUnsyncedRewardsAsync(BatchSize);
                if (batch.Count == 0) break;

                var payload = batch.Select(r => new
                {
                    action_type = r.ActionType,
                    xp_amount = r.XpAmount,
                    description = r.Description,
                    reference_id = r.ReferenceId,
                    created_at = r.CreatedAt
                }).ToList();

                var synced = await _apiClient.BatchSyncRewardsAsync(new { rewards = payload });

                if (synced > 0)
                {
                    await _store.MarkRewardsSyncedAsync(batch.Select(r => r.Id));
                    totalSynced += synced;
                    _logger.LogDebug("Synced {Count} rewards to server", synced);
                }
                else
                {
                    _logger.LogWarning("Server returned 0 synced for {Count} rewards — stopping batch", batch.Count);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reward sync failed after {Synced} — will retry next cycle", totalSynced);
        }

        return totalSynced;
    }
}
