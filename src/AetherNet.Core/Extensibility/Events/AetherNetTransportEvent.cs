// SPDX-License-Identifier: MIT

namespace AetherNet.Extensibility.Events;

/// <summary>Physical or logical transport medium Aether is using.</summary>
public enum AetherNetTransportKind : byte
{
    /// <summary>Aether Blue — Bluetooth Low Energy.</summary>
    Bluetooth,

    /// <summary>Aether Green — Wi-Fi Direct / P2P.</summary>
    WiFiDirect,

    /// <summary>Aether Teal — Huawei NearLink.</summary>
    NearLink,

    /// <summary>Aether Orange — LoRa long-range radio.</summary>
    LoRa,

    /// <summary>Aether Silver — Near Field Communication (bootstrap only).</summary>
    NFC,

    /// <summary>Aether Purple — HTTP relay (internet-routed fallback).</summary>
    HttpRelay,

    /// <summary>Aether White — standard IEEE 802.11 Wi-Fi infrastructure.</summary>
    WiFi,

    /// <summary>Transport type is not known or has not been classified.</summary>
    Unknown,
}

/// <summary>Kinds of transport-layer observations Aether can emit.</summary>
public enum AetherNetTransportEventKind : byte
{
    /// <summary>A transport was chosen for communicating with a peer.</summary>
    Selected,

    /// <summary>The active transport to a peer has switched to a different medium.</summary>
    Changed,

    /// <summary>A latency measurement was completed for a peer link.</summary>
    LatencyMeasured,

    /// <summary>A packet loss event was detected on a peer link.</summary>
    PacketLoss,
}

/// <summary>
/// Emitted when Aether selects, changes, or measures quality on a transport
/// channel. The AI layer uses this to correlate transport behaviour with
/// threat patterns and tune <see cref="IAetherNetAiProvider.GetTransportBiasesAsync"/>.
/// </summary>
/// <param name="NodeId">UHID of the peer this transport event concerns.</param>
/// <param name="Kind">The type of transport observation.</param>
/// <param name="Transport">The transport medium involved.</param>
/// <param name="Latency">Measured latency, or <c>null</c> if not applicable.</param>
/// <param name="PacketLossRate">Packet loss rate (0.0–1.0), or <c>null</c> if not applicable.</param>
/// <param name="OccurredAt">UTC timestamp of the event.</param>
public sealed record AetherNetTransportEvent(
    string                    NodeId,
    AetherNetTransportEventKind  Kind,
    AetherNetTransportKind       Transport,
    TimeSpan?                 Latency,
    double?                   PacketLossRate,
    DateTimeOffset            OccurredAt)
{
    /// <summary>
    /// Returns <c>true</c> when <see cref="PacketLossRate"/> is set and exceeds
    /// <paramref name="threshold"/> (0.0–1.0).
    /// </summary>
    public bool ExceedsLoss(double threshold) =>
        PacketLossRate.HasValue && PacketLossRate.Value > threshold;
}
