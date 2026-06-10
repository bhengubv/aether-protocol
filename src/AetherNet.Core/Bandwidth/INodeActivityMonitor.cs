// SPDX-License-Identifier: MIT

namespace AetherNet.Bandwidth;

/// <summary>
/// Observable node activity monitor — the UI-facing layer of the AetherNet
/// Bandwidth Measurement Framework.
///
/// <para>
/// Produces <see cref="NodeActivitySnapshot"/> objects at a configurable cadence
/// (default 500 ms). Each snapshot aggregates per-transport ingress/egress rates,
/// active peer counts, and a unified <see cref="NodeActivityState"/> for status indicators.
/// </para>
///
/// <para>Consumption patterns:</para>
/// <list type="bullet">
///   <item>
///     <b>Status bar / widget (polling):</b>
///     Read <see cref="Current"/> on a 1-second timer.
///     No subscription overhead; thread-safe.
///   </item>
///   <item>
///     <b>Blazor / reactive UI:</b>
///     Subscribe to <see cref="SnapshotChanged"/> or
///     <see cref="Activity"/> (IObservable).
///   </item>
///   <item>
///     <b>BigBruh SignalR dashboard:</b>
///     Subscribe to <see cref="SnapshotChanged"/> and push snapshots to the hub.
///   </item>
///   <item>
///     <b>ABR controller:</b>
///     Subscribe to watch for <see cref="NodeActivityState.Degraded"/> and
///     step down the bitrate ladder before users notice quality drops.
///   </item>
/// </list>
///
/// <para>
/// The monitor is intentionally read-only and allocation-minimal — snapshots are
/// <c>sealed record</c> value types; the inner per-transport list is pre-allocated
/// and reused between cycles. The background sampling loop runs at low CPU priority.
/// </para>
/// </summary>
public interface INodeActivityMonitor
{
    /// <summary>
    /// The most recent snapshot. Never null after first sample; initialises to an
    /// <see cref="NodeActivityState.Offline"/> snapshot with zero rates.
    /// Thread-safe (volatile reference swap on each update cycle).
    /// </summary>
    NodeActivitySnapshot Current { get; }

    /// <summary>
    /// Fires on the update thread every <see cref="SampleIntervalMs"/> milliseconds
    /// when the snapshot changes (state, rates, or active peer count changed).
    /// Does NOT fire when the snapshot is identical to the previous one (avoids
    /// spamming unchanged dashboards).
    /// </summary>
    event EventHandler<NodeActivitySnapshot> SnapshotChanged;

    /// <summary>
    /// IObservable stream of snapshots. Emits one item per update cycle
    /// (regardless of change) for consumers that want a steady heartbeat.
    /// Use <see cref="SnapshotChanged"/> for change-only notifications.
    /// </summary>
    IObservable<NodeActivitySnapshot> Activity { get; }

    /// <summary>How often the monitor re-samples (milliseconds). Default: 500.</summary>
    int SampleIntervalMs { get; set; }

    /// <summary>
    /// How long without observed traffic before a transport is considered idle (seconds).
    /// Default: 5.
    /// </summary>
    int IdleThresholdSeconds { get; set; }
}
