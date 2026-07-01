// SPDX-License-Identifier: MIT

namespace AetherNet.Transport.CircuitRelay;

/// <summary>
/// The underlying hop-link a <see cref="CircuitRelayTransportService"/> uses to exchange
/// raw relay frames with <em>directly reachable</em> nodes. This is the seam between
/// circuit-relay-v2 (which is transport-agnostic) and whatever real transport actually
/// carries a frame one hop — BLE, Wi-Fi Direct, WebRTC, the HTTP relay, or an in-process
/// link in tests. A relay node forwards on this link; an endpoint reaches its relay on it.
/// </summary>
public interface IRelayLink
{
    /// <summary>
    /// Sends a raw relay frame to a node this link can reach in one hop.
    /// </summary>
    /// <returns><see langword="true"/> if the frame was handed to the node's link.</returns>
    Task<bool> SendFrameAsync(string nodeUhid, byte[] frame, CancellationToken cancellationToken = default);

    /// <summary>Whether this node currently has a direct one-hop link to <paramref name="nodeUhid"/>.</summary>
    bool CanReach(string nodeUhid);

    /// <summary>
    /// Raised when a raw relay frame arrives from a directly-reachable node.
    /// First argument is the sending node's UHID, second is the frame bytes.
    /// </summary>
    event Action<string, byte[]>? FrameReceived;
}

/// <summary>
/// Tuning + policy for <see cref="CircuitRelayTransportService"/>.
/// </summary>
public sealed class CircuitRelayOptions
{
    /// <summary>How long a granted reservation remains valid.</summary>
    public TimeSpan ReservationTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Maximum concurrent reservations this node will hold as a relay.</summary>
    public int MaxReservations { get; set; } = 128;

    /// <summary>Maximum concurrent bridges this node will service as a relay.</summary>
    public int MaxBridges { get; set; } = 128;

    /// <summary>Per-bridge data budget in bytes granted by this relay. 0 = unlimited.</summary>
    public long BridgeDataLimitBytes { get; set; } = 0;

    /// <summary>Per-bridge duration budget in seconds granted by this relay. 0 = unlimited.</summary>
    public int BridgeDurationLimitSeconds { get; set; } = 0;

    /// <summary>How long a client waits for a CONNECT to be confirmed before giving up.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a client waits for a RESERVE to be confirmed before giving up.</summary>
    public TimeSpan ReserveTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether this node grants reservations and bridges traffic for others.</summary>
    public bool ActAsRelay { get; set; } = true;
}
