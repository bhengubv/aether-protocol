// SPDX-License-Identifier: MIT

using AetherNet.Tipping.Models;

namespace AetherNet.Tipping.Services;

/// <summary>
/// On-device tipping client: validates a tip against the regulated policy, queues
/// it locally (so it survives offline), and batch-syncs queued tips to the backend
/// which settles them into both parties' SDPKT wallets.
///
/// <para>
/// Anyone with an SDPKT wallet can use this — the code is not a secret. A tip is
/// never an access gate: <see cref="TipNodeAsync"/> only ever returns false (and
/// queues nothing) when the recipient is not a registered operator, the amount is
/// outside policy, or the daily cap would be exceeded; service itself is unaffected.
/// </para>
/// </summary>
public interface ITippingService
{
    /// <summary>
    /// Validate and queue a tip to a node operator. Returns true if the tip was
    /// accepted and queued; false if the local node is not initialised, the
    /// recipient is not a registered tip-accepting operator, the amount is outside
    /// the traffic type's policy band, or the per-tipper daily cap would be exceeded.
    /// </summary>
    Task<bool> TipNodeAsync(string recipientUhid, decimal amount, TipTrafficType trafficType, Guid? referenceId = null);

    /// <summary>
    /// Resolve the effective <see cref="TipPolicy"/> for a traffic type — the cached
    /// server policy if present, otherwise a regulated default built from
    /// <see cref="AetherNet.Tipping.TipPolicyConstants"/>.
    /// </summary>
    Task<TipPolicy?> GetPolicyAsync(TipTrafficType trafficType);

    /// <summary>Total ZAR this node has tipped today (across all traffic types).</summary>
    Task<decimal> GetDailyTotalAsync();

    /// <summary>Number of tips queued locally and not yet synced to the backend.</summary>
    Task<int> GetPendingTipCountAsync();

    /// <summary>
    /// Batch-sync all queued tips to the backend (50 per call). Returns the total
    /// number of tips the server accepted. On any failure the loop stops and the
    /// remaining tips stay queued for the next cycle — never throws.
    /// </summary>
    Task<int> SyncTipsToServerAsync(CancellationToken ct = default);
}
