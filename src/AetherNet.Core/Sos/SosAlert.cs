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

    /// <summary>
    /// Distinct UHIDs of peers that have acknowledged receiving this alert. Populated on the
    /// ORIGINATING node only, as <c>SosAck</c> packets arrive back — it lets the sender see how many
    /// devices their emergency reached. Access is synchronised by the SOS service via a <c>lock</c>
    /// on this set.
    /// </summary>
    public HashSet<string> AcknowledgedBy { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Raised on the originating node when a peer acknowledges receipt of one of its active SOS alerts.
/// </summary>
public sealed class SosAcknowledgement
{
    /// <summary>Id of the SOS broadcast that was acknowledged.</summary>
    public Guid BroadcastId { get; set; }

    /// <summary>UHID of the peer that acknowledged receiving the SOS.</summary>
    public string ResponderUhid { get; set; } = string.Empty;

    /// <summary>Total distinct peers that have acknowledged this SOS so far (this responder included).</summary>
    public int TotalAcknowledgements { get; set; }
}
