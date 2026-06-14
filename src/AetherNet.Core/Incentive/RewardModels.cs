// SPDX-License-Identifier: MIT

namespace AetherNet.Incentive;

/// <summary>
/// DTO for batch-syncing a pending reward to the backend.
/// </summary>
public class PendingReward
{
    public string ActionType { get; set; } = string.Empty;
    public int XpAmount { get; set; }
    public string? Description { get; set; }
    public Guid? ReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A persisted pending-reward row (carries the local row id used to mark it synced).
/// </summary>
public class PendingRewardRow
{
    public long Id { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public int XpAmount { get; set; }
    public string? Description { get; set; }
    public Guid? ReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
