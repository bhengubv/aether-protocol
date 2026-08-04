// SPDX-License-Identifier: MIT

namespace AetherNet.Qos;

/// <summary>
/// A traffic lane — <em>how</em> the scheduler should move a packet, independent of <em>what</em> it
/// carries. The pipe knows the lane, never the app: a <see cref="Realtime"/> lane carries a voice call,
/// a watch-together, or a game all the same. Ordered most-urgent to most-deferrable.
///
/// <para>This is a LOCAL scheduling hint chosen on the sending node to order its own outbound queue — it
/// is never written to the wire (an app-identifying class on the wire would be a profiling surface).</para>
/// </summary>
public enum TrafficClass
{
    /// <summary>Life-safety traffic (SOS). Always sent first, never deferred.</summary>
    Emergency = 0,

    /// <summary>Control plane — routing, handshakes, keepalives, ACKs, discovery, measurement. Small, must-deliver.</summary>
    Control = 1,

    /// <summary>Latency-sensitive interactive media — voice, video, screen-share, watch-together.</summary>
    Realtime = 2,

    /// <summary>Normal application traffic — messages, profile sync. The default lane.</summary>
    Standard = 3,

    /// <summary>Throughput-oriented, latency-tolerant bulk — content chunks, downloads, DTN bundles, sync.</summary>
    Bulk = 4,
}
