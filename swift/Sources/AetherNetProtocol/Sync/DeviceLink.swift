// SPDX-License-Identifier: MIT

import Foundation

/// Errors thrown while parsing or building a ``DeviceLink``.
public enum DeviceLinkError: Error, Equatable {
    case tooShort
    case unsupportedVersion(UInt8)
    case truncated
    case invalidDevicePublicKeyLength
    case invalidSignatureLength
    case deviceIdTooLong
}

/// A signed device-membership record. A user links a new device by having their
/// long-term Ed25519 identity key sign the new device's own public key; every
/// other device verifies that signature to admit the newcomer into the "self"
/// device set — no central directory, no server. Because Ed25519 signatures are
/// deterministic, the serialized record is byte-identical across SDKs.
///
/// Mirrors the C# `DeviceLink` record (`src/AetherNet.Security/Sync/`).
public struct DeviceLink: Equatable, Sendable {
    /// The linked device's identifier.
    public let deviceId: String
    /// The device's own 32-byte Ed25519 public key.
    public let devicePublicKey: Data
    /// When the link was issued (Unix ms).
    public let issuedAtMs: Int64
    /// 64-byte Ed25519 signature by the user's identity key over the signed body.
    public let signature: Data

    public init(deviceId: String, devicePublicKey: Data, issuedAtMs: Int64, signature: Data) {
        self.deviceId = deviceId
        self.devicePublicKey = devicePublicKey
        self.issuedAtMs = issuedAtMs
        self.signature = signature
    }
}

/// Serializes, signs and verifies ``DeviceLink`` records.
///
/// Mirrors the C# `DeviceLinkCodec`.
public enum DeviceLinkCodec {
    /// Wire format version; readers reject any other value.
    public static let formatVersion: UInt8 = 0x01

    /// The canonical signed body (everything but the signature): version ·
    /// device_id(u16 len + utf8) · device_public_key(32) · issued_at_ms(i64 LE).
    /// Signer and verifier operate over exactly these bytes.
    public static func signedBody(deviceId: String, devicePublicKey: Data, issuedAtMs: Int64) throws -> Data {
        guard devicePublicKey.count == 32 else { throw DeviceLinkError.invalidDevicePublicKeyLength }
        let id = Array(deviceId.utf8)
        if id.count > 0xFFFF { throw DeviceLinkError.deviceIdTooLong }

        var body = Data()
        body.reserveCapacity(1 + 2 + id.count + 32 + 8)
        body.append(formatVersion)
        appendU16(&body, id.count)
        body.append(contentsOf: id)
        body.append(devicePublicKey)
        appendI64(&body, issuedAtMs)
        return body
    }

    /// Creates a device-link signed by the user's 32-byte Ed25519 identity
    /// private key (seed).
    public static func create(
        deviceId: String,
        devicePublicKey: Data,
        issuedAtMs: Int64,
        identitySeed: Data
    ) throws -> DeviceLink {
        let body = try signedBody(deviceId: deviceId, devicePublicKey: devicePublicKey, issuedAtMs: issuedAtMs)
        let signature = try Ed25519Service.sign(identitySeed, body)
        return DeviceLink(
            deviceId: deviceId,
            devicePublicKey: devicePublicKey,
            issuedAtMs: issuedAtMs,
            signature: signature)
    }

    /// True if `link` was signed by the identity behind `identityPublicKey` —
    /// i.e. this device belongs to that user.
    public static func verify(_ link: DeviceLink, identityPublicKey: Data) -> Bool {
        guard link.signature.count == 64 else { return false }
        guard link.devicePublicKey.count == 32 else { return false }
        guard let body = try? signedBody(
            deviceId: link.deviceId,
            devicePublicKey: link.devicePublicKey,
            issuedAtMs: link.issuedAtMs)
        else { return false }
        return Ed25519Service.verify(identityPublicKey, body, link.signature)
    }

    /// Serializes a link as its signed body followed by the 64-byte signature.
    public static func serialize(_ link: DeviceLink) throws -> Data {
        guard link.signature.count == 64 else { throw DeviceLinkError.invalidSignatureLength }
        var out = try signedBody(
            deviceId: link.deviceId,
            devicePublicKey: link.devicePublicKey,
            issuedAtMs: link.issuedAtMs)
        out.append(link.signature)
        return out
    }

    /// Parses a serialized link, validating framing.
    public static func deserialize(_ data: Data) throws -> DeviceLink {
        let d = Array(data)
        var o = 0

        if d.count < 1 + 2 + 32 + 8 + 64 { throw DeviceLinkError.tooShort }
        if d[o] != formatVersion { throw DeviceLinkError.unsupportedVersion(d[o]) }
        o += 1

        let idLen = readU16(d, &o)
        if o + idLen + 32 + 8 + 64 > d.count { throw DeviceLinkError.truncated }
        let deviceId = String(decoding: d[o..<o + idLen], as: UTF8.self); o += idLen
        let devicePublicKey = Data(d[o..<o + 32]); o += 32
        let issuedAtMs = readI64(d, &o)
        let signature = Data(d[o..<o + 64]); o += 64

        return DeviceLink(
            deviceId: deviceId,
            devicePublicKey: devicePublicKey,
            issuedAtMs: issuedAtMs,
            signature: signature)
    }

    // MARK: - primitives (little-endian, mirroring DtnEnvelope)

    private static func appendU16(_ out: inout Data, _ v: Int) {
        out.append(UInt8(v & 0xff))
        out.append(UInt8((v >> 8) & 0xff))
    }

    private static func appendI64(_ out: inout Data, _ v: Int64) {
        let u = UInt64(bitPattern: v)
        for i in 0..<8 { out.append(UInt8((u >> (8 * i)) & 0xff)) }
    }

    private static func readU16(_ d: [UInt8], _ o: inout Int) -> Int {
        let v = Int(d[o]) | (Int(d[o + 1]) << 8)
        o += 2
        return v
    }

    private static func readI64(_ d: [UInt8], _ o: inout Int) -> Int64 {
        var u: UInt64 = 0
        for i in 0..<8 { u |= UInt64(d[o + i]) << (8 * i) }
        o += 8
        return Int64(bitPattern: u)
    }
}
