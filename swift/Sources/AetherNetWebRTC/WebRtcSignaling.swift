// SPDX-License-Identifier: MIT

import Foundation

/// Carries WebRTC SDP/ICE signalling between two peers by UHID, so a direct data channel can be
/// negotiated without a central signalling server.
///
/// Any already-reachable channel can back this — the AetherNet relay, the radio mesh, or (for cold
/// first contact between distant peers) an SMS ignition link. The implementation frames signals so
/// the underlying channel only ever forwards opaque bytes.
public protocol WebRtcSignaling: AnyObject, Sendable {
    /// Delivers a signalling message to `peerUhid`.
    /// - Returns: `true` if the signal was handed to the underlying channel; `false` otherwise.
    @discardableResult
    func send(peerUhid: String, signal: WebRtcSignal) async -> Bool

    /// Registers the handler invoked for signals addressed to the local node. Replacing the handler
    /// is allowed; signals delivered before a handler is set are dropped (ICE re-gathers on retry).
    func onSignal(_ handler: @escaping @Sendable (WebRtcSignal) -> Void) async
}

/// In-process ``WebRtcSignaling`` bus that routes signals between endpoints by UHID.
///
/// The reference signalling implementation: it needs no network and no server, so it backs
/// same-process scenarios (multi-node simulations, a single device holding several identities) and
/// the test suite. Production cross-device signalling rides a real transport instead.
///
/// Each endpoint delivers inbound signals on its own ordered pump, so signals arrive in send order
/// and never re-enter the sender's call stack — matching the ordered, reliable delivery a real
/// signalling channel provides.
public actor InMemoryWebRtcSignalingBus {
    private var endpoints: [String: Endpoint] = [:]

    public init() {}

    /// Returns the signalling endpoint for `uhid`, creating it once.
    public func endpoint(_ uhid: String) -> WebRtcSignaling {
        if let existing = endpoints[uhid] { return existing }
        let endpoint = Endpoint(bus: self)
        endpoints[uhid] = endpoint
        return endpoint
    }

    /// Stops all endpoint pumps and clears the routing table.
    public func close() {
        for endpoint in endpoints.values {
            endpoint.close()
        }
        endpoints.removeAll()
    }

    fileprivate func route(_ signal: WebRtcSignal) -> Bool {
        guard let target = endpoints[signal.toUhid] else { return false }
        target.deliver(signal)
        return true
    }

    /// One signalling endpoint. Inbound signals are pushed onto an ``AsyncStream`` and drained, in
    /// order, by a single long-lived pump task — so the handler never runs on the sender's stack.
    fileprivate final class Endpoint: WebRtcSignaling, @unchecked Sendable {
        private let bus: InMemoryWebRtcSignalingBus
        private let lock = NSLock()
        private var handler: (@Sendable (WebRtcSignal) -> Void)?
        private let continuation: AsyncStream<WebRtcSignal>.Continuation
        private var pump: Task<Void, Never>?

        init(bus: InMemoryWebRtcSignalingBus) {
            self.bus = bus
            let (stream, continuation) = AsyncStream<WebRtcSignal>.makeStream(
                bufferingPolicy: .bufferingNewest(256))
            self.continuation = continuation
            // Start the ordered drain after stored properties are initialised. `Endpoint` is a
            // reference type, so capturing `self` here is safe; the handler is read under the lock.
            self.pump = Task { [self] in
                for await signal in stream {
                    lock.lock()
                    let h = handler
                    lock.unlock()
                    h?(signal)
                }
            }
        }

        func send(peerUhid: String, signal: WebRtcSignal) async -> Bool {
            await bus.route(signal)
        }

        func onSignal(_ handler: @escaping @Sendable (WebRtcSignal) -> Void) async {
            lock.lock(); defer { lock.unlock() }
            self.handler = handler
        }

        func deliver(_ signal: WebRtcSignal) {
            // Best-effort: a full buffer drops the oldest (ICE re-gathers on reconnect).
            continuation.yield(signal)
        }

        func close() {
            continuation.finish()
            pump?.cancel()
        }
    }
}
