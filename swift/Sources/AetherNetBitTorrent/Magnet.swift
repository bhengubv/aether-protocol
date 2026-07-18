// SPDX-License-Identifier: MIT

import Foundation

/// A parsed magnet: URI (BEP-9 xt=urn:btih:).
public struct MagnetLink {
    public let infoHash: [UInt8]        // 20 bytes
    public let displayName: String
    public let trackers: [String]

    /// Lowercase hex of the info-hash (40 chars).
    public var infoHashHex: String { BTHex.encode(infoHash) }
}

public enum MagnetError: Error, Equatable {
    case invalid(String)
}

/// Parses a magnet URI, accepting a 40-char hex or 32-char base32 info-hash.
public func parseMagnet(_ uri: String) throws -> MagnetLink {
    let prefix = "magnet:?"
    guard uri.hasPrefix(prefix) else {
        throw MagnetError.invalid("not a magnet URI")
    }
    let query = String(uri.dropFirst(prefix.count))
    let values = parseQuery(query)

    var hash: [UInt8]? = nil
    for xt in values["xt"] ?? [] {
        let btih = "urn:btih:"
        if xt.hasPrefix(btih) {
            hash = try decodeInfoHash(String(xt.dropFirst(btih.count)))
            break
        }
    }
    guard let h = hash else {
        throw MagnetError.invalid("magnet has no xt=urn:btih: topic")
    }

    return MagnetLink(
        infoHash: h,
        displayName: values["dn"]?.first ?? "",
        trackers: values["tr"] ?? []
    )
}

private func decodeInfoHash(_ s: String) throws -> [UInt8] {
    switch s.count {
    case 40:
        let bytes = BTHex.decode(s)
        // Validate the input was well-formed hex (BTHex.decode is lenient).
        if bytes.count != 20 || !s.allSatisfy({ $0.isHexDigit }) {
            throw MagnetError.invalid("invalid hex info-hash")
        }
        return bytes
    case 32:
        guard let bytes = base32Decode(s.uppercased()), bytes.count == 20 else {
            throw MagnetError.invalid("invalid base32 info-hash")
        }
        return bytes
    default:
        throw MagnetError.invalid("info-hash must be 40 hex or 32 base32 chars, got \(s.count)")
    }
}

/// Minimal application/x-www-form-urlencoded query parser: splits on '&', then '='
/// once; '+' decodes to space and %XX percent-escapes are resolved. Returns a
/// key → [value] multimap (matching Go's url.Values).
private func parseQuery(_ query: String) -> [String: [String]] {
    var out: [String: [String]] = [:]
    guard !query.isEmpty else { return out }
    for pair in query.split(separator: "&", omittingEmptySubsequences: true) {
        let kv = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
        let key = percentDecode(String(kv[0]))
        let value = kv.count > 1 ? percentDecode(String(kv[1])) : ""
        out[key, default: []].append(value)
    }
    return out
}

private func percentDecode(_ s: String) -> String {
    let plusReplaced = s.replacingOccurrences(of: "+", with: " ")
    return plusReplaced.removingPercentEncoding ?? plusReplaced
}

/// RFC 4648 base32 decode (no padding), matching Go's
/// base32.StdEncoding.WithPadding(NoPadding).
private func base32Decode(_ s: String) -> [UInt8]? {
    let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".utf8)
    var lookup = [Int](repeating: -1, count: 256)
    for (i, c) in alphabet.enumerated() { lookup[Int(c)] = i }

    var bits = 0
    var buffer = 0
    var out: [UInt8] = []
    for ch in s.utf8 {
        let v = lookup[Int(ch)]
        if v < 0 { return nil }
        buffer = (buffer << 5) | v
        bits += 5
        if bits >= 8 {
            bits -= 8
            out.append(UInt8((buffer >> bits) & 0xFF))
        }
    }
    return out
}
