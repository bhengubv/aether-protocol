// SPDX-License-Identifier: MIT

import Foundation

/// Cross-transport bandwidth synthesis and mesh gossip coordinator.
///
/// The director sits above individual `BandwidthEstimator` instances and provides two
/// capabilities that no existing congestion-control standard addresses:
///
/// 1. **Multi-transport BDP matrix.** AetherNet nodes may have BLE, Wi-Fi Direct,
///    NearLink, and HTTP relay transports active simultaneously. The director maintains
///    a per-peer-per-transport estimate matrix and answers "which transport should I use
///    for a 1 MB transfer to peer X?" correctly, even when the transports have wildly
///    different bandwidth profiles.
///
/// 2. **Mesh gossip pre-warming.** When two nodes first handshake, the director emits a
///    `BandwidthGossipPayload` carrying the local node's current BtlBw estimate. The
///    receiving node's director feeds this into the appropriate estimator via
///    `warmFromGossip` so the new session starts with a non-zero estimate. QUIC and TCP
///    always start cold at ~14.6 kB/s (RFC 6928 §2); gossip warming is unique to AetherNet.
///
/// Transport selection algorithm:
/// 1. Score = AvailableBps / PowerCostRelative (higher is better).
/// 2. If payload > BDP: prefer the transport with the largest BDP (reduces round-trips).
/// 3. Penalise transports with `BandwidthConfidence.none` by 50 % (untrusted estimate).
///
/// Thread safety: implemented as a Swift `actor`.
public final actor BandwidthDirector {

    // MARK: - State

    // (peerUhid, transportName) → latest sample
    private var matrix: [PeerTransportKey: BandwidthSample] = [:]

    // transportName → estimator
    private var estimators: [String: BandwidthEstimator] = [:]

    // Power costs per transport name (lower = preferred).
    private static let defaultPowerCosts: [String: Double] = [
        "NearLink":     1,
        "BLE":          2,
        "Wi-Fi Direct": 3,
        "CircleLink":   3,
        "QUIC Relay":   10,
        "HTTP Relay":   10,
    ]

    // MARK: - Init

    public init() {}

    // MARK: - Registration

    /// Register an estimator with this director. Called once per transport at startup.
    public func register(_ estimator: BandwidthEstimator) async {
        let name = estimator.transportName
        estimators[name] = estimator

        // Subscribe to sample improvements so the matrix stays current.
        await estimator.onSampleImproved.append { [weak self] sample in
            guard let self else { return }
            Task { await self.handleSampleImproved(sample) }
        }
    }

    // MARK: - Query

    /// Get the bandwidth estimate for a specific peer on a specific transport.
    /// Returns nil if no estimate exists yet.
    public func getEstimate(peerUhid: String, transport: String) async -> BandwidthSample? {
        matrix[PeerTransportKey(peerUhid: peerUhid, transportName: transport)]
    }

    /// Get all current estimates for a peer across all transports, ranked by
    /// `availableBps` descending.
    public func getEstimates(peerUhid: String) async -> [BandwidthSample] {
        matrix
            .filter { $0.key.peerUhid.lowercased() == peerUhid.lowercased() }
            .map { $0.value }
            .sorted { $0.availableBps > $1.availableBps }
    }

    /// Recommend the best transport for a payload of `payloadBytes`.
    /// Takes BDP, utilization, and power cost into account. Returns nil if the node
    /// has no available transports.
    public func recommendTransport(peerUhid: String, payloadBytes: Int64) async -> String? {
        let candidates = await getEstimates(peerUhid: peerUhid)

        if candidates.isEmpty {
            // No measurement data yet — fall back to the registered transport with lowest power cost.
            return estimators.values
                .min {
                    let a = BandwidthDirector.defaultPowerCosts[$0.transportName] ?? 5
                    let b = BandwidthDirector.defaultPowerCosts[$1.transportName] ?? 5
                    return a < b
                }
                .map { $0.transportName }
        }

        var bestSample: BandwidthSample? = nil
        var bestScore: Double = -.greatestFiniteMagnitude

        for s in candidates {
            let powerCost = BandwidthDirector.defaultPowerCosts[s.transportName] ?? 5.0
            let available = Double(s.availableBps)

            // Oversize payloads get a NEUTRAL 1.0 (not 0.0) so the available-bandwidth/
            // power term still ranks them — keeps selection identical across all 8 SDKs.
            let bdpBonus: Double = payloadBytes > s.bdpBytes ? 1.0 : 1.5

            // Penalise untrusted estimates.
            let confidenceFactor: Double = s.confidence == .none ? 0.5 : 1.0

            let score = (available / powerCost) * bdpBonus * confidenceFactor

            if score > bestScore {
                bestScore = score
                bestSample = s
            }
        }

        return bestSample?.transportName
    }

    /// Build a gossip payload for a new peer that has just completed handshake.
    /// Returns nil if no estimator exists for the transport or confidence is `.none`.
    public func buildGossipPayload(peerUhid: String, transport: String) async -> BandwidthGossipPayload? {
        guard let estimator = estimators[transport] else { return nil }
        let s = await estimator.currentSample
        guard s.confidence != .none else { return nil }

        return BandwidthGossipPayload(
            peerUhid:      peerUhid,
            transportName: transport,
            btlBwBps:      s.btlBwBps,
            rtPropUs:      Int64(s.rtProp * 1_000_000.0),
            confidence:    s.confidence,
            measuredAt:    s.measuredAt
        )
    }

    /// Receive and apply a gossip payload from a remote peer.
    public func applyGossip(_ payload: BandwidthGossipPayload) async {
        guard let estimator = estimators[payload.transportName] else { return }

        let rtProp = Double(payload.rtPropUs) / 1_000_000.0
        await estimator.warmFromGossip(
            btlBwBps:   payload.btlBwBps,
            rtProp:     rtProp,
            confidence: payload.confidence
        )

        // Seed the matrix so getEstimate returns something even before we probe.
        let sample = await estimator.currentSample
        let key = PeerTransportKey(peerUhid: payload.peerUhid, transportName: payload.transportName)
        matrix[key] = sample
    }

    // MARK: - Internal

    private func handleSampleImproved(_ sample: BandwidthSample) {
        // When an estimator fires, update every known peer's entry for this transport.
        for key in matrix.keys where key.transportName.lowercased() == sample.transportName.lowercased() {
            matrix[key] = sample
        }
    }
}

// MARK: - Key type

private struct PeerTransportKey: Hashable {
    let peerUhid: String
    let transportName: String
}
