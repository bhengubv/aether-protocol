// SPDX-License-Identifier: MIT

using AetherNet.Tipping.Models;

namespace AetherNet.Tipping;

/// <summary>
/// On-device persistence for the tipping layer: the local outbound tip queue, the
/// per-tipper daily running total, cached server tip policies, node-operator
/// registration profiles, and tipper reputation.
///
/// <para>
/// This is the tip-specific storage seam the tipping services depend on (the
/// equivalent of the larger host SQLite store in the private codebase, narrowed to
/// exactly the tip surface). A default in-memory implementation
/// (<see cref="InMemoryAetherTipStore"/>) ships for tests and demos; hosts that need
/// durability across restarts (the real SDPKT-settling case) supply their own
/// SQLite/keychain-backed implementation. Mirrors the project's
/// <c>IRouteStore</c>/<c>InMemoryRouteStore</c> store-plus-default convention.
/// </para>
/// </summary>
public interface IAetherTipStore
{
    // ── Outbound tip queue ──────────────────────────────────────────────────────

    /// <summary>Queue a tip for later batch-sync to the backend.</summary>
    Task QueueTipAsync(LocalTipTransaction tip);

    /// <summary>Return up to <paramref name="limit"/> queued tips not yet synced.</summary>
    Task<List<LocalTipTransaction>> GetUnsyncedTipsAsync(int limit = 50);

    /// <summary>Mark the given queued tips as synced so they are not sent again.</summary>
    Task MarkTipsSyncedAsync(IEnumerable<long> tipIds);

    /// <summary>Total ZAR the given tipper has tipped today (rolling UTC day).</summary>
    Task<decimal> GetDailyTipTotalAsync(string tipperUhid);

    // ── Server-driven tip policies (cached) ─────────────────────────────────────

    /// <summary>All cached tip policies.</summary>
    Task<List<TipPolicy>> GetTipPoliciesAsync();

    /// <summary>Replace the cached tip policy set.</summary>
    Task SaveTipPoliciesAsync(IEnumerable<TipPolicy> policies);

    // ── Node-operator profiles ──────────────────────────────────────────────────

    /// <summary>The operator profile for a UHID, or null if none is stored.</summary>
    Task<NodeOperatorProfile?> GetNodeOperatorAsync(string uhid);

    /// <summary>Insert or replace an operator profile.</summary>
    Task SaveNodeOperatorAsync(NodeOperatorProfile profile);

    // ── Tipper reputation ───────────────────────────────────────────────────────

    /// <summary>The tipper reputation for a UHID, or null if none is stored.</summary>
    Task<TipperReputation?> GetTipperReputationAsync(string uhid);

    /// <summary>Insert or replace a tipper reputation.</summary>
    Task SaveTipperReputationAsync(TipperReputation rep);
}
