// SPDX-License-Identifier: MIT

using Aether.Transport.Models;

namespace Aether.Transport.Abstractions;

/// <summary>
/// Bluetooth Low Energy transport for short-range, low-power mesh communication.
/// BLE is optimal for small payloads (≤1KB) such as heartbeats, route discovery,
/// and presence beacons. Typical range: 10-100m, bandwidth: ~2 Mbps.
/// </summary>
public interface IBleTransportService : ITransportService
{
    /// <summary>
    /// Sends a BLE advertisement packet for passive peer discovery.
    /// Advertisements are broadcast to all nearby BLE-capable nodes.
    /// </summary>
    /// <param name="advertisement">The advertisement data to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the advertisement was sent successfully.</returns>
    Task<bool> SendAdvertisementAsync(BleAdvertisement advertisement, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a BLE advertisement is received from a nearby peer.
    /// </summary>
    event Action<BleAdvertisement>? AdvertisementReceived;
}
