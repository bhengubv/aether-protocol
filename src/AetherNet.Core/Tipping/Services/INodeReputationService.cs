// SPDX-License-Identifier: MIT

using AetherNet.Tipping.Models;

namespace AetherNet.Tipping.Services;

/// <summary>
/// Node-operator registration and reputation. Registering binds this node's UHID to
/// an SDPKT wallet so it can receive tips; reputation (reliability, relay volume,
/// uptime, earnings) is fetched from the backend and drives how much traffic and how
/// many tips a node attracts.
/// </summary>
public interface INodeReputationService
{
    /// <summary>
    /// Fetch a node's backend-scored reputation, or null if unavailable (offline or
    /// not yet scored). Never throws — a transport failure returns null.
    /// </summary>
    Task<NodeReputation?> GetReputationAsync(string uhid);

    /// <summary>
    /// Register the local node as a tip-accepting operator bound to the given SDPKT
    /// wallet. Saves the profile locally first (so it survives offline) then attempts
    /// backend registration; a backend failure is logged and retried next cycle.
    /// </summary>
    Task RegisterAsOperatorAsync(string sdpktWalletAddress);

    /// <summary>True if the given UHID is a locally-known registered operator.</summary>
    Task<bool> IsOperatorRegisteredAsync(string uhid);

    /// <summary>
    /// Refresh server-driven tip policies into the local cache. Never throws — a
    /// transport failure leaves the existing cache in place.
    /// </summary>
    Task RefreshReputationsAsync(CancellationToken ct = default);
}
