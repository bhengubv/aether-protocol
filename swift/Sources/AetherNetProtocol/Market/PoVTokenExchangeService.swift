// SPDX-License-Identifier: MIT
//
// On-mesh Proof-of-Vicinity token exchange — the directed, two-key witness→subject co-presence proof,
// carried over PacketType.povTokenExchange (43). Swift port of AetherNet.Market.PoVTokenExchangeService,
// mirroring the Go and TypeScript ports and the AetherNet handler idiom established by MeshTipService
// (sign payload with the identity key → wrap in a signed MeshPacket → send) and ReputationGossipService
// (verify the enclosing packet against the supplied sender public key, which also enforces freshness +
// nonce replay-dedup).
//
// CRYPTO: signatures are real Ed25519 over the canonical token body (PoVTokenCodec.buildSignableTokenData
// = "SubjectUhid + TimestampTicks + Transport"), byte-identical to every other language implementation,
// so a token exchanged here interoperates on one mesh.
//
// SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity signal. It attaches
// NO value semantics and never touches any money/reward layer.

import Foundation

// MARK: - Seams

/// Minimal directed-send surface needed by ``PoVTokenExchangeService``.
public protocol PoVMeshSender: Sendable {
    /// The local node's UHID.
    var localUhid: String { get }

    /// Deliver `packet` toward `subjectUhid` (directed — one short-range hop). Returns `true` on
    /// success.
    func send(packet: MeshPacket, subjectUhid: String) async -> Bool
}

/// Signs and verifies the enclosing ``MeshPacket`` envelope. `verify` MUST also enforce freshness and
/// nonce replay-dedup (mirroring the C# `IPacketSigningService`), so a replayed or stale PoV exchange
/// is rejected here before any crypto on the body.
public protocol PoVPacketSigner: Sendable {
    /// Populate `packet.packetNonce`, `packet.timestampMs`, and `packet.signature` in place.
    func sign(packet: inout MeshPacket) async throws

    /// Verify `packet`'s envelope signature against `senderPublicKey` AND enforce freshness +
    /// replay-dedup. Returns `true` only for a fresh, correctly-signed, non-replayed packet.
    func verify(packet: MeshPacket, senderPublicKey: [UInt8]) async throws -> Bool
}

/// Signs/verifies canonical token bodies with Ed25519 identity keys.
public protocol PoVIdentitySigner: Sendable {
    /// Produce a 64-byte Ed25519 signature over `data` using the local identity key.
    func signData(_ data: Data) async throws -> Data

    /// Verify `signature` over `data` against `publicKey`.
    func verifySignature(publicKey: [UInt8], data: Data, signature: Data) -> Bool
}

// MARK: - Service

/// Issues and accepts on-mesh PoV tokens over packet type 43.
///
/// Thread-safety: implemented as a Swift `actor`; all state mutations run on the actor's executor.
public actor PoVTokenExchangeService {

    private let sender: any PoVMeshSender
    private let signing: any PoVPacketSigner
    private let identity: any PoVIdentitySigner

    /// Accepted tokens indexed by SubjectUhid → the tokens vouching for that subject.
    private var tokensBySubject: [String: [PoVToken]] = [:]

    /// Fires once a counter-signed token has been recorded locally. `@Sendable` because it is invoked
    /// from the actor's executor and registered from outside it.
    private var onTokenReceived: (@Sendable (PoVToken) -> Void)?

    /// TTL applied to every outbound PoV exchange — co-present: the subject is one short-range hop
    /// away.
    private static let exchangeTtl: Int32 = 1

    public init(
        sender: any PoVMeshSender,
        signing: any PoVPacketSigner,
        identity: any PoVIdentitySigner
    ) {
        self.sender = sender
        self.signing = signing
        self.identity = identity
    }

    /// Registers (or clears) the callback fired when a counter-signed token is recorded.
    public func setOnTokenReceived(_ callback: (@Sendable (PoVToken) -> Void)?) {
        onTokenReceived = callback
    }

    /// Mints a witness-signed PoV token for `subjectUhid` and sends it directed (TTL 1) over packet 43.
    /// It refuses to mint over a non-short-range transport or to vouch for itself. Returns the token
    /// that was issued (with an empty subject signature — the subject fills it on receipt), or `nil`
    /// when issuance was refused.
    @discardableResult
    public func issueToken(
        subjectUhid: String,
        transport: PoVTransportType = .ble
    ) async throws -> PoVToken? {
        if subjectUhid.isEmpty { return nil }

        // ANTI-REMOTE-MINTING: a vicinity proof is only meaningful over a short-range channel.
        if !transport.isShortRange { return nil }

        let localUhid = sender.localUhid
        if localUhid.isEmpty { return nil }

        // A node cannot vouch for itself — that would be a free, unbounded self-attestation.
        if localUhid == subjectUhid { return nil }

        let timestampTicks = povDateToTicks(Date())

        // Witness signs the canonical token body with the node's REAL Ed25519 identity key.
        let signable = PoVTokenCodec.buildSignableTokenData(
            subjectUhid: subjectUhid,
            timestampTicks: timestampTicks,
            transport: transport
        )
        let witnessSignature = try await identity.signData(signable)

        // The returned token reflects what is put on the wire: the subject signature stays empty until
        // the subject counter-signs on receipt.
        let token = PoVToken(
            witnessUhid: localUhid,
            subjectUhid: subjectUhid,
            timestampTicks: timestampTicks,
            transportUsed: transport,
            witnessSignature: witnessSignature,
            subjectSignature: nil
        )

        let body = try token.toJSON()

        var packet = MeshPacket(
            type: .povTokenExchange,
            sourceUhid: localUhid,
            destinationUhid: subjectUhid, // directed — NOT a broadcast.
            ttl: Self.exchangeTtl,
            payload: body
        )

        // Sign the envelope (fills signature, packetNonce, timestampMs).
        try await signing.sign(packet: &packet)

        _ = await sender.send(packet: packet, subjectUhid: subjectUhid)

        return token
    }

    /// Processes an inbound PoV exchange packet (type 43).
    ///
    /// Returns `true` when the token was accepted, counter-signed, and recorded.
    /// Returns `false` when the packet should be silently discarded (wrong type, bad/stale/replayed
    /// envelope, malformed payload, self-echo, not addressed to us, missing/invalid witness signature,
    /// witness == subject).
    @discardableResult
    public func handleTokenExchange(
        packet: MeshPacket,
        senderPublicKey: [UInt8]
    ) async throws -> Bool {
        guard packet.type == .povTokenExchange else { return false }

        // 1. Verify the enclosing MeshPacket signature (also enforces freshness + nonce replay-dedup).
        let signatureValid: Bool
        do {
            signatureValid = try await signing.verify(packet: packet, senderPublicKey: senderPublicKey)
        } catch {
            // A duplicate-nonce / verification error means the packet is stale, replayed, or invalid.
            return false
        }
        guard signatureValid else { return false }

        // 2. Deserialise the token body. A malformed payload (incl. an unknown transport byte) is
        //    dropped.
        guard var token = try? PoVToken.parse(packet.payload) else { return false }
        guard !token.witnessUhid.isEmpty, !token.subjectUhid.isEmpty else { return false }

        // 3. The incoming token must already carry the witness's signature.
        guard let witnessSignature = token.witnessSignature, !witnessSignature.isEmpty else {
            return false
        }

        let localUhid = sender.localUhid

        // 4. Ignore our own token echoed back to us (witness == us).
        if !localUhid.isEmpty && token.witnessUhid == localUhid { return false }

        // 5. The token must be addressed to us — we are the subject being vouched for.
        if !localUhid.isEmpty && token.subjectUhid != localUhid { return false }

        // 6. Verify the WITNESS's Ed25519 signature over the canonical body, against the verified
        //    sender key (the witness is the packet source, so the envelope and the body share a
        //    signing key). A forged or tampered witness signature is rejected before we counter-sign.
        let signable = token.signableData()
        guard identity.verifySignature(
            publicKey: senderPublicKey,
            data: signable,
            signature: witnessSignature
        ) else {
            return false
        }

        // 6b. A witness must not be vouching for itself — distinct parties is a hard PoV invariant.
        if token.witnessUhid == token.subjectUhid { return false }

        // 7. Counter-sign the SAME canonical body as the subject, with our REAL Ed25519 identity key.
        //    The token now carries BOTH signatures and becomes valid.
        token.subjectSignature = try await identity.signData(signable)

        // 8. Record it (increments the witness's contribution to OUR score) and notify.
        recordToken(token)
        onTokenReceived?(token)

        return true
    }

    /// Returns the local PoV trust score for `uhid`, derived from recorded tokens.
    public func getScore(_ uhid: String) -> PoVScore {
        let tokens = tokensBySubject[uhid] ?? []
        let uniqueWitnesses = Set(tokens.map { $0.witnessUhid }).count
        let weighted = uniqueWitnesses > 0
            ? Double(uniqueWitnesses) / (Double(uniqueWitnesses) + 1.0)
            : 0.0
        return PoVScore(
            uhid: uhid,
            uniqueWitnesses: uniqueWitnesses,
            weightedScore: weighted,
            lastUpdated: Date()
        )
    }

    /// Sorted list of subject UHIDs with at least one recorded token. Mainly for tests/diagnostics.
    public func acceptedSubjects() -> [String] {
        tokensBySubject.keys.sorted()
    }

    // MARK: - Helpers

    private func recordToken(_ token: PoVToken) {
        tokensBySubject[token.subjectUhid, default: []].append(token)
    }
}
