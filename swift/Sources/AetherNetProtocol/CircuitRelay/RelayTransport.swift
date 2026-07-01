// SPDX-License-Identifier: MIT

import Foundation

/// The one-hop link a RelayTransport uses to exchange raw relay frames with
/// directly-reachable nodes (mirrors the C# IRelayLink / Go RelayLink).
public protocol RelayLink: AnyObject {
    func sendFrame(_ node: String, _ frame: Data) -> Bool
    func canReach(_ node: String) -> Bool
    func onFrame(_ handler: @escaping (String, Data) -> Void)
}

/// Tuning / policy for a RelayTransport (mirrors C# CircuitRelayOptions).
public struct RelayTransportOptions {
    public var reservationTTL: TimeInterval = 30 * 60
    public var maxReservations = 128
    public var maxBridges = 128
    public var bridgeDataLimitBytes: Int64 = 0
    public var bridgeDurationLimitSeconds: Int32 = 0
    public var connectTimeout: TimeInterval = 10
    public var reserveTimeout: TimeInterval = 10
    public var actAsRelay = true
    public init() {}
}

/// Native circuit-relay-v2 engine: any node can be target (reserve), client (send
/// over a known relay route), and relay (grant reservations, bridge CONNECT→STOP,
/// forward DATA under a budget). Faithful port of the C# CircuitRelayTransportService
/// / Go Transport. State is guarded by a lock; the CONNECT/RESERVE response waits use
/// a DispatchSemaphore signalled when the matching response frame is handled.
public final class RelayTransport {
    private let localUhid: String
    private let link: RelayLink
    private let opts: RelayTransportOptions
    private let now: () -> Date

    private let lock = NSLock()
    private var reservations: [String: Date] = [:]
    private var bridges: [UUID: Bridge] = [:]
    private var routes: [String: String] = [:]
    private var peerBridges: [String: ActiveBridge] = [:]
    private var pendingConnects: [UUID: Pending] = [:]
    private var pendingReservations: [String: Pending] = [:]

    /// Invoked when tunnelled data is delivered to this node as an endpoint (sender, payload).
    public var onData: ((String, Data) -> Void)?

    private final class Pending {
        let sem = DispatchSemaphore(value: 0)
        var status: RelayStatus = .connectionFailed
    }
    private final class Bridge {
        let a: String, b: String
        let dataBudget: Int64
        let deadline: Date?   // nil => no duration limit
        var dataUsed: Int64 = 0
        var open = false
        init(a: String, b: String, dataBudget: Int64, deadline: Date?) {
            self.a = a; self.b = b; self.dataBudget = dataBudget; self.deadline = deadline
        }
    }
    private struct ActiveBridge { let connID: UUID; let relay: String }

    public init(localUhid: String, link: RelayLink,
                options: RelayTransportOptions = .init(),
                now: @escaping () -> Date = { Date() }) {
        self.localUhid = localUhid
        self.link = link
        self.opts = options
        self.now = now
        link.onFrame { [weak self] from, frame in self?.onFrame(from, frame) }
    }

    // MARK: - public API

    public func setOnData(_ cb: @escaping (String, Data) -> Void) { onData = cb }

    public func setRoute(_ dest: String, relay: String) {
        lock.lock(); routes[dest] = relay; lock.unlock()
    }

    public var activeBridgeCount: Int { lock.lock(); defer { lock.unlock() }; return bridges.count }
    public var activeReservationCount: Int { lock.lock(); defer { lock.unlock() }; return reservations.count }
    public func isConnected(_ peer: String) -> Bool { lock.lock(); defer { lock.unlock() }; return peerBridges[peer] != nil }

    /// Reserve capacity on relay so peers can reach this node through it.
    public func reserve(_ relay: String) -> Bool {
        guard link.canReach(relay) else { return false }
        let p = Pending()
        lock.lock(); pendingReservations[relay] = p; lock.unlock()
        defer { lock.lock(); pendingReservations[relay] = nil; lock.unlock() }

        let f = RelayFrame(type: .reserve, sourceUhid: localUhid, relayUhid: relay)
        _ = link.sendFrame(relay, RelayFrameSerializer.serialize(f))
        return awaitStatus(p, opts.reserveTimeout) == .ok
    }

    /// Send data to peer, establishing a relay bridge first if needed.
    @discardableResult
    public func send(_ peer: String, _ data: Data) -> Bool {
        lock.lock(); let existing = peerBridges[peer]; lock.unlock()
        if let ab = existing { return sendData(ab, peer, data) }

        lock.lock(); let relay = routes[peer]; lock.unlock()
        guard let relay = relay, link.canReach(relay) else { return false }
        guard connect(peer, relay) == .ok else { return false }
        lock.lock(); let ab = peerBridges[peer]; lock.unlock()
        guard let ab = ab else { return false }
        return sendData(ab, peer, data)
    }

    // MARK: - client handshake

    private func connect(_ dest: String, _ relay: String) -> RelayStatus {
        let connID = UUID()
        let p = Pending()
        lock.lock(); pendingConnects[connID] = p; lock.unlock()
        defer { lock.lock(); pendingConnects[connID] = nil; lock.unlock() }

        let f = RelayFrame(type: .connect, sourceUhid: localUhid, destinationUhid: dest,
                           relayUhid: relay, connectionId: connID)
        guard link.sendFrame(relay, RelayFrameSerializer.serialize(f)) else { return .connectionFailed }
        return awaitStatus(p, opts.connectTimeout)
    }

    private func awaitStatus(_ p: Pending, _ timeout: TimeInterval) -> RelayStatus {
        if p.sem.wait(timeout: .now() + timeout) == .timedOut { return .connectionFailed }
        return p.status
    }

    private func sendData(_ ab: ActiveBridge, _ peer: String, _ data: Data) -> Bool {
        let f = RelayFrame(type: .data, sourceUhid: localUhid, destinationUhid: peer,
                           relayUhid: ab.relay, connectionId: ab.connID, payload: data)
        return link.sendFrame(ab.relay, RelayFrameSerializer.serialize(f))
    }

    // MARK: - inbound dispatch

    private func onFrame(_ from: String, _ frame: Data) {
        guard let f = RelayFrameSerializer.deserialize(frame) else { return }
        switch f.type {
        case .reserve: handleReserve(from, f)
        case .reserveResponse: handleReserveResponse(from, f)
        case .connect: handleConnect(from, f)
        case .stop: handleStop(from, f)
        case .stopResponse: handleStopResponse(from, f)
        case .connectResponse: handleConnectResponse(from, f)
        case .data: handleData(from, f)
        }
    }

    private func handleReserve(_ from: String, _ f: RelayFrame) {
        lock.lock()
        if !opts.actAsRelay || reservations.count >= opts.maxReservations {
            lock.unlock()
            reply(from, RelayFrame(type: .reserveResponse, status: .reservationRefused,
                                   sourceUhid: f.sourceUhid, relayUhid: localUhid))
            return
        }
        let expiry = now().addingTimeInterval(opts.reservationTTL)
        reservations[f.sourceUhid] = expiry
        lock.unlock()
        reply(from, RelayFrame(type: .reserveResponse, status: .ok, sourceUhid: f.sourceUhid,
                               relayUhid: localUhid,
                               reservationExpiresAtMs: Int64(expiry.timeIntervalSince1970 * 1000)))
    }

    private func handleReserveResponse(_ from: String, _ f: RelayFrame) {
        lock.lock(); let p = pendingReservations[from]; lock.unlock()
        if let p = p { p.status = f.status; p.sem.signal() }
    }

    private func handleConnect(_ from: String, _ f: RelayFrame) {
        let a = f.sourceUhid, b = f.destinationUhid
        guard opts.actAsRelay else { replyConnect(a, f, .connectionFailed); return }
        lock.lock()
        let exp = reservations[b]
        if exp == nil || !(now() < exp!) {
            reservations[b] = nil
            lock.unlock()
            replyConnect(a, f, .noReservation); return
        }
        guard link.canReach(b) else { lock.unlock(); replyConnect(a, f, .connectionFailed); return }
        guard bridges.count < opts.maxBridges else { lock.unlock(); replyConnect(a, f, .resourceLimitExceeded); return }
        let deadline: Date? = opts.bridgeDurationLimitSeconds > 0
            ? now().addingTimeInterval(TimeInterval(opts.bridgeDurationLimitSeconds)) : nil
        bridges[f.connectionId] = Bridge(a: a, b: b, dataBudget: opts.bridgeDataLimitBytes, deadline: deadline)
        lock.unlock()
        reply(b, RelayFrame(type: .stop, sourceUhid: a, destinationUhid: b, relayUhid: localUhid,
                            connectionId: f.connectionId,
                            limitDurationSeconds: opts.bridgeDurationLimitSeconds,
                            limitDataBytes: opts.bridgeDataLimitBytes))
    }

    private func handleStop(_ from: String, _ f: RelayFrame) {
        lock.lock(); peerBridges[f.sourceUhid] = ActiveBridge(connID: f.connectionId, relay: from); lock.unlock()
        reply(from, RelayFrame(type: .stopResponse, status: .ok, sourceUhid: f.sourceUhid,
                               destinationUhid: localUhid, relayUhid: from, connectionId: f.connectionId))
    }

    private func handleStopResponse(_ from: String, _ f: RelayFrame) {
        lock.lock()
        guard let br = bridges[f.connectionId] else { lock.unlock(); return }
        if f.status != .ok {
            bridges[f.connectionId] = nil
            let a = br.a
            lock.unlock()
            replyConnect(a, f, .connectionFailed); return
        }
        br.open = true
        let a = br.a, b = br.b, budget = br.dataBudget
        lock.unlock()
        reply(a, RelayFrame(type: .connectResponse, status: .ok, sourceUhid: a, destinationUhid: b,
                            relayUhid: localUhid, connectionId: f.connectionId, limitDataBytes: budget))
    }

    private func handleConnectResponse(_ from: String, _ f: RelayFrame) {
        if f.status == .ok {
            lock.lock(); peerBridges[f.destinationUhid] = ActiveBridge(connID: f.connectionId, relay: from); lock.unlock()
        }
        lock.lock(); let p = pendingConnects[f.connectionId]; lock.unlock()
        if let p = p { p.status = f.status; p.sem.signal() }
    }

    private func handleData(_ from: String, _ f: RelayFrame) {
        if f.destinationUhid == localUhid { onData?(f.sourceUhid, f.payload); return }
        lock.lock()
        guard let br = bridges[f.connectionId], br.open, from == br.a || from == br.b else { lock.unlock(); return }
        if let dl = br.deadline, !(now() < dl) { bridges[f.connectionId] = nil; lock.unlock(); return }
        br.dataUsed += Int64(f.payload.count)
        if br.dataBudget > 0 && br.dataUsed > br.dataBudget { bridges[f.connectionId] = nil; lock.unlock(); return }
        lock.unlock()
        _ = link.sendFrame(f.destinationUhid, RelayFrameSerializer.serialize(f)) // forward unchanged to dst
    }

    // MARK: - helpers

    private func reply(_ to: String, _ f: RelayFrame) { _ = link.sendFrame(to, RelayFrameSerializer.serialize(f)) }

    private func replyConnect(_ client: String, _ connect: RelayFrame, _ status: RelayStatus) {
        reply(client, RelayFrame(type: .connectResponse, status: status, sourceUhid: connect.sourceUhid,
                                 destinationUhid: connect.destinationUhid, relayUhid: localUhid,
                                 connectionId: connect.connectionId))
    }
}
