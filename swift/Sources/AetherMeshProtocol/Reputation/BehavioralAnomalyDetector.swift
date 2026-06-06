// SPDX-License-Identifier: MIT

import Foundation

// MARK: - Options

/// Tuning knobs for all three anomaly detectors.
///
/// All fields have production-safe defaults; pass a customised instance in
/// tests to exercise edge-cases without waiting for real-time windows.
public struct AnomalyDetectorOptions {
    /// Length of the rolling volume window in milliseconds (default 30 s).
    public var volumeWindowMs: Int64         = 30_000
    /// Packets-per-window multiplier over the EWMA baseline that trips a spike (default 5×).
    public var volumeSpikeMultiplier: Double = 5.0
    /// Exponential smoothing factor applied when a volume window rolls (default α = 0.20).
    public var ewmaAlpha: Double             = 0.20
    /// Sliding window over which unique destinations are counted (default 60 s).
    public var scatterWindowMs: Int64        = 60_000
    /// Unique-destination count that trips scatter detection (default 50).
    public var scatterThreshold: Int         = 50
    /// Number of leading geohash characters compared for location verification (default 4).
    public var geohashPrefixLength: Int      = 4
    /// Minimum gap in ms between consecutive geohash-mismatch penalties for the same UHID (default 60 s).
    public var geohashRateLimitMs: Int64     = 60_000

    public init() {}
}

// MARK: - Actor

/// Thread-safe behavioral anomaly detector that drives ``NodeReputationService``
/// based on three orthogonal signal sources:
///
/// 1. **Volume spike** — EWMA-based packet-rate anomaly per source UHID.
/// 2. **Destination scatter** — fan-out to an unusually large set of peers.
/// 3. **Geohash mismatch** — claimed location doesn't match routing evidence.
///
/// All detection is reactive (no background timers); call the `observe*` methods
/// from your packet-processing pipeline.
public actor BehavioralAnomalyDetector {

    // MARK: - Dependencies

    /// The reputation service that receives penalties when anomalies fire.
    public let reputation: NodeReputationService

    // MARK: - Configuration

    private let opts: AnomalyDetectorOptions

    // MARK: - Volume state

    /// Per-source rolling-window + EWMA state.
    private var volumeState: [String: VolumeState] = [:]

    private struct VolumeState {
        var windowStart: Int64   = 0
        var windowCount: Int     = 0
        var ewmaBaseline: Double = 0
        var hasBaseline: Bool    = false
    }

    // MARK: - Scatter state

    /// Per-source list of (destination, timestamp) observations within the scatter window.
    private var scatterEntries: [String: [(dest: String, ts: Int64)]] = [:]

    // MARK: - Geohash state

    /// Timestamp of the last geohash-mismatch penalty issued per UHID (for rate-limiting).
    private var geoLastSignal: [String: Int64] = [:]

    // MARK: - Initialiser

    public init(reputation: NodeReputationService,
                opts: AnomalyDetectorOptions = .init()) {
        self.reputation = reputation
        self.opts = opts
    }

    // MARK: - Public API

    /// Record a packet from `sourceUhid` to `destinationUhid` at `timestampMs`.
    ///
    /// Internally runs both the volume-spike check and the destination-scatter check.
    public func observePacket(sourceUhid: String,
                               destinationUhid: String,
                               timestampMs: Int64) async {
        await checkVolume(sourceUhid: sourceUhid, timestampMs: timestampMs)
        await checkScatter(sourceUhid: sourceUhid,
                           destinationUhid: destinationUhid,
                           timestampMs: timestampMs)
    }

    /// Compare `claimedGeohash` against the geohash inferred from routing
    /// (`observedRoutingGeohash`).  When the leading `geohashPrefixLength`
    /// characters differ, a ``NodeReputationService/recordSignatureFailure(uhid:)``
    /// is fired — subject to `geohashRateLimitMs` per-UHID throttling.
    ///
    /// - Parameter timestampMs: Caller-supplied logical timestamp (ms since epoch).
    ///   Accepting this explicitly keeps the method deterministically testable.
    public func observeGeohashClaim(uhid: String,
                                     claimedGeohash: String,
                                     observedRoutingGeohash: String,
                                     timestampMs: Int64) async {
        let claimedPrefix  = String(claimedGeohash.prefix(opts.geohashPrefixLength))
        let observedPrefix = String(observedRoutingGeohash.prefix(opts.geohashPrefixLength))

        guard claimedPrefix != observedPrefix else { return }

        // Rate-limit: suppress if a penalty was already issued within the window.
        let shouldFire: Bool
        if let last = geoLastSignal[uhid] {
            shouldFire = (timestampMs - last) >= opts.geohashRateLimitMs
        } else {
            shouldFire = true
        }

        if shouldFire {
            geoLastSignal[uhid] = timestampMs
            await reputation.recordSignatureFailure(uhid: uhid)
        }
    }

    /// Observe an SPK signature verification failure for `uhid`.
    ///
    /// This is a direct passthrough to ``NodeReputationService/recordSignatureFailure(uhid:)``.
    public func observeSpkSigFailure(uhid: String) async {
        await reputation.recordSignatureFailure(uhid: uhid)
    }

    // MARK: - Internal: volume-spike detection

    /// Rolling-window EWMA volume-spike check.
    ///
    /// **Algorithm:**
    /// - While the current timestamp is still within the active window, just
    ///   increment the counter.
    /// - When the window expires, compare the completed window's packet count
    ///   against the EWMA baseline:
    ///   - First completed window seeds the baseline (no penalty yet).
    ///   - Subsequent windows update the EWMA with α and fire a flood penalty
    ///     if `windowCount > multiplier × ewmaBaseline` (and baseline > 0).
    ///   - Reset window start and count for the new period.
    private func checkVolume(sourceUhid: String, timestampMs: Int64) async {
        var state = volumeState[sourceUhid] ?? VolumeState()

        if state.windowStart == 0 {
            // First packet ever from this source — open the first window.
            state.windowStart = timestampMs
            state.windowCount = 1
            volumeState[sourceUhid] = state
            return
        }

        let elapsed = timestampMs - state.windowStart

        if elapsed >= opts.volumeWindowMs {
            // Window has expired — evaluate then roll.
            let completedCount = state.windowCount

            if !state.hasBaseline {
                // Seed baseline from first completed window; no penalty.
                state.ewmaBaseline = Double(completedCount)
                state.hasBaseline  = true
            } else {
                // Update EWMA.
                let alpha    = opts.ewmaAlpha
                let oldEwma  = state.ewmaBaseline
                state.ewmaBaseline = alpha * Double(completedCount) + (1.0 - alpha) * oldEwma

                // Fire if spike is above threshold.
                if state.ewmaBaseline > 0,
                   Double(completedCount) > opts.volumeSpikeMultiplier * oldEwma {
                    await reputation.recordRreqFloodAttempt(uhid: sourceUhid)
                }
            }

            // Roll the window.
            state.windowStart = timestampMs
            state.windowCount = 1
        } else {
            state.windowCount += 1
        }

        volumeState[sourceUhid] = state
    }

    // MARK: - Internal: destination-scatter detection

    /// Sliding-window unique-destination fan-out check.
    ///
    /// Appends the current observation, prunes entries outside `scatterWindowMs`,
    /// then fires a flood penalty when the unique-destination count exceeds
    /// `scatterThreshold`.
    private func checkScatter(sourceUhid: String,
                               destinationUhid: String,
                               timestampMs: Int64) async {
        var entries = scatterEntries[sourceUhid] ?? []

        // Append new observation.
        entries.append((dest: destinationUhid, ts: timestampMs))

        // Prune entries that have aged out of the window.
        entries = entries.filter { timestampMs - $0.ts <= opts.scatterWindowMs }

        scatterEntries[sourceUhid] = entries

        // Count unique destinations in the pruned window.
        let uniqueCount = Set(entries.map(\.dest)).count
        if uniqueCount > opts.scatterThreshold {
            await reputation.recordRreqFloodAttempt(uhid: sourceUhid)
        }
    }
}
