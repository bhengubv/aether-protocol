// SPDX-License-Identifier: MIT

namespace AetherNet.Bandwidth;

/// <summary>
/// Per-transport link bandwidth estimator.
///
/// <para>
/// Implements a BBRv3-inspired four-phase state machine:
/// <list type="number">
///   <item><b>Startup</b> — doubles effective pacing rate each RTT until BtlBw plateaus
///     (gain ≤ 25 % over 3 consecutive rounds).</item>
///   <item><b>Drain</b> — reduces in-flight to ≤ BDP to drain the queue created during Startup.</item>
///   <item><b>ProbeBW</b> — cycles: probe-up at 1.25 × BDP, drain, then cruise × 6 rounds.</item>
///   <item><b>ProbeRTT</b> — every 10 s, reduces in-flight to 4 packets for 200 ms to refresh RTprop.</item>
/// </list>
/// </para>
///
/// <para>Two observation paths:</para>
/// <list type="bullet">
///   <item><b>Passive:</b> <see cref="RecordDelivery"/> feeds the BBRv3 delivery-rate filter.
///     No probes needed when the link is carrying application traffic.</item>
///   <item><b>Active:</b> <see cref="RecordProbeResult"/> consumes ack timestamps from
///     <see cref="IBandwidthProbeService"/> to maintain estimates on idle links.</item>
/// </list>
///
/// <para>
/// Innovations beyond existing standards (BBRv3, RFC 9002, GCC):
/// <list type="bullet">
///   <item><b>PHY-layer capping:</b> RSSI-to-BtlBw mapping constrains estimates before
///     any probe data arrives, improving early ABR decisions on weak radio links.</item>
///   <item><b>Gossip warm-start:</b> <see cref="WarmFromGossip"/> pre-seeds the estimator
///     from a peer's measured value so sessions start warm, not cold.</item>
///   <item><b>Confidence tiers:</b> <see cref="BandwidthConfidence"/> lets consumers
///     distinguish a 1-probe estimate from a stable 30-round estimate.</item>
/// </list>
/// </para>
/// </summary>
public interface IBandwidthEstimator
{
    /// <summary>Transport identifier (e.g. "BLE", "NearLink", "Wi-Fi Direct").</summary>
    string TransportName { get; }

    // ── Current estimates ────────────────────────────────────────────────────

    /// <summary>BBRv3 BtlBw: max delivery rate over the 10×RTprop window (bps).</summary>
    long BtlBwBps { get; }

    /// <summary>Available bandwidth: BtlBwBps × (1 − LossRate).</summary>
    long AvailableBps { get; }

    /// <summary>Bandwidth-Delay Product in bytes: BtlBwBps × RtProp / 8.</summary>
    long BdpBytes { get; }

    /// <summary>RFC 6298 smoothed RTT.</summary>
    TimeSpan Srtt { get; }

    /// <summary>RFC 6298 RTT mean deviation (RTTVAR).</summary>
    TimeSpan RttVar { get; }

    /// <summary>BBRv3 RTprop: minimum RTT observed in last 10 s.</summary>
    TimeSpan RtProp { get; }

    /// <summary>EWMA fractional loss rate [0, 1]; α = 0.10.</summary>
    double LossRate { get; }

    BandwidthConfidence Confidence { get; }

    /// <summary>Full snapshot of the current estimate (immutable value type — safe to share).</summary>
    BandwidthSample CurrentSample { get; }

    // ── Observation feed ─────────────────────────────────────────────────────

    /// <summary>
    /// Record a successful delivery of <paramref name="bytes"/>.
    /// Both timestamps are microseconds since Unix epoch on the <b>same clock</b>.
    /// </summary>
    void RecordDelivery(int bytes, long sendTimestampUs, long deliverTimestampUs);

    /// <summary>Record that <paramref name="bytes"/> were lost (timeout or explicit NAK).</summary>
    void RecordLoss(int bytes);

    /// <summary>
    /// Feed an active probe ack into the estimator.
    /// <paramref name="localReceiveUs"/> is the local clock µs at ACK receipt.
    /// </summary>
    void RecordProbeResult(BandwidthProbeAck ack, long localReceiveUs);

    /// <summary>
    /// Pre-warm from a gossip payload. Only effective when <see cref="Confidence"/>
    /// is <see cref="BandwidthConfidence.None"/> — never downgrades an existing estimate.
    /// </summary>
    void WarmFromGossip(long btlBwBps, TimeSpan rtProp, BandwidthConfidence sourceConfidence);

    /// <summary>
    /// Apply a physical-layer hint. RSSI-to-BtlBw caps the estimate before probes complete.
    /// <paramref name="rssiDbm"/> is the received signal strength in dBm.
    /// </summary>
    void ApplyPhyHint(int rssiDbm);

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires when BtlBw improves by ≥ 5 % or <see cref="Confidence"/> advances.
    /// Consumers: ABR controller, transport selector, streaming bitrate ladder.
    /// </summary>
    event EventHandler<BandwidthSample> SampleImproved;
}
