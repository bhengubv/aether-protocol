// SPDX-License-Identifier: MIT

import Foundation

// MARK: - BandwidthConfidence

/// How confident we are in the current bandwidth estimate.
/// Rises with probe rounds; resets on topology change or extended idle.
public enum BandwidthConfidence: UInt8, Sendable, Comparable, Hashable, Codable {
    case none   = 0
    case low    = 1
    case medium = 2
    case high   = 3

    public static func < (lhs: BandwidthConfidence, rhs: BandwidthConfidence) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

// MARK: - BandwidthSample

/// Point-in-time bandwidth measurement for a single transport link.
///
/// Derivation follows BBRv3 (draft-cardwell-iccrg-bbr-congestion-control-02):
/// - `btlBwBps` — max delivery rate over 10×RTprop window.
/// - `rtProp`   — minimum RTT observed in last 10 s (ProbeRTT window).
/// - `srtt`     — RFC 6298 smoothed RTT (α = 1/8).
/// - `rttVar`   — RFC 6298 mean deviation (β = 1/4).
///
/// AetherNet innovations beyond existing standards:
/// - `bdpBytes`  — pre-computed BDP so callers never have to re-derive it.
/// - `phyCapBps` — PHY-layer cap from RSSI mapping; 0 if unknown.
/// - `confidence` — explicit quality tier used by ABR to decide trust level.
public struct BandwidthSample: Hashable, Sendable {
    /// Transport identifier (e.g. "BLE", "NearLink", "Wi-Fi Direct").
    public let transportName: String

    /// BBRv3 BtlBw: maximum sustained delivery rate the network can carry (bps).
    public let btlBwBps: Int64

    /// Available bandwidth ceiling: BtlBwBps × (1 − lossRate).
    public let availableBps: Int64

    /// Bandwidth-Delay Product: BtlBwBps × RtProp / 8 (bytes). Optimal in-flight window size.
    public let bdpBytes: Int64

    /// RFC 6298 smoothed RTT.
    public let srtt: TimeInterval

    /// RFC 6298 RTT mean deviation (RTTVAR).
    public let rttVar: TimeInterval

    /// BBRv3 RTprop: minimum observed RTT over the last 10 seconds.
    public let rtProp: TimeInterval

    /// EWMA fractional loss rate [0, 1]; α = 0.10.
    public let lossRate: Double

    /// PHY-layer bandwidth cap from RSSI hints (bps). 0 = unknown.
    public let phyCapBps: Int64

    /// How confident we are in this estimate.
    public let confidence: BandwidthConfidence

    /// UTC instant when this snapshot was built.
    public let measuredAt: Date

    public init(
        transportName: String,
        btlBwBps: Int64,
        availableBps: Int64,
        bdpBytes: Int64,
        srtt: TimeInterval,
        rttVar: TimeInterval,
        rtProp: TimeInterval,
        lossRate: Double,
        phyCapBps: Int64,
        confidence: BandwidthConfidence,
        measuredAt: Date
    ) {
        self.transportName = transportName
        self.btlBwBps      = btlBwBps
        self.availableBps  = availableBps
        self.bdpBytes      = bdpBytes
        self.srtt          = srtt
        self.rttVar        = rttVar
        self.rtProp        = rtProp
        self.lossRate      = lossRate
        self.phyCapBps     = phyCapBps
        self.confidence    = confidence
        self.measuredAt    = measuredAt
    }

    /// RFC 6298 §2.4 RTO: SRTT + max(G, 4×RTTVAR), G = 1 ms clock granularity.
    /// Clamped to [200 ms, 60 s] per §2.4.
    public var rto: TimeInterval {
        let raw = srtt + max(0.001, 4.0 * rttVar)
        return min(max(raw, 0.200), 60.0)
    }

    /// Effective bandwidth: min of btlBwBps and phyCapBps (if known).
    public var effectiveBps: Int64 {
        phyCapBps > 0 ? min(btlBwBps, phyCapBps) : btlBwBps
    }
}

// MARK: - BandwidthProbeAck

/// Four-timestamp probe ACK for two-way delay / RTT measurement (RFC 5136 §3).
/// All timestamps are microseconds since Unix epoch on each peer's local clock.
/// Clock synchronisation is not required — RTT is computed from sender-side timestamps only.
public struct BandwidthProbeAck: Sendable {
    public let sequence: UInt32
    public let senderSendUs: Int64
    public let receiverReceiveUs: Int64
    public let receiverSendUs: Int64
    public let senderReceiveUs: Int64
    public let probeBytes: Int32

    public init(
        sequence: UInt32,
        senderSendUs: Int64,
        receiverReceiveUs: Int64,
        receiverSendUs: Int64,
        senderReceiveUs: Int64,
        probeBytes: Int32
    ) {
        self.sequence          = sequence
        self.senderSendUs      = senderSendUs
        self.receiverReceiveUs = receiverReceiveUs
        self.receiverSendUs    = receiverSendUs
        self.senderReceiveUs   = senderReceiveUs
        self.probeBytes        = probeBytes
    }

    /// Round-trip time (clock-sync-free).
    /// RTT = (SenderReceive − SenderSend) − receiver processing time.
    public var rtt: TimeInterval {
        let totalUs = (senderReceiveUs - senderSendUs) - (receiverSendUs - receiverReceiveUs)
        return Double(totalUs) / 1_000_000.0
    }

    /// Forward one-way delay (sender → receiver). Requires loose clock sync;
    /// treat as approximate unless NTP/PTP is available.
    public var forwardOwd: TimeInterval {
        Double(receiverReceiveUs - senderSendUs) / 1_000_000.0
    }
}

// MARK: - BandwidthGossipPayload

/// Gossip payload that a node broadcasts to new peers during handshake.
/// Allows the new session to start with a warm BtlBw estimate instead of
/// probing from zero — unique to AetherNet's mesh topology awareness.
/// QUIC and TCP always cold-start; gossip warming is an AetherNet invention.
public struct BandwidthGossipPayload: Sendable {
    public let peerUhid: String
    public let transportName: String
    public let btlBwBps: Int64
    /// RTprop expressed in microseconds.
    public let rtPropUs: Int64
    public let confidence: BandwidthConfidence
    public let measuredAt: Date

    public init(
        peerUhid: String,
        transportName: String,
        btlBwBps: Int64,
        rtPropUs: Int64,
        confidence: BandwidthConfidence,
        measuredAt: Date
    ) {
        self.peerUhid      = peerUhid
        self.transportName = transportName
        self.btlBwBps      = btlBwBps
        self.rtPropUs      = rtPropUs
        self.confidence    = confidence
        self.measuredAt    = measuredAt
    }
}

// MARK: - NodeActivityState

/// High-level activity state of a node — suitable for status-bar indicators,
/// dashboard health badges, and connection-quality icons.
public enum NodeActivityState: Sendable, Equatable {
    /// No transports available. Node is isolated.
    case offline

    /// Transports available but no data in the last 5 s.
    case idle

    /// Data flowing; link utilization < 50 % of estimated capacity.
    case active

    /// Link utilization ≥ 50 %; performance good but approaching limits.
    case busy

    /// Loss rate > 5 % or delivery rate declining — likely interference.
    case degraded
}

// MARK: - TransportActivitySnapshot

/// Activity snapshot for a single transport within the node.
public struct TransportActivitySnapshot: Sendable {
    public let transportName: String
    public let isAvailable: Bool

    /// Bytes per second being received on this transport.
    public let ingressBps: Int64

    /// Bytes per second being sent on this transport.
    public let egressBps: Int64

    /// Smoothed RTT from the estimator.
    public let srtt: TimeInterval

    /// Bottleneck bandwidth from the estimator.
    public let btlBwBps: Int64

    /// Egress utilization fraction: egressBps / btlBwBps. 0 if btlBwBps = 0.
    public let utilizationFraction: Double

    public let state: NodeActivityState
    public let confidence: BandwidthConfidence

    public init(
        transportName: String,
        isAvailable: Bool,
        ingressBps: Int64,
        egressBps: Int64,
        srtt: TimeInterval,
        btlBwBps: Int64,
        utilizationFraction: Double,
        state: NodeActivityState,
        confidence: BandwidthConfidence
    ) {
        self.transportName      = transportName
        self.isAvailable        = isAvailable
        self.ingressBps         = ingressBps
        self.egressBps          = egressBps
        self.srtt               = srtt
        self.btlBwBps           = btlBwBps
        self.utilizationFraction = utilizationFraction
        self.state              = state
        self.confidence         = confidence
    }

    /// Human-readable utilization percentage string (e.g. "34 %").
    public var utilizationPercent: String {
        String(format: "%.0f %%", utilizationFraction * 100.0)
    }
}

// MARK: - NodeActivitySnapshot

/// Full node activity snapshot — the top-level model surfaced to UI.
///
/// Intended consumption patterns:
/// - **Status bar / widget:** poll `current` every 1 s.
/// - **Reactive UI:** subscribe via `NodeActivityMonitor.subscribe(_:)`.
/// - **ABR controller:** subscribe to check whether `state == .degraded` and
///   step down the bitrate ladder.
public struct NodeActivitySnapshot: Sendable {
    public let state: NodeActivityState

    /// Aggregate bytes per second flowing INTO this node (all transports).
    public let ingressBps: Int64

    /// Aggregate bytes per second flowing OUT of this node (all transports).
    public let egressBps: Int64

    /// Number of remote peers that had traffic in the last idle window.
    public let activePeers: Int

    /// Number of transports currently carrying data.
    public let activeTransports: Int

    /// Per-transport breakdown.
    public let transports: [TransportActivitySnapshot]

    /// Dominant transport: the one carrying the most egress bytes.
    /// Nil if node is offline or idle.
    public let primaryTransportName: String?

    public let timestamp: Date

    public init(
        state: NodeActivityState,
        ingressBps: Int64,
        egressBps: Int64,
        activePeers: Int,
        activeTransports: Int,
        transports: [TransportActivitySnapshot],
        primaryTransportName: String?,
        timestamp: Date
    ) {
        self.state                = state
        self.ingressBps           = ingressBps
        self.egressBps            = egressBps
        self.activePeers          = activePeers
        self.activeTransports     = activeTransports
        self.transports           = transports
        self.primaryTransportName = primaryTransportName
        self.timestamp            = timestamp
    }

    /// Combined throughput (ingress + egress).
    public var totalBps: Int64 { ingressBps + egressBps }

    /// True if any transport has data flowing.
    public var hasActivity: Bool {
        state == .active || state == .busy || state == .degraded
    }
}
