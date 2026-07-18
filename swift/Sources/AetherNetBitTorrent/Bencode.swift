// SPDX-License-Identifier: MIT

// AetherNetBitTorrent is a from-scratch, interoperable BitTorrent implementation
// (BEP-3 and friends) — the Swift port of the C# reference in src/AetherNet.BitTorrent
// and the Go reference in go/bittorrent. Encoded bytes and hashes are byte-identical
// to every other AetherNet language SDK; the fixture corpus in
// fixtures/bittorrent/vectors.json is the cross-language gate.

import Foundation

/// Any BEP-3 bencoding violation (leading zeros, negative zero, duplicate/unsorted
/// keys on decode, trailing data, overflow, …) plus the typed-accessor mismatches.
public enum BencodeError: Error, Equatable {
    case decode(String)
    case notInteger
    case notByteString
    case notList
    case notDict
    case duplicateKey(String)
}

/// A decoded bencode value: an integer, a byte string, a list, or a dictionary.
/// Byte strings hold raw bytes — they are NOT necessarily text.
public indirect enum BencodeValue {
    case int(Int64)
    case bytes([UInt8])
    case list([BencodeValue])
    case dict(BDict)

    /// Byte string from UTF-8 text (Go's `BStr(string)`).
    public static func text(_ s: String) -> BencodeValue { .bytes(Array(s.utf8)) }

    // ── typed accessors (mirror Go's AsInt/AsBytes/AsText/AsList/AsDict) ─────────

    public func intValue() throws -> Int64 {
        if case .int(let n) = self { return n }
        throw BencodeError.notInteger
    }

    public func bytesValue() throws -> [UInt8] {
        if case .bytes(let b) = self { return b }
        throw BencodeError.notByteString
    }

    public func textValue() throws -> String {
        String(decoding: try bytesValue(), as: UTF8.self)
    }

    public func listValue() throws -> [BencodeValue] {
        if case .list(let l) = self { return l }
        throw BencodeError.notList
    }

    public func dictValue() throws -> BDict {
        if case .dict(let d) = self { return d }
        throw BencodeError.notDict
    }
}

/// A bencode dictionary: keys are raw byte strings, unique, emitted sorted by raw
/// (unsigned) byte order per BEP-3. Insertion order is preserved for iteration; the
/// encoder sorts independently.
public final class BDict {
    public private(set) var orderedKeys: [[UInt8]] = []
    public private(set) var values: [BencodeValue] = []
    private var lookup: [[UInt8]: Int] = [:]

    public init() {}

    /// Inserts a key/value, rejecting duplicate keys. Key is UTF-8 encoded.
    @discardableResult
    public func add(_ key: String, _ value: BencodeValue) throws -> BDict {
        try addRaw(Array(key.utf8), value)
        return self
    }

    /// Inserts a raw-byte key/value, rejecting duplicates (used by the decoder for
    /// keys that may not be valid UTF-8).
    func addRaw(_ key: [UInt8], _ value: BencodeValue) throws {
        if lookup[key] != nil {
            throw BencodeError.duplicateKey(String(decoding: key, as: UTF8.self))
        }
        lookup[key] = orderedKeys.count
        orderedKeys.append(key)
        values.append(value)
    }

    /// Returns the value for a key, or nil if absent.
    public func get(_ key: String) -> BencodeValue? {
        guard let i = lookup[Array(key.utf8)] else { return nil }
        return values[i]
    }

    /// The number of entries.
    public var count: Int { orderedKeys.count }

    /// The dictionary keys in insertion order, interpreted as UTF-8.
    public var keys: [String] {
        orderedKeys.map { String(decoding: $0, as: UTF8.self) }
    }
}

// ── encode ──────────────────────────────────────────────────────────────────

/// Returns the canonical bencoding of `v` (dictionary keys sorted by raw byte order).
public func bencodeEncode(_ v: BencodeValue) -> [UInt8] {
    var out: [UInt8] = []
    encodeInto(v, &out)
    return out
}

private let asciiI = UInt8(ascii: "i")
private let asciiL = UInt8(ascii: "l")
private let asciiD = UInt8(ascii: "d")
private let asciiE = UInt8(ascii: "e")
private let asciiColon = UInt8(ascii: ":")
private let asciiMinus = UInt8(ascii: "-")
private let ascii0 = UInt8(ascii: "0")
private let ascii9 = UInt8(ascii: "9")

private func encodeInto(_ v: BencodeValue, _ out: inout [UInt8]) {
    switch v {
    case .int(let n):
        out.append(asciiI)
        out.append(contentsOf: Array(String(n).utf8))
        out.append(asciiE)
    case .bytes(let b):
        out.append(contentsOf: Array(String(b.count).utf8))
        out.append(asciiColon)
        out.append(contentsOf: b)
    case .list(let items):
        out.append(asciiL)
        for item in items { encodeInto(item, &out) }
        out.append(asciiE)
    case .dict(let d):
        out.append(asciiD)
        let order = Array(0..<d.orderedKeys.count).sorted {
            compareBytes(d.orderedKeys[$0], d.orderedKeys[$1]) < 0
        }
        for idx in order {
            let k = d.orderedKeys[idx]
            out.append(contentsOf: Array(String(k.count).utf8))
            out.append(asciiColon)
            out.append(contentsOf: k)
            encodeInto(d.values[idx], &out)
        }
        out.append(asciiE)
    }
}

/// Unsigned byte-lexicographic comparison, matching Go's `bytes.Compare`.
func compareBytes(_ a: [UInt8], _ b: [UInt8]) -> Int {
    let n = min(a.count, b.count)
    var i = 0
    while i < n {
        if a[i] != b[i] { return a[i] < b[i] ? -1 : 1 }
        i += 1
    }
    if a.count == b.count { return 0 }
    return a.count < b.count ? -1 : 1
}

// ── decode ──────────────────────────────────────────────────────────────────

/// Parses a single bencode value and rejects any trailing data.
public func bencodeDecode(_ data: [UInt8]) throws -> BencodeValue {
    let (v, n) = try decodeValue(data, 0)
    if n != data.count {
        throw BencodeError.decode("\(data.count - n) trailing byte(s) after value")
    }
    return v
}

/// Parses one bencode value starting at `start`, returning it and the absolute index
/// one past its last byte (equivalent to Go's DecodeN "bytes consumed").
public func bencodeDecodeN(_ data: [UInt8], _ start: Int) throws -> (BencodeValue, Int) {
    try decodeValue(data, start)
}

func decodeValue(_ data: [UInt8], _ start: Int) throws -> (BencodeValue, Int) {
    if start >= data.count {
        throw BencodeError.decode("empty input")
    }
    let c = data[start]
    switch c {
    case asciiI: return try decodeInt(data, start)
    case asciiL: return try decodeList(data, start)
    case asciiD: return try decodeDict(data, start)
    case ascii0...ascii9: return try decodeString(data, start)
    default:
        throw BencodeError.decode("unexpected byte 0x\(BTHex.encode([c]))")
    }
}

private func indexOf(_ data: [UInt8], _ target: UInt8, from: Int) -> Int? {
    var i = from
    while i < data.count {
        if data[i] == target { return i }
        i += 1
    }
    return nil
}

private func decodeInt(_ data: [UInt8], _ start: Int) throws -> (BencodeValue, Int) {
    guard let end = indexOf(data, asciiE, from: start) else {
        throw BencodeError.decode("integer has no terminating 'e'")
    }
    let bodyBytes = Array(data[(start + 1)..<end])
    if bodyBytes.isEmpty {
        throw BencodeError.decode("empty integer")
    }
    if bodyBytes == [asciiMinus, ascii0] {
        throw BencodeError.decode("negative zero is not allowed")
    }
    var digits = bodyBytes
    if digits.first == asciiMinus {
        digits.removeFirst()
        if digits.isEmpty {
            throw BencodeError.decode("bare minus sign")
        }
    }
    if digits.count > 1 && digits[0] == ascii0 {
        throw BencodeError.decode("integer has a leading zero")
    }
    for ch in digits where ch < ascii0 || ch > ascii9 {
        throw BencodeError.decode("integer has a non-digit")
    }
    let body = String(decoding: bodyBytes, as: UTF8.self)
    guard let val = Int64(body) else {
        throw BencodeError.decode("integer overflow: \(body)")
    }
    return (.int(val), end + 1)
}

private func decodeString(_ data: [UInt8], _ start: Int) throws -> (BencodeValue, Int) {
    guard let colon = indexOf(data, asciiColon, from: start) else {
        throw BencodeError.decode("byte string has no ':'")
    }
    let lenBytes = Array(data[start..<colon])
    if lenBytes.isEmpty {
        throw BencodeError.decode("byte string has an empty length")
    }
    if lenBytes.count > 1 && lenBytes[0] == ascii0 {
        throw BencodeError.decode("byte-string length has a leading zero")
    }
    for ch in lenBytes where ch < ascii0 || ch > ascii9 {
        throw BencodeError.decode("byte-string length has a non-digit")
    }
    guard let n = Int(String(decoding: lenBytes, as: UTF8.self)) else {
        throw BencodeError.decode("byte-string length overflow")
    }
    let dataStart = colon + 1
    if dataStart + n > data.count {
        throw BencodeError.decode("byte string runs past end of input")
    }
    let out = Array(data[dataStart..<(dataStart + n)])
    return (.bytes(out), dataStart + n)
}

private func decodeList(_ data: [UInt8], _ start: Int) throws -> (BencodeValue, Int) {
    var pos = start + 1
    var list: [BencodeValue] = []
    while true {
        if pos >= data.count {
            throw BencodeError.decode("list has no terminating 'e'")
        }
        if data[pos] == asciiE {
            return (.list(list), pos + 1)
        }
        let (item, n) = try decodeValue(data, pos)
        list.append(item)
        pos = n
    }
}

private func decodeDict(_ data: [UInt8], _ start: Int) throws -> (BencodeValue, Int) {
    var pos = start + 1
    let d = BDict()
    var prevKey: [UInt8]? = nil
    while true {
        if pos >= data.count {
            throw BencodeError.decode("dictionary has no terminating 'e'")
        }
        if data[pos] == asciiE {
            return (.dict(d), pos + 1)
        }
        let (keyVal, n) = try decodeString(data, pos)
        guard case .bytes(let key) = keyVal else {
            throw BencodeError.decode("dictionary key must be a byte string")
        }
        pos = n
        if let pk = prevKey {
            let cmp = compareBytes(pk, key)
            if cmp == 0 {
                throw BencodeError.decode("duplicate dictionary key")
            }
            if cmp > 0 {
                throw BencodeError.decode("dictionary keys are not sorted")
            }
        }
        prevKey = key
        if pos >= data.count {
            throw BencodeError.decode("dictionary key without a value")
        }
        let (valVal, n2) = try decodeValue(data, pos)
        pos = n2
        try d.addRaw(key, valVal)
    }
}
