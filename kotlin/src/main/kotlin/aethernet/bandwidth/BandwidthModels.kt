// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import java.time.Duration
import java.time.Instant

// ── Confidence ────────────────────────────────────────────────────────────────

/**
 * How confident we are in the current bandwidth estimate.
 * Rises with probe rounds; resets on topology change or extended idle.
 */
enum class BandwidthConfidence { NONE, LOW, MEDIUM, HIGH }

// ── BandwidthSample ───────────────────────────────────────────────────────────

/**
 * Point-in-time bandwidth measurement for a single transport link.
 *
 * Derivation follows BBRv3 (draft-cardwell-iccrg-bbr-congestion-control-02):
 * - [btlBwBps] — max delivery rate over 10×RTprop window.
 * - [rtProp]   — minimum RTT observed in last 10 s (ProbeRTT window).
 * - [srtt]     — RFC 6298 smoothed RTT (α = 1/8).
 * - [rttVar]   — RFC 6298 mean deviation (β = 1/4).
 *
 * AetherNet-specific extensions beyond BBRv3 / RFC 9002 / GCC:
 * - [bdpBytes]  — pre-computed BDP so callers never re-derive it.
 * - [phyCapBps] — PHY-layer cap from RSSI mapping; 0 if unknown.
 * - [confidence] — explicit quality tier for ABR decisions.
 */
data class BandwidthSample(
    val transportName: String,

    /** BBRv3 BtlBw: maximum sustained delivery rate the network can carry (bps). */
    val btlBwBps: Long,

    /** Available bandwidth ceiling: btlBwBps × (1 − lossRate). */
    val availableBps: Long,

    /** Bandwidth-Delay Product: btlBwBps × rtProp / 8 (bytes). Optimal in-flight window size. */
    val bdpBytes: Long,

    /** RFC 6298 smoothed RTT. */
    val srtt: Duration,

    /** RFC 6298 RTT mean deviation (RTTVAR). */
    val rttVar: Duration,

    /** BBRv3 RTprop: minimum observed RTT over the last 10 seconds. */
    val rtProp: Duration,

    /** EWMA fractional loss rate [0, 1]; α = 0.10. */
    val lossRate: Double,

    /** PHY-layer bandwidth cap from RSSI hints (bps). 0 = unknown. */
    val phyCapBps: Long,

    val confidence: BandwidthConfidence,
    val measuredAt: Instant,
) {
    /**
     * RFC 6298 §2.4 RTO: SRTT + max(G, 4×RTTVAR), G = 1 ms clock granularity.
     * Clamped to [200 ms, 60 s] per §2.4.
     */
    val rto: Duration
        get() {
            val rawMs = srtt.toMillis() + maxOf(1.0, 4.0 * rttVar.toMillis().toDouble())
            return Duration.ofMillis(rawMs.toLong().coerceIn(200L, 60_000L))
        }

    /** Effective bandwidth: min of btlBwBps and phyCapBps (if known). */
    val effectiveBps: Long
        get() = if (phyCapBps > 0L) minOf(btlBwBps, phyCapBps) else btlBwBps
}

// ── Probe wire models ─────────────────────────────────────────────────────────

/**
 * Four-timestamp probe ACK for two-way delay / RTT measurement (RFC 5136 §3).
 * All timestamps are microseconds since Unix epoch on each peer's local clock.
 * Clock synchronisation is not required — RTT is computed from sender-side timestamps only.
 */
data class BandwidthProbeAck(
    val sequence: UInt,
    val senderSendUs: Long,
    val receiverReceiveUs: Long,
    val receiverSendUs: Long,
    val senderReceiveUs: Long,
    val probeBytes: Int,
) {
    /**
     * Round-trip time (clock-sync-free).
     * RTT = (SenderReceive − SenderSend) − receiver processing time.
     */
    val rtt: Duration
        get() = Duration.ofNanos(
            ((senderReceiveUs - senderSendUs) - (receiverSendUs - receiverReceiveUs)) * 1_000L
        )

    /**
     * Forward one-way delay (sender → receiver). Requires loose clock sync;
     * treat as approximate unless NTP/PTP is available.
     */
    val forwardOwd: Duration
        get() = Duration.ofNanos((receiverReceiveUs - senderSendUs) * 1_000L)
}

// ── Gossip warm-start ─────────────────────────────────────────────────────────

/**
 * Gossip payload that a node broadcasts to new peers during handshake.
 * Allows the new session to start with a warm BtlBw estimate instead of
 * probing from zero — unique to AetherNet's mesh topology awareness.
 * QUIC and TCP always cold-start; gossip warming is an AetherNet invention.
 */
data class BandwidthGossipPayload(
    val peerUhid: String,
    val transportName: String,
    val btlBwBps: Long,
    val rtPropUs: Long,
    val confidence: BandwidthConfidence,
    val measuredAt: Instant,
)

// ── Node activity (UI layer) ──────────────────────────────────────────────────

/**
 * High-level activity state of a node — suitable for status-bar indicators,
 * dashboard health badges, and connection-quality icons.
 */
enum class NodeActivityState {
    /** No transports available. Node is isolated. */
    OFFLINE,

    /** Transports available but no data in the last 5 s. */
    IDLE,

    /** Data flowing; link utilization < 50 % of estimated capacity. */
    ACTIVE,

    /** Link utilization ≥ 50 %; performance good but approaching limits. */
    BUSY,

    /** Loss rate > 5 % or delivery rate declining — likely interference. */
    DEGRADED,
}

/**
 * Activity snapshot for a single transport within the node.
 */
data class TransportActivitySnapshot(
    val transportName: String,
    val isAvailable: Boolean,

    /** Bytes per second being received on this transport. */
    val ingressBps: Long,

    /** Bytes per second being sent on this transport. */
    val egressBps: Long,

    /** Smoothed RTT from BandwidthEstimator. */
    val srtt: Duration,

    /** Bottleneck bandwidth from BandwidthEstimator. */
    val btlBwBps: Long,

    /** Egress utilization fraction: egressBps / btlBwBps. 0 if btlBwBps = 0. */
    val utilizationFraction: Double,

    val state: NodeActivityState,
    val confidence: BandwidthConfidence,
) {
    /** Human-readable utilization percentage string (e.g. "34 %"). */
    val utilizationPercent: String
        get() = "%.0f %%".format(utilizationFraction * 100.0)
}

/**
 * Full node activity snapshot — the top-level model surfaced to UI.
 *
 * Intended consumption patterns:
 * - Status bar / widget: poll [NodeActivityMonitor.current] every 1 s.
 * - Dashboard / reactive UI: subscribe to snapshot callbacks.
 * - ABR controller: watch for [NodeActivityState.DEGRADED] and step down the bitrate ladder.
 */
data class NodeActivitySnapshot(
    val state: NodeActivityState,

    /** Aggregate bytes per second flowing INTO this node (all transports). */
    val ingressBps: Long,

    /** Aggregate bytes per second flowing OUT of this node (all transports). */
    val egressBps: Long,

    /** Number of remote peers that had traffic in the last 5 s. */
    val activePeers: Int,

    /** Number of transports currently carrying data. */
    val activeTransports: Int,

    /** Per-transport breakdown. */
    val transports: List<TransportActivitySnapshot>,

    /**
     * Dominant transport: the one carrying the most egress bytes.
     * Null if node is offline or idle.
     */
    val primaryTransportName: String?,

    val timestamp: Instant,
) {
    /** Combined throughput (ingress + egress). */
    val totalBps: Long get() = ingressBps + egressBps

    /** True if any transport has data flowing. */
    val hasActivity: Boolean
        get() = state == NodeActivityState.ACTIVE ||
                state == NodeActivityState.BUSY ||
                state == NodeActivityState.DEGRADED
}
