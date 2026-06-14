// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherNet.Incentive;

/// <summary>
/// Volatile, process-local <see cref="IAetherRewardStore"/>. Real working storage —
/// nothing is stubbed — backed by a thread-safe in-memory map. Suitable for tests,
/// demos, and ephemeral nodes. Hosts that need the XP queue to survive a process
/// restart register a durable implementation before <c>AddTipping()</c> and this
/// default is skipped (TryAdd).
/// </summary>
public sealed class InMemoryAetherRewardStore : IAetherRewardStore
{
    private long _nextRewardId;
    private readonly ConcurrentDictionary<long, RewardEntry> _rewards = new();

    public Task QueueRewardAsync(string actionType, int xpAmount, string? description, Guid? referenceId)
    {
        var id = Interlocked.Increment(ref _nextRewardId);
        _rewards[id] = new RewardEntry
        {
            Row = new PendingRewardRow
            {
                Id = id,
                ActionType = actionType,
                XpAmount = xpAmount,
                Description = description,
                ReferenceId = referenceId,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            IsSynced = false,
        };
        return Task.CompletedTask;
    }

    public Task<int> GetPendingRewardCountAsync()
        => Task.FromResult(_rewards.Values.Count(r => !r.IsSynced));

    public Task<List<PendingRewardRow>> GetUnsyncedRewardsAsync(int limit = 50)
    {
        var batch = _rewards.Values
            .Where(r => !r.IsSynced)
            .OrderBy(r => r.Row.Id)
            .Take(limit)
            .Select(r => r.Row)
            .ToList();
        return Task.FromResult(batch);
    }

    public Task MarkRewardsSyncedAsync(IEnumerable<long> rewardIds)
    {
        ArgumentNullException.ThrowIfNull(rewardIds);
        foreach (var id in rewardIds)
        {
            if (_rewards.TryGetValue(id, out var entry))
                entry.IsSynced = true;
        }
        return Task.CompletedTask;
    }

    private sealed class RewardEntry
    {
        public required PendingRewardRow Row { get; init; }
        public bool IsSynced { get; set; }
    }
}
