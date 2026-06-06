// SPDX-License-Identifier: MIT
//
// Cross-language wire-format fixture verifier for ChunkBitmapPayload.
// Reads `fixtures/content/chunk_bitmap_vectors.json` from the repo root and
// asserts that BitsetCodec + marshalChunkBitmapJson produce byte-identical
// results for each canonical vector.

import Foundation
import XCTest
@testable import AetherNetProtocol

final class ChunkBitmapTests: XCTestCase {

    // ── Fixture model ────────────────────────────────────────────────────────

    private struct Vector: Decodable {
        let name: String
        let root_hash: String
        let chunk_count: Int
        let have_indices: [Int]
        let have_bitset_hex: String
        let have_bitset_base64: String
        let generation: UInt32
        let expected_json: String
    }

    // ── Fixture loader ───────────────────────────────────────────────────────

    /// Walk parent directories until we find the repo root that contains
    /// `fixtures/content/chunk_bitmap_vectors.json`.
    private func repoRoot() -> URL? {
        var url = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        for _ in 0..<12 {
            let candidate = url
                .appendingPathComponent("fixtures")
                .appendingPathComponent("content")
                .appendingPathComponent("chunk_bitmap_vectors.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return url
            }
            let parent = url.deletingLastPathComponent()
            guard parent.path != url.path else { break }
            url = parent
        }
        return nil
    }

    private func loadVectors() throws -> [Vector] {
        guard let root = repoRoot() else {
            XCTFail("Could not locate fixtures/content/chunk_bitmap_vectors.json")
            return []
        }
        let url = root
            .appendingPathComponent("fixtures")
            .appendingPathComponent("content")
            .appendingPathComponent("chunk_bitmap_vectors.json")
        let data = try Data(contentsOf: url)
        return try JSONDecoder().decode([Vector].self, from: data)
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private func toHex(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// BitsetCodec.encode produces the correct bitset bytes.
    func testEncodeProducesCorrectBitset() throws {
        for vec in try loadVectors() {
            let bs = BitsetCodec.encode(chunkCount: vec.chunk_count,
                                        haveIndices: vec.have_indices)
            let expectedLen = (vec.chunk_count + 7) / 8
            XCTAssertEqual(bs.count, expectedLen,
                           "\(vec.name): byte length should be ceil(chunk_count/8)")
            XCTAssertEqual(toHex(bs), vec.have_bitset_hex,
                           "\(vec.name): bitset hex mismatch")
        }
    }

    /// BitsetCodec.decode recovers the correct sorted indices.
    func testDecodeRecoverCorrectIndices() throws {
        for vec in try loadVectors() {
            guard let bitset = Data(base64Encoded: vec.have_bitset_base64) else {
                XCTFail("\(vec.name): failed to decode base64 '\(vec.have_bitset_base64)'")
                continue
            }
            let indices = BitsetCodec.decode(bitset: bitset,
                                              chunkCount: vec.chunk_count)
            XCTAssertEqual(indices, vec.have_indices,
                           "\(vec.name): decoded indices mismatch")
        }
    }

    /// marshalChunkBitmapJson produces the canonical JSON string.
    func testJsonSerializationMatchesExpected() throws {
        for vec in try loadVectors() {
            let bs = BitsetCodec.encode(chunkCount: vec.chunk_count,
                                        haveIndices: vec.have_indices)
            let json = marshalChunkBitmapJson(
                rootHash: vec.root_hash,
                chunkCount: vec.chunk_count,
                haveBitset: bs,
                generation: vec.generation
            )
            XCTAssertEqual(json, vec.expected_json,
                           "\(vec.name): JSON mismatch")
        }
    }

    /// Bitset length is exactly ceil(chunk_count / 8).
    func testBitsetLengthIsCeilDiv8() throws {
        for vec in try loadVectors() {
            let bs = BitsetCodec.encode(chunkCount: vec.chunk_count,
                                        haveIndices: vec.have_indices)
            let expected = (vec.chunk_count + 7) / 8
            XCTAssertEqual(bs.count, expected,
                           "\(vec.name): length should be \(expected)")
        }
    }

    /// Trailing bits beyond chunk_count are zero (no information leakage).
    func testTrailingBitsAreZero() throws {
        for vec in try loadVectors() {
            guard vec.chunk_count > 0 else { continue }
            let bs = BitsetCodec.encode(chunkCount: vec.chunk_count,
                                        haveIndices: vec.have_indices)
            let trailing = vec.chunk_count % 8
            guard trailing != 0 else { continue }   // full byte — nothing to check
            let last = bs[bs.count - 1]
            let validMask = UInt8((1 << trailing) - 1)
            let badBits = last & ~validMask
            XCTAssertEqual(badBits, 0,
                "\(vec.name): trailing bits should be zero; last byte=0x\(String(format:"%02x",last))")
        }
    }
}
