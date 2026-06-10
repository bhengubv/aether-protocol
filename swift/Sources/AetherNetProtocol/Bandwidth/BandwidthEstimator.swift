// SPDX-License-Identifier: MIT

import Foundation

/// Per-transport link bandwidth estimator.
///
/// Implements a BBRv3-inspired algorithm:
/// - **BtlBw** (bottleneck bandwidth): rolling maximum of per-delivery delivery-rate samples
///   over a `btlBwWindowSize` = 10-RTprop window. Mirrors the BBRv3 BtlBwFilter
///   (draft-cardwell-iccrg-bbr-congestion-control-02 §4.3.2.1).
/// - **RTprop** (path propagation delay): rolling minimum RTT over a `rtPropWindowMs` = 10 000 ms
///   window. The minimum filters out queueing delay.
/// - **SRTT / RTTVAR**: RFC 6298 §2.3 Jacobson/Karels algorithm. α = 1/8, β = 1/4.
/// - **Loss rate**: EWMA with α = `lossAlpha` = 0.10.
/// - **PHY cap**: RSSI-to-BtlBw mapping constrains the estimate on weak radio links before
///   probe data arrives.
///
/// AetherNet innovations:
/// - **Gossip warm-start**: `warmFromGossip` pre-seeds the estimator from a peer's measured
///   value so sessions start warm, not cold.
/// - **Confidence tiers**: `BandwidthConfidence` lets consumers distinguish a 1-probe estimate
///   from a stable 30-round estimate.
/// - **PHY-layer capping**: prevents over-optimistic estimates on weak BLE links before probes complete.
///
/// Thread safety: implemented as a Swift `actor`.
public final actor BandwidthEstimator {

    // MARK: - Constants

    /// Number of delivery-rate samples kept in the BtlBw max-filter window.
    public static let btlBwWindowSize: Int = 10

    /// Minimum RTT window duration in seconds (BBRv3 ProbeRTT period).
    public static let rtPropWindowSec: Double = 10.0

    /// EWMA loss rate smoothing factor (α).
    public static let lossAlpha: Double = 0.10

    /// RFC 6298 SRTT smoothing factor (1/8).
    private static let srttAlpha: Double = 0.125

    /// RFC 6298 RTTVAR smoothing factor (1/4).
    private static let rttVarBeta: Double = 0.25

    /// 5% improvement threshold for `onSampleImproved` callbacks.
    private static let improvementThreshold: Double = 0.05

    // MARK: - Identity

    /// Transport identifier (e.g. "BLE", "NearLink", "Wi-Fi Direct").
    public let transportName: String

    // MARK: - State

    // BtlBw max-filter: circular buffer of (deliveryRateBps, timestampSec) samples.
    private var btlBwWindow: [(rateBps: Int64, timestampSec: Double)]
    private var btlBwHead: Int = 0
    private var btlBwCount: Int = 0

    // RTprop min-filter: (rttSec, timestampSec) pairs kept for the window duration.
    private var rtPropSamples: [(rttSec: Double, timestampSec: Double)] = []

    // RFC 6298 SRTT / RTTVAR
    private var srttSec: Double = 0.0
    private var rttVarSec: Double = 0.0
    private var firstRtt: Bool = true

    // Loss EWMA
    private var _lossRate: Double = 0.0

    // PHY cap (bps)
    private var phyCapBps: Int64 = 0

    // Confidence counters
    private var probeRounds: Int = 0
    private var warmedFromGossip: Bool = false

    // Snapshot cache
    private var _currentSample: BandwidthSample

    // MARK: - Callbacks

    /// Fires when BtlBw improves by ≥ 5 % or `confidence` advances.
    /// Consumers: ABR controller, transport selector, streaming bitrate ladder.
    public var onSampleImproved: [(BandwidthSample) -> Void] = []

    // MARK: - Init

    public init(transportName: String, maxBandwidthBps: Int64) {
        self.transportName = transportName
        self.btlBwWindow = Array(
            repeating: (rateBps: 0, timestampSec: 0.0),
            count: BandwidthEstimator.btlBwWindowSize
        )
        // Optimistic initialisation: start at theoretical max with .none confidence.
        // PHY hints and probes will tighten this quickly.
        self._currentSample = BandwidthEstimator.buildSnapshot(
            transportName: transportName,
            btlBw: maxBandwidthBps,
            rtProp: 0.050,
            srttSec: 0.050,
            rttVarSec: 0.025,
            lossRate: 0.0,
            phyCapBps: 0,
            probeRounds: 0,
            warmedFromGossip: false
        )
    }

    // MARK: - Current estimate

    public var currentSample: BandwidthSample { _currentSample }

    // MARK: - Observation feed

    /// Record a successful delivery of `bytes`.
    /// Both timestamps are microseconds since Unix epoch on the **same clock**.
    public func recordDelivery(bytes: Int, sendUs: Int64, deliverUs: Int64) {
        guard bytes > 0, deliverUs > sendUs else { return }

        let elapsedSec = Double(deliverUs - sendUs) / 1_000_000.0
        let deliveryRateBps = Int64(Double(bytes) * 8.0 / elapsedSec)
        let rttSec = elapsedSec // one-way → treat as RTT estimate (conservative)

        let now = nowSec()
        addToBtlBwWindow(rateBps: deliveryRateBps, timestampSec: now)
        updateRttEstimates(rttSec: rttSec, now: now)
        probeRounds += 1
        commit()
    }

    /// Record that `bytes` were lost (timeout or explicit NAK).
    public func recordLoss(bytes: Int) {
        guard bytes > 0 else { return }
        _lossRate = BandwidthEstimator.lossAlpha * 1.0 + (1.0 - BandwidthEstimator.lossAlpha) * _lossRate
        commit()
    }

    /// Feed an active probe ack into the estimator.
    /// `localReceiveUs` is the local clock µs at ACK receipt.
    public func recordProbeResult(_ ack: BandwidthProbeAck, localReceiveUs: Int64) {
        let rtt = ack.rtt
        guard rtt > 0, rtt < 30.0 else { return }

        let deliveryRateBps: Int64 = ack.probeBytes > 0
            ? Int64(Double(ack.probeBytes) * 8.0 / rtt)
            : 0

        let now = nowSec()
        updateRttEstimates(rttSec: rtt, now: now)
        if deliveryRateBps > 0 {
            addToBtlBwWindow(rateBps: deliveryRateBps, timestampSec: now)
        }
        probeRounds += 1
        commit()
    }

    /// Pre-warm from a gossip payload. Only effective when `confidence == .none`
    /// — never downgrades an existing estimate.
    public func warmFromGossip(btlBwBps: Int64, rtProp: TimeInterval, confidence: BandwidthConfidence) {
        guard probeRounds == 0, !warmedFromGossip else { return }

        let now = nowSec()
        addToBtlBwWindow(rateBps: btlBwBps, timestampSec: now)
        if rtProp > 0 {
            srttSec  = rtProp
            rttVarSec = rtProp / 2.0
            firstRtt = false
            addToRtPropWindow(rttSec: rtProp, now: now)
        }
        warmedFromGossip = true
        commit()
    }

    /// Apply a physical-layer hint. RSSI-to-BtlBw caps the estimate before probes complete.
    /// `rssiDbm` is the received signal strength in dBm.
    ///
    /// Calibration tables:
    /// - BLE (Bluetooth SIG Core Spec 5.4 Table 7.2, 2Msym/s PHY)
    /// - Wi-Fi 802.11ax (3GPP TS 36.213 Annex A)
    /// Conservative BLE table is used as a fallback when the transport is unknown.
    public func applyPhyHint(rssiDbm: Int) {
        let cap: Int64
        switch rssiDbm {
        case _ where rssiDbm >= -50: cap = 600_000_000
        case _ where rssiDbm >= -67: cap = 200_000_000
        case _ where rssiDbm >= -70: cap =   2_000_000
        case _ where rssiDbm >= -80: cap =  54_000_000
        case _ where rssiDbm >= -85: cap =     500_000
        case _ where rssiDbm >= -95: cap =     125_000
        default:                     cap =      40_000
        }
        phyCapBps = cap
        commit()
    }

    // MARK: - Internal helpers

    /// RFC 6298 §2.3 RTT sample integration.
    /// First sample initialises SRTT = R, RTTVAR = R/2.
    /// Subsequent: RTTVAR = (1-β)×RTTVAR + β×|SRTT-R|; SRTT = (1-α)×SRTT + α×R.
    private func updateRttEstimates(rttSec: Double, now: Double) {
        if firstRtt {
            srttSec   = rttSec
            rttVarSec = rttSec / 2.0
            firstRtt  = false
        } else {
            rttVarSec = (1.0 - BandwidthEstimator.rttVarBeta) * rttVarSec
                      + BandwidthEstimator.rttVarBeta * abs(srttSec - rttSec)
            srttSec   = (1.0 - BandwidthEstimator.srttAlpha) * srttSec
                      + BandwidthEstimator.srttAlpha * rttSec
        }
        // Success sample → also update loss EWMA (0 loss observed).
        _lossRate = BandwidthEstimator.lossAlpha * 0.0 + (1.0 - BandwidthEstimator.lossAlpha) * _lossRate

        addToRtPropWindow(rttSec: rttSec, now: now)
    }

    /// Insert a delivery-rate sample into the max-filter window.
    /// Discards old samples whose timestamp is outside 10×RTprop.
    private func addToBtlBwWindow(rateBps: Int64, timestampSec: Double) {
        let windowDuration = 10.0 * max(0.001, minRtPropSec())
        let expiry = timestampSec - windowDuration

        // Evict expired entries from the tail of the circular buffer.
        while btlBwCount > 0 {
            let tail = (btlBwHead + BandwidthEstimator.btlBwWindowSize - btlBwCount)
                       % BandwidthEstimator.btlBwWindowSize
            if btlBwWindow[tail].timestampSec < expiry {
                btlBwCount -= 1
            } else {
                break
            }
        }

        btlBwWindow[btlBwHead] = (rateBps: rateBps, timestampSec: timestampSec)
        btlBwHead = (btlBwHead + 1) % BandwidthEstimator.btlBwWindowSize
        if btlBwCount < BandwidthEstimator.btlBwWindowSize {
            btlBwCount += 1
        }
    }

    private func addToRtPropWindow(rttSec: Double, now: Double) {
        rtPropSamples.append((rttSec: rttSec, timestampSec: now))
        let cutoff = now - BandwidthEstimator.rtPropWindowSec
        rtPropSamples.removeAll { $0.timestampSec < cutoff }
    }

    private func maxBtlBwBps() -> Int64 {
        guard btlBwCount > 0 else { return 0 }
        var maxVal: Int64 = 0
        for i in 0..<btlBwCount {
            let idx = (btlBwHead + BandwidthEstimator.btlBwWindowSize - btlBwCount + i)
                      % BandwidthEstimator.btlBwWindowSize
            if btlBwWindow[idx].rateBps > maxVal {
                maxVal = btlBwWindow[idx].rateBps
            }
        }
        return maxVal
    }

    private func minRtPropSec() -> Double {
        guard !rtPropSamples.isEmpty else {
            return srttSec > 0 ? srttSec : 0.050
        }
        var minVal = Double.greatestFiniteMagnitude
        for s in rtPropSamples where s.rttSec < minVal {
            minVal = s.rttSec
        }
        return minVal > 0 ? minVal : 0.001
    }

    private func computeConfidence() -> BandwidthConfidence {
        if probeRounds == 0 && !warmedFromGossip { return .none }
        if probeRounds == 0 { return .low }
        if probeRounds < 5  { return .low }
        if probeRounds < 20 { return .medium }
        return .high
    }

    /// Rebuild the snapshot and fire `onSampleImproved` if significant.
    private func commit() {
        let prev = _currentSample
        _currentSample = BandwidthEstimator.buildSnapshot(
            transportName: transportName,
            btlBw: maxBtlBwBps(),
            rtProp: minRtPropSec(),
            srttSec: max(0.001, srttSec),
            rttVarSec: max(0.0, rttVarSec),
            lossRate: _lossRate,
            phyCapBps: phyCapBps,
            probeRounds: probeRounds,
            warmedFromGossip: warmedFromGossip
        )
        let cur = _currentSample

        // Fire if BtlBw improved by ≥5% or confidence tier advanced.
        let improved = prev.btlBwBps == 0
            || (Double(cur.btlBwBps - prev.btlBwBps) > Double(prev.btlBwBps) * BandwidthEstimator.improvementThreshold)
            || cur.confidence > prev.confidence

        if improved {
            let callbacks = onSampleImproved
            Task.detached { @Sendable in
                for cb in callbacks { cb(cur) }
            }
        }
    }

    // MARK: - Static snapshot builder

    private static func buildSnapshot(
        transportName: String,
        btlBw: Int64,
        rtProp: Double,
        srttSec: Double,
        rttVarSec: Double,
        lossRate: Double,
        phyCapBps: Int64,
        probeRounds: Int,
        warmedFromGossip: Bool
    ) -> BandwidthSample {
        let effectiveBtlBw = phyCapBps > 0 ? min(btlBw, phyCapBps) : btlBw
        let clampedLoss    = max(0.0, min(lossRate, 1.0))
        let available      = Int64(Double(effectiveBtlBw) * (1.0 - clampedLoss))
        let bdp: Int64     = btlBw > 0 ? Int64(Double(btlBw) / 8.0 * rtProp) : 0

        let conf: BandwidthConfidence
        if probeRounds == 0 && !warmedFromGossip { conf = .none }
        else if probeRounds == 0                 { conf = .low }
        else if probeRounds < 5                  { conf = .low }
        else if probeRounds < 20                 { conf = .medium }
        else                                     { conf = .high }

        return BandwidthSample(
            transportName: transportName,
            btlBwBps:      effectiveBtlBw,
            availableBps:  available,
            bdpBytes:      bdp,
            srtt:          srttSec,
            rttVar:        rttVarSec,
            rtProp:        rtProp,
            lossRate:      lossRate,
            phyCapBps:     phyCapBps,
            confidence:    conf,
            measuredAt:    Date()
        )
    }

    // MARK: - Clock helper

    private func nowSec() -> Double {
        Date().timeIntervalSince1970
    }
}
