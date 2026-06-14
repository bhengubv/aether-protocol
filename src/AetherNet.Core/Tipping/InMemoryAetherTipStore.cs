// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Tipping.Models;

namespace AetherNet.Tipping;

/// <summary>
/// Volatile, process-local <see cref="IAetherTipStore"/>. Real working storage —
/// nothing is stubbed — backed by thread-safe in-memory collections. Suitable for
/// tests, demos, and ephemeral nodes. Hosts that need tips/profiles to survive a
/// process restart (the real SDPKT-settling case) register a durable implementation
/// before <c>AddTipping()</c> and this default is skipped (TryAdd). Mirrors the
/// project's <c>InMemoryRouteStore</c>/<c>InMemoryDtnBundleStore</c> convention.
/// </summary>
public sealed class InMemoryAetherTipStore : IAetherTipStore
{
    private long _nextTipId;
    private readonly ConcurrentDictionary<long, LocalTipTransaction> _tips = new();
    private readonly ConcurrentDictionary<TipTrafficType, TipPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, NodeOperatorProfile> _operators = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TipperReputation> _tipperReputations = new(StringComparer.Ordinal);

    public Task QueueTipAsync(LocalTipTransaction tip)
    {
        ArgumentNullException.ThrowIfNull(tip);
        var id = Interlocked.Increment(ref _nextTipId);
        tip.Id = id;
        tip.IsSynced = false;
        _tips[id] = tip;
        return Task.CompletedTask;
    }

    public Task<List<LocalTipTransaction>> GetUnsyncedTipsAsync(int limit = 50)
    {
        var batch = _tips.Values
            .Where(t => !t.IsSynced)
            .OrderBy(t => t.Id)
            .Take(limit)
            .ToList();
        return Task.FromResult(batch);
    }

    public Task MarkTipsSyncedAsync(IEnumerable<long> tipIds)
    {
        ArgumentNullException.ThrowIfNull(tipIds);
        foreach (var id in tipIds)
        {
            if (_tips.TryGetValue(id, out var tip))
                tip.IsSynced = true;
        }
        return Task.CompletedTask;
    }

    public Task<decimal> GetDailyTipTotalAsync(string tipperUhid)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var total = _tips.Values
            .Where(t => string.Equals(t.TipperUhid, tipperUhid, StringComparison.Ordinal)
                        && t.CreatedAt.UtcDateTime.Date == today)
            .Sum(t => t.Amount);
        return Task.FromResult(total);
    }

    public Task<List<TipPolicy>> GetTipPoliciesAsync()
        => Task.FromResult(_policies.Values.ToList());

    public Task SaveTipPoliciesAsync(IEnumerable<TipPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        foreach (var policy in policies)
            _policies[policy.TrafficType] = policy;
        return Task.CompletedTask;
    }

    public Task<NodeOperatorProfile?> GetNodeOperatorAsync(string uhid)
        => Task.FromResult(_operators.GetValueOrDefault(uhid));

    public Task SaveNodeOperatorAsync(NodeOperatorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _operators[profile.Uhid] = profile;
        return Task.CompletedTask;
    }

    public Task<TipperReputation?> GetTipperReputationAsync(string uhid)
        => Task.FromResult(_tipperReputations.GetValueOrDefault(uhid));

    public Task SaveTipperReputationAsync(TipperReputation rep)
    {
        ArgumentNullException.ThrowIfNull(rep);
        _tipperReputations[rep.TipperUhid] = rep;
        return Task.CompletedTask;
    }
}
