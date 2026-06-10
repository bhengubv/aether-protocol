// SPDX-License-Identifier: MIT

namespace AetherNet.Bandwidth;

/// <summary>
/// Active bandwidth probe service.
///
/// <para>
/// Sends <c>BandwidthProbe</c> packets (type 53) to a target peer and awaits
/// <c>BandwidthAck</c> responses (type 54), deriving RTT and delivery rate from
/// the four embedded timestamps. Probes are self-paced — the service limits
/// overhead to &lt; 0.5 % of the current BDP estimate so probes never compete
/// with application traffic (same discipline as QUIC's probe-at-1.25×BDP rule,
/// RFC 9002 §7.7).
/// </para>
///
/// <para>
/// Callers do not need to manage probe lifecycle. The service is typically started
/// once at node startup and schedules probes internally when a link is idle.
/// Explicit calls to <see cref="ProbeAsync"/> are available for on-demand use
/// (e.g. before starting a stream or large content transfer).
/// </para>
/// </summary>
public interface IBandwidthProbeService
{
    /// <summary>
    /// Send an immediate probe to <paramref name="peerUhid"/> via the named transport.
    /// Returns null if the peer is unreachable or the probe times out.
    /// </summary>
    Task<BandwidthProbeResult?> ProbeAsync(
        string peerUhid,
        string transportName,
        CancellationToken ct = default);

    /// <summary>True if an active probe is in flight to this peer on any transport.</summary>
    bool IsProbing(string peerUhid);

    /// <summary>Fires when any probe round-trip completes (success or timeout).</summary>
    event EventHandler<BandwidthProbeResult> ProbeCompleted;
}

/// <summary>Result of a single active probe round-trip.</summary>
public sealed record BandwidthProbeResult(
    string PeerUhid,
    string TransportName,
    long MeasuredBtlBwBps,
    TimeSpan Rtt,
    bool Succeeded,
    DateTimeOffset MeasuredAt)
{
    /// <summary>A timed-out probe with zero bandwidth and a 30-second sentinel RTT.</summary>
    public static BandwidthProbeResult TimedOut(string peerUhid, string transport) =>
        new(peerUhid, transport, 0L, TimeSpan.FromSeconds(30), false, DateTimeOffset.UtcNow);
}
