// SPDX-License-Identifier: MIT

import CommonCrypto
import Crypto
import Foundation

/// Bluetooth-LE tracking protection: a rotating Service UUID and IRK-based
/// Resolvable Private Addresses (RPA), so a mesh node is discoverable by its
/// peers without exposing a stable, trackable Bluetooth fingerprint on the air.
///
/// - The Service UUID rotates every 15 minutes, HMAC-SHA256-derived from a
///   shared rotation key and the current time window. Every node in the same
///   window derives the same UUID, so peers still find each other — but a
///   passive scanner sees an identifier that changes and cannot be linked over
///   time.
/// - The node's stable id is removed from the advertisement; a peer that holds
///   the node's 128-bit Identity Resolving Key (IRK) resolves its rotating
///   6-byte RPA instead (the BLE "ah" function).
///
/// The window-based operations are deterministic and byte-identical across every
/// AetherNet SDK (verified against `fixtures/bleprivacy/vectors.json`). The time
/// window is encoded as a little-endian Int64.
///
/// Mirrors `AetherNet.Security.Privacy.BlePrivacy` (C#) byte-for-byte.
public enum BlePrivacy {

    /// Rotation period in seconds (15 minutes).
    public static let rotationSeconds: Int = 900

    /// The rotation window index for a Unix-seconds timestamp.
    public static func windowFor(_ unixSeconds: Int64) -> Int64 {
        unixSeconds / Int64(rotationSeconds)
    }

    /// The rotating BLE Service UUID for a rotation key and time window. Every
    /// node sharing the rotation key derives the same UUID within the window,
    /// enabling mutual discovery with no static identifier on the air.
    ///
    /// `mac = HMAC-SHA256(rotationKey, le64(window))`; the first 16 bytes are
    /// formatted as a lowercase canonical UUID string.
    public static func serviceUuid(_ rotationKey: Data, window: Int64) -> String {
        let mac = hmacSha256(key: rotationKey, message: windowBytes(window))
        return formatUuid(mac.prefix(16))
    }

    /// A 6-byte Resolvable Private Address for a 16-byte IRK and time window:
    /// `hash(3) || prand(3)`, where prand is HMAC-derived (with the RPA
    /// address-type bits set) and `hash = AES-128(IRK, prand-block)`. Rotates
    /// every window; only a peer holding the IRK can link successive addresses.
    ///
    /// - Throws: ``BlePrivacyError/invalidIrkLength`` if `irk` is not 16 bytes.
    public static func resolvableAddress(_ irk: Data, window: Int64) throws -> Data {
        guard irk.count == 16 else {
            throw BlePrivacyError.invalidIrkLength(irk.count)
        }

        // prand = HMAC-SHA256(irk, le64(window))[0..<3]; set RPA address-type bits (0b01).
        var prand = Data(hmacSha256(key: irk, message: windowBytes(window)).prefix(3))
        prand[prand.startIndex] = (prand[prand.startIndex] & 0x3F) | 0x40

        let hash = try ah(irk: irk, prand: prand) // 3 bytes

        var rpa = Data(capacity: 6)
        rpa.append(hash)  // bytes 0..<3
        rpa.append(prand) // bytes 3..<6
        return rpa
    }

    /// True if `rpa` was generated from `irk` — i.e. this node recognises the
    /// peer behind the rotating address. Returns false (never throws) for any
    /// malformed input.
    public static func resolveAddress(_ irk: Data, rpa: Data) -> Bool {
        guard irk.count == 16, rpa.count == 6 else { return false }

        // prand is the trailing 3 bytes of the RPA.
        let prand = Data(rpa.suffix(3))
        guard let hash = try? ah(irk: irk, prand: prand) else { return false }
        return hash.elementsEqual(rpa.prefix(3))
    }

    // MARK: - Private

    /// BLE "ah" hash: AES-128-ECB(irk, 0^13 || prand), keep the first 3 bytes.
    ///
    /// swift-crypto exposes no AES-ECB, so this uses Apple's CommonCrypto for
    /// the single fixed-size block. No padding; a 16-byte key selects AES-128.
    private static func ah(irk: Data, prand: Data) throws -> Data {
        precondition(prand.count == 3, "prand must be 3 bytes")

        var block = [UInt8](repeating: 0, count: 16)
        // block[13..<16] = prand
        for (i, byte) in prand.enumerated() {
            block[13 + i] = byte
        }

        let key = [UInt8](irk)
        var out = [UInt8](repeating: 0, count: 16)
        var moved = 0

        let status = key.withUnsafeBytes { keyPtr in
            block.withUnsafeBytes { blockPtr in
                out.withUnsafeMutableBytes { outPtr in
                    CCCrypt(
                        CCOperation(kCCEncrypt),
                        CCAlgorithm(kCCAlgorithmAES),
                        CCOptions(kCCOptionECBMode), // ECB, no padding
                        keyPtr.baseAddress, 16,      // key, keyLength 16 -> AES-128
                        nil,                          // no IV in ECB mode
                        blockPtr.baseAddress, 16,     // single input block
                        outPtr.baseAddress, 16,       // output buffer
                        &moved
                    )
                }
            }
        }

        guard status == CCCryptorStatus(kCCSuccess), moved >= 3 else {
            throw BlePrivacyError.aesEcbFailed(Int(status))
        }
        return Data(out.prefix(3))
    }

    /// HMAC-SHA256 over `message` keyed by `key`, matching the C#
    /// `HMACSHA256.HashData(key, message)`.
    private static func hmacSha256(key: Data, message: Data) -> Data {
        let mac = HMAC<SHA256>.authenticationCode(
            for: message,
            using: SymmetricKey(data: key)
        )
        return Data(mac)
    }

    /// The time window encoded as a little-endian Int64 (8 bytes) — the HMAC
    /// input, matching `BinaryPrimitives.WriteInt64LittleEndian`.
    private static func windowBytes(_ window: Int64) -> Data {
        var le = UInt64(bitPattern: window).littleEndian
        return withUnsafeBytes(of: &le) { Data($0) }
    }

    /// Formats 16 bytes as a lowercase canonical UUID string
    /// `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` (byte groups 0-3, 4-5, 6-7, 8-9,
    /// 10-15), matching the C# `FormatUuid`.
    private static func formatUuid<S: Sequence>(_ bytes: S) -> String where S.Element == UInt8 {
        let hex = bytes.map { String(format: "%02x", $0) }
        precondition(hex.count == 16, "UUID requires 16 bytes")
        let groups = [
            hex[0..<4].joined(),
            hex[4..<6].joined(),
            hex[6..<8].joined(),
            hex[8..<10].joined(),
            hex[10..<16].joined()
        ]
        return groups.joined(separator: "-")
    }
}

/// Errors raised by ``BlePrivacy``.
public enum BlePrivacyError: Error, Equatable {
    /// The supplied IRK was not exactly 16 bytes.
    case invalidIrkLength(Int)
    /// The CommonCrypto AES-128-ECB block operation failed (CCCryptorStatus).
    case aesEcbFailed(Int)
}
