// SPDX-License-Identifier: MIT

namespace AetherNet.Bandwidth;

// ── Confidence ───────────────────────────────────────────────────────────────

/// <summary>
/// How confident we are in the current bandwidth estimate.
/// Rises with probe rounds; resets on topology change or extended idle.
/// </summary>
public enum BandwidthConfidence { None, Low, Medium, High }

// ── BandwidthSample ──────────────────────────────────────────────────────────

/// <summary>
/// Point-in-time bandwidth measurement for a single transport link.
///
/// <para>Derivation follows BBRv3 (draft-cardwell-iccrg-bbr-congestion-control-02):
/// <list type="bullet">
///   <item><see cref="BtlBwBps"/> — max delivery rate over 10×RTprop window.</item>
///   <item><see cref="RtProp"/> — minimum RTT observed in last 10 s (ProbeRTT window).</item>
///   <item><see cref="Srtt"/> — RFC 6298 smoothed RTT (α = 1/8).</item>
///   <item><see cref="RttVar"/> — RFC 6298 mean deviation (β = 1/4).</item>
/// </list></para>
///
/// <para>What sets this apart from existing standards:
/// <list type="bullet">
///   <item><b>BdpBytes</b> — pre-computed BDP so callers never have to re-derive it.</item>
///   <item><b>PhyCapBps</b> — PHY-layer cap from RSSI mapping; 0 if unknown. Prevents
///     over-optimistic estimates on weak BLE links before probes complete.</item>
///   <item><b>Confidence</b> — explicit quality tier used by ABR to decide whether
///     to trust the estimate or fall back to a conservative safe bitrate.</item>
/// </list></para>
/// </summary>
public sealed record BandwidthSample(
    string TransportName,

    /// <summary>BBRv3 BtlBw: maximum sustained delivery rate the network can carry (bps).</summary>
    long BtlBwBps,

    /// <summary>Available bandwidth ceiling: BtlBwBps × (1 − LossRate).</summary>
    long AvailableBps,

    /// <summary>Bandwidth-Delay Product: BtlBwBps × RtProp / 8 (bytes). Optimal in-flight window size.</summary>
    long BdpBytes,

    /// <summary>RFC 6298 smoothed RTT.</summary>
    TimeSpan Srtt,

    /// <summary>RFC 6298 RTT mean deviation (RTTVAR).</summary>
    TimeSpan RttVar,

    /// <summary>BBRv3 RTprop: minimum observed RTT over the last 10 seconds.</summary>
    TimeSpan RtProp,

    /// <summary>EWMA fractional loss rate [0, 1]; α = 0.10.</summary>
    double LossRate,

    /// <summary>PHY-layer bandwidth cap from RSSI hints (bps). 0 = unknown.</summary>
    long PhyCapBps,

    BandwidthConfidence Confidence,
    DateTimeOffset MeasuredAt)
{
    /// <summary>
    /// RFC 6298 §2.4 RTO: SRTT + max(G, 4×RTTVAR), G = 1 ms clock granularity.
    /// Clamped to [200 ms, 60 s] per §2.4.
    /// </summary>
    public TimeSpan Rto
    {
        get
        {
            var raw = Srtt + TimeSpan.FromMilliseconds(Math.Max(1.0, 4.0 * RttVar.TotalMilliseconds));
            return TimeSpan.FromMilliseconds(Math.Clamp(raw.TotalMilliseconds, 200.0, 60_000.0));
        }
    }

    /// <summary>Effective bandwidth: min of BtlBwBps and PhyCapBps (if known).</summary>
    public long EffectiveBps =>
        PhyCapBps > 0 ? Math.Min(BtlBwBps, PhyCapBps) : BtlBwBps;
}

// ── Probe wire models ─────────────────────────────────────────────────────────

/// <summary>
/// Four-timestamp probe ACK for two-way delay / RTT measurement (RFC 5136 §3).
/// All timestamps are microseconds since Unix epoch on each peer's local clock.
/// Clock synchronisation is not required — RTT is computed from sender-side timestamps only.
/// </summary>
public sealed record BandwidthProbeAck(
    uint Sequence,
    long SenderSendUs,
    long ReceiverReceiveUs,
    long ReceiverSendUs,
    long SenderReceiveUs,
    int ProbeBytes)
{
    /// <summary>
    /// Round-trip time (clock-sync-free).
    /// RTT = (SenderReceive − SenderSend) − receiver processing time.
    /// </summary>
    public TimeSpan Rtt => TimeSpan.FromMicroseconds(
        (SenderReceiveUs - SenderSendUs) - (ReceiverSendUs - ReceiverReceiveUs));

    /// <summary>
    /// Forward one-way delay (sender → receiver). Requires loose clock sync;
    /// treat as approximate unless NTP/PTP is available.
    /// </summary>
    public TimeSpan ForwardOwd => TimeSpan.FromMicroseconds(ReceiverReceiveUs - SenderSendUs);
}

// ── Gossip warm-start ─────────────────────────────────────────────────────────

/// <summary>
/// Gossip payload that a node broadcasts to new peers during handshake.
/// Allows the new session to start with a warm BtlBw estimate instead of
/// probing from zero — unique to AetherNet's mesh topology awareness.
/// QUIC and TCP always cold-start; gossip warming is an AetherNet invention.
/// </summary>
public sealed record BandwidthGossipPayload(
    string PeerUhid,
    string TransportName,
    long BtlBwBps,
    long RtPropUs,
    BandwidthConfidence Confidence,
    DateTimeOffset MeasuredAt);

// ── Node activity (UI layer) ──────────────────────────────────────────────────

/// <summary>
/// High-level activity state of a node — suitable for status-bar indicators,
/// dashboard health badges, and connection-quality icons.
/// </summary>
public enum NodeActivityState
{
    /// <summary>No transports available. Node is isolated.</summary>
    Offline,

    /// <summary>Transports available but no data in the last 5 s.</summary>
    Idle,

    /// <summary>Data flowing; link utilization &lt; 50 % of estimated capacity.</summary>
    Active,

    /// <summary>Link utilization ≥ 50 %; performance good but approaching limits.</summary>
    Busy,

    /// <summary>Loss rate &gt; 5 % or delivery rate declining — likely interference.</summary>
    Degraded,
}

/// <summary>
/// Activity snapshot for a single transport within the node.
/// </summary>
public sealed record TransportActivitySnapshot(
    string TransportName,
    bool IsAvailable,

    /// <summary>Bytes per second being received on this transport.</summary>
    long IngressBps,

    /// <summary>Bytes per second being sent on this transport.</summary>
    long EgressBps,

    /// <summary>Smoothed RTT from IBandwidthEstimator.</summary>
    TimeSpan Srtt,

    /// <summary>Bottleneck bandwidth from IBandwidthEstimator.</summary>
    long BtlBwBps,

    /// <summary>Egress utilization fraction: EgressBps / BtlBwBps. 0 if BtlBwBps = 0.</summary>
    double UtilizationFraction,

    NodeActivityState State,
    BandwidthConfidence Confidence)
{
    /// <summary>Human-readable utilization percentage string (e.g. "34 %").</summary>
    public string UtilizationPercent =>
        $"{UtilizationFraction * 100.0:F0} %";
}

/// <summary>
/// Full node activity snapshot — the top-level model surfaced to UI.
///
/// <para>Intended consumption patterns:
/// <list type="bullet">
///   <item><b>Status bar / widget:</b> poll <see cref="INodeActivityMonitor.Current"/> every 1 s.</item>
///   <item><b>Dashboard / SignalR:</b> subscribe to <see cref="INodeActivityMonitor.Activity"/>
///     or handle <see cref="INodeActivityMonitor.SnapshotChanged"/>.</item>
///   <item><b>ABR controller:</b> subscribe to check whether <see cref="State"/> is
///     <see cref="NodeActivityState.Degraded"/> and step down the bitrate ladder.</item>
/// </list></para>
/// </summary>
public sealed record NodeActivitySnapshot(
    NodeActivityState State,

    /// <summary>Aggregate bytes per second flowing INTO this node (all transports).</summary>
    long IngressBps,

    /// <summary>Aggregate bytes per second flowing OUT of this node (all transports).</summary>
    long EgressBps,

    /// <summary>Number of remote peers that had traffic in the last 5 s.</summary>
    int ActivePeers,

    /// <summary>Number of transports currently carrying data.</summary>
    int ActiveTransports,

    /// <summary>Per-transport breakdown.</summary>
    IReadOnlyList<TransportActivitySnapshot> Transports,

    /// <summary>
    /// Dominant transport: the one carrying the most egress bytes.
    /// Null if node is offline or idle.
    /// </summary>
    string? PrimaryTransportName,

    DateTimeOffset Timestamp)
{
    /// <summary>Combined throughput (ingress + egress).</summary>
    public long TotalBps => IngressBps + EgressBps;

    /// <summary>True if any transport has data flowing.</summary>
    public bool HasActivity => State is NodeActivityState.Active or NodeActivityState.Busy or NodeActivityState.Degraded;
}
