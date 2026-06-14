// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language Reed-Solomon parity: the Swift port must reproduce the C# reference vectors
/// (`fixtures/vault/reed_solomon_basic.json`, systematic Cauchy-Reed-Solomon K=10/M=4 over GF(2⁸)
/// poly 0x11D) byte-for-byte — every shard and every recovery byte.
final class ReedSolomonFixtureTests: XCTestCase {

    private struct Vectors: Decodable {
        struct Field: Decodable {
            let primitive_polynomial: String
            let alpha: Int
            let gf_bits: Int
        }
        struct Shard: Decodable {
            let index: Int
            let hex: String
        }
        struct Recovery: Decodable {
            let note: String
            let survivor_indices: [Int]
            let recovered: String
        }
        struct ShouldFail: Decodable {
            let note: String
            let survivor_indices: [Int]
        }
        let field: Field
        let k: Int
        let m: Int
        let n: Int
        let input_size: Int
        let shard_size: Int
        let input: String
        let shards: [Shard]
        let recovery: [Recovery]
        let should_fail: ShouldFail
    }

    private func loadVectors() throws -> Vectors {
        let repoRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
        let url = repoRoot.appendingPathComponent("fixtures/vault/reed_solomon_basic.json")
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }

    private func hexToBytes(_ s: String) -> [UInt8] {
        var out = [UInt8]()
        out.reserveCapacity(s.count / 2)
        var i = s.startIndex
        while i < s.endIndex {
            let next = s.index(i, offsetBy: 2)
            out.append(UInt8(s[i..<next], radix: 16) ?? 0)
            i = next
        }
        return out
    }

    private func hex(_ bytes: [UInt8]) -> String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }

    /// The Swift encoder reproduces every C# shard (systematic data + Cauchy parity) byte-for-byte.
    func testShardParity() throws {
        let v = try loadVectors()
        XCTAssertEqual(v.k, 10)
        XCTAssertEqual(v.m, 4)
        XCTAssertEqual(v.n, 14)

        let input = hexToBytes(v.input)
        XCTAssertEqual(input.count, v.input_size, "input size")

        let codec = try ReedSolomonCodec(k: v.k, m: v.m)
        let shards = try codec.encodeData(input)
        XCTAssertEqual(shards.count, v.n, "shard count")
        XCTAssertEqual(shards[0].count, v.shard_size, "shard size")

        for want in v.shards {
            XCTAssertEqual(hex(shards[want.index]), want.hex, "shard \(want.index)")
        }
    }

    /// Every recovery subset decodes to the fixture input byte-for-byte (covers the systematic
    /// fast-path, the all-parity-mix path, and the all-data path).
    func testRecoveryParity() throws {
        let v = try loadVectors()
        let input = hexToBytes(v.input)

        let codec = try ReedSolomonCodec(k: v.k, m: v.m)
        let shards = try codec.encodeData(input)

        for rec in v.recovery {
            var available = [Int: [UInt8]]()
            for idx in rec.survivor_indices { available[idx] = shards[idx] }

            let recovered = try codec.reconstructData(available, originalSize: v.input_size)
            XCTAssertEqual(hex(recovered), rec.recovered, "recovery: \(rec.note)")
            XCTAssertEqual(recovered, input, "recovery must equal original input: \(rec.note)")
        }
    }

    /// Only K-1 survivors is unrecoverable (the fixture's should_fail case).
    func testKMinusOneFails() throws {
        let v = try loadVectors()
        let input = hexToBytes(v.input)

        let codec = try ReedSolomonCodec(k: v.k, m: v.m)
        let shards = try codec.encodeData(input)

        XCTAssertEqual(v.should_fail.survivor_indices.count, v.k - 1, "should_fail must carry K-1 survivors")

        var available = [Int: [UInt8]]()
        for idx in v.should_fail.survivor_indices { available[idx] = shards[idx] }

        XCTAssertThrowsError(try codec.reconstructData(available, originalSize: v.input_size)) { error in
            XCTAssertTrue(error is ReedSolomonError, "K-1 survivors must throw ReedSolomonError")
        }
    }

    /// Recovery works from JUST the M parity shards plus enough data shards to reach K — exercising the
    /// general matrix-inversion path with the maximum number of parity rows.
    func testParityAssistedRecovery() throws {
        let v = try loadVectors()
        let input = hexToBytes(v.input)

        let codec = try ReedSolomonCodec(k: v.k, m: v.m)
        let shards = try codec.encodeData(input)

        // Drop the first M data shards; survive on data[M..K-1] + all M parity shards = K total.
        var available = [Int: [UInt8]]()
        for i in v.m..<v.k { available[i] = shards[i] }
        for i in v.k..<v.n { available[i] = shards[i] }

        let recovered = try codec.reconstructData(available, originalSize: v.input_size)
        XCTAssertEqual(recovered, input, "parity-assisted recovery must reproduce the original input")
    }
}
