// SPDX-License-Identifier: MIT
import Foundation
@testable import AetherProtocol

/// Records a single unicast send made through ``FakeMeshSender``.
public struct UnicastRecord: Sendable {
    public let packet: MeshPacket
    public let nextHopUhid: String
}

/// In-memory ``MeshSender`` for unit tests. Records all unicast/broadcast
/// activity and lets a test pre-seed connected peers and force-fail sends to a
/// specific peer.
public final class FakeMeshSender: MeshSender, @unchecked Sendable {
    public let localUhid: String
    public let localGeohash: String?

    private let lock = NSLock()
    private var peers: [PeerInfo] = []
    private var failTo: Set<String> = []
    private var unicastsList: [UnicastRecord] = []
    private var broadcastsList: [MeshPacket] = []

    public init(localUhid: String, localGeohash: String? = nil) {
        self.localUhid = localUhid
        self.localGeohash = localGeohash
    }

    // MARK: - Mutators

    public func addPeer(_ peer: PeerInfo) {
        lock.lock(); defer { lock.unlock() }
        peers.append(peer)
    }
    public func failSendsTo(_ uhid: String) {
        lock.lock(); defer { lock.unlock() }
        failTo.insert(uhid)
    }
    public func clear() {
        lock.lock(); defer { lock.unlock() }
        unicastsList.removeAll()
        broadcastsList.removeAll()
    }

    // MARK: - Recorded snapshots

    public func unicasts() -> [UnicastRecord] {
        lock.lock(); defer { lock.unlock() }
        return unicastsList
    }
    public func broadcasts() -> [MeshPacket] {
        lock.lock(); defer { lock.unlock() }
        return broadcastsList
    }

    // MARK: - MeshSender

    public func connectedPeers() -> [PeerInfo] {
        lock.lock(); defer { lock.unlock() }
        return peers
    }

    public func send(_ packet: MeshPacket, nextHopUhid: String) async -> Bool {
        lock.lock()
        let blocked = failTo.contains(nextHopUhid)
        if !blocked {
            unicastsList.append(UnicastRecord(packet: packet, nextHopUhid: nextHopUhid))
        }
        lock.unlock()
        return !blocked
    }

    public func broadcast(_ packet: MeshPacket) async -> Int {
        lock.lock()
        broadcastsList.append(packet)
        let n = peers.count
        lock.unlock()
        return n
    }
}
