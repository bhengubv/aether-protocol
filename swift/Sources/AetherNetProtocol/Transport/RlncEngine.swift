// SPDX-License-Identifier: MIT
// RLNC Engine — Random Linear Network Coding over GF(2⁸).
//
// Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1 (0x11D — same as AES Rijndael).
//
// Components
// ──────────
//   GF256          — GF(2⁸) log/exp tables and arithmetic helpers.
//   RlncEncoder    — systematic + random-repair packet generation.
//   RlncDecoder    — incremental Gauss-Jordan elimination.
//   RlncCodec      — FecCodec adapter (implements FecCodec protocol).
//
// Wire format per packet:
//   [ K coefficient bytes ][ symbolSize data bytes ]

import Foundation

// ── GF(2⁸) arithmetic ─────────────────────────────────────────────────────────

private let GF256_EXP: [UInt8] = {
    var exp = [UInt8](repeating: 0, count: 512)
    var x   = 1
    for i in 0 ..< 255 {
        exp[i] = UInt8(x)
        x <<= 1
        if x & 0x100 != 0 { x ^= 0x11D }
        x &= 0xFF
    }
    for i in 255 ..< 512 { exp[i] = exp[i - 255] }
    return exp
}()

private let GF256_LOG: [UInt8] = {
    var log = [UInt8](repeating: 0, count: 256)
    var x   = 1
    for i in 0 ..< 255 {
        log[x] = UInt8(i)
        x <<= 1
        if x & 0x100 != 0 { x ^= 0x11D }
        x &= 0xFF
    }
    log[1] = 0 // log_α(1) = 0
    return log
}()

@inline(__always) private func gf256Mul(_ a: UInt8, _ b: UInt8) -> UInt8 {
    guard a != 0, b != 0 else { return 0 }
    return GF256_EXP[Int(GF256_LOG[Int(a)]) + Int(GF256_LOG[Int(b)])]
}

@inline(__always) private func gf256Inv(_ a: UInt8) -> UInt8 {
    precondition(a != 0, "rlnc: GF256 inverse of zero")
    return GF256_EXP[255 - Int(GF256_LOG[Int(a)])]
}

@inline(__always) private func gf256Add(_ a: UInt8, _ b: UInt8) -> UInt8 { a ^ b }

// ── RlncEncoder ───────────────────────────────────────────────────────────────

/// Encodes K source symbols as systematic + random-repair RLNC packets.
///
/// The first `generationSize` packets are systematic (identity coefficient vectors;
/// byte-identical to the source symbols).  Subsequent packets use random GF(2⁸)
/// coefficients.
public final class RlncEncoder {

    private let source:     [[UInt8]]
    private var nextIndex = 0
    private let systematic: Bool

    /// Number of source symbols in this generation.
    public var generationSize: Int { source.count }
    /// Byte length of each source symbol.
    public var symbolSize:     Int { source[0].count }

    /// - Parameters:
    ///   - source:     Array of K byte arrays, each `symbolSize` bytes.
    ///   - systematic: When `true` (default), first K packets carry identity
    ///                 coefficients and are byte-identical to the source.
    public init(source: [[UInt8]], systematic: Bool = true) {
        precondition(!source.isEmpty, "rlnc: source must have at least one symbol")
        self.source     = source
        self.systematic = systematic
    }

    /// Returns `(coefficients, encodedSymbol)` for the next packet.
    public func nextPacket() -> (coefficients: [UInt8], encodedSymbol: [UInt8]) {
        let k = generationSize
        let s = symbolSize
        let coefficients: [UInt8]
        let encodedSymbol: [UInt8]

        if systematic && nextIndex < k {
            var coeff = [UInt8](repeating: 0, count: k)
            coeff[nextIndex] = 1
            coefficients  = coeff
            encodedSymbol = source[nextIndex]
        } else {
            var coeff = [UInt8](repeating: 0, count: k)
            // Fill with cryptographically random bytes.
            var rng = SystemRandomNumberGenerator()
            for i in 0..<k { coeff[i] = rng.next() }
            if coeff.allSatisfy({ $0 == 0 }) { coeff[0] = 1 }
            coefficients  = coeff
            encodedSymbol = encodeSymbol(coeff)
        }

        nextIndex += 1
        return (coefficients, encodedSymbol)
    }

    private func encodeSymbol(_ coefficients: [UInt8]) -> [UInt8] {
        let s = symbolSize
        var out = [UInt8](repeating: 0, count: s)
        for (kIdx, sym) in source.enumerated() {
            let c = coefficients[kIdx]
            if c == 0 { continue }
            for i in 0 ..< s {
                out[i] = gf256Add(out[i], gf256Mul(c, sym[i]))
            }
        }
        return out
    }
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

/// Incremental Gauss-Jordan decoder over GF(2⁸).
///
/// Maintains the accumulated coefficient matrix in RREF as packets arrive.
/// Decoding is immediate when `rank` equals `generationSize`.
public final class RlncDecoder {

    public let generationSize: Int
    public let symbolSize:     Int

    private var pivotCoeff: [[UInt8]?]
    private var pivotData:  [[UInt8]?]
    private var _rank = 0

    /// Number of linearly independent packets received.
    public var rank:       Int  { _rank }
    /// `true` when all K source symbols can be reconstructed.
    public var isComplete: Bool { _rank == generationSize }

    public init(generationSize: Int, symbolSize: Int) {
        self.generationSize = generationSize
        self.symbolSize     = symbolSize
        self.pivotCoeff     = [[UInt8]?](repeating: nil, count: generationSize)
        self.pivotData      = [[UInt8]?](repeating: nil, count: generationSize)
    }

    /// Submit an encoded packet. Returns `true` if rank increased.
    @discardableResult
    public func addPacket(coefficients: [UInt8], encodedSymbol: [UInt8]) -> Bool {
        let k = generationSize
        let s = symbolSize
        var row  = coefficients
        var data = encodedSymbol

        // ── Forward-elimination ──────────────────────────────────────────────
        for j in 0 ..< k {
            guard row[j] != 0, let pr = pivotCoeff[j], let pd = pivotData[j] else { continue }
            let c = row[j]
            for i in 0 ..< k { row[i]  = gf256Add(row[i],  gf256Mul(c, pr[i])) }
            for i in 0 ..< s { data[i] = gf256Add(data[i], gf256Mul(c, pd[i])) }
        }

        // ── Find pivot column ────────────────────────────────────────────────
        guard let pivotCol = (0 ..< k).first(where: { row[$0] != 0 }) else { return false }

        // ── Normalise ────────────────────────────────────────────────────────
        let inv = gf256Inv(row[pivotCol])
        for i in 0 ..< k { row[i]  = gf256Mul(inv, row[i]) }
        for i in 0 ..< s { data[i] = gf256Mul(inv, data[i]) }

        // ── Back-substitution ────────────────────────────────────────────────
        for r in 0 ..< k {
            guard var pr = pivotCoeff[r], var pd = pivotData[r] else { continue }
            let c = pr[pivotCol]
            if c == 0 { continue }
            for i in 0 ..< k { pr[i] = gf256Add(pr[i], gf256Mul(c, row[i])) }
            for i in 0 ..< s { pd[i] = gf256Add(pd[i], gf256Mul(c, data[i])) }
            pivotCoeff[r] = pr
            pivotData[r]  = pd
        }

        pivotCoeff[pivotCol] = row
        pivotData[pivotCol]  = data
        _rank += 1
        return true
    }

    /// Returns decoded source bytes when `isComplete`, or `nil` otherwise.
    public func tryDecode() -> [UInt8]? {
        guard isComplete else { return nil }
        var result = [UInt8](repeating: 0, count: generationSize * symbolSize)
        for j in 0 ..< generationSize {
            let base = j * symbolSize
            for i in 0 ..< symbolSize { result[base + i] = pivotData[j]![i] }
        }
        return result
    }
}

// ── RlncCodec : FecCodec ──────────────────────────────────────────────────────

/// `FecCodec` adapter for RLNC over GF(2⁸).
///
/// Each encoded packet is `[ K coefficient bytes ][ symbolSize data bytes ]`.
public final class RlncCodec: FecCodec {

    private let k: Int

    public init(generationSize: Int = 16) {
        precondition((1...255).contains(generationSize), "rlnc: generationSize must be in [1, 255]")
        self.k = generationSize
    }

    public var codecName:            String { "RLNC-GF256" }
    public var deviceTierRequired:   UInt8  { 0 }
    public var overheadFraction:     Double { 0.05 }
    public var fixedSymbolSizeBytes: Int    { 0 }

    public func encode(source: Data, targetSymbolCount: Int) throws -> Data {
        precondition(!source.isEmpty, "rlnc: source must not be empty")
        let sourceBytes = [UInt8](source)
        let symbolSize  = (sourceBytes.count + k - 1) / k
        let packetSize  = k + symbolSize
        let symbols     = splitIntoSymbols(sourceBytes, symbolSize: symbolSize)
        let enc         = RlncEncoder(source: symbols, systematic: true)
        var output      = [UInt8](repeating: 0, count: targetSymbolCount * packetSize)

        for i in 0 ..< targetSymbolCount {
            let (coeff, data) = enc.nextPacket()
            let offset        = i * packetSize
            for j in 0 ..< k           { output[offset + j]     = coeff[j] }
            for j in 0 ..< symbolSize  { output[offset + k + j] = data[j] }
        }
        return Data(output)
    }

    public func tryDecode(receivedSymbols: [Data], sourceSymbolCount: Int) -> Data? {
        guard !receivedSymbols.isEmpty else { return nil }
        let symbolSize = receivedSymbols[0].count - k
        guard symbolSize > 0 else { return nil }

        let dec = RlncDecoder(generationSize: k, symbolSize: symbolSize)
        for pkt in receivedSymbols {
            let bytes = [UInt8](pkt)
            dec.addPacket(
                coefficients:  Array(bytes.prefix(k)),
                encodedSymbol: Array(bytes.dropFirst(k))
            )
            if dec.isComplete { break }
        }

        guard let decoded = dec.tryDecode() else { return nil }
        return Data(decoded)
    }

    private func splitIntoSymbols(_ source: [UInt8], symbolSize: Int) -> [[UInt8]] {
        (0 ..< k).map { i in
            var sym    = [UInt8](repeating: 0, count: symbolSize)
            let offset = i * symbolSize
            let length = min(symbolSize, source.count - offset)
            if length > 0 {
                for j in 0 ..< length { sym[j] = source[offset + j] }
            }
            return sym
        }
    }
}
