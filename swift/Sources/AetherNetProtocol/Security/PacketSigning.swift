// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// Packet signing service with nonce deduplication for replay prevention.
public actor PacketSigningService {
    /// Maximum age for cached nonces (5 minutes).
    private let maxNonceAgeSeconds: Int = 300

    /// Deduplication cache keyed by `"<sourceUhid>:<hex(nonce)>"` -> timestamp.
    ///
    /// Pre-2026-05-05 the Swift port keyed this cache by `sourceUhid` alone
    /// and stored only ONE nonce per source — every new packet overwrote
    /// the prior cached nonce, so a replay of the prior nonce went
    /// undetected after any subsequent packet. Matches the C# fix in
    /// `AetherNet.Security.Services.PacketSigningService` (`_seenNonces` keyed
    /// by `string.Concat(packet.SourceUhid, ":", Convert.ToHexString(packet.PacketNonce))`)
    /// so cross-language behaviour is consistent.
    private var seenNonces: [String: Date] = [:]

    private let ed25519PrivateKey: Data
    private let ed25519PublicKey: Data

    /// Optional reputation service. When set, replay and signature-failure
    /// events are forwarded so that the per-UHID reputation score is updated
    /// in real time. Injected after construction via ``setReputation(_:)``
    /// to avoid a circular dependency between the security and reputation
    /// layers at initialisation time.
    private var reputation: NodeReputationService?

    public init(privateKey: Data, publicKey: Data) {
        self.ed25519PrivateKey = privateKey
        self.ed25519PublicKey = publicKey
    }

    /// Wires up (or removes) the reputation service used to penalise
    /// misbehaving peers. Pass `nil` to detach.
    public func setReputation(_ rep: NodeReputationService?) {
        reputation = rep
    }

    /// Signs a mesh packet according to the Aether protocol.
    /// The signature covers: PacketNonce || TimestampMs || Type || SourceUhidLength || SourceUhid ||
    /// DestinationUhidLength || DestinationUhid || SHA256(Payload) || Ttl || Priority
    public func signPacket(_ packet: inout MeshPacket) throws {
        let signableData = try constructSignableData(packet)
        packet.signature = try Ed25519Service.sign(ed25519PrivateKey, signableData)
    }

    /// Verifies a packet signature and checks for replay attacks.
    ///
    /// Reputation hooks:
    /// - A duplicate-nonce (replay) error penalises `sourceUhid` via
    ///   ``NodeReputationService/recordReplayAttempt(uhid:)`` before the
    ///   error is re-thrown to the caller.
    /// - A false signature result penalises `sourceUhid` via
    ///   ``NodeReputationService/recordSignatureFailure(uhid:)`` before
    ///   `false` is returned.
    public func verifyPacket(
        _ packet: MeshPacket,
        againstPublicKey publicKey: Data
    ) async throws -> Bool {
        // Check nonce deduplication — notify reputation on replay.
        do {
            try checkNonceDuplicate(packet.sourceUhid, packet.packetNonce)
        } catch let error as PacketSigningError {
            if case .duplicateNonce(let sourceUhid) = error {
                await reputation?.recordReplayAttempt(uhid: sourceUhid)
            }
            throw error
        }

        // Construct and verify signature — notify reputation on failure.
        let signableData = try constructSignableData(packet)
        let isValid = Ed25519Service.verify(publicKey, signableData, packet.signature)
        if !isValid {
            await reputation?.recordSignatureFailure(uhid: packet.sourceUhid)
        }
        return isValid
    }

    /// Gets the public key for this signing service.
    public func getPublicKey() -> Data {
        ed25519PublicKey
    }

    // MARK: - Private Methods

    private func constructSignableData(_ packet: MeshPacket) throws -> Data {
        Self.buildSignableData(packet)
    }

    /// Builds the canonical signable byte layout for a packet — the EXACT same
    /// bytes the source signed and every other language implementation shares:
    ///
    ///   PacketNonce || TimestampMs(LE 8) || Type(LE 4) || SourceUhidLength(LE 4) ||
    ///   SourceUhid(UTF-8) || DestinationUhidLength(LE 4) || DestinationUhid(UTF-8) ||
    ///   SHA256(Payload)(32) || Ttl(LE 4) || Priority(LE 4)
    ///
    /// Exposed as a static so the routing layer's Ed25519 RREP verifier can
    /// recompute the same signable bytes without owning a signing keypair —
    /// mirrors C# PacketSigningService.BuildSignableData(MeshPacket). This is a
    /// pure reformat of the EXISTING on-the-wire layout; it introduces NO new
    /// field, ordering, or width, so the wire format and all fixtures are unchanged.
    public static func buildSignableData(_ packet: MeshPacket) -> Data {
        var data = Data()

        // PacketNonce (8 bytes)
        data.append(packet.packetNonce)

        // TimestampMs (8 bytes, little-endian)
        var timestamp = packet.timestampMs.littleEndian
        data.append(withUnsafeBytes(of: &timestamp) { Data($0) })

        // Type (4 bytes, little-endian)
        var typeValue = Int32(packet.type.rawValue).littleEndian
        data.append(withUnsafeBytes(of: &typeValue) { Data($0) })

        // SourceUhidLength (4 bytes, little-endian)
        let sourceBytes = packet.sourceUhid.data(using: .utf8) ?? Data()
        var sourceLen = Int32(sourceBytes.count).littleEndian
        data.append(withUnsafeBytes(of: &sourceLen) { Data($0) })

        // SourceUhid (UTF-8)
        data.append(sourceBytes)

        // DestinationUhidLength (4 bytes, little-endian)
        let destBytes = packet.destinationUhid.data(using: .utf8) ?? Data()
        var destLen = Int32(destBytes.count).littleEndian
        data.append(withUnsafeBytes(of: &destLen) { Data($0) })

        // DestinationUhid (UTF-8)
        data.append(destBytes)

        // SHA256(Payload) (32 bytes)
        let payloadHash = Data(SHA256.hash(data: packet.payload))
        data.append(payloadHash)

        // Ttl (4 bytes, little-endian)
        var ttlValue = Int32(packet.ttl).littleEndian
        data.append(withUnsafeBytes(of: &ttlValue) { Data($0) })

        // Priority (4 bytes, little-endian)
        var priorityValue = Int32(packet.priority).littleEndian
        data.append(withUnsafeBytes(of: &priorityValue) { Data($0) })

        return data
    }

    private func checkNonceDuplicate(_ sourceUhid: String, _ nonce: Data) throws {
        // Key by (sourceUhid, nonce) so:
        //   1. distinct senders cannot collide on the same nonce — a
        //      colliding nonce from a different sender is legitimate
        //      traffic, not a replay;
        //   2. an attacker who pre-registers a nonce against a recipient
        //      cannot block the legitimate sender's first packet;
        //   3. a replay of the SAME (source, nonce) pair is the only thing
        //      that gets rejected — which is exactly the replay-attack
        //      shape the dedup is supposed to catch.
        // Matches the C# `AetherNet.Security.Services.PacketSigningService`
        // 2026-05-05 fix.
        let cacheKey = nonceCacheKey(sourceUhid: sourceUhid, nonce: nonce)

        // Clean up old cache entries.
        let now = Date()
        let threshold = now.addingTimeInterval(-TimeInterval(maxNonceAgeSeconds))
        for (key, ts) in seenNonces where ts < threshold {
            seenNonces.removeValue(forKey: key)
        }

        // Check for duplicate (source, nonce) pair.
        if seenNonces[cacheKey] != nil {
            throw PacketSigningError.duplicateNonce(sourceUhid)
        }

        // Record the (source, nonce) pair so a replay of THIS exact pair
        // gets rejected next time.
        seenNonces[cacheKey] = now
    }

    /// Constructs the dedup-cache key in the same shape as the C# port —
    /// `"<sourceUhid>:<UPPERCASE-HEX(nonce)>"`. Uppercase matches
    /// `Convert.ToHexString` exactly so a future cross-language test that
    /// inspects raw cache keys stays observably equivalent.
    private func nonceCacheKey(sourceUhid: String, nonce: Data) -> String {
        let hex = nonce.map { String(format: "%02X", $0) }.joined()
        return "\(sourceUhid):\(hex)"
    }
}

public enum PacketSigningError: Error, Equatable {
    case duplicateNonce(String)
    case signatureFailed(String)
    case verificationFailed(String)
}
