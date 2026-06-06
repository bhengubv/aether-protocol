// SPDX-License-Identifier: MIT
// Predictive transport selector — 2-state Kalman RTT filter over PerTransportMetrics.
//
// Why Kalman over EWMA?
// ─────────────────────
// EWMA is a 1-pole IIR: it smooths past measurements but cannot predict future RTT
// when a link is actively degrading.  The Kalman filter models RTT as a constant-
// velocity process [rtt, drift]:
//
//   x_t = F × x_{t−1} + w   (F = [[1,1],[0,1]])
//   z_t = H × x_t   + v    (H = [1,0])
//
// Positive drift signals a rising RTT *before* it exceeds a threshold, enabling
// proactive transport switching.  The posterior variance further penalises uncertain
// links even when their point estimate looks good.
//
// Score formula:
//   (effectiveBps / powerCost) × (1 − lossRate) / max(kalmanRtt, 1) × (1 / (1 + σ/100))
//
// Thread-safe via NSLock.
//
// NOTE: TransportService is AnyObject, so ObjectIdentifier is used as dictionary key
// to enable pointer-identity comparisons without requiring Hashable conformance.

import Foundation

// ── KalmanRttFilter ───────────────────────────────────────────────────────────

/// Two-state Kalman filter estimating RTT and drift for a single transport link.
///
/// State: x = [rtt; drift] — F = [[1,1],[0,1]], H = [1,0].
///
/// **Not thread-safe.** Callers must hold the selector's lock before calling `update`.
private final class KalmanRttFilter {

    private let qRtt:   Double
    private let qDrift: Double
    private let r:      Double

    private(set) var rtt:   Double
    private(set) var drift: Double
    private(set) var p00:   Double
    private(set) var p01:   Double
    private(set) var p11:   Double

    /// Posterior variance of the RTT estimate (ms²). Lower = more confident.
    var rttVariance: Double { p00 }

    init(
        initialRttMs: Double = 200.0,
        qRtt:         Double = 25.0,
        qDrift:       Double = 5.0,
        r:            Double = 100.0
    ) {
        self.rtt    = initialRttMs
        self.drift  = 0.0
        self.p00    = 400.0
        self.p01    = 0.0
        self.p11    = 100.0
        self.qRtt   = qRtt
        self.qDrift = qDrift
        self.r      = r
    }

    /// Incorporate a new RTT measurement and return the updated estimate.
    @discardableResult
    func update(measuredRttMs: Double) -> Double {
        // ── 1. Predict ────────────────────────────────────────────────────────
        let rttPred   = rtt + drift
        let driftPred = drift

        // P_pred = F·P·Fᵀ + Q  (F = [[1,1],[0,1]])
        let pp00 = p00 + 2.0 * p01 + p11 + qRtt
        let pp01 = p01 + p11
        let pp11 = p11 + qDrift

        // ── 2. Kalman gain (H = [1, 0]) ──────────────────────────────────────
        let S  = pp00 + r
        let k0 = pp00 / S
        let k1 = pp01 / S

        // ── 3. Update ─────────────────────────────────────────────────────────
        let innovation = measuredRttMs - rttPred
        rtt   = rttPred   + k0 * innovation
        drift = driftPred + k1 * innovation

        // P = (I − K·H)·P_pred
        p00 = (1.0 - k0) * pp00
        p01 = (1.0 - k0) * pp01
        p11 = -k1 * pp01 + pp11

        // Clamp to prevent numerical drift below zero.
        p00 = max(p00, 1e-6)
        p11 = max(p11, 1e-6)

        return rtt
    }
}

// ── PredictiveTransportSelector ───────────────────────────────────────────────

/// A transport paired with its Kalman-predictive score and uncertainty metadata.
public struct PredictedRankedTransport {
    /// The ranked transport backend.
    public let transport:      any TransportService
    /// Composite predictive score (higher = better).
    public let score:          Double
    /// Kalman-estimated RTT in milliseconds.
    public let predictedRttMs: Double
    /// Posterior RTT variance (ms²). Lower = more confident.
    public let rttVariance:    Double
}

/// Predictive transport selector maintaining a per-transport Kalman RTT filter.
///
/// `TransportService` conforms to `AnyObject`, so `ObjectIdentifier` is used as
/// the dictionary key for stable pointer-identity comparisons without requiring
/// `Hashable` conformance on the protocol.
///
/// Thread-safe via `NSLock`.
public final class PredictiveTransportSelector: @unchecked Sendable {

    private let lock = NSLock()

    // Keyed by ObjectIdentifier so we don't need TransportService: Hashable.
    private var filters:    [ObjectIdentifier: KalmanRttFilter]       = [:]
    private var transports: [ObjectIdentifier: any TransportService]  = [:]

    public init() {}

    // ── Registration ──────────────────────────────────────────────────────────

    /// Register a transport for Kalman tracking with an initial RTT prior.
    /// Safe to call multiple times — subsequent calls for already-registered
    /// transports are no-ops.
    public func register(_ transport: any TransportService, initialRttMs: Double = 200.0) {
        let id = ObjectIdentifier(transport)
        lock.withLock {
            guard filters[id] == nil else { return }
            filters[id]    = KalmanRttFilter(initialRttMs: initialRttMs)
            transports[id] = transport
        }
    }

    /// Remove a transport and discard its Kalman state.
    public func unregister(_ transport: any TransportService) {
        let id = ObjectIdentifier(transport)
        lock.withLock {
            filters.removeValue(forKey: id)
            transports.removeValue(forKey: id)
        }
    }

    // ── Observation ───────────────────────────────────────────────────────────

    /// Feed a new sample to both the transport's PerTransportMetrics EWMA and
    /// our Kalman filter.  Call after every completed send attempt.
    ///
    /// - Parameters:
    ///   - transport:        The transport that just completed a send.
    ///   - rttMs:            Measured round-trip time in ms.
    ///   - success:          Whether the peer acknowledged receipt.
    ///   - bytesTransferred: Bytes successfully transferred.
    public func observeMetrics(
        _ transport:       any TransportService,
        rttMs:             Double,
        success:           Bool,
        bytesTransferred:  Int
    ) {
        transport.metrics?.recordSample(
            rttMs: rttMs, success: success, bytesTransferred: bytesTransferred)

        guard rttMs > 0, success else { return }

        let id = ObjectIdentifier(transport)
        lock.withLock {
            filters[id]?.update(measuredRttMs: rttMs)
        }
    }

    // ── Ranking ───────────────────────────────────────────────────────────────

    /// Returns transports in descending predictive-score order.
    ///
    /// Only available transports are included.  `payloadBytes` is used to
    /// exclude transports too slow to deliver this payload within 30 s.
    public func rank(payloadBytes: Int = 512) -> [PredictedRankedTransport] {
        var result: [PredictedRankedTransport] = []

        lock.withLock {
            for (id, filter) in filters {
                guard let transport = transports[id] else { continue }
                guard transport.isAvailable else { continue }

                let bw = transport.maxBandwidthBps
                if bw > 0 {
                    let serialSec = Double(payloadBytes) * 8.0 / Double(bw)
                    if serialSec > 30.0 { continue }
                }

                let kalmanRtt = max(filter.rtt, 1.0)
                let variance  = filter.rttVariance
                let stddev    = variance.squareRoot()
                let power     = Double(max(transport.powerCostRelative, 1))

                let lossRate: Double
                let effectiveBps: Double

                if let m = transport.metrics {
                    lossRate     = m.ewmaLossRate
                    effectiveBps = max(m.ewmaThroughputBps, Double(bw) * 0.1)
                } else {
                    lossRate     = 0.05
                    effectiveBps = Double(bw) * 0.1
                }

                // Reliability factor: 1.0 at σ=0 ms, ~0.5 at σ=100 ms.
                let reliabilityFactor = 1.0 / (1.0 + stddev / 100.0)
                let score = (effectiveBps / power) * (1.0 - lossRate) / kalmanRtt * reliabilityFactor

                result.append(PredictedRankedTransport(
                    transport:      transport,
                    score:          score,
                    predictedRttMs: kalmanRtt,
                    rttVariance:    variance
                ))
            }
        }

        return result.sorted { $0.score > $1.score }
    }

    /// Returns the highest-scoring available transport, or `nil`.
    public func selectBest(payloadBytes: Int = 512) -> (any TransportService)? {
        rank(payloadBytes: payloadBytes).first?.transport
    }

    /// Returns `(rttMs, driftMs, variance)` for a registered transport, or `nil`.
    public func kalmanState(
        for transport: any TransportService
    ) -> (rttMs: Double, driftMs: Double, variance: Double)? {
        let id = ObjectIdentifier(transport)
        return lock.withLock {
            guard let f = filters[id] else { return nil }
            return (f.rtt, f.drift, f.p00)
        }
    }
}
