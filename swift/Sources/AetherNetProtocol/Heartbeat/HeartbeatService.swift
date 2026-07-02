// SPDX-License-Identifier: MIT

import Foundation

/// A peer's last observed liveness, maintained by ``HeartbeatService`` on the receiving node.
///
/// Mirrors `AetherNet.Heartbeat.PeerLiveness` (C#). Records are keyed by the originating
/// packet's `sourceUhid`; the payload only carries the beat's ``lastSequence`` and
/// ``lastSentAtMs``, while ``receivedAtMs`` is stamped locally on receipt.
public struct PeerLiveness: Equatable, Sendable {
    /// UHID of the peer this liveness record describes.
    public var uhid: String

    /// The ``HeartbeatPayload`` sequence of the most recent heartbeat seen from the peer.
    public var lastSequence: Int32

    /// The peer-stamped `sentAtMs` of the most recent heartbeat.
    public var lastSentAtMs: Int64

    /// Local Unix-ms timestamp when the most recent heartbeat was received.
    public var receivedAtMs: Int64

    public init(uhid: String, lastSequence: Int32, lastSentAtMs: Int64, receivedAtMs: Int64) {
        self.uhid = uhid
        self.lastSequence = lastSequence
        self.lastSentAtMs = lastSentAtMs
        self.receivedAtMs = receivedAtMs
    }
}

/// Broadcasts and handles ``PacketType/heartbeat`` liveness beacons (PacketType 10).
///
/// A node periodically emits a heartbeat to its direct neighbours (TTL 1 — single hop);
/// receivers maintain a per-peer ``PeerLiveness`` table (keyed by the packet's `sourceUhid`)
/// and can query which peers are currently live. Unauthenticated by design — like SOS, a
/// heartbeat is a low-stakes liveness hint, not a security assertion.
///
/// Mirrors the C# `HeartbeatService` / `IHeartbeatService`. Modelled as a `public actor`
/// (matching ``SosBroadcastService``) so the sequence counter and peer table are mutated
/// without data races.
public actor HeartbeatService {
    private let sender: any MeshSender

    /// Monotonic heartbeat sequence number (starts at 0; the first ``sendHeartbeat`` emits 1).
    private var sequence: Int32 = 0

    /// Per-peer liveness, keyed by the originating packet's `sourceUhid`.
    private var peers: [String: PeerLiveness] = [:]

    /// Raised when a heartbeat is received from a peer (new or refreshed liveness).
    /// Mirrors the C# `PeerSeen` event.
    public var onPeerSeen: (@Sendable (PeerLiveness) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnPeerSeen(_ callback: (@Sendable (PeerLiveness) -> Void)?) {
        onPeerSeen = callback
    }

    /// Broadcast a single heartbeat to all directly connected peers (TTL 1). The sequence
    /// number increments on every call. Returns the number of peers the beacon was delivered to.
    @discardableResult
    public func sendHeartbeat() async -> Int {
        sequence += 1
        let body = encodeHeartbeatWire(
            sequence: sequence,
            sentAtMs: Int64(Date().timeIntervalSince1970 * 1000)
        )

        let packet = MeshPacket(
            type: .heartbeat,
            sourceUhid: sender.localUhid,
            destinationUhid: "*",
            ttl: 1, // heartbeats are single-hop: liveness of DIRECT neighbours only
            payload: body
        )

        return await sender.broadcast(packet)
    }

    /// Process an incoming ``PacketType/heartbeat`` packet: refresh the sender's liveness
    /// record (keyed by `sourceUhid`) and fire ``onPeerSeen``. No-op (returns `false`) for
    /// the wrong packet type, a self-originated heartbeat echoed back, or a malformed payload.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .heartbeat else { return false }

        // Ignore our own heartbeat echoed back.
        if packet.sourceUhid == sender.localUhid { return false }

        guard let body = parseHeartbeatWire(packet.payload) else { return false }

        let liveness = PeerLiveness(
            uhid: packet.sourceUhid,
            lastSequence: body.sequence,
            lastSentAtMs: body.sentAtMs,
            receivedAtMs: Int64(Date().timeIntervalSince1970 * 1000)
        )
        peers[packet.sourceUhid] = liveness
        onPeerSeen?(liveness)
        return true
    }

    /// Snapshot of every peer this node has ever seen a heartbeat from.
    public func getKnownPeers() -> [PeerLiveness] {
        Array(peers.values)
    }

    /// Peers whose most recent heartbeat was received within the last `withinSeconds` seconds.
    ///
    /// A negative `withinSeconds` pushes the recency horizon into the future, so it excludes
    /// even a just-seen peer — matching the C# behaviour exercised by the tests.
    public func getLivePeers(withinSeconds: Int) -> [PeerLiveness] {
        let cutoff = Int64(Date().timeIntervalSince1970 * 1000) - Int64(withinSeconds) * 1000
        return peers.values.filter { $0.receivedAtMs >= cutoff }
    }
}

// ─── Heartbeat wire (PacketType 10) ───
//
// Serialises to snake_case keys, field order `sequence` then `sent_at_ms`, no whitespace,
// both values bare integers. This is the byte-identity gate (fixtures/heartbeat/vectors.json)
// and must stay byte-identical across all eight language ports.

/// JSON payload for ``PacketType/heartbeat`` packets. Wire format: UTF-8 JSON with snake_case
/// property names declared directly on the struct so a synthesized `Codable` encodes them in
/// declaration order. Both fields are integers (no UUID here), so the encoding is byte-identical
/// across all language ports. Mirrors the C# `HeartbeatPayload`.
private struct HeartbeatWire: Codable {
    let sequence: Int32
    let sent_at_ms: Int64
}

private func encodeHeartbeatWire(sequence: Int32, sentAtMs: Int64) -> Data {
    let w = HeartbeatWire(sequence: sequence, sent_at_ms: sentAtMs)
    return (try? JSONEncoder().encode(w)) ?? Data()
}

private func parseHeartbeatWire(_ data: Data) -> (sequence: Int32, sentAtMs: Int64)? {
    guard let w = try? JSONDecoder().decode(HeartbeatWire.self, from: data) else { return nil }
    return (w.sequence, w.sent_at_ms)
}

/// Test-only shim exposing the real ``HeartbeatWire`` serialization path (the struct itself stays
/// `private`) so byte-identity vectors in `fixtures/heartbeat/vectors.json` can be verified.
internal func _heartbeatWireBytesForTests(sequence: Int32, sentAtMs: Int64) -> Data {
    encodeHeartbeatWire(sequence: sequence, sentAtMs: sentAtMs)
}
