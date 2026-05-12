// SPDX-License-Identifier: MIT

import Foundation

/// Protocol defining the contract for transport implementations.
public protocol TransportService: AnyObject, Sendable {
    /// Human-readable identifier for this transport (e.g., "BLE", "Wi-Fi Direct").
    var name: String { get }

    /// Whether the transport is currently usable on this device.
    var isAvailable: Bool { get }

    /// Maximum throughput in bytes per second.
    var maxBandwidthBps: Int64 { get }

    /// Maximum communication range in meters.
    var maxRangeMeters: Int32 { get }

    /// Relative power consumption (1 = low, 10 = high).
    var powerCostRelative: Int32 { get }

    /// Maximum simultaneous peer connections.
    var maxConcurrentPeers: Int32 { get }

    /// Send a byte array to a specific peer.
    /// - Parameters:
    ///   - peerUhid: The recipient's UHID
    ///   - data: The bytes to send
    ///   - cancellationToken: Optional cancellation token
    /// - Returns: True on success, false on failure
    func sendAsync(
        peerUhid: String,
        data: Data,
        cancellationToken: CancellationToken?
    ) async -> Bool

    /// Send a stream to a peer (for large transfers, voice, video).
    /// - Parameters:
    ///   - peerUhid: The recipient's UHID
    ///   - data: The stream to send
    ///   - cancellationToken: Optional cancellation token
    /// - Returns: True on success, false on failure
    func sendStreamAsync(
        peerUhid: String,
        data: Data,
        cancellationToken: CancellationToken?
    ) async -> Bool

    /// Check if a connection is active to a peer.
    func isConnected(peerUhid: String) -> Bool

    /// Optional runtime metrics for adaptive transport selection.
    var metrics: PerTransportMetrics? { get }
}

// ── PerTransportMetrics ───────────────────────────────────────────────────────

/// Per-transport EWMA metrics used by PredictiveTransportSelector and rankTransports().
///
/// All EWMAs use α = 0.2.
///
/// RTT bootstraps from 200 ms (neutral prior).
/// Throughput (in bits/second) bootstraps from 0 and updates only on success with rttMs > 0.
/// Loss rate bootstraps from 5 % and updates on every sample.
///
/// Thread-safe via NSLock.
public final class PerTransportMetrics: @unchecked Sendable {
    private let lock            = NSLock()
    private var _ewmaRttMs:         Double = 200.0
    private var _ewmaLossRate:      Double = 0.05
    private var _ewmaThroughputBps: Double = 0.0
    private var _sampleCount:       Int    = 0

    public init() {}

    // MARK: - Readable state

    /// Number of samples recorded.
    public var sampleCount: Int    { lock.withLock { _sampleCount } }
    /// EWMA of round-trip time in milliseconds.
    public var ewmaRttMs: Double   { lock.withLock { _ewmaRttMs } }
    /// EWMA packet-loss rate (0.0–1.0).
    public var ewmaLossRate: Double { lock.withLock { _ewmaLossRate } }
    /// EWMA throughput in bits per second.
    public var ewmaThroughputBps: Double { lock.withLock { _ewmaThroughputBps } }

    // MARK: - Sampling

    /// Record one send outcome.
    ///
    /// - Parameters:
    ///   - rttMs:            Measured round-trip time in milliseconds (skipped when ≤ 0).
    ///   - success:          Whether the peer acknowledged receipt.
    ///   - bytesTransferred: Payload bytes successfully delivered.
    public func recordSample(rttMs: Double, success: Bool, bytesTransferred: Int) {
        lock.withLock {
            let alpha = 0.2

            // RTT — update only when rttMs is positive (skip on failure / no response)
            if rttMs > 0 {
                _ewmaRttMs = alpha * rttMs + (1.0 - alpha) * _ewmaRttMs
            }

            // Loss rate — always updated
            _ewmaLossRate = success
                ? (1.0 - alpha) * _ewmaLossRate
                : alpha + (1.0 - alpha) * _ewmaLossRate

            // Throughput (bits/second) — update only on success with a valid RTT
            if success && rttMs > 0 && bytesTransferred > 0 {
                let bps = Double(bytesTransferred) * 8.0 * 1000.0 / rttMs
                if _ewmaThroughputBps == 0.0 {
                    _ewmaThroughputBps = bps          // bootstrap: set directly
                } else {
                    _ewmaThroughputBps = alpha * bps + (1.0 - alpha) * _ewmaThroughputBps
                }
            }

            _sampleCount += 1
        }
    }

    // MARK: - Scoring

    /// Static/composite score suitable for sorting: higher is better.
    ///
    /// Formula: `(effectiveBps / powerCost) × (1 − lossRate) / rttMs`
    ///
    /// Where `effectiveBps = max(ewmaThroughputBps, maxBandwidthBps × 0.1)` and
    /// `powerCost = max(powerCostRelative, 1)`.
    public func compositeScore(maxBandwidthBps: Int64, powerCostRelative: Int32) -> Double {
        lock.withLock {
            let effectiveBps = max(_ewmaThroughputBps, Double(maxBandwidthBps) * 0.1)
            let power        = Double(max(powerCostRelative, 1))
            let rtt          = max(_ewmaRttMs, 1.0)
            return (effectiveBps / power) * (1.0 - _ewmaLossRate) / rtt
        }
    }
}

// ── RankedTransport ───────────────────────────────────────────────────────────

/// A transport paired with its static or composite score.
public struct RankedTransport {
    /// The ranked transport backend.
    public let transport: any TransportService
    /// Composite or static score (higher = better).
    public let score: Double
}

// ── rankTransports ────────────────────────────────────────────────────────────

/// Sort available transports by score (descending).
///
/// - If the transport provides `PerTransportMetrics`, the composite score formula is used.
/// - Otherwise the static score `maxBandwidthBps / max(powerCostRelative, 1)` is used.
///
/// Unavailable transports are excluded.
public func rankTransports(_ transports: [any TransportService]) -> [RankedTransport] {
    transports
        .filter { $0.isAvailable }
        .map { t -> RankedTransport in
            let score: Double
            if let m = t.metrics {
                score = m.compositeScore(
                    maxBandwidthBps: t.maxBandwidthBps,
                    powerCostRelative: t.powerCostRelative)
            } else {
                let power = Double(max(t.powerCostRelative, 1))
                score = Double(t.maxBandwidthBps) / power
            }
            return RankedTransport(transport: t, score: score)
        }
        .sorted { $0.score > $1.score }
}

// ── Forward Error Correction ──────────────────────────────────────────────────

/// Forward Error Correction codec protocol.
public protocol FecCodec {
    /// Human-readable codec identifier.
    var codecName:            String { get }
    /// Minimum device tier for encoding (0 = any device).
    var deviceTierRequired:   UInt8  { get }
    /// Fraction of overhead packets added (e.g., 0.05 = 5%).
    var overheadFraction:     Double { get }
    /// Fixed symbol size in bytes (0 = variable).
    var fixedSymbolSizeBytes: Int    { get }

    /// Encode `source` into FEC-protected packets.
    /// - Parameters:
    ///   - source: Raw bytes to encode.
    ///   - targetSymbolCount: Number of output packets to produce (>= K for repair redundancy).
    /// - Returns: Concatenated encoded packets as a single `Data` blob.
    func encode(source: Data, targetSymbolCount: Int) throws -> Data

    /// Try to reconstruct original data from received symbols.
    func tryDecode(receivedSymbols: [Data], sourceSymbolCount: Int) -> Data?
}

// ── CancellationToken ─────────────────────────────────────────────────────────

/// Cancellation token for async operations.
public class CancellationToken {
    private var isCancelled: Bool = false
    private let lock = NSLock()

    public func cancel() {
        lock.withLock {
            isCancelled = true
        }
    }

    public var cancelled: Bool {
        lock.withLock { isCancelled }
    }
}

// ── InProcessTransport ────────────────────────────────────────────────────────

/// In-memory transport for testing and local communication.
public actor InProcessTransport {
    nonisolated public let name = "InProcess"
    nonisolated public let isAvailable = true
    nonisolated public let maxBandwidthBps: Int64 = 1_000_000_000  // 1 GB/s
    nonisolated public let maxRangeMeters: Int32 = 1
    nonisolated public let powerCostRelative: Int32 = 1
    nonisolated public let maxConcurrentPeers: Int32 = 1000
    nonisolated public var metrics: PerTransportMetrics? { nil }

    /// Route messages to other InProcessTransport instances by UHID
    private nonisolated static let sharedRouter = NSLock()
    private nonisolated(unsafe) static var registry: [String: InProcessTransport] = [:]

    private let uhid: String
    private var onDataReceived: ((String, Data) -> Void)? = nil

    public init(uhid: String) {
        self.uhid = uhid
        Self.sharedRouter.withLock {
            Self.registry[uhid] = self
        }
    }

    deinit {
        _ = Self.sharedRouter.withLock {
            Self.registry.removeValue(forKey: uhid)
        }
    }

    /// Register a callback for incoming data.
    public func onDataReceived(_ callback: @escaping (String, Data) -> Void) {
        self.onDataReceived = callback
    }

    public func sendAsync(
        peerUhid: String,
        data: Data,
        cancellationToken: CancellationToken?
    ) async -> Bool {
        // Simulate network delay
        try? await Task.sleep(nanoseconds: 1_000_000)  // 1ms

        guard let peer = Self.registry[peerUhid] else {
            return false
        }

        // Deliver the message
        await peer._deliverData(from: uhid, data: data)
        return true
    }

    public func sendStreamAsync(
        peerUhid: String,
        data: Data,
        cancellationToken: CancellationToken?
    ) async -> Bool {
        await sendAsync(peerUhid: peerUhid, data: data, cancellationToken: cancellationToken)
    }

    nonisolated public func isConnected(peerUhid: String) -> Bool {
        Self.sharedRouter.withLock {
            Self.registry[peerUhid] != nil
        }
    }

    private func _deliverData(from senderUhid: String, data: Data) {
        onDataReceived?(senderUhid, data)
    }
}
