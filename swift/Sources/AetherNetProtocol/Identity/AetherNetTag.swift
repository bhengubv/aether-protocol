// SPDX-License-Identifier: MIT

/// Human-readable identity address derived from a node's Ed25519 public key.
///
/// Algorithm:
///   1. SHA-256(publicKey) → 32-byte hash
///   2. Extract first 50 bits from bytes 0-6 (48 from bytes 0-5, top 2 from byte 6)
///   3. Encode as 10 Crockford base-32 characters (5 bits each, MSB first)
///   4. Format as "XXXXX-XXXXX"
///
/// Crockford alphabet: "0123456789ABCDEFGHJKMNPQRSTVWXYZ" (omits I, L, O, U)
///
/// Bit packing (UInt64):
///   bits = (hash[0]<<42)|(hash[1]<<34)|(hash[2]<<26)|(hash[3]<<18)|(hash[4]<<10)|(hash[5]<<2)|(hash[6]>>6)
public struct AetherNetTag: Equatable, Hashable, CustomStringConvertible {

    // MARK: - Errors

    public enum AetherNetTagError: Error, Equatable {
        case invalidPublicKeyLength(Int)
        case invalidLength(Int)
        case invalidCharacter(Character)
    }

    // MARK: - Constants

    private static let alphabet: [UInt8] =
        Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ".utf8)

    /// Reverse lookup: ASCII code → 5-bit value, or 0xFF for invalid.
    private static let decodeTable: [UInt8] = {
        var table = [UInt8](repeating: 0xFF, count: 128)
        let alpha = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
        for (value, scalar) in alpha.unicodeScalars.enumerated() {
            let code = Int(scalar.value)
            table[code] = UInt8(value)
            // Accept lowercase
            if code >= 65 && code <= 90 {          // 'A'–'Z'
                table[code + 32] = UInt8(value)    // 'a'–'z'
            }
        }
        return table
    }()

    // MARK: - Storage

    /// Canonical tag in "XXXXX-XXXXX" form (all uppercase, 11 chars).
    public let value: String

    private init(value: String) {
        self.value = value
    }

    // MARK: - Factory

    /// Derives an AetherNetTag from a 32-byte Ed25519 public key.
    /// - Parameter publicKey: Exactly 32 bytes.
    /// - Throws: ``AetherNetTagError/invalidPublicKeyLength(_:)`` when not 32 bytes.
    public static func fromPublicKey(_ publicKey: [UInt8]) throws -> AetherNetTag {
        guard publicKey.count == 32 else {
            throw AetherNetTagError.invalidPublicKeyLength(publicKey.count)
        }
        let hash = sha256(publicKey)
        return AetherNetTag(value: encode(hash))
    }

    // MARK: - Parsing

    /// Parses a tag string, case-insensitive, with or without the hyphen separator.
    /// - Throws: ``AetherNetTagError`` on invalid format or characters.
    public static func parse(_ tag: String) throws -> AetherNetTag {
        // Foundation-free: filter hyphens and uppercase using ASCII arithmetic
        var strippedBytes = [UInt8]()
        strippedBytes.reserveCapacity(tag.utf8.count)
        for byte in tag.utf8 {
            if byte == UInt8(ascii: "-") { continue }
            // lowercase a-z → uppercase A-Z
            let up: UInt8 = (byte >= 97 && byte <= 122) ? byte - 32 : byte
            strippedBytes.append(up)
        }
        let stripped = String(decoding: strippedBytes, as: UTF8.self)

        guard stripped.count == 10 else {
            throw AetherNetTagError.invalidLength(stripped.count)
        }

        for ch in stripped {
            guard let ascii = ch.asciiValue,
                  ascii < 128,
                  decodeTable[Int(ascii)] != 0xFF else {
                throw AetherNetTagError.invalidCharacter(ch)
            }
        }

        let start = stripped.startIndex
        let mid   = stripped.index(start, offsetBy: 5)
        let canonical = String(stripped[start..<mid]) + "-" + String(stripped[mid...])
        return AetherNetTag(value: canonical)
    }

    /// Returns `nil` instead of throwing when the tag string is invalid.
    public static func tryParse(_ tag: String) -> AetherNetTag? {
        try? parse(tag)
    }

    // MARK: - Verification

    /// Returns `true` when the tag matches the one derived from `publicKey`.
    public static func verify(_ tag: String, publicKey: [UInt8]) -> Bool {
        guard let parsed  = tryParse(tag),
              let derived = try? fromPublicKey(publicKey) else {
            return false
        }
        return parsed == derived
    }

    // MARK: - Properties

    /// Always `true` for instances created by ``fromPublicKey(_:)`` or ``parse(_:)``.
    public var isValid: Bool { !value.isEmpty }

    // MARK: - CustomStringConvertible

    public var description: String { value }

    // MARK: - Private helpers

    private static func encode(_ hash: [UInt8]) -> String {
        // Pack 50 bits: bytes 0-5 contribute 8 bits each (shifts 42..2),
        // byte 6 contributes top 2 bits (>> 6).
        let bits: UInt64 =
            (UInt64(hash[0]) << 42) |
            (UInt64(hash[1]) << 34) |
            (UInt64(hash[2]) << 26) |
            (UInt64(hash[3]) << 18) |
            (UInt64(hash[4]) << 10) |
            (UInt64(hash[5]) <<  2) |
             UInt64(hash[6] >> 6)

        // Extract 10 groups of 5 bits (MSB group first), index into alphabet.
        var chars = [UInt8](repeating: 0, count: 11)
        for i in 0..<5 {
            chars[i]     = alphabet[Int((bits >> ((9 - i) * 5)) & 0x1F)]
            chars[i + 6] = alphabet[Int((bits >> ((4 - i) * 5)) & 0x1F)]
        }
        chars[5] = UInt8(ascii: "-")
        return String(decoding: chars, as: UTF8.self)
    }

    // MARK: - Pure-Swift SHA-256
    // Implements FIPS PUB 180-4 SHA-256.

    private static let k: [UInt32] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5,
        0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
        0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
        0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
        0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
        0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5,
        0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
        0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
    ]

    // swiftlint:disable identifier_name
    private static func sha256(_ message: [UInt8]) -> [UInt8] {
        var h0: UInt32 = 0x6a09e667
        var h1: UInt32 = 0xbb67ae85
        var h2: UInt32 = 0x3c6ef372
        var h3: UInt32 = 0xa54ff53a
        var h4: UInt32 = 0x510e527f
        var h5: UInt32 = 0x9b05688c
        var h6: UInt32 = 0x1f83d9ab
        var h7: UInt32 = 0x5be0cd19

        // Pre-processing: add padding
        var msg = message
        let originalBitLen = UInt64(message.count) &* 8
        msg.append(0x80)
        while msg.count % 64 != 56 {
            msg.append(0x00)
        }
        // Append original length in bits as 64-bit big-endian
        for i in stride(from: 56, through: 0, by: -8) {
            msg.append(UInt8((originalBitLen >> i) & 0xFF))
        }

        // Process each 512-bit (64-byte) chunk
        var w = [UInt32](repeating: 0, count: 64)
        for chunkStart in stride(from: 0, to: msg.count, by: 64) {
            let chunk = msg[chunkStart..<chunkStart + 64]

            for i in 0..<16 {
                let base = chunk.startIndex + i * 4
                w[i] = (UInt32(chunk[base])     << 24) |
                       (UInt32(chunk[base + 1]) << 16) |
                       (UInt32(chunk[base + 2]) <<  8) |
                        UInt32(chunk[base + 3])
            }
            for i in 16..<64 {
                let s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3)
                let s1 = rotr(w[i - 2],  17) ^ rotr(w[i - 2],  19) ^ (w[i - 2]  >> 10)
                w[i] = w[i - 16] &+ s0 &+ w[i - 7] &+ s1
            }

            var a = h0, b = h1, c = h2, d = h3
            var e = h4, f = h5, g = h6, h = h7

            for i in 0..<64 {
                let S1  = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)
                let ch  = (e & f) ^ (~e & g)
                let tmp1 = h &+ S1 &+ ch &+ k[i] &+ w[i]
                let S0  = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)
                let maj = (a & b) ^ (a & c) ^ (b & c)
                let tmp2 = S0 &+ maj

                h = g; g = f; f = e
                e = d &+ tmp1
                d = c; c = b; b = a
                a = tmp1 &+ tmp2
            }

            h0 = h0 &+ a; h1 = h1 &+ b; h2 = h2 &+ c; h3 = h3 &+ d
            h4 = h4 &+ e; h5 = h5 &+ f; h6 = h6 &+ g; h7 = h7 &+ h
        }

        // Produce the final hash
        var digest = [UInt8](repeating: 0, count: 32)
        for (idx, word) in [h0, h1, h2, h3, h4, h5, h6, h7].enumerated() {
            digest[idx * 4]     = UInt8((word >> 24) & 0xFF)
            digest[idx * 4 + 1] = UInt8((word >> 16) & 0xFF)
            digest[idx * 4 + 2] = UInt8((word >>  8) & 0xFF)
            digest[idx * 4 + 3] = UInt8( word         & 0xFF)
        }
        return digest
    }
    // swiftlint:enable identifier_name

    @inline(__always)
    private static func rotr(_ value: UInt32, _ count: UInt32) -> UInt32 {
        (value >> count) | (value << (32 - count))
    }
}
