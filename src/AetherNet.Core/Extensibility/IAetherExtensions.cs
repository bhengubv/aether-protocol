// SPDX-License-Identifier: MIT

using AetherNet.Incentive;
using AetherNet.Models;
using AetherNet.Protocol;

namespace AetherNet.Extensibility;

/// <summary>
/// Extension point for incentive mechanisms. Implementations can reward nodes
/// for relaying packets, prioritise high-value traffic, or integrate with
/// external reward systems.
///
/// The default no-op implementation records nothing and never prioritises.
/// </summary>
public interface IAetherNetIncentiveProvider
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

    /// <summary>
    /// Called when a <see cref="Protocol.PacketType.TipPacket"/> is received off the mesh.
    /// Default no-op — a bare node accepts and relays the packet but settles nothing.
    /// Hosts override this to settle the tip however they choose. The protocol carries
    /// the signal; settlement is the host's business.
    ///
    /// <para>
    /// The <paramref name="tip"/> amount is a bare number with no units. The protocol
    /// imposes no policy, minimum, or maximum — interpretation is entirely up to the
    /// implementer. Distinct from <see cref="RecordCreatorTipAsync"/> (a direct creator tip
    /// initiated by the local user) and <see cref="RecordRelayAsync"/> (relay byte credit):
    /// this is the inbound side of a peer-to-peer mesh tip addressed to a recipient.
    /// </para>
    /// </summary>
    /// <param name="tip">The deserialised tip envelope received off the mesh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SettleMeshTipAsync(TipPacketPayload tip, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Called when the local user tips a content author. Distinct from
    /// <see cref="RecordRelayAsync"/> (relay credit — paid to nodes that forward bytes);
    /// this records direct creator → consumer settlement (paid to the user who
    /// AUTHORED the content). Host implementations (e.g. SDPKT, BhenguPay) wire
    /// their settlement logic here. Default no-op does nothing.
    /// Added in v1.2.0 — closes Issue #61 surfaced by Wave 16.
    /// </summary>
    /// <param name="creatorUhid">UHID of the content's author (recipient of the tip).</param>
    /// <param name="amount">Tip amount in the host's settlement currency (typically ZAR for SDPKT-backed hosts).</param>
    /// <param name="contentHash">Root hash of the tipped content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordCreatorTipAsync(string creatorUhid, decimal amount, string contentHash, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// Extension point for backend/cloud synchronisation. Implementations can sync
/// node state to a central server, fetch pre-key bundles for end-to-end encryption,
/// or integrate with any backend infrastructure.
///
/// The default no-op implementation does nothing — the mesh operates fully offline.
/// </summary>
public interface IAetherNetBackendClient
{
    /// <summary>
    /// Synchronises the local node's state (capabilities, location, presence) with a backend server.
    /// Called periodically when internet connectivity is available.
    /// </summary>
    /// <param name="node">The local node to sync.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the sync succeeded.</returns>
    Task<bool> SyncNodeAsync(AetherNetNode node, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Optional fallback path for DTN bundles when no peer-to-peer route is available.
    /// Backend implementations can store-and-forward the bundle until the recipient
    /// reconnects. The default no-op returns false (offline-only mesh, no backend relay).
    /// </summary>
    /// <param name="bundle">The bundle to relay.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the backend accepted custody of the bundle for relay.</returns>
    Task<bool> SyncDtnBundleAsync(AetherNet.Models.DtnBundle bundle, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Optional cloud path for SOS broadcasts. Mirrors mesh flooding so the alert reaches
    /// emergency operators even if the originator has internet but the mesh has no carriers.
    /// The default no-op returns false (mesh-only SOS).
    /// </summary>
    /// <param name="alert">The SOS alert to mirror to the backend.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the backend accepted the alert.</returns>
    Task<bool> SyncSosAsync(AetherNet.Models.SosAlert alert, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Optional cloud-relay path for 1-to-1 messages when no peer-to-peer route is
    /// available and DTN cannot accept custody. Used by the messaging layer as the
    /// third send tier (mesh route → DTN → backend relay → fail). The default no-op
    /// returns false, meaning the message stays in the outbox for retry.
    /// </summary>
    /// <param name="senderUhid">UHID of the sender.</param>
    /// <param name="recipientUhid">UHID of the recipient.</param>
    /// <param name="encryptedContent">Opaque ciphertext to relay.</param>
    /// <param name="priority">Original packet priority.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the backend accepted the message for relay.</returns>
    Task<bool> RelayMessageAsync(
        string senderUhid,
        string recipientUhid,
        byte[] encryptedContent,
        byte priority,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// Extension point for feature flags. Implementations can gate protocol features
/// behind remote configuration, A/B tests, or gradual rollouts.
///
/// The default implementation returns true for all features (everything enabled).
/// </summary>
public interface IAetherNetFeatureFlagProvider
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
