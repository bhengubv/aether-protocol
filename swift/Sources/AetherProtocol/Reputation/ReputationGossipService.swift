// SPDX-License-Identifier: MIT

import Foundation

// MARK: - Payload

/// Wire-format payload for a reputation-gossip broadcast.
///
/// JSON keys use snake_case to match the cross-language Aether wire convention.
public struct ReputationUpdatePayload: Codable {
    public let reporterUhid: String
    public let targetUhid: String
    public let scoreDelta: Double
    public let timestampMs: Int64
    public let reason: String

    enum CodingKeys: String, CodingKey {
        case reporterUhid = "reporter_uhid"
        case targetUhid   = "target_uhid"
        case scoreDelta   = "score_delta"
        case timestampMs  = "timestamp_ms"
        case reason
    }

    public init(
        reporterUhid: String,
        targetUhid: String,
        scoreDelta: Double,
        timestampMs: Int64,
        reason: String
    ) {
        self.reporterUhid = reporterUhid
        self.targetUhid   = targetUhid
        self.scoreDelta   = scoreDelta
        self.timestampMs  = timestampMs
        self.reason       = reason
    }
}

// MARK: - Protocols

/// Minimal broadcast abstraction consumed by ``ReputationGossipService``.
///
/// Intentionally distinct from the routing-layer ``MeshSender`` so that the
/// gossip service can be injected with a thin adapter over any transport.
public protocol GossipMeshSender: Sendable {
    /// The local node's UHID, used as `reporterUhid` on outbound gossip.
    var localUhid: String { get }

    /// Broadcast `packet` to all reachable peers; returns the fan-out count.
    func broadcast(packet: MeshPacket) async throws -> Int
}

/// Signs and verifies ``MeshPacket`` instances for gossip traffic.
public protocol GossipPacketSigner: Sendable {
    /// Populate `packet.packetNonce`, `packet.timestampMs`, and
    /// `packet.signature` in place.
    func sign(packet: inout MeshPacket) async throws

    /// Return `true` iff the packet signature is valid for `senderPublicKey`.
    func verify(packet: MeshPacket, senderPublicKey: [UInt8]) async throws -> Bool
}

// MARK: - Service

/// Gossip layer for distributing reputation observations across the mesh.
///
/// - Broadcasts a signed ``ReputationUpdatePayload`` to all peers (TTL = 3).
/// - Accepts incoming gossip, verifies authenticity and freshness, then applies
///   a *weighted* score delta — scaled by the reporter's own local reputation
///   score — to prevent low-trust nodes from injecting large adjustments.
///
/// Thread-safety: implemented as a Swift `actor`; all state mutations run on
/// the actor's executor.
public actor ReputationGossipService {

    // MARK: - Dependencies

    private let sender:     any GossipMeshSender
    private let signing:    any GossipPacketSigner
    private let reputation: NodeReputationService

    // MARK: - Constants

    /// Maximum allowed clock skew between gossip payload timestamp and local
    /// wall-clock time before a packet is considered stale.
    private static let freshnessWindowMs: Int64 = 5 * 60 * 1_000   // 5 minutes

    /// TTL applied to every outbound reputation-gossip broadcast.
    private static let gossipTtl: Int32 = 3

    // MARK: - Init

    public init(
        sender:     any GossipMeshSender,
        signing:    any GossipPacketSigner,
        reputation: NodeReputationService
    ) {
        self.sender     = sender
        self.signing    = signing
        self.reputation = reputation
    }

    // MARK: - Outbound

    /// Broadcast a reputation observation about `targetUhid` to all peers.
    ///
    /// `scoreDelta` is clamped to [−1, 1] before encoding so that no single
    /// broadcast can carry an extreme adjustment.
    @discardableResult
    public func broadcastReputationUpdate(
        targetUhid: String,
        scoreDelta: Double,
        reason: String
    ) async throws -> Int {
        let clamped = max(-1.0, min(1.0, scoreDelta))

        let nowMs = Int64(Date().timeIntervalSince1970 * 1_000)
        let payload = ReputationUpdatePayload(
            reporterUhid: sender.localUhid,
            targetUhid:   targetUhid,
            scoreDelta:   clamped,
            timestampMs:  nowMs,
            reason:       reason
        )

        let payloadData = try JSONEncoder().encode(payload)

        var packet = MeshPacket(
            type:            .reputationUpdate,
            sourceUhid:      sender.localUhid,
            destinationUhid: "*",
            ttl:             Self.gossipTtl,
            payload:         payloadData
        )

        try await signing.sign(packet: &packet)
        return try await sender.broadcast(packet: packet)
    }

    // MARK: - Inbound

    /// Process an inbound gossip packet received from a peer.
    ///
    /// Returns `true` if the packet was accepted and the reputation store was
    /// updated; `false` for any rejection (wrong type, bad signature, stale,
    /// malformed, own-echo, etc.).
    public func handleGossipPacket(
        packet: MeshPacket,
        senderPublicKey: [UInt8]
    ) async throws -> Bool {

        // 1. Correct packet type guard.
        guard packet.type == .reputationUpdate else { return false }

        // 2. Signature verification.
        let valid = try await signing.verify(packet: packet,
                                             senderPublicKey: senderPublicKey)
        guard valid else { return false }

        // 3. Decode payload.
        guard let payload = try? JSONDecoder().decode(
            ReputationUpdatePayload.self, from: packet.payload
        ) else { return false }

        // 4. Freshness check — reject if timestamp drifts more than 5 minutes.
        let nowMs = Int64(Date().timeIntervalSince1970 * 1_000)
        guard abs(nowMs - payload.timestampMs) <= Self.freshnessWindowMs else {
            return false
        }

        // 5. Non-empty reporter and target UHIDs.
        guard !payload.reporterUhid.isEmpty, !payload.targetUhid.isEmpty else {
            return false
        }

        // 6. Own-echo guard — never apply gossip we ourselves originated.
        guard payload.reporterUhid != sender.localUhid else { return false }

        // 7. Weight the delta by the reporter's own reputation score.
        let reporterScore = await reputation.reputationScore(for: payload.reporterUhid)

        // 8. Clamp the raw delta from the payload.
        let clamped = max(-1.0, min(1.0, payload.scoreDelta))

        // 9. Scale by reporter trust.
        let effectiveDelta = clamped * reporterScore

        // 10. Apply to the target's score.
        await reputation.applyWeightedDelta(uhid: payload.targetUhid,
                                            weightedDelta: effectiveDelta)

        return true
    }
}
