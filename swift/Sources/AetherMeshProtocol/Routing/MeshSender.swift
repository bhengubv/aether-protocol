// SPDX-License-Identifier: MIT

import Foundation

/// Minimal sending abstraction for routing/DTN/SOS. Hosts wire this with a thin
/// adapter over their transport so the protocol services do not take a hard
/// dependency on a specific transport implementation.
public protocol MeshSender: Sendable {
    /// The local node's UHID. Used as packet.sourceUhid on outbound packets.
    var localUhid: String { get }

    /// Local node's last-known geohash, or nil if not shared.
    var localGeohash: String? { get }

    /// Snapshot of currently directly-connected peers.
    func connectedPeers() -> [PeerInfo]

    /// Forward a packet to a single next-hop peer.
    func send(_ packet: MeshPacket, nextHopUhid: String) async -> Bool

    /// Broadcast a packet to every connected peer; returns the fan-out count.
    func broadcast(_ packet: MeshPacket) async -> Int
}

public extension MeshSender {
    var localGeohash: String? { nil }
    func connectedPeers() -> [PeerInfo] { [] }
}
