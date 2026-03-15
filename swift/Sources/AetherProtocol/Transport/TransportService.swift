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
}

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

/// In-memory transport for testing and local communication.
public actor InProcessTransport {
    nonisolated public let name = "InProcess"
    nonisolated public let isAvailable = true
    nonisolated public let maxBandwidthBps: Int64 = 1_000_000_000  // 1 GB/s
    nonisolated public let maxRangeMeters: Int32 = 1
    nonisolated public let powerCostRelative: Int32 = 1
    nonisolated public let maxConcurrentPeers: Int32 = 1000

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
