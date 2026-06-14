// SPDX-License-Identifier: MIT
//
// Systematic Cauchy-Reed-Solomon (K, N) erasure codec for the AetherNet vault — the production
// erasure-coding promised by the vault contract ("a file is split into K+M shards; any K shards
// reconstruct it"). Swift port of AetherNet.Vault.ReedSolomonCodec, byte-identical to the C# reference
// and every other language implementation (Go, TypeScript, etc.).
//
// FIELD: arithmetic is over GF(2⁸) with primitive polynomial x⁸+x⁴+x³+x²+1 (0x11D, the AES/Rijndael
// polynomial), α = 2 — the SAME field as the RLNC engine (RlncCodec). Identical field ⇒ identical
// parity bytes, which is what makes a parity shard scattered by one node decodable by any other node
// on the mesh.
//
// SHARD LAYOUT: the K DATA shards are SYSTEMATIC — shard i (0…K-1) is exactly
//   plaintext[i*shardSize .. i*shardSize+shardSize], zero-padded if short, shardSize = ceil(size/K)
// The M parity shards are real Cauchy-Reed-Solomon (MDS), so ANY K of the N shards reconstruct the
// original.
//
// MDS GUARANTEE: the parity rows form a Cauchy matrix C[i][j] = 1 / (x_i ⊕ y_j) over GF(256) with two
// disjoint sets of distinct field elements (y_0…y_{K-1} = 0…K-1, x_0…x_{M-1} = K…K+M-1). Every square
// submatrix of a Cauchy matrix is invertible, so stacked on the systematic K×K identity it yields a
// true MDS code: any K of the N generator rows are linearly independent ⇒ any K surviving shards
// reconstruct the original; K-1 or fewer is unrecoverable.

import Foundation

// MARK: - GF(2⁸) arithmetic — primitive polynomial 0x11D, α = 2

/// Byte-for-byte identical field to the RLNC engine's GF256 (whose tables are file-private, so the
/// table-generation logic is mirrored here rather than referenced across files — identical tables are
/// what guarantee identical parity bytes).
private enum GF256 {
    /// `exp[i] = α^i`; doubled to 512 entries so `Mul` never needs a modular wrap on the log sum.
    static let exp: [UInt8] = {
        var e = [UInt8](repeating: 0, count: 512)
        var x = 1
        for i in 0..<255 {
            e[i] = UInt8(x & 0xFF)
            x <<= 1
            if x & 0x100 != 0 { x ^= 0x11D } // reduce mod p(x) = x⁸+x⁴+x³+x²+1
        }
        for i in 255..<512 { e[i] = e[i - 255] }
        return e
    }()

    /// `log[v] = log_α(v)` for v ∈ [1, 255].
    static let log: [UInt8] = {
        var l = [UInt8](repeating: 0, count: 256)
        var x = 1
        for i in 0..<255 {
            l[x] = UInt8(i)
            x <<= 1
            if x & 0x100 != 0 { x ^= 0x11D }
        }
        l[1] = 0 // log_α(1) = 0
        return l
    }()

    @inline(__always) static func mul(_ a: UInt8, _ b: UInt8) -> UInt8 {
        if a == 0 || b == 0 { return 0 }
        return exp[Int(log[Int(a)]) + Int(log[Int(b)])]
    }

    @inline(__always) static func inv(_ a: UInt8) -> UInt8 {
        precondition(a != 0, "vault: GF256 inverse of zero")
        return exp[255 - Int(log[Int(a)])]
    }
}

// MARK: - Codec

/// Errors raised by ``ReedSolomonCodec``.
public enum ReedSolomonError: Error, Equatable {
    /// Constructor argument out of range (K < 1, M < 0, or K + M > 256).
    case invalidParameters(String)
    /// `encode` received the wrong number of data shards or shards of unequal length.
    case invalidShards(String)
    /// `decodeDataShards` was given fewer than K survivors (unrecoverable), uneven shards, or a
    /// singular submatrix.
    case unrecoverable(String)
}

/// Systematic Reed-Solomon (K data + M parity) erasure codec over GF(2⁸). The K data shards are the
/// plaintext partitioned into equal zero-padded slices (byte-identical to the vault data layout); the
/// M parity shards are Cauchy-Reed-Solomon over the canonical 0x11D field. Any K of the K+M shards
/// reconstruct the original.
public struct ReedSolomonCodec {
    private let k: Int
    private let m: Int
    private let n: Int

    /// Parity generator rows: `parity[i]` (i = 0…M-1) is the K-byte Cauchy coefficient vector for
    /// parity shard K+i. Together with the implicit K×K systematic identity for the data shards these
    /// form the full N×K MDS generator matrix.
    private let parity: [[UInt8]]

    /// Total shard count (K + M).
    public var shardCount: Int { n }
    /// Number of data shards (K).
    public var dataShards: Int { k }
    /// Number of parity shards (M).
    public var parityShards: Int { m }

    /// - Parameters:
    ///   - k: Number of data shards (must be ≥ 1).
    ///   - m: Number of parity shards (must be ≥ 0). K + M must be ≤ 256.
    public init(k: Int, m: Int) throws {
        if k < 1 { throw ReedSolomonError.invalidParameters("K must be >= 1.") }
        if m < 0 { throw ReedSolomonError.invalidParameters("M must be >= 0.") }
        if k + m > 256 { throw ReedSolomonError.invalidParameters("K + M must be <= 256.") }
        self.k = k
        self.m = m
        self.n = k + m
        self.parity = Self.buildCauchyParityMatrix(k: k, m: m)
    }

    /// Encode `dataShards` (K byte arrays of equal length `shardSize`) into the full set of N shards.
    /// Shards 0…K-1 are the data shards unchanged (systematic); shards K…N-1 are the M Reed-Solomon
    /// parity shards. The returned shards are fresh copies — the caller keeps ownership of the input.
    public func encode(_ dataShards: [[UInt8]]) throws -> [[UInt8]] {
        guard dataShards.count == k else {
            throw ReedSolomonError.invalidShards("Expected \(k) data shards, got \(dataShards.count).")
        }
        let shardSize = dataShards[0].count
        for j in 0..<k where dataShards[j].count != shardSize {
            throw ReedSolomonError.invalidShards("All data shards must be the same length.")
        }

        var shards = [[UInt8]](repeating: [], count: n)

        // Systematic: the first K shards ARE the data shards (defensive copy — caller keeps ownership).
        for j in 0..<k { shards[j] = dataShards[j] }

        // Parity: shard K+i = Σ_j parity[i][j] · dataShards[j] over GF(256).
        for i in 0..<m {
            var parityShard = [UInt8](repeating: 0, count: shardSize)
            let coeffs = parity[i]
            for j in 0..<k {
                let c = coeffs[j]
                if c == 0 { continue }
                let src = dataShards[j]
                for b in 0..<shardSize {
                    parityShard[b] ^= GF256.mul(c, src[b])
                }
            }
            shards[k + i] = parityShard
        }

        return shards
    }

    /// Reconstruct the K data shards from any K available shards. `available` maps a shard index
    /// (0…N-1) to its bytes; exactly K distinct entries are required, all of equal length. Returns the
    /// K data shards (indices 0…K-1, in order). Throws ``ReedSolomonError/unrecoverable(_:)`` if fewer
    /// than K shards are supplied (K-1 or fewer is unrecoverable).
    public func decodeDataShards(_ available: [Int: [UInt8]]) throws -> [[UInt8]] {
        // Take the K lowest-indexed available shards (deterministic; any K suffice for an MDS code).
        let picked = available
            .filter { $0.key >= 0 && $0.key < n }
            .sorted { $0.key < $1.key }
            .prefix(k)

        if picked.count < k {
            throw ReedSolomonError.unrecoverable("Cannot decode: only \(picked.count)/\(k) shards available.")
        }

        let pickedArray = Array(picked)
        let shardSize = pickedArray[0].value.count
        for kv in pickedArray where kv.value.count != shardSize {
            throw ReedSolomonError.unrecoverable("All supplied shards must be the same length.")
        }

        // Fast path: if all K data shards (0…K-1) are present, no inversion is needed — the data is
        // the systematic prefix verbatim. This is the common, byte-identical-to-canonical case.
        if pickedArray.allSatisfy({ $0.key < k }) {
            var direct = [[UInt8]](repeating: [], count: k)
            for kv in pickedArray { direct[kv.key] = kv.value }
            return direct
        }

        // General path: build the K×K generator submatrix A for the picked shard indices, invert it,
        // and apply A⁻¹ to the picked symbol-vectors to recover the K source (data) symbols.
        var a = [[UInt8]](repeating: [], count: k)
        var rhs = [[UInt8]](repeating: [], count: k)
        for r in 0..<k {
            a[r] = generatorRow(pickedArray[r].key)
            rhs[r] = pickedArray[r].value
        }

        let invMatrix = try invertMatrix(a)

        var data = [[UInt8]](repeating: [], count: k)
        for r in 0..<k {
            var symbol = [UInt8](repeating: 0, count: shardSize)
            for c in 0..<k {
                let coeff = invMatrix[r][c]
                if coeff == 0 { continue }
                let src = rhs[c]
                for b in 0..<shardSize {
                    symbol[b] ^= GF256.mul(coeff, src[b])
                }
            }
            data[r] = symbol
        }

        return data
    }

    // MARK: - Generator matrix

    /// The K-byte generator row for shard `index` (identity for a data shard, Cauchy coefficients for
    /// a parity shard).
    private func generatorRow(_ index: Int) -> [UInt8] {
        if index < k {
            // Systematic data row = standard basis vector e_index.
            var row = [UInt8](repeating: 0, count: k)
            row[index] = 1
            return row
        }
        // Parity row (copy — the caller mutates rows during inversion).
        return parity[index - k]
    }

    /// Build the M×K Cauchy parity matrix over GF(256): `C[i][j] = 1 / (x_i ⊕ y_j)` with disjoint
    /// distinct element sets `y_j = j` (0…K-1) and `x_i = K + i` (K…K+M-1). Cauchy ⇒ every square
    /// submatrix invertible ⇒ MDS when stacked on the systematic identity.
    private static func buildCauchyParityMatrix(k: Int, m: Int) -> [[UInt8]] {
        var matrix = [[UInt8]](repeating: [], count: m)
        for i in 0..<m {
            var row = [UInt8](repeating: 0, count: k)
            let xi = UInt8((k + i) & 0xFF)
            for j in 0..<k {
                let yj = UInt8(j & 0xFF)
                // x_i and y_j are from disjoint ranges, so x_i ⊕ y_j is never 0 → always invertible.
                row[j] = GF256.inv(xi ^ yj)
            }
            matrix[i] = row
        }
        return matrix
    }

    // MARK: - GF(256) matrix inversion (Gauss-Jordan)

    /// Invert a K×K GF(256) matrix via Gauss-Jordan elimination. The Cauchy/identity stack guarantees
    /// the picked submatrix is non-singular.
    private func invertMatrix(_ matrix: [[UInt8]]) throws -> [[UInt8]] {
        let size = k
        // Augment [matrix | I].
        var aug = [[UInt8]](repeating: [], count: size)
        for r in 0..<size {
            var row = [UInt8](repeating: 0, count: 2 * size)
            for c in 0..<size { row[c] = matrix[r][c] }
            row[size + r] = 1
            aug[r] = row
        }

        for col in 0..<size {
            // Find a pivot row at or below `col` with a non-zero entry in this column.
            var pivot = -1
            for r in col..<size where aug[r][col] != 0 {
                pivot = r
                break
            }
            if pivot < 0 {
                throw ReedSolomonError.unrecoverable("Singular matrix — shard set is not decodable.")
            }

            if pivot != col {
                aug.swapAt(col, pivot)
            }

            // Normalise the pivot row so the pivot element becomes 1.
            let invPivot = GF256.inv(aug[col][col])
            for c in 0..<(2 * size) {
                aug[col][c] = GF256.mul(aug[col][c], invPivot)
            }

            // Eliminate this column from every other row.
            for r in 0..<size {
                if r == col { continue }
                let factor = aug[r][col]
                if factor == 0 { continue }
                for c in 0..<(2 * size) {
                    aug[r][c] ^= GF256.mul(factor, aug[col][c])
                }
            }
        }

        // Right half is the inverse.
        var result = [[UInt8]](repeating: [], count: size)
        for r in 0..<size {
            result[r] = Array(aug[r][size..<(2 * size)])
        }
        return result
    }
}
