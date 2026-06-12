// SPDX-License-Identifier: MIT

import Foundation
import Crypto

/// Ephemeral Routing Id (ERID) — a rotating, key-derived wire address designed to
/// replace the stable, phone-derived UHID on the public wire.
///
/// ## The problem it solves
/// A node's UHID is `SHA-256(phone : deviceId : publicKey)` — stable for the life of
/// the install and carried in cleartext on every packet. A passive observer who never
/// breaks any encryption can therefore (a) follow any node indefinitely across time and
/// place, and (b) — because the value is phone-derived — attempt to confirm a suspected
/// phone number by recomputing the hash. That is a surveillance and targeting primitive,
/// independent of the fact that message contents are end-to-end encrypted.
///
/// ## The design
///   ERID(epoch) = base32( HMAC-SHA256(routingKey, epoch) )[0..<length]
/// - `routingKey` is SECRET — derived from the node's identity secret via
///   ``deriveRoutingKey(_:)``. It is NEVER derived from the public key.
/// - `epoch = floor(unixSeconds / epochSeconds)` — a 15-minute window by default.
/// - Two ERIDs from the same node in different epochs are cryptographically uncorrelated
///   to an outside observer — no cross-time linkage, no phone recovery.
///
/// The epoch is encoded big-endian (8-byte signed Int64) so every language port produces
/// byte-identical input to the HMAC.
public enum EphemeralRoutingId {

    // MARK: - Errors

    public enum EphemeralRoutingIdError: Error, Equatable {
        case emptySecret
        case emptyRoutingKey
        case invalidEpochSeconds
        case invalidLength
    }

    // MARK: - Constants

    /// Same Crockford base-32 alphabet as ``AetherNetTag`` (no I/L/O/U — visually unambiguous).
    private static let alphabet: [UInt8] =
        Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ".utf8)

    /// HKDF domain-separation label. Must match the C# reference (and every other port).
    private static let routingKeyInfo = Data("aether-erid-routing-key-v1".utf8)

    /// Default rotation window: 15 minutes, expressed in seconds.
    public static let defaultEpochSeconds: Int64 = 900

    /// Default ERID length in base-32 characters (16 chars × 5 bits = 80 bits of entropy).
    public static let defaultLength: Int = 16

    // MARK: - Routing key

    /// Derives the 32-byte SECRET routing key from a node's identity secret (e.g. its
    /// Ed25519 private-key bytes). Domain-separated via HKDF-SHA256 (RFC 5869, no salt).
    /// MUST be fed a secret — never a public value, or the rotation schedule becomes
    /// computable by anyone.
    /// - Throws: ``EphemeralRoutingIdError/emptySecret`` when `identitySecret` is empty.
    public static func deriveRoutingKey(_ identitySecret: [UInt8]) throws -> [UInt8] {
        guard !identitySecret.isEmpty else {
            throw EphemeralRoutingIdError.emptySecret
        }
        let derived = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: Data(identitySecret)),
            salt: Data(),
            info: routingKeyInfo,
            outputByteCount: 32
        )
        return derived.withUnsafeBytes { Array($0) }
    }

    // MARK: - Epoch

    /// The epoch (rotation-window index) that contains the given Unix time. Negative
    /// `unixSeconds` clamp to 0.
    /// - Throws: ``EphemeralRoutingIdError/invalidEpochSeconds`` when `epochSeconds <= 0`.
    public static func epochFor(_ unixSeconds: Int64,
                                epochSeconds: Int64 = defaultEpochSeconds) throws -> Int64 {
        guard epochSeconds > 0 else {
            throw EphemeralRoutingIdError.invalidEpochSeconds
        }
        let u = unixSeconds < 0 ? 0 : unixSeconds
        return u / epochSeconds
    }

    // MARK: - Derivation

    /// Derives the ERID for the epoch that contains `unixSeconds`.
    public static func derive(_ routingKey: [UInt8],
                              unixSeconds: Int64,
                              epochSeconds: Int64 = defaultEpochSeconds,
                              length: Int = defaultLength) throws -> String {
        let epoch = try epochFor(unixSeconds, epochSeconds: epochSeconds)
        return try deriveForEpoch(routingKey, epoch: epoch, length: length)
    }

    /// Derives the ERID for an explicit epoch number. The epoch is encoded big-endian so
    /// every language port produces byte-identical input to the HMAC.
    /// - Throws: ``EphemeralRoutingIdError/emptyRoutingKey`` or
    ///   ``EphemeralRoutingIdError/invalidLength``.
    public static func deriveForEpoch(_ routingKey: [UInt8],
                                      epoch: Int64,
                                      length: Int = defaultLength) throws -> String {
        guard !routingKey.isEmpty else {
            throw EphemeralRoutingIdError.emptyRoutingKey
        }
        guard length >= 1 && length <= 51 else {
            throw EphemeralRoutingIdError.invalidLength
        }

        // 8-byte big-endian signed Int64 — matches BinaryPrimitives.WriteInt64BigEndian.
        let be = UInt64(bitPattern: epoch).bigEndian
        var epochBytes = [UInt8](repeating: 0, count: 8)
        withUnsafeBytes(of: be) { epochBytes.replaceSubrange(0..<8, with: $0) }

        let mac = HMAC<SHA256>.authenticationCode(
            for: Data(epochBytes),
            using: SymmetricKey(data: Data(routingKey))
        )
        return base32(Array(mac), length: length)
    }

    // MARK: - Private

    /// Encodes the first `length * 5` bits of `data` as Crockford base-32, MSB first.
    private static func base32(_ data: [UInt8], length: Int) -> String {
        var out = [UInt8](repeating: 0, count: length)
        var bitPos = 0
        for i in 0..<length {
            let byteIndex = bitPos >> 3
            let bitOffset = bitPos & 7
            let hi = Int(data[byteIndex])
            let lo = (byteIndex + 1 < data.count) ? Int(data[byteIndex + 1]) : 0
            let window = (hi << 8) | lo
            let val = (window >> (11 - bitOffset)) & 0x1F
            out[i] = alphabet[val]
            bitPos += 5
        }
        return String(decoding: out, as: UTF8.self)
    }
}
