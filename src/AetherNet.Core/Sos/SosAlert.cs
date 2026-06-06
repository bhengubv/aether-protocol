// SPDX-License-Identifier: MIT

namespace AetherNet.Models;

/// <summary>
/// An SOS alert observed on the mesh — either originated locally and broadcast
/// outwards, or received from another node via flood.
/// </summary>
public sealed class SosAlert
{
    /// <summary>Globally unique identifier for this SOS event.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UHID of the node that originated the alert.</summary>
    public string SenderUhid { get; set; } = string.Empty;

    /// <summary>
    /// Caller-defined alert category — e.g. "sos", "panic", "medical", "fire". The mesh
    /// does not interpret this string; it is opaque metadata for receivers.
    /// </summary>
    public string BroadcastType { get; set; } = "sos";

    /// <summary>Optional free-text message accompanying the alert.</summary>
    public string? Message { get; set; }

    /// <summary>Latitude of the alert origin in decimal degrees.</summary>
    public double Latitude { get; set; }

    /// <summary>Longitude of the alert origin in decimal degrees.</summary>
    public double Longitude { get; set; }

    /// <summary>Optional precomputed geohash for the origin (opaque to the protocol; produced by the host).</summary>
    public string? Geohash { get; set; }

    /// <summary>UTC timestamp when this alert was received locally (or originated).</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
