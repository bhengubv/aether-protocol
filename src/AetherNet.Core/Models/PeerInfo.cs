// SPDX-License-Identifier: MIT

namespace AetherNet.Models;

/// <summary>
/// Information about a directly discovered peer on the mesh network.
/// Unlike <see cref="AetherNetNode"/> which is a persisted record, PeerInfo
/// represents a live peer visible through a transport (BLE, Wi-Fi Direct, etc.).
/// </summary>
public sealed class PeerInfo
{
    /// <summary>Universal Hash ID of the peer.</summary>
    public string Uhid { get; set; } = string.Empty;

    /// <summary>Human-readable name for the peer.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Peer's public key bytes for identity verification.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>Advertised capabilities of the peer.</summary>
    public NodeCapabilities Capabilities { get; set; } = NodeCapabilities.None;

    /// <summary>
    /// Reliability score from 0.0 to 1.0, based on observed relay success rate.
    /// </summary>
    public double ReliabilityScore { get; set; } = 0.5;

    /// <summary>Whether this peer provides internet gateway access.</summary>
    public bool IsGateway { get; set; }

    /// <summary>
    /// Received Signal Strength Indicator in dBm. Null if not applicable
    /// (e.g. for Wi-Fi Direct or internet-routed peers).
    /// </summary>
    public int? Rssi { get; set; }

    /// <summary>
    /// The transport over which this peer was discovered
    /// (e.g. "ble", "wifi-direct", "nearlink", "internet").
    /// </summary>
    public string TransportType { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this peer was first discovered in the current session.</summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp of the last communication with this peer.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether the local user has blocked this peer.</summary>
    public bool IsBlocked { get; set; }

    /// <summary>Geohash of the peer's reported position (may be approximate).</summary>
    public string? Geohash { get; set; }

    /// <summary>
    /// Estimated distance quality based on RSSI. Lower (more negative) RSSI = farther away.
    /// </summary>
    public string EstimatedProximity => Rssi switch
    {
        null => "unknown",
        > -50 => "immediate",
        > -70 => "near",
        > -90 => "far",
        _ => "very-far"
    };

    public override string ToString() =>
        $"{DisplayName} ({Uhid}) via {TransportType} rssi={Rssi}";
}
