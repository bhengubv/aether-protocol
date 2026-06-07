// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;

namespace AetherNet.Dtn;

/// <summary>
/// Delay-tolerant networking layer. Hosts call <see cref="CreateBundleAsync"/> to send,
/// pump received DTN packets through <see cref="HandleAsync"/>, and run
/// <see cref="RunDeliveryScanAsync"/> + <see cref="ExpireStaleAsync"/> on a periodic loop.
/// </summary>
public interface IDtnService
{
    /// <summary>Raised once when a bundle this node sent is confirmed delivered.</summary>
    event EventHandler<DtnDeliveryReceipt>? BundleDelivered;

    /// <summary>
    /// Raised when a DTN bundle addressed to the local node has been received.
    /// Distinct from <see cref="BundleDelivered"/> (which fires on the SENDER side
    /// once a delivery receipt flows back). Subscribers that want to know "a bundle
    /// arrived for me" should listen here; subscribers that want to confirm "my
    /// outbound bundle reached the recipient" should listen to <see cref="BundleDelivered"/>.
    /// Added in v1.2.0.
    /// </summary>
    event EventHandler<DtnBundleReceivedEventArgs>? BundleReceived;

    /// <summary>Create and queue a new bundle. Attempts immediate mesh delivery; falls back to the store on failure.</summary>
    Task<DtnBundle> CreateBundleAsync(string recipientUhid, byte[] encryptedPayload, BundlePriority priority = BundlePriority.Normal, string? recipientLastGeohash = null, CancellationToken cancellationToken = default);

    /// <summary>Pump an incoming DTN-related packet (Bundle / CustodyAck / DeliveryReceipt) into the service.</summary>
    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>Run one pass of the delivery loop: re-attempt mesh delivery for active bundles and replicate to chosen peers.</summary>
    Task RunDeliveryScanAsync(CancellationToken cancellationToken = default);

    /// <summary>Mark expired bundles in the store and free their slots.</summary>
    Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns active (non-delivered, non-expired) bundles currently held.</summary>
    Task<IReadOnlyList<DtnBundle>> GetActiveBundlesAsync(CancellationToken cancellationToken = default);
}
