// SPDX-License-Identifier: MIT
//
// Proof-of-Vicinity (PoV) anti-Sybil trust service (single-node, in-memory). Swift port of
// AetherNet.Market.IPoVService / InMemoryPoVService. Two users meet physically; their devices exchange
// a signed token over a short-range transport (BLE/NFC/NearLink). Over time a directed trust graph maps
// how many distinct humans have verified a profile.
//
// Signatures are REAL Ed25519 (Ed25519Service / Curve25519.Signing) over the canonical token body
// (PoVTokenCodec.buildSignableTokenData = "SubjectUhid + TimestampTicks + Transport"). The single-node
// service holds one identity key and produces both the witness and subject signatures with it; the
// two-party mesh exchange (each side counter-signs with its own key) is PoVTokenExchangeService.
//
// SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity signal — it attaches
// NO value semantics and never touches any money/reward layer.

import Foundation

/// The Proof-of-Vicinity trust service.
public protocol PoVServiceProtocol {
    func issueToken(witnessUhid: String, subjectUhid: String, transport: PoVTransportType) async throws -> PoVToken
    func acceptToken(_ token: PoVToken) async
    func getScore(uhid: String) async -> PoVScore
    func verifyToken(_ token: PoVToken) -> Bool
    func reportDefection(witnessUhid: String, defectorUhid: String) async
}

/// Single-node, in-memory `PoVServiceProtocol` for testing / single-node scenarios.
public final class InMemoryPoVService: PoVServiceProtocol {
    private var tokensBySubject: [String: [PoVToken]] = [:]
    private var scoreOverrides: [String: Double] = [:]

    // Self-contained real Ed25519 identity; both signatures on a token it issues use this one key.
    private let privateKey: Data
    private let publicKey: Data

    /// Fires when a token is issued or accepted.
    public var onTokenReceived: ((PoVToken) -> Void)?

    public init() {
        let kp = Ed25519Service.generateKeyPair()
        self.privateKey = kp.privateKey
        self.publicKey = kp.publicKey
    }

    public func issueToken(witnessUhid: String, subjectUhid: String,
                           transport: PoVTransportType = .ble) async throws -> PoVToken {
        let ticks = povDateToTicks(Date())
        let signable = PoVTokenCodec.buildSignableTokenData(
            subjectUhid: subjectUhid, timestampTicks: ticks, transport: transport)
        // REAL Ed25519 over the canonical body; both signatures from this node's one key (single-node).
        let sig = try Ed25519Service.sign(privateKey, signable)
        let token = PoVToken(
            witnessUhid: witnessUhid,
            subjectUhid: subjectUhid,
            timestampTicks: ticks,
            transportUsed: transport,
            witnessSignature: sig,
            subjectSignature: sig
        )
        onTokenReceived?(token)
        return token
    }

    public func acceptToken(_ token: PoVToken) async {
        // Record only a token that cryptographically verifies — both signatures valid + distinct parties.
        guard verifyToken(token) else { return }
        tokensBySubject[token.subjectUhid, default: []].append(token)
        onTokenReceived?(token)
    }

    public func getScore(uhid: String) async -> PoVScore {
        let tokens = tokensBySubject[uhid] ?? []
        let override = scoreOverrides[uhid]

        if tokens.isEmpty {
            // A UHID with no inbound tokens still surfaces a stored defection override.
            return PoVScore(uhid: uhid, uniqueWitnesses: 0, weightedScore: override ?? 0, lastUpdated: Date())
        }

        let unique = Set(tokens.map { $0.witnessUhid }).count
        // Sigmoid-ish: w / (w + 1).
        var score = Double(unique) / (Double(unique) + 1.0)
        if let o = override { score = o }
        return PoVScore(uhid: uhid, uniqueWitnesses: unique, weightedScore: score, lastUpdated: Date())
    }

    public func verifyToken(_ token: PoVToken) -> Bool {
        // Structural: both parties signed, both UHIDs present, and distinct.
        guard let ws = token.witnessSignature, !ws.isEmpty,
              let ss = token.subjectSignature, !ss.isEmpty,
              !token.witnessUhid.isEmpty, !token.subjectUhid.isEmpty,
              token.witnessUhid != token.subjectUhid else {
            return false
        }
        // Cryptographic: BOTH signatures valid over the canonical body.
        let signable = token.signableData()
        return Ed25519Service.verify(publicKey, signable, ws)
            && Ed25519Service.verify(publicKey, signable, ss)
    }

    public func reportDefection(witnessUhid: String, defectorUhid: String) async {
        let score = await getScore(uhid: witnessUhid)
        scoreOverrides[witnessUhid] = score.weightedScore * 0.8
    }
}
