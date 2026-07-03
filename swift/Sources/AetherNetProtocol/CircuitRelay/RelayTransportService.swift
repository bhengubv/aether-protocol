// SPDX-License-Identifier: MIT

import Foundation

/// Receive surface a mesh transport exposes *beyond* the ``TransportService`` protocol — a
/// single-handler registration for inbound bytes (sender UHID, payload). Concrete transports
/// (``LoRaSerialTransport``, `WebRtcTransportService`, ``InProcessTransport``) already publish
/// exactly this `onDataReceived(_:)` method; declaring it as a protocol lets ``TransportManager``
/// subscribe to any of them uniformly and re-raise deliveries tagged with the carrying transport's
/// name. This is the Swift counterpart of the Go `dataReceiver` interface the manager type-asserts,
/// and of the C# `ITransportService.DataReceived` event the C# `TransportManager` subscribes to.
public protocol DataReceivingTransport: AnyObject {
    /// Registers the handler invoked when this transport delivers data to this node
    /// (sender UHID, payload). Replaces any previously registered handler.
    func onDataReceived(_ handler: @escaping (String, Data) -> Void)
}

/// Native circuit-relay-v2 **transport**. Adapts the transport-agnostic circuit-relay engine
/// (``RelayTransport``) to the mesh's ``TransportService`` contract, so a transport manager can
/// select it exactly like BLE / Wi-Fi Direct / WebRTC / the HTTP relay — a REAL transport, not an
/// app-level libp2p sidecar. Faithful counterpart of the C# `CircuitRelayTransportService :
/// ITransportService`, the Go `circuitrelay.TransportService`, and the Python
/// `CircuitRelayTransportService`.
///
/// Any AetherNet node can be a relay: a node that cannot reach a peer directly routes through a
/// third node that can reach both. All three roles live in the wrapped engine (a node can be
/// any/all at once):
/// - **Target** — ``reserve(_:)`` reserves capacity on a relay so peers behind NAT can reach it.
/// - **Client** — ``sendAsync(peerUhid:data:cancellationToken:)`` to a peer for which a relay route
///   is known (``setRoute(_:relay:)``) performs the CONNECT handshake then tunnels DATA.
/// - **Relay** — the engine grants reservations, bridges CONNECT→STOP, and forwards DATA under a
///   data/duration budget.
///
/// The engine stays the single source of truth for all relay behaviour and the ONLY code that
/// touches the (fixture-locked) wire format; this adapter merely presents it through the standard
/// interface and forwards delivered data to the registered handler. Relay-/target-role operations
/// that are not part of the generic transport contract — `reserve`, `setRoute`, and the
/// `activeBridgeCount` / `activeReservationCount` diagnostics — are exposed directly on this type
/// (mirroring the Go adapter's `Engine()` accessor), and via ``engine`` for full access.
///
/// The blocking engine calls (`reserve`, `send`) wait on a `DispatchSemaphore` that is only
/// signalled when the matching response frame is handled on the receive path (another thread).
/// To keep from parking a cooperative-executor thread, ``sendAsync`` / ``reserveAsync`` run the
/// blocking call on a background `DispatchQueue` and bridge the result back with a checked
/// continuation — the same blocking→async pattern used by `DirectoryService` / `RoutingService`.
public final class RelayCircuitTransport: TransportService, DataReceivingTransport, @unchecked Sendable {

    /// Human-readable name the relay transport reports. Byte-for-byte identical to the C#
    /// `CircuitRelayTransportService.Name`, Go `TransportName`, and Python
    /// `CircuitRelayTransportService.name`, so the manager's "via" tag matches across languages.
    public static let transportName = "Circuit Relay (v2)"

    /// Relative power cost of relayed traffic. An extra hop through a third node is costly, so it
    /// sits just below the HTTP relay's last-resort cost of 100 — high enough that a manager only
    /// falls through to it once every cheaper direct transport has declined. Mirrors the C# / Go /
    /// Python `PowerCostRelative` == 90.
    public static let powerCostRelay: Int32 = 90

    private let _engine: RelayTransport
    private let _metrics = PerTransportMetrics()
    private let queue = DispatchQueue(label: "aethernet.circuitrelay.transport", attributes: .concurrent)

    private let lock = NSLock()
    private var onData: ((String, Data) -> Void)?
    private var disposed = false

    /// Wraps an existing relay engine as a ``TransportService``. Takes over the engine's data
    /// callback to surface tunnelled DATA through ``onDataReceived(_:)`` — callers should not also
    /// call `engine.setOnData` afterwards.
    public init(engine: RelayTransport) {
        self._engine = engine
        engine.setOnData { [weak self] from, data in
            guard let self = self else { return }
            self.lock.lock(); let cb = self.onData; self.lock.unlock()
            cb?(from, data)
        }
    }

    /// The underlying relay engine, for relay-/target-role operations outside the generic transport
    /// contract. Prefer the convenience methods on this type (``reserve(_:)`` / ``reserveAsync(_:)``,
    /// ``setRoute(_:relay:)``, ``activeBridgeCount``) for the common cases.
    public var engine: RelayTransport { _engine }

    // MARK: - DataReceivingTransport (receive surface)

    /// Registers the handler invoked when tunnelled DATA is delivered to this node as the final
    /// destination (sender UHID, payload). This is the receive surface ``TransportManager``
    /// subscribes to; it mirrors `LoRaSerialTransport.onDataReceived` / `WebRtcTransportService`
    /// and the C# `ITransportService.DataReceived` event.
    public func onDataReceived(_ handler: @escaping (String, Data) -> Void) {
        lock.lock(); onData = handler; lock.unlock()
    }

    // MARK: - Relay / target role (not part of TransportService)

    /// Reserves capacity on `relay` so peers can reach this node through it (target role).
    /// Synchronous; blocks until the relay confirms or the reserve timeout elapses.
    @discardableResult
    public func reserve(_ relay: String) -> Bool { _engine.reserve(relay) }

    /// Async variant of ``reserve(_:)`` — runs the blocking reserve off the cooperative executor.
    @discardableResult
    public func reserveAsync(_ relay: String) async -> Bool {
        await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            queue.async { cont.resume(returning: self._engine.reserve(relay)) }
        }
    }

    /// Records that `dest` is reachable via `relay` (in production from the directory / reservation
    /// gossip; tests set it directly).
    public func setRoute(_ dest: String, relay: String) { _engine.setRoute(dest, relay: relay) }

    /// Number of bridges this node is currently servicing as a relay (diagnostics/tests).
    public var activeBridgeCount: Int { _engine.activeBridgeCount }

    /// Number of reservations this node is currently holding as a relay (diagnostics/tests).
    public var activeReservationCount: Int { _engine.activeReservationCount }

    // MARK: - TransportService metadata

    public var name: String { Self.transportName }

    /// The relay is available until disposed; whether a specific peer is reachable is decided
    /// per-send (a false ``sendAsync`` lets a manager move on), exactly as the C# transport reports
    /// `IsAvailable = !disposed`.
    public var isAvailable: Bool { lock.lock(); defer { lock.unlock() }; return !disposed }

    /// Conservative relayed-path bandwidth — below a direct link, since every byte crosses an extra
    /// hop. Matches the C# / Go / Python reference (5 MB/s).
    public var maxBandwidthBps: Int64 { 5_000_000 }

    /// 0 — the relay is internet-scope, not range-bound.
    public var maxRangeMeters: Int32 { 0 }

    /// 90 — just below the HTTP relay's last-resort cost of 100, so a manager auto-selects the
    /// relay only after every cheaper transport is exhausted.
    public var powerCostRelative: Int32 { Self.powerCostRelay }

    /// Concurrent-peer ceiling. Matches the C# / Go / Python 256.
    public var maxConcurrentPeers: Int32 { 256 }

    public var metrics: PerTransportMetrics? { _metrics }

    // MARK: - TransportService send / status

    /// Delivers `data` to `peerUhid` over the relay, establishing a bridge first if one does not
    /// already exist. Returns `true` on delivery; `false` when no relay route to the peer is
    /// reachable yet (a normal "this transport can't reach that peer right now" signal, not a
    /// fault) or when disposed. A manager treats any `false` as "this transport declined" and,
    /// with no cheaper option left, reports overall failure.
    ///
    /// The engine's `send` is blocking (it awaits the CONNECT response on a semaphore), so it runs
    /// on a background queue and the result is bridged back with a checked continuation.
    public func sendAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool {
        lock.lock(); let isDisposed = disposed; lock.unlock()
        if isDisposed || peerUhid.isEmpty { return false }
        if cancellationToken?.cancelled == true { return false }

        let ok = await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            queue.async { cont.resume(returning: self._engine.send(peerUhid, data)) }
        }
        _metrics.recordSample(rttMs: 0, success: ok, bytesTransferred: ok ? data.count : 0)
        return ok
    }

    /// Sends a whole buffer to a peer over the relay. The relay tunnels discrete DATA frames, so a
    /// stream is delivered as one buffered send — same shape as the C# / Go / Python reference.
    public func sendStreamAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool {
        await sendAsync(peerUhid: peerUhid, data: data, cancellationToken: cancellationToken)
    }

    /// True once a relay bridge to `peerUhid` has been established.
    public func isConnected(peerUhid: String) -> Bool { _engine.isConnected(peerUhid) }

    /// Marks the transport unavailable so a manager stops selecting it. The underlying engine is
    /// left intact (it may still be servicing bridges for other roles).
    public func shutdown() {
        lock.lock(); disposed = true; lock.unlock()
    }
}

// ── MeshCircuitRelay factory ────────────────────────────────────────────────────

/// Wires a ``RelayCircuitTransport`` onto a ``MeshRelayLink``, mirroring the C#
/// `MeshCircuitRelay.Create`, the Go `circuitrelay.Create`, and the Python
/// `mesh_circuit_relay.create` factories. The host then:
///
/// 1. registers the returned ``TransportService`` with its ``TransportManager`` — it is
///    auto-selected as the last-resort fallback at ``RelayCircuitTransport/powerCostRelay`` 90
///    (just below the HTTP relay); and
/// 2. routes every received `.circuitRelayControl` (PacketType 57) packet from the host's receive
///    path to the returned link's ``MeshRelayLink/handleIncomingPacket(_:)``.
///
/// The `sendOneHop` closure MUST exclude the circuit-relay transport itself so a frame never
/// recurses back through the relay.
public enum MeshCircuitRelay {

    /// Creates the relay transport + its mesh link as a pair.
    ///
    /// - Parameters:
    ///   - localUhid: this node's UHID (stamped as the relay-packet source).
    ///   - sendOneHop: sends a `MeshPacket` to a directly-connected peer; `true` if handed off.
    ///     Must exclude the circuit-relay transport itself.
    ///   - canReach: reports whether this node has a direct one-hop link to a peer.
    ///   - options: engine policy/tuning (defaults mirror the C# `CircuitRelayOptions`).
    /// - Returns: the ``RelayCircuitTransport`` to register with the manager, and the
    ///   ``MeshRelayLink`` whose ``MeshRelayLink/handleIncomingPacket(_:)`` the host feeds inbound
    ///   `.circuitRelayControl` packets into.
    public static func create(
        localUhid: String,
        sendOneHop: @escaping (MeshPacket) -> Bool,
        canReach: @escaping (String) -> Bool,
        options: RelayTransportOptions = .init()
    ) -> (transport: RelayCircuitTransport, link: MeshRelayLink) {
        let link = MeshRelayLink(localUhid: localUhid, sendOneHop: sendOneHop, canReach: canReach)
        let engine = RelayTransport(localUhid: localUhid, link: link, options: options)
        let transport = RelayCircuitTransport(engine: engine)
        return (transport, link)
    }
}
