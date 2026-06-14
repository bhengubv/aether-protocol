// SPDX-License-Identifier: MIT

using AetherNet.ApiClients;
using AetherNet.Incentive;
using AetherNet.Security.Services;
using AetherNet.Tipping.Models;
using Microsoft.Extensions.Logging;

namespace AetherNet.Tipping.Services;

/// <summary>
/// On-device tipping client. Validates a tip against the regulated policy, queues it
/// to the local <see cref="IAetherTipStore"/> (so it survives offline), credits the
/// tipper XP, and batch-syncs queued tips to the backend which settles them into both
/// parties' SDPKT wallets.
///
/// <para>
/// A tip is never an access gate. <see cref="TipNodeAsync"/> returns false (and queues
/// nothing) only when the local node is uninitialised, the recipient is not a
/// registered tip-accepting operator, the amount is outside the traffic type's policy
/// band, or the per-tipper daily cap would be exceeded — service itself is unaffected.
/// </para>
/// </summary>
public sealed class TippingService : ITippingService
{
    private readonly IAetherTipStore _store;
    private readonly ILocalNodeProvider _localNode;
    private readonly IAetherApiClient _apiClient;
    private readonly IAetherRewardService _rewards;
    private readonly ILogger<TippingService> _logger;

    public TippingService(
        IAetherTipStore store,
        ILocalNodeProvider localNode,
        IAetherApiClient apiClient,
        IAetherRewardService rewards,
        ILogger<TippingService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _localNode = localNode ?? throw new ArgumentNullException(nameof(localNode));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _rewards = rewards ?? throw new ArgumentNullException(nameof(rewards));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> TipNodeAsync(string recipientUhid, decimal amount, TipTrafficType trafficType, Guid? referenceId = null)
    {
        // Get local node UHID
        var localUhid = await _localNode.GetLocalUhidAsync();
        if (string.IsNullOrEmpty(localUhid))
        {
            _logger.LogWarning("Cannot tip — local node not initialized");
            return false;
        }

        // Validate recipient is registered operator
        var operator_ = await _store.GetNodeOperatorAsync(recipientUhid);
        if (operator_ is null || !operator_.IsRegistered || !operator_.AcceptsTips)
        {
            _logger.LogDebug("Tip rejected — recipient {Uhid} is not a registered operator or doesn't accept tips",
                LogSanitizer.SanitizeUhid(recipientUhid));
            return false;
        }

        // Validate against policy
        var policy = await GetPolicyAsync(trafficType);
        if (policy is not null && policy.IsEnabled)
        {
            if (amount < policy.MinAmount || amount > policy.MaxAmount)
            {
                _logger.LogDebug("Tip rejected — amount {Amount} outside policy bounds [{Min}, {Max}]",
                    amount, policy.MinAmount, policy.MaxAmount);
                return false;
            }
        }

        // Check daily cap
        var dailyTotal = await _store.GetDailyTipTotalAsync(localUhid);
        var dailyCap = policy?.DailyCapPerTipper ?? TipPolicyConstants.DefaultDailyCapZar;
        if (dailyTotal + amount > dailyCap)
        {
            _logger.LogDebug("Tip rejected — daily cap exceeded ({Daily} + {Amount} > {Cap})",
                dailyTotal, amount, dailyCap);
            return false;
        }

        // Queue tip locally
        var tip = new LocalTipTransaction
        {
            TipperUhid = localUhid,
            RecipientUhid = recipientUhid,
            Amount = amount,
            TrafficType = trafficType,
            ReferenceId = referenceId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _store.QueueTipAsync(tip);

        // Queue XP reward
        await _rewards.QueueRewardAsync(AetherRewardActions.MeshTip, TipPolicyConstants.XpMeshTip,
            $"Tip {amount:F2} ZAR for {trafficType}", referenceId);

        _logger.LogInformation("Tip queued: {Amount} ZAR to {Recipient} for {TrafficType}",
            amount, LogSanitizer.SanitizeUhid(recipientUhid), trafficType);
        return true;
    }

    public async Task<TipPolicy?> GetPolicyAsync(TipTrafficType trafficType)
    {
        var policies = await _store.GetTipPoliciesAsync();
        var policy = policies.FirstOrDefault(p => p.TrafficType == trafficType);

        // Return default if no cached policy
        if (policy is null)
        {
            return new TipPolicy
            {
                TrafficType = trafficType,
                MinAmount = TipPolicyConstants.DefaultTipMinZar,
                MaxAmount = TipPolicyConstants.DefaultTipMaxZar,
                DailyCapPerTipper = TipPolicyConstants.DefaultDailyCapZar,
                SuggestedAmount = GetDefaultSuggestedAmount(trafficType),
                IsEnabled = true
            };
        }

        return policy;
    }

    public async Task<decimal> GetDailyTotalAsync()
    {
        var localUhid = await _localNode.GetLocalUhidAsync();
        if (string.IsNullOrEmpty(localUhid)) return 0m;
        return await _store.GetDailyTipTotalAsync(localUhid);
    }

    public async Task<int> GetPendingTipCountAsync()
    {
        var tips = await _store.GetUnsyncedTipsAsync(int.MaxValue);
        return tips.Count;
    }

    public async Task<int> SyncTipsToServerAsync(CancellationToken ct = default)
    {
        var totalSynced = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await _store.GetUnsyncedTipsAsync(TipPolicyConstants.TipSyncBatchSize);
                if (batch.Count == 0) break;

                var payload = batch.Select(t => new
                {
                    tipper_uhid = t.TipperUhid,
                    recipient_uhid = t.RecipientUhid,
                    amount = t.Amount,
                    currency = "ZAR",
                    traffic_type = t.TrafficType.ToString(),
                    reference_id = t.ReferenceId,
                    created_at = t.CreatedAt
                }).ToList();

                var synced = await _apiClient.BatchSyncTipsAsync(new { tips = payload });

                if (synced > 0)
                {
                    await _store.MarkTipsSyncedAsync(batch.Select(t => t.Id));
                    totalSynced += synced;
                    _logger.LogDebug("Synced {Count} tips to server", synced);
                }
                else
                {
                    _logger.LogWarning("Server returned 0 synced for {Count} tips — stopping batch", batch.Count);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tip sync failed after {Synced} — will retry next cycle", totalSynced);
        }

        return totalSynced;
    }

    private static decimal GetDefaultSuggestedAmount(TipTrafficType trafficType) => trafficType switch
    {
        TipTrafficType.MessageRelay => TipPolicyConstants.SuggestedTipMessageRelay,
        TipTrafficType.ChunkServe => TipPolicyConstants.SuggestedTipChunkServe,
        TipTrafficType.StreamRelay => TipPolicyConstants.SuggestedTipStreamRelay,
        TipTrafficType.DtnCustody => TipPolicyConstants.SuggestedTipDtnCustody,
        TipTrafficType.VoiceRelay => TipPolicyConstants.SuggestedTipVoiceRelay,
        _ => TipPolicyConstants.DefaultTipMinZar
    };
}
