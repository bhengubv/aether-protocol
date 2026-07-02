// SPDX-License-Identifier: MIT

import Foundation

/// Production `RelayLink` that carries circuit-relay-v2 frames one hop over the real mesh —
/// mirrors the C# `MeshRelayLink` and the Go / Python / TS / Rust / Kotlin `MeshRelayLink`.
///
/// Each frame is wrapped in a `MeshPacket` of type `.circuitRelayControl` and handed to the
/// host's send-to-connected-peer closure; inbound CircuitRelayControl packets are fed back into
/// the engine via `handleIncomingPacket`. The two closures are the seam to whatever real
/// transport the host runs (BLE / Wi-Fi Direct / WebRTC / the HTTP relay). It never calls a
/// radio directly and never recurses through itself (the host's one-hop send must exclude the
/// circuit-relay transport).
public final class MeshRelayLink: RelayLink {
    private let localUhid: String
    private let sendOneHop: (MeshPacket) -> Bool
    private let canReachFn: (String) -> Bool
    private let lock = NSLock()
    private var handler: ((String, Data) -> Void)?

    /// - Parameters:
    ///   - localUhid: this node's UHID (stamped as the packet source).
    ///   - sendOneHop: sends a MeshPacket to a directly-connected peer; `true` if handed off.
    ///   - canReach: reports whether this node has a direct one-hop link to a peer.
    public init(localUhid: String,
                sendOneHop: @escaping (MeshPacket) -> Bool,
                canReach: @escaping (String) -> Bool) {
        self.localUhid = localUhid
        self.sendOneHop = sendOneHop
        self.canReachFn = canReach
    }

    public func sendFrame(_ node: String, _ frame: Data) -> Bool {
        // ttl 1: relay frames travel exactly one hop; end-to-end routing is the engine's job.
        let pkt = MeshPacket(type: .circuitRelayControl, sourceUhid: localUhid,
                             destinationUhid: node, ttl: 1, payload: frame)
        return sendOneHop(pkt)
    }

    public func canReach(_ node: String) -> Bool { canReachFn(node) }

    public func onFrame(_ handler: @escaping (String, Data) -> Void) {
        lock.lock(); self.handler = handler; lock.unlock()
    }

    /// Feeds an inbound `.circuitRelayControl` packet from the host's receive path into the relay
    /// engine (non-relay packet types are ignored). The host must call this for every received
    /// `.circuitRelayControl` packet.
    public func handleIncomingPacket(_ packet: MeshPacket) {
        guard packet.type == .circuitRelayControl else { return }
        lock.lock(); let h = handler; lock.unlock()
        h?(packet.sourceUhid, packet.payload)
    }
}
