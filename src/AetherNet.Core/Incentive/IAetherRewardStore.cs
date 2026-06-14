// SPDX-License-Identifier: MIT

namespace AetherNet.Incentive;

/// <summary>
/// On-device persistence for the XP-reward queue consumed by
/// <see cref="IAetherRewardService"/>. The equivalent of the host's pending-rewards
/// table, narrowed to exactly the reward surface. A default in-memory implementation
/// (<see cref="InMemoryAetherRewardStore"/>) ships for tests and demos; durable hosts
/// supply their own. Mirrors the project's store-plus-default convention.
/// </summary>
public interface IAetherRewardStore
{
    /// <summary>Queue a reward row (the store assigns the id and creation timestamp).</summary>
    Task QueueRewardAsync(string actionType, int xpAmount, string? description, Guid? referenceId);

    /// <summary>Number of queued rewards not yet synced.</summary>
    Task<int> GetPendingRewardCountAsync();

    /// <summary>Up to <paramref name="limit"/> queued rewards not yet synced, oldest first.</summary>
    Task<List<PendingRewardRow>> GetUnsyncedRewardsAsync(int limit = 50);

    /// <summary>Mark the given queued rewards as synced.</summary>
    Task MarkRewardsSyncedAsync(IEnumerable<long> rewardIds);
}
