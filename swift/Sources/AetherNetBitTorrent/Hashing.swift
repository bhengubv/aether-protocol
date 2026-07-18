// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// SHA-1 / SHA-256 over raw bytes, via swift-crypto (deterministic — byte-identical
/// to crypto/sha1 and crypto/sha256 in the Go reference and the .NET hashers in C#).
public enum BTHash {
    public static func sha1(_ bytes: [UInt8]) -> [UInt8] {
        Insecure.SHA1.hash(data: Data(bytes)).withUnsafeBytes { Array($0) }
    }

    public static func sha256(_ bytes: [UInt8]) -> [UInt8] {
        SHA256.hash(data: Data(bytes)).withUnsafeBytes { Array($0) }
    }
}

/// Lowercase-hex helpers shared by the codecs and the fixture tests.
public enum BTHex {
    public static func encode(_ bytes: [UInt8]) -> String {
        var s = ""
        s.reserveCapacity(bytes.count * 2)
        for b in bytes {
            s.append(hexDigit(b >> 4))
            s.append(hexDigit(b & 0x0F))
        }
        return s
    }

    public static func decode(_ hex: String) -> [UInt8] {
        let chars = Array(hex.utf8)
        var out: [UInt8] = []
        out.reserveCapacity(chars.count / 2)
        var i = 0
        while i + 1 < chars.count {
            let hi = nibble(chars[i])
            let lo = nibble(chars[i + 1])
            out.append((hi << 4) | lo)
            i += 2
        }
        return out
    }

    private static func hexDigit(_ v: UInt8) -> Character {
        v < 10 ? Character(UnicodeScalar(0x30 + v)) : Character(UnicodeScalar(0x61 + (v - 10)))
    }

    private static func nibble(_ c: UInt8) -> UInt8 {
        switch c {
        case 0x30...0x39: return c - 0x30
        case 0x61...0x66: return c - 0x61 + 10
        case 0x41...0x46: return c - 0x41 + 10
        default: return 0
        }
    }
}
