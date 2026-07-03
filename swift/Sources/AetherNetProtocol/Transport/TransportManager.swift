// SPDX-License-Identifier: MIT

import Foundation

/// Multi-transport manager: routes an outbound send through the best available transport and
/// surfaces every inbound delivery through a single callback tagged with the carrying transport's
/// name. Swift counterpart of the C# `AetherNet.Transport.Services.TransportManager`, the Go
/// `transport.Manager`, and the Python `TransportManager`, reduced to the real selection path the
/// mesh needs:
///
/// - transports are held sorted **ascending** by ``TransportService/powerCostRelative``, so the
///   cheapest is tried first and an expensive last-resort transport (the circuit relay, cost 90)
///   is only reached after every cheaper one has declined; and
/// - ``sendAsync(peerUhid:data:cancellationToken:)`` falls through the ordered transports until one
///   returns `true` or all decline.
///
/// This is exactly the C# manager's "step 6: additional transports, sorted by `PowerCostRelative`
/// (ascending), fall through until one succeeds". Typed BLE / Wi-Fi Direct / NearLink slots are not
/// modelled here — on this SDK every transport (including those) is registered as an ordinary
/// transport and ordered purely by power cost, which is what makes the relay a genuine
/// auto-selected fallback rather than a hand-wired special case.
///
/// On the receive side the manager subscribes to each transport that adopts
/// ``DataReceivingTransport`` (every real mesh transport does) and re-raises its deliveries through
/// ``onDataReceived(_:)`` tagged `(sender, data, via)`, where `via` is the delivering transport's
/// ``TransportService/name`` — the Swift equivalent of the C# manager subscribing to each
/// `ITransportService.DataReceived` event and the Go manager's `dataReceiver` type-assertion.
public final class TransportManager: @unchecked Sendable {

    /// `(senderUhid, data, viaTransportName)` — the manager's received-data contract.
    public typealias DataReceivedHandler = (String, Data, String) -> Void

    private let transports: [any TransportService]
    private let lock = NSLock()
    private var onData: DataReceivedHandler?

    /// Builds a manager over `transports`, ordered ascending by ``TransportService/powerCostRelative``
    /// so the lowest-cost transport is preferred and the highest-cost (e.g. the circuit relay at 90)
    /// is the last-resort fallback. Subscribes to each transport's receive surface (those adopting
    /// ``DataReceivingTransport``); inbound data is re-raised through ``onDataReceived(_:)`` tagged
    /// with that transport's ``TransportService/name``.
    ///
    /// The ordering is stable for equal costs (registration order is preserved), matching the C#
    /// `OrderBy(t => t.PowerCostRelative)` and Go `sort.SliceStable` stable sorts.
    public init(_ transports: [any TransportService]) {
        // Stable ascending sort by power cost (Swift's `sorted(by:)` is not guaranteed stable, so
        // pair each transport with its original index and break ties on it to preserve order).
        self.transports = transports
            .enumerated()
            .sorted { lhs, rhs in
                if lhs.element.powerCostRelative != rhs.element.powerCostRelative {
                    return lhs.element.powerCostRelative < rhs.element.powerCostRelative
                }
                return lhs.offset < rhs.offset
            }
            .map { $0.element }

        for t in self.transports {
            guard let rx = t as? DataReceivingTransport else { continue }
            let via = t.name
            rx.onDataReceived { [weak self] sender, data in
                guard let self = self else { return }
                self.lock.lock(); let cb = self.onData; self.lock.unlock()
                cb?(sender, data, via)
            }
        }
    }

    /// Convenience initializer for the common single-transport case (e.g. the gap-2 acceptance
    /// test wires each node's relay as its only transport).
    public convenience init(_ transports: any TransportService...) {
        self.init(transports)
    }

    /// Registers the callback invoked when any transport delivers data to this node. Arguments are
    /// `(senderUhid, payload, name of the transport that carried it)` — the "via" tag proves which
    /// transport the manager selected on the receive side, mirroring the C#
    /// `TransportManager.DataReceived (sender, data, transportName)` event.
    public func onDataReceived(_ handler: @escaping DataReceivedHandler) {
        lock.lock(); onData = handler; lock.unlock()
    }

    /// Sends `data` to `peerUhid`, trying each available transport in ascending power-cost order
    /// until one succeeds. Returns `true` on the first transport that reports delivery; `false` if
    /// every transport is unavailable or declines. A transport that declines (e.g. the relay with
    /// "no route yet") is skipped and the manager moves to the next candidate — identical to the
    /// C# / Go / Python fall-through.
    @discardableResult
    public func sendAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken? = nil) async -> Bool {
        for t in transports {
            if !t.isAvailable { continue }
            if await t.sendAsync(peerUhid: peerUhid, data: data, cancellationToken: cancellationToken) {
                return true
            }
        }
        return false
    }

    /// Sends a whole buffer to `peerUhid` via the first stream-capable transport that succeeds,
    /// in ascending power-cost order.
    @discardableResult
    public func sendStreamAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken? = nil) async -> Bool {
        for t in transports {
            if !t.isAvailable { continue }
            if await t.sendStreamAsync(peerUhid: peerUhid, data: data, cancellationToken: cancellationToken) {
                return true
            }
        }
        return false
    }

    /// The manager's transports in the order they are tried (ascending power cost). The returned
    /// array is a copy; mutating it does not affect the manager.
    public var orderedTransports: [any TransportService] { transports }
}
