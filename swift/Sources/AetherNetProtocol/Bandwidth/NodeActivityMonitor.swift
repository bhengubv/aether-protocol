// SPDX-License-Identifier: MIT

import Foundation

/// Observable node activity monitor — the UI-facing layer of the AetherNet
/// Bandwidth Measurement Framework.
///
/// Produces `NodeActivitySnapshot` objects at a configurable cadence (default 500 ms).
/// Each snapshot aggregates per-transport ingress/egress rates, active peer counts,
/// and a unified `NodeActivityState` for status indicators.
///
/// Consumption patterns:
/// - **Status bar / widget (polling):** read `current` on a 1-second timer. No subscription overhead.
/// - **Reactive UI / SwiftUI:** subscribe via `subscribe(_:)`.
/// - **BigBruh dashboard:** subscribe and push snapshots to the server.
/// - **ABR controller:** subscribe to watch for `.degraded` and step down the bitrate ladder.
///
/// Thread safety: implemented as a Swift `actor`. All public methods are actor-isolated.
public final actor NodeActivityMonitor {

    // MARK: - Configuration

    /// How often the monitor re-samples (milliseconds). Default: 500.
    /// Set via ``setSampleIntervalMs(_:)`` — actor-isolated state cannot be
    /// assigned across the actor boundary (Swift 6).
    public private(set) var sampleIntervalMs: Int = 500

    /// How long without observed traffic before a transport is considered idle (seconds).
    /// Default: 5. Set via ``setIdleThresholdSeconds(_:)``.
    public private(set) var idleThresholdSeconds: Int = 5

    /// Set the sample interval (clamped to [100, 60000] ms).
    public func setSampleIntervalMs(_ value: Int) {
        sampleIntervalMs = max(100, min(value, 60_000))
    }

    /// Set the idle threshold (clamped to [1, 300] s).
    public func setIdleThresholdSeconds(_ value: Int) {
        idleThresholdSeconds = max(1, min(value, 300))
    }

    // MARK: - Registered transports

    private var transports: [String: TransportEntry] = [:]

    // MARK: - Active-peer tracking

    /// Maps peerUhid → last-seen Unix ms. A peer is "active" if it had ingress or
    /// egress within `idleThresholdSeconds`. Populated only by the peer-aware
    /// `recordIngress`/`recordEgress` overloads; the transport-only overloads do not
    /// contribute (the caller did not supply a peer). Stale entries are pruned each
    /// tick so the dictionary stays bounded by recently-active peers, not the
    /// lifetime peer set.
    private var lastSeenPeerMs: [String: Int64] = [:]

    // MARK: - Snapshot

    private var _current: NodeActivitySnapshot = NodeActivityMonitor.offlineSnapshot()

    /// The most recent snapshot. Initialises to an `.offline` snapshot with zero rates.
    public var current: NodeActivitySnapshot { _current }

    // MARK: - Subscribers

    private var subscribers: [UUID: @Sendable (NodeActivitySnapshot) -> Void] = [:]

    // MARK: - Background Task

    private var samplingTask: Task<Void, Never>?
    private var lastTickSec: Double = 0.0

    // MARK: - Init

    public init() {}

    // MARK: - Registration

    /// Register a named transport and its estimator so its activity is included in snapshots.
    public func register(name: String, estimator: BandwidthEstimator) {
        transports[name] = TransportEntry(estimator: estimator)
    }

    // MARK: - Traffic recording

    /// Record inbound bytes on a transport. Call from the transport receive path.
    public func recordIngress(transport: String, bytes: Int) {
        transports[transport]?.ingressBytes += bytes
    }

    /// Record outbound bytes on a transport. Call from the transport send path.
    public func recordEgress(transport: String, bytes: Int) {
        guard var entry = transports[transport] else { return }
        entry.egressBytes += bytes
        entry.lastEgressSec = Date().timeIntervalSince1970
        transports[transport] = entry
    }

    /// Record inbound bytes on a transport from a specific peer.
    /// Tracks the peer for the `NodeActivitySnapshot.activePeers` count.
    public func recordIngress(transport: String, peerUhid: String, bytes: Int) {
        recordIngress(transport: transport, bytes: bytes)
        guard !peerUhid.isEmpty else { return }
        lastSeenPeerMs[peerUhid] = NodeActivityMonitor.nowMs()
    }

    /// Record outbound bytes on a transport to a specific peer.
    /// Tracks the peer for the `NodeActivitySnapshot.activePeers` count.
    public func recordEgress(transport: String, peerUhid: String, bytes: Int) {
        recordEgress(transport: transport, bytes: bytes)
        guard !peerUhid.isEmpty else { return }
        lastSeenPeerMs[peerUhid] = NodeActivityMonitor.nowMs()
    }

    // MARK: - Lifecycle

    /// Start the background sampling loop.
    public func start() {
        guard samplingTask == nil else { return }
        lastTickSec = Date().timeIntervalSince1970
        samplingTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.tick()
                let intervalNs = UInt64(await self.sampleIntervalMs) * 1_000_000
                try? await Task.sleep(nanoseconds: intervalNs)
            }
        }
    }

    /// Stop the background sampling loop.
    public func stop() {
        samplingTask?.cancel()
        samplingTask = nil
    }

    // MARK: - Subscription

    /// Subscribe to snapshot updates. Returns an unsubscribe closure.
    /// The callback is called on an unspecified Task.
    @discardableResult
    public func subscribe(
        _ callback: @escaping @Sendable (NodeActivitySnapshot) -> Void
    ) -> () -> Void {
        let id = UUID()
        subscribers[id] = callback
        return { [weak self] in
            Task { await self?.unsubscribe(id: id) }
        }
    }

    private func unsubscribe(id: UUID) {
        subscribers.removeValue(forKey: id)
    }

    // MARK: - Tick

    private func tick() async {
        let nowSec = Date().timeIntervalSince1970
        let elapsedSec = max(0.001, nowSec - lastTickSec)
        lastTickSec = nowSec

        let idleThresholdSec = Double(idleThresholdSeconds)
        var transportSnapshots: [TransportActivitySnapshot] = []
        var totalIngress: Int64 = 0
        var totalEgress: Int64 = 0
        var activeTransports = 0

        // Count distinct peers active within the idle window; prune stale entries
        // so the dictionary stays bounded by recently-active peers.
        let nowMs = NodeActivityMonitor.nowMs()
        let idleThresholdMs = Int64(idleThresholdSeconds) * 1000
        var activePeers = 0
        for (peer, lastSeen) in lastSeenPeerMs {
            if nowMs - lastSeen < idleThresholdMs {
                activePeers += 1
            } else {
                lastSeenPeerMs.removeValue(forKey: peer)
            }
        }

        for (name, entry) in transports {
            // Consume and reset byte counters.
            let ingressDelta = entry.ingressBytes
            let egressDelta  = entry.egressBytes
            transports[name]?.ingressBytes = 0
            transports[name]?.egressBytes  = 0

            let ingressBps = Int64(Double(ingressDelta) * 8.0 / elapsedSec)
            let egressBps  = Int64(Double(egressDelta)  * 8.0 / elapsedSec)

            let sample = await entry.estimator.currentSample
            let utilFraction = sample.btlBwBps > 0
                ? max(0.0, min(Double(egressBps) / Double(sample.btlBwBps), 1.0))
                : 0.0

            let isRecent = (nowSec - entry.lastEgressSec) < idleThresholdSec
            let state = NodeActivityMonitor.computeTransportState(
                egressBps: egressBps,
                ingressBps: ingressBps,
                sample: sample,
                isRecent: isRecent
            )

            if state != .offline && state != .idle {
                activeTransports += 1
            }

            totalIngress += ingressBps
            totalEgress  += egressBps

            transportSnapshots.append(TransportActivitySnapshot(
                transportName:       name,
                isAvailable:         true,
                ingressBps:          ingressBps,
                egressBps:           egressBps,
                srtt:                sample.srtt,
                btlBwBps:            sample.btlBwBps,
                utilizationFraction: utilFraction,
                state:               state,
                confidence:          sample.confidence
            ))
        }

        let nodeState = NodeActivityMonitor.computeNodeState(transports: transportSnapshots)
        let primary = transportSnapshots.max(by: { $0.egressBps < $1.egressBps })?.transportName

        let snapshot = NodeActivitySnapshot(
            state:                nodeState,
            ingressBps:           totalIngress,
            egressBps:            totalEgress,
            activePeers:          activePeers,
            activeTransports:     activeTransports,
            transports:           transportSnapshots,
            primaryTransportName: primary,
            timestamp:            Date()
        )

        let prev = _current
        _current = snapshot

        // Notify subscribers.
        let callbacks = Array(subscribers.values)
        Task.detached { @Sendable in
            for cb in callbacks { cb(snapshot) }
        }

        // Only fire change notifications when something meaningful changed.
        let changed = snapshot.state != prev.state
            || abs(snapshot.totalBps - prev.totalBps) > 1_000
            || snapshot.activeTransports != prev.activeTransports
        _ = changed // reserved for future SnapshotChanged event or publisher
    }

    // MARK: - State computation

    private static func computeTransportState(
        egressBps: Int64,
        ingressBps: Int64,
        sample: BandwidthSample,
        isRecent: Bool
    ) -> NodeActivityState {
        if egressBps == 0 && ingressBps == 0 { return .idle }
        if sample.lossRate > 0.05 { return .degraded }
        let util = sample.btlBwBps > 0
            ? Double(egressBps) / Double(sample.btlBwBps)
            : 0.0
        return util >= 0.5 ? .busy : .active
    }

    private static func computeNodeState(transports: [TransportActivitySnapshot]) -> NodeActivityState {
        if transports.isEmpty { return .offline }
        if transports.contains(where: { $0.state == .degraded }) { return .degraded }
        if transports.contains(where: { $0.state == .busy })     { return .busy }
        if transports.contains(where: { $0.state == .active })   { return .active }
        if transports.allSatisfy({ $0.state == .offline })       { return .offline }
        return .idle
    }

    // MARK: - Static helpers

    /// Current Unix time in milliseconds, matching C#'s `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`.
    private static func nowMs() -> Int64 {
        Int64(Date().timeIntervalSince1970 * 1000.0)
    }

    private static func offlineSnapshot() -> NodeActivitySnapshot {
        NodeActivitySnapshot(
            state:                .offline,
            ingressBps:           0,
            egressBps:            0,
            activePeers:          0,
            activeTransports:     0,
            transports:           [],
            primaryTransportName: nil,
            timestamp:            Date()
        )
    }
}

// MARK: - Internal entry type

private struct TransportEntry {
    let estimator: BandwidthEstimator
    var ingressBytes: Int = 0
    var egressBytes: Int  = 0
    var lastEgressSec: Double = Date().timeIntervalSince1970
}
