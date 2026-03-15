// SPDX-License-Identifier: MIT

using Aether.Models;
using Aether.Protocol;

namespace Aether.Extensibility;

/// <summary>
/// Extension point for incentive mechanisms. Implementations can reward nodes
/// for relaying packets, prioritise high-value traffic, or integrate with
/// external reward systems.
///
/// The default no-op implementation records nothing and never prioritises.
/// </summary>
public interface IAetherIncentiveProvider
{
    /// <summary>
    /// Called when this node successfully relays a packet on behalf of another node.
    /// Implementations can record the relay for later reward/reputation calculation.
    /// </summary>
    /// <param name="relayNodeUhid">UHID of the node that performed the relay (typically this node).</param>
    /// <param name="packet">The packet that was relayed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordRelayAsync(string relayNodeUhid, MeshPacket packet, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Determines whether a packet should be prioritised for forwarding.
    /// Implementations might prioritise packets from nodes that have contributed
    /// more to the network, or deprioritise free-riders.
    /// </summary>
    /// <param name="packet">The packet to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the packet should receive priority forwarding.</returns>
    Task<bool> ShouldPrioritizeAsync(MeshPacket packet, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// Extension point for backend/cloud synchronisation. Implementations can sync
/// node state to a central server, fetch pre-key bundles for end-to-end encryption,
/// or integrate with any backend infrastructure.
///
/// The default no-op implementation does nothing — the mesh operates fully offline.
/// </summary>
public interface IAetherBackendClient
{
    /// <summary>
    /// Synchronises the local node's state (capabilities, location, presence) with a backend server.
    /// Called periodically when internet connectivity is available.
    /// </summary>
    /// <param name="node">The local node to sync.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the sync succeeded.</returns>
    Task<bool> SyncNodeAsync(AetherNode node, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <summary>
    /// Fetches a pre-key bundle for a remote node, enabling end-to-end encrypted
    /// session establishment (e.g. X3DH key agreement).
    /// </summary>
    /// <param name="targetUhid">UHID of the node whose pre-key bundle is needed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pre-key bundle bytes, or null if unavailable.</returns>
    Task<byte[]?> FetchPreKeyBundleAsync(string targetUhid, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);
}

/// <summary>
/// Extension point for feature flags. Implementations can gate protocol features
/// behind remote configuration, A/B tests, or gradual rollouts.
///
/// The default implementation returns true for all features (everything enabled).
/// </summary>
public interface IAetherFeatureFlagProvider
{
    /// <summary>
    /// Checks whether a named feature is enabled.
    /// </summary>
    /// <param name="featureName">The feature flag name (e.g. "dtn", "voice", "streaming").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the feature is enabled.</returns>
    Task<bool> IsEnabledAsync(string featureName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
